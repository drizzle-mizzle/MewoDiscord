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

        // Пинг: в канале с сессиями продолжает последнюю активную
        if (message.MentionedUsers.Any(u => u.Id == botId))
        {
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
                await ReplyAsync(message, BotMessages.ChatGptEmptyPrompt());
                return;
            }

            BotLogger.LogAi(BotLogger.ChatGptThreadKey, "Хит в сессию {Id} ({Type}) от {User}", entry.Id, ChatGptSessionStore.TypeToString(entry.Type), message.Author.Username);

            using var typing = message.Channel.EnterTypingState();

            if (entry.Type == ChatGptSessionType.Chat)
            {
                await HandleChatHitAsync(message, entry, text, files);
            }
            else
            {
                await HandleImageHitAsync(message, entry, text, files);
            }
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>
    /// Чат: ответ модели реплаем, длинный — чанками; привязка сессии переезжает
    /// на последний чанк. При ошибке привязка не трогается — реплай можно повторить.
    /// </summary>
    private static async Task HandleChatHitAsync(SocketUserMessage message, ChatGptSessionStore.SessionEntry entry, string text, List<ChatGptClient.InputFile> files)
    {
        var reply = await ChatGptClient.ChatAsync(entry.Runtime, text, files);

        if (string.IsNullOrEmpty(reply))
        {
            await ReplyAsync(message, BotMessages.ChatGptRequestFailed());
            return;
        }

        IUserMessage? last = null;
        var first = true;

        foreach (var chunk in BotLogger.SplitMessage(reply))
        {
            last = await message.Channel.SendMessageAsync(
                chunk,
                allowedMentions: AllowedMentions.None,
                messageReference: first ? new MessageReference(message.Id, failIfNotExists: false) : null);
            first = false;
        }

        if (last != null)
        {
            ChatGptSessionStore.Rebind(entry, last.Id);
        }
    }

    /// <summary>
    /// Картинки: первый хит — генерация (вложения — референсы), дальше — правка
    /// последней картинки (вложения — дополнительные референсы).
    /// </summary>
    private static async Task HandleImageHitAsync(SocketUserMessage message, ChatGptSessionStore.SessionEntry entry, string text, List<ChatGptClient.InputFile> files)
    {
        ChatGptClient.GeneratedImage? image;

        if (entry.Runtime.HasImage)
        {
            image = await ChatGptClient.ContinueImageAsync(entry.Runtime, text, extraReferences: files.Count > 0 ? files : null);
        }
        else if (files.Count > 0)
        {
            image = await ChatGptClient.GenerateImageAsync(entry.Runtime, text, files);
        }
        else
        {
            image = await ChatGptClient.GenerateImageAsync(entry.Runtime, text);
        }

        if (image == null)
        {
            await ReplyAsync(message, BotMessages.ChatGptImageFailed());
            return;
        }

        var uploadLimit = ((message.Channel as SocketGuildChannel)?.Guild)?.MaxUploadLimit ?? FallbackUploadLimit;

        if ((ulong)image.Content.Length > uploadLimit)
        {
            // Картинка уже в состоянии сессии — привязку двигаем на сообщение об ошибке,
            // чтобы правки реплаем оставались возможны
            var notice = await ReplyAsync(message, BotMessages.ChatGptImageTooBig(FormatSize(image.Content.Length)));

            if (notice != null)
            {
                ChatGptSessionStore.Rebind(entry, notice.Id);
            }

            return;
        }

        using var stream = new MemoryStream(image.Content);
        using var attachment = new FileAttachment(stream, BuildImageFileName(image.MimeType));

        var sent = await message.Channel.SendFilesAsync(
            [attachment],
            allowedMentions: AllowedMentions.None,
            messageReference: new MessageReference(message.Id, failIfNotExists: false));

        ChatGptSessionStore.Rebind(entry, sent.Id);
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

    private static async Task<IUserMessage?> ReplyAsync(SocketUserMessage message, string text)
    {
        try
        {
            return await message.Channel.SendMessageAsync(
                text,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(message.Id, failIfNotExists: false));
        }
        catch (Exception ex)
        {
            BotLogger.Error("ChatGPT: не удалось отправить ответ: {Message}", ex.Message);
            return null;
        }
    }

    private static string BuildImageFileName(string mime) =>
        mime switch
        {
            "image/jpeg" => "gpt-image.jpg",
            "image/webp" => "gpt-image.webp",
            "image/gif" => "gpt-image.gif",
            _ => "gpt-image.png"
        };

    private static string FormatSize(long bytes) =>
        $"{bytes / 1024d / 1024d:F1} МБ";

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex BotMentionRegex();
}
