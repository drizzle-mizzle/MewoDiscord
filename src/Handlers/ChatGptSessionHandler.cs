using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using MewoDiscord.AiActionsProcessors;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

/// <summary>
/// Роутинг сообщений в сессии ChatGPT. Реплай на последнее сообщение сессии — хит в неё;
/// пинг бота в канале с сессиями — хит в последнюю активную. Реплай на старое сообщение
/// сессии игнорируется (правки истории и вилки не поддерживаются). Решение о хите
/// принимается быстро под канальным замком, сам хит выполняется в фоне под замком сессии.
/// </summary>
public static partial class ChatGptSessionHandler
{
    /// <summary>
    /// Запасной лимит вложения, если гильдию определить не удалось.
    /// </summary>
    private const ulong FallbackUploadLimit = 10 * 1024 * 1024;

    private const int AttachmentDownloadTimeoutSeconds = 60;

    /// <summary>
    /// Клиент для скачивания вложений Discord (CDN, прокси не нужен).
    /// </summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(AttachmentDownloadTimeoutSeconds)
    };

    /// <summary>
    /// Собранный ход сессии: текст, файлы, обстановка для шапки сообщения и словарь
    /// «имя → id» тех, кого модели позволено упомянуть в ответе.
    /// </summary>
    internal record TurnRequest(
        string Text,
        IReadOnlyList<ChatGptClient.InputFile> Files,
        ChatGptClient.ChatContext Context,
        IReadOnlyDictionary<string, ulong> Mentionable);

    /// <summary>
    /// Пытается направить сообщение в сессию ChatGPT.
    /// true — сообщение потреблено (хит запущен или реплай проигнорирован по правилам).
    /// </summary>
    public static async Task<bool> TryHandleAsync(SocketUserMessage message)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        if (guild is null)
        {
            return false;
        }

        var botId = guild.CurrentUser.Id;
        var referencedId = message.Reference?.MessageId.IsSpecified == true ? message.Reference.MessageId.Value : (ulong?)null;

        // Реплай в закреплённое сообщение сессии — хит в неё. Цитаты здесь нет:
        // это не «ответ кому-то», а продолжение того же разговора
        if (referencedId != null)
        {
            // Медиа-сессия проверяется первой, хотя на одном сообщении может висеть
            // и она, и сессия ChatGPT. Так и задумано: медиа-путь умеет отдать работу
            // модели (просьба оказалась не механической), а обратно — нет, поэтому
            // решать, чей это ход, должен именно он.
            // Упоминание бота на этом пути не нужно, как и в сессиях ChatGPT:
            // ответ в закреплённое сообщение сам по себе однозначен
            var media = MediaSessionStore.FindByAnchor(referencedId.Value);

            if (media != null)
            {
                StartMediaTurn(message, media);
                return true;
            }

            var anchored = ChatGptSessionStore.FindByMessageId(referencedId.Value);

            if (anchored != null)
            {
                StartHit(message, anchored, quoted: null);
                return true;
            }
        }

        if (!message.MentionedUsers.Any(u => u.Id == botId))
        {
            return false;
        }

        // Пинг с реплаем на чужое сообщение — обращение с цитатой: её увидит модель
        IMessage? quoted = null;

        if (referencedId != null)
        {
            quoted = message.Channel.GetCachedMessage(referencedId.Value);

            // Embed'ы ссылок Discord дорисовывает уже после доставки сообщения, и в кэше
            // их может не быть — за гифкой по ссылке идём в REST за свежей копией
            if (quoted == null || (quoted.Attachments.Count == 0 && quoted.Embeds.Count == 0))
            {
                quoted = await FetchMessageAsync(message.Channel, referencedId.Value) ?? quoted;
            }

            if (quoted?.Author.Id == botId && ChatGptSessionStore.HasSessions(message.Channel.Id))
            {
                // Не последнее сообщение сессии — по ТЗ просто игнор, без реакции
                BotLogger.Information("ChatGPT: реплай на старое сообщение {MessageId} проигнорирован", referencedId.Value);
                return true;
            }
        }

        if (await CustomAiActionHandler.TryHandleAsync(message, botId, quoted))
        {
            return true;
        }

        var entry = ChatGptSessionStore.FindLastActive(message.Channel.Id);

        if (entry != null)
        {
            StartHit(message, entry, quoted);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Запускает обработку хита в фоне: генерация занимает минуты и не должна
    /// держать канальный замок MessageHandler.
    /// </summary>
    /// <summary>
    /// Запускает круг правок медиа в фоне: ffmpeg работает секундами и минутами,
    /// а канальный замок MessageHandler держать столько нельзя.
    /// </summary>
    private static void StartMediaTurn(SocketUserMessage message, MediaSession session)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ConvertMedia.ContinueAsync(message, session);
            }
            catch (Exception ex)
            {
                BotLogger.Error(ex, "Ошибка продолжения медиа-сессии {Anchor}", session.AnchorMessageId);
            }
        });
    }

    private static void StartHit(SocketUserMessage message, ChatGptSessionStore.SessionEntry entry, IMessage? quoted)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessHitAsync(message, entry, quoted);
            }
            catch (Exception ex)
            {
                BotLogger.Error(ex, "Ошибка обработки хита в сессию ChatGPT {Id}", entry.Id);
            }
        });
    }

    private static async Task ProcessHitAsync(SocketUserMessage message, ChatGptSessionStore.SessionEntry entry, IMessage? quoted)
    {
        // Хиты в одну сессию — строго по одному: ChatGptSession не потокобезопасна
        await entry.Lock.WaitAsync();

        try
        {
            var guild = (message.Channel as SocketGuildChannel)?.Guild;

            // Упоминания не вырезаем, а разворачиваем в имена — включая упоминание самого
            // бота: модель должна видеть, к кому обращаются
            var text = DiscordMentions.Humanize(message.Content.Trim(), message, guild);
            var (files, notes) = await DownloadMediaAsync(message);

            // Картинки цитируемого сообщения — тоже предмет разговора: «@bot добавь ей ушки»
            // в ответ на чужую гифку говорит именно о ней
            if (quoted != null)
            {
                var (quotedFiles, quotedNotes) = await DownloadMediaAsync(quoted);
                files.AddRange(quotedFiles);
                notes.AddRange(quotedNotes);
            }

            if (notes.Count > 0)
            {
                text = string.Join('\n', new[] { text }.Concat(notes)).Trim();
            }

            if (text.Length == 0 && files.Count == 0)
            {
                await ReplyAsync(message.Channel, message.Id, BotEmbeds.Warning(BotMessages.ChatGptEmptyPrompt()));
                return;
            }

            BotLogger.LogAi(BotLogger.ChatGptThreadKey, "Хит в сессию {Id} от {User}", entry.Id, message.Author.Username);

            using var typing = message.Channel.EnterTypingState();

            await RunTurnAsync(message.Channel, message.Id, entry, BuildRequest(message, guild, quoted, text, files));
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>
    /// Собирает ход: обстановку для шапки сообщения и список тех, кого модели позволено
    /// упомянуть в ответе. Список — только участники этого обмена: автор, упомянутые им
    /// и автор цитаты. Позвать кого-то ещё модель не сможет.
    /// </summary>
    private static TurnRequest BuildRequest(
        SocketUserMessage message,
        SocketGuild? guild,
        IMessage? quoted,
        string text,
        IReadOnlyList<ChatGptClient.InputFile> files)
    {
        var botId = guild?.CurrentUser.Id ?? 0;
        var mentionable = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        void Allow(IUser? user)
        {
            if (user != null && user.Id != botId)
            {
                mentionable[DiscordMentions.DisplayNameOf(user)] = user.Id;
            }
        }

        Allow(message.Author);
        Allow(quoted?.Author);

        foreach (var user in message.MentionedUsers)
        {
            Allow(user);
        }

        var context = new ChatGptClient.ChatContext(
            guild?.CurrentUser.DisplayName,
            DiscordMentions.DisplayNameOf(message.Author),
            quoted == null ? null : DiscordMentions.DisplayNameOf(quoted.Author),
            quoted == null ? null : DiscordMentions.Humanize(quoted.Content, guild));

        return new TurnRequest(text, files, context, mentionable);
    }

    /// <summary>
    /// Выполняет ход сессии и отправляет ответ модели в канал: текст чанками по 2000
    /// символов, картинки — вложениями. Модель сама решает, рисовать ли, поэтому в ответе
    /// может быть и то, и другое. Ответ реплаится на referenceMessageId, а привязка сессии
    /// переезжает на последнее отправленное сообщение; при ошибке не трогается —
    /// реплай на прежнее сообщение можно повторить. Замок сессии берёт вызывающий.
    /// </summary>
    /// <summary>
    /// Возвращает последнее отправленное сообщение ответа — за него цепляются те, кому
    /// нужно знать, чем кончился ход: медиа-сессия переезжает на него, если модель
    /// прислала картинку. null — ответа не вышло.
    /// </summary>
    internal static async Task<IUserMessage?> RunTurnAsync(
        ISocketMessageChannel channel,
        ulong referenceMessageId,
        ChatGptSessionStore.SessionEntry entry,
        TurnRequest request)
    {
        var reply = await ChatGptClient.ChatAsync(entry.Runtime, request.Text, request.Files, request.Context);

        if (reply.Text.Length == 0 && reply.Images.Count == 0)
        {
            await ReplyAsync(channel, referenceMessageId, BotEmbeds.Error(BotMessages.ChatGptRequestFailed()));
            return null;
        }

        // Модель пишет @имя — возвращаем настоящие упоминания. Пинговать разрешаем только
        // тех, кого она действительно назвала, и только из участников обмена
        var (replyText, mentioned) = DiscordMentions.Restore(reply.Text, request.Mentionable);

        var allowedMentions = new AllowedMentions
        {
            UserIds = mentioned.ToList(),
            MentionRepliedUser = false
        };

        // Картинки крупнее лимита сервера не грузятся — вместо них уведомление
        var uploadLimit = DiscordLimits.UploadLimit(channel, FallbackUploadLimit);
        var images = new List<ChatGptClient.GeneratedImage>();
        var oversized = new List<string>();

        foreach (var image in reply.Images)
        {
            if ((ulong)image.Content.Length <= uploadLimit)
            {
                images.Add(image);
            }
            else
            {
                oversized.Add(BotMessages.ChatGptImageTooBig(DiscordLimits.FormatSize(image.Content.Length)));
            }
        }

        // Уведомление едет отдельным embed'ом на последнем сообщении ответа: текст модели
        // остаётся обычным сообщением, системная пометка не смешивается с ним
        var notice = oversized.Count > 0 ? BotEmbeds.Warning(string.Join('\n', oversized)) : null;
        var chunks = replyText.Length > 0 ? BotLogger.SplitMessage(replyText) : [];

        // Ответ целиком не поместился: остаётся только уведомление
        if (chunks.Count == 0 && images.Count == 0)
        {
            if (notice != null)
            {
                await ReplyAsync(channel, referenceMessageId, notice);
            }

            return null;
        }

        // Текст без картинок: последний чанк уходит обычным сообщением,
        // с картинками — они прикрепляются к последнему сообщению
        var plainChunks = images.Count > 0 ? chunks.Count : chunks.Count - 1;
        IUserMessage? last = null;
        var first = true;

        for (var i = 0; i < plainChunks; i++)
        {
            last = await channel.SendMessageAsync(
                chunks[i],
                allowedMentions: allowedMentions,
                messageReference: first ? BuildReference(referenceMessageId) : null);
            first = false;
        }

        if (images.Count > 0)
        {
            var attachments = images
                .Select((image, index) => new FileAttachment(new MemoryStream(image.Content), BuildImageFileName(image.MimeType, index)))
                .ToList();

            try
            {
                last = await channel.SendFilesAsync(
                    attachments,
                    embed: notice,
                    allowedMentions: allowedMentions,
                    messageReference: first ? BuildReference(referenceMessageId) : null);
            }
            finally
            {
                foreach (var attachment in attachments)
                {
                    attachment.Dispose();
                }
            }
        }
        else
        {
            last = await channel.SendMessageAsync(
                chunks[^1],
                embed: notice,
                allowedMentions: allowedMentions,
                messageReference: first ? BuildReference(referenceMessageId) : null);
        }

        if (last != null)
        {
            ChatGptSessionStore.Rebind(entry, last.Id);
        }

        return last;
    }

    /// <summary>
    /// Скачивает вложения сообщения. Непригодные (слишком большие, не скачавшиеся)
    /// превращаются в пометки, которые дописываются к тексту запроса.
    /// </summary>
    private static async Task<(List<ChatGptClient.InputFile> Files, List<string> Notes)> DownloadMediaAsync(IMessage message)
    {
        var files = new List<ChatGptClient.InputFile>();
        var notes = new List<string>();

        foreach (var attachment in message.Attachments)
        {
            if (attachment.Size > ChatGptClient.MaxInputFileBytes)
            {
                notes.Add($"[файл {attachment.Filename} пропущен: превышен лимит размера]");
                continue;
            }

            try
            {
                var content = await Http.GetByteArrayAsync(attachment.Url);
                files.Add(new ChatGptClient.InputFile(attachment.Filename, content, attachment.ContentType));
            }
            catch (Exception ex)
            {
                BotLogger.Warning("ChatGPT: не удалось скачать вложение {Url}: {Message}", attachment.Url, ex.Message);
                notes.Add($"[файл {attachment.Filename} не удалось скачать]");
            }
        }

        // Картинка или гифка, вставленная ссылкой, вложением не является: Discord показывает
        // её embed'ом, а сам файл лежит на чужом хосте
        foreach (var url in CollectEmbedImageUrls(message))
        {
            try
            {
                var content = await Http.GetByteArrayAsync(url);

                if (content.Length > ChatGptClient.MaxInputFileBytes)
                {
                    notes.Add("[картинка по ссылке пропущена: превышен лимит размера]");
                    continue;
                }

                files.Add(new ChatGptClient.InputFile(DiscordLimits.FileNameFromUrl(url, "image.png"), content));
            }
            catch (Exception ex)
            {
                BotLogger.Warning("ChatGPT: не удалось скачать картинку из embed'а {Url}: {Message}", url, ex.Message);
            }
        }

        return (files, notes);
    }

    /// <summary>
    /// Ссылки на картинки из embed'ов сообщения. Берём только то, что действительно
    /// картинка: превью статьи или ссылки — шум, а не предмет разговора.
    /// </summary>
    private static List<string> CollectEmbedImageUrls(IMessage message)
    {
        var urls = new List<string>();

        foreach (var embed in message.Embeds)
        {
            var url = embed.Image?.ProxyUrl ?? embed.Image?.Url;

            if (url == null && embed.Type is EmbedType.Image or EmbedType.Gifv or EmbedType.Video)
            {
                url = embed.Thumbnail?.ProxyUrl ?? embed.Thumbnail?.Url;
            }

            if (url != null)
            {
                urls.Add(PreferStaticImage(url));
            }
        }

        return urls;
    }

    /// <summary>
    /// Просит у прокси Discord статичную png. Гифка нужна модели одним кадром: анимацию
    /// она всё равно не понимает, а свой прокси Discord умеет конвертировать на лету.
    /// Чужие хосты (tenor и прочие) остаются как есть — там такого параметра нет.
    /// </summary>
    private static string PreferStaticImage(string url)
    {
        if (!url.Contains("media.discordapp.net", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return url.Contains('?') ? url + "&format=png" : url + "?format=png";
    }

    /// <summary>
    /// Вырезает упоминания бота из текста пинга.
    /// </summary>
    internal static string StripBotMention(string content, ulong botId)
    {
        return BotMentionRegex().Replace(content, match =>
            match.Groups[1].Value == botId.ToString() ? string.Empty : match.Value).Trim();
    }

    /// <summary>
    /// Достаёт сообщение по идентификатору. В кэше его может не быть — например,
    /// когда медиа-сессия возвращается к исходнику, отправленному давно.
    /// </summary>
    internal static async Task<IMessage?> FetchMessageAsync(ISocketMessageChannel channel, ulong messageId)
    {
        try
        {
            return await channel.GetMessageAsync(messageId);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("ChatGPT: не удалось получить сообщение {MessageId}: {Message}", messageId, ex.Message);
            return null;
        }
    }

    private static async Task<IUserMessage?> ReplyAsync(ISocketMessageChannel channel, ulong referenceMessageId, Embed embed)
    {
        try
        {
            return await channel.SendMessageAsync(
                embed: embed,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(referenceMessageId, failIfNotExists: false));
        }
        catch (Exception ex)
        {
            BotLogger.Error("ChatGPT: не удалось отправить ответ: {Message}", ex.Message);
            return null;
        }
    }

    private static MessageReference BuildReference(ulong referenceMessageId) =>
        new(referenceMessageId, failIfNotExists: false);

    private static string BuildImageFileName(string mime, int index)
    {
        var extension = mime switch
        {
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => "png"
        };

        return index == 0 ? $"gpt-image.{extension}" : $"gpt-image-{index + 1}.{extension}";
    }

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex BotMentionRegex();
}
