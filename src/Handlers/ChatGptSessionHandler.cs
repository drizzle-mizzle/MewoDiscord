using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
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

        // Реплай: попадание в закреплённое сообщение — хит; в старое сообщение бота — игнор
        if (message.Reference?.MessageId.IsSpecified == true)
        {
            var referencedId = message.Reference.MessageId.Value;
            var entry = ChatGptSessionStore.FindByMessageId(referencedId);

            if (entry != null)
            {
                StartHit(message, entry, stripMention: false);
                return true;
            }

            if (!ChatGptSessionStore.HasSessions(message.Channel.Id))
            {
                return false;
            }

            var referenced = message.Channel.GetCachedMessage(referencedId) ?? await FetchMessageAsync(message.Channel, referencedId);

            if (referenced?.Author.Id == botId)
            {
                // Не последнее сообщение сессии — по ТЗ просто игнор, без реакции
                BotLogger.Information("ChatGPT: реплай на старое сообщение {MessageId} проигнорирован", referencedId);
                return true;
            }

            return false;
        }

        // Пинг: сначала кастомные действия, потом продолжение последней активной сессии
        if (message.MentionedUsers.Any(u => u.Id == botId))
        {
            if (await CustomAiActionHandler.TryHandleAsync(message, botId))
            {
                return true;
            }

            var entry = ChatGptSessionStore.FindLastActive(message.Channel.Id);

            if (entry != null)
            {
                StartHit(message, entry, stripMention: true);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Запускает обработку хита в фоне: генерация занимает минуты и не должна
    /// держать канальный замок MessageHandler.
    /// </summary>
    private static void StartHit(SocketUserMessage message, ChatGptSessionStore.SessionEntry entry, bool stripMention)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessHitAsync(message, entry, stripMention);
            }
            catch (Exception ex)
            {
                BotLogger.Error(ex, "Ошибка обработки хита в сессию ChatGPT {Id}", entry.Id);
            }
        });
    }

    private static async Task ProcessHitAsync(SocketUserMessage message, ChatGptSessionStore.SessionEntry entry, bool stripMention)
    {
        // Хиты в одну сессию — строго по одному: ChatGptSession не потокобезопасна
        await entry.Lock.WaitAsync();

        try
        {
            var botId = ((message.Channel as SocketGuildChannel)?.Guild)?.CurrentUser.Id ?? 0;
            var text = stripMention ? StripBotMention(message.Content, botId) : message.Content.Trim();
            var (files, notes) = await DownloadAttachmentsAsync(message);

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

            await RunTurnAsync(message.Channel, message.Id, entry, text, files);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>
    /// Выполняет ход сессии и отправляет ответ модели в канал: текст чанками по 2000
    /// символов, картинки — вложениями. Модель сама решает, рисовать ли, поэтому в ответе
    /// может быть и то, и другое. Ответ реплаится на referenceMessageId, а привязка сессии
    /// переезжает на последнее отправленное сообщение; при ошибке не трогается —
    /// реплай на прежнее сообщение можно повторить. Замок сессии берёт вызывающий.
    /// </summary>
    internal static async Task RunTurnAsync(
        ISocketMessageChannel channel,
        ulong referenceMessageId,
        ChatGptSessionStore.SessionEntry entry,
        string text,
        IReadOnlyList<ChatGptClient.InputFile> files)
    {
        var reply = await ChatGptClient.ChatAsync(entry.Runtime, text, files);

        if (reply.Text.Length == 0 && reply.Images.Count == 0)
        {
            await ReplyAsync(channel, referenceMessageId, BotEmbeds.Error(BotMessages.ChatGptRequestFailed()));
            return;
        }

        // Картинки крупнее лимита сервера не грузятся — вместо них уведомление
        var uploadLimit = ((channel as SocketGuildChannel)?.Guild)?.MaxUploadLimit ?? FallbackUploadLimit;
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
                oversized.Add(BotMessages.ChatGptImageTooBig(FormatSize(image.Content.Length)));
            }
        }

        // Уведомление едет отдельным embed'ом на последнем сообщении ответа: текст модели
        // остаётся обычным сообщением, системная пометка не смешивается с ним
        var notice = oversized.Count > 0 ? BotEmbeds.Warning(string.Join('\n', oversized)) : null;
        var chunks = reply.Text.Length > 0 ? BotLogger.SplitMessage(reply.Text) : [];

        // Ответ целиком не поместился: остаётся только уведомление
        if (chunks.Count == 0 && images.Count == 0)
        {
            if (notice != null)
            {
                await ReplyAsync(channel, referenceMessageId, notice);
            }

            return;
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
                allowedMentions: AllowedMentions.None,
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
                    allowedMentions: AllowedMentions.None,
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
                allowedMentions: AllowedMentions.None,
                messageReference: first ? BuildReference(referenceMessageId) : null);
        }

        if (last != null)
        {
            ChatGptSessionStore.Rebind(entry, last.Id);
        }
    }

    /// <summary>
    /// Скачивает вложения сообщения. Непригодные (слишком большие, не скачавшиеся)
    /// превращаются в пометки, которые дописываются к тексту запроса.
    /// </summary>
    private static async Task<(List<ChatGptClient.InputFile> Files, List<string> Notes)> DownloadAttachmentsAsync(SocketUserMessage message)
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

        return (files, notes);
    }

    /// <summary>
    /// Вырезает упоминания бота из текста пинга.
    /// </summary>
    internal static string StripBotMention(string content, ulong botId)
    {
        return BotMentionRegex().Replace(content, match =>
            match.Groups[1].Value == botId.ToString() ? string.Empty : match.Value).Trim();
    }

    private static async Task<IMessage?> FetchMessageAsync(ISocketMessageChannel channel, ulong messageId)
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

    private static string FormatSize(long bytes) =>
        $"{bytes / 1024d / 1024d:F1} МБ";

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex BotMentionRegex();
}
