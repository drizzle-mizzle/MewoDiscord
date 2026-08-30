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
    /// Клиент для скачивания вложений Discord. Потолок буфера обязателен: картинка
    /// из embed'а качается с чужого хоста, размер заранее неизвестен, а без предела
    /// ответ буферизуется в память целиком. Превышение приезжает исключением.
    /// </summary>
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(AttachmentDownloadTimeoutSeconds),
        MaxResponseContentBufferSize = ChatGptClient.MaxInputFileBytes
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
            // Медиа-сессия проверяется первой, хотя на сообщении может висеть и она,
            // и сессия ChatGPT: медиа-путь умеет отдать работу модели, а обратно — нет.
            // Упоминание бота здесь не нужно — ответ в закреплённое сообщение однозначен
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

            // Реплай в ветку сессии, но не в её конец: правки истории и вилки
            // не поддерживаются, такое сообщение просто игнорируется. Проверяется
            // принадлежность сессии, а не авторство бота: иначе так же молча пропадал бы
            // «@бот сделай гифку» реплаем на медиа-контейнер
            if (quoted?.Author.Id == botId
                && ChatGptSessionStore.IsKnownMessage(message.Channel.Id, referencedId.Value))
            {
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

    /// <summary>
    /// Запускает обработку хита в фоне: генерация занимает минуты и не должна
    /// держать канальный замок MessageHandler.
    /// </summary>
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
    /// Выполняет ход сессии и отправляет ответ в канал: текст чанками по 2000 символов,
    /// картинки — вложениями. Ответ реплаится на referenceMessageId, привязка сессии
    /// переезжает на последнее отправленное сообщение; при ошибке не трогается, чтобы
    /// реплай можно было повторить. Замок сессии берёт вызывающий.
    /// </summary>
    /// <returns>Последнее сообщение ответа; null — ответа не вышло.</returns>
    internal static async Task<IUserMessage?> RunTurnAsync(
        ISocketMessageChannel channel,
        ulong referenceMessageId,
        ChatGptSessionStore.SessionEntry entry,
        TurnRequest request)
    {
        var reply = await ChatGptClient.ChatAsync(entry.Runtime, request.Text, request.Files, request.Context);

        if (reply.Text.Length == 0 && reply.Images.Count == 0)
        {
            // Умершая авторизация повтором не лечится — про неё говорим прямо,
            // иначе пользователь будет долбить реплаями впустую
            var error = reply.Unauthorized
                ? BotMessages.ChatGptNotAuthorized()
                : BotMessages.ChatGptRequestFailed();

            await ReplyAsync(channel, referenceMessageId, BotEmbeds.Error(error));
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
                var content = await _http.GetByteArrayAsync(attachment.Url);
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
            // Адрес пришёл из ссылки в чужом сообщении, а не от Discord: туда же,
            // куда и медиа соцсетей, ходим только по проверенному адресу
            if (!SocialMediaHttp.IsSafeUrl(url))
            {
                continue;
            }

            try
            {
                // Размер здесь заранее неизвестен (хост чужой), поэтому потолок держит
                // сам клиент: сверх него скачивание обрывается исключением
                var content = await _http.GetByteArrayAsync(url);

                files.Add(new ChatGptClient.InputFile(DiscordLimits.FileNameFromUrl(url, "image.png"), content));
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
            {
                BotLogger.Warning("ChatGPT: картинка по ссылке {Url} больше лимита", url);
                notes.Add("[картинка по ссылке пропущена: превышен лимит размера]");
            }
            catch (Exception ex)
            {
                BotLogger.Warning("ChatGPT: не удалось скачать картинку из embed'а {Url}: {Message}", url, ex.Message);
                notes.Add("[картинку по ссылке не удалось скачать]");
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
