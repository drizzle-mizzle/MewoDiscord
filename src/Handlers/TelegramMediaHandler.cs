using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

/// <summary>
/// Подтягивает медиа из публичных постов Telegram, ссылки на которые появились в чате,
/// и отвечает на исходное сообщение оформленным embed'ом с файлами.
/// </summary>
public static partial class TelegramMediaHandler
{
    private const int MaxLinksPerMessage = 3;
    private const int MaxAttachments = 10;
    private const int MaxFileNameLength = 64;

    /// <summary>
    /// Лимит описания embed — 4096 символов, оставляем запас на разметку цитаты.
    /// </summary>
    private const int MaxCaptionLength = 3800;

    /// <summary>
    /// Запасной лимит вложения, если гильдию определить не удалось.
    /// </summary>
    private const ulong FallbackUploadLimit = 10 * 1024 * 1024;

    /// <summary>
    /// Фирменный синий Telegram.
    /// </summary>
    private static readonly Color TelegramBlue = new(0x2AABEE);

    /// <summary>
    /// Запускает обработку в фоне: скачивание медиа не должно задерживать остальную
    /// обработку сообщений канала, которая идёт под общим замком.
    /// </summary>
    public static void HandleInBackground(SocketUserMessage message)
    {
        var links = FindLinks(message.Content);

        if (links.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var replied = false;

            foreach (var (channel, postId) in links)
            {
                try
                {
                    replied |= await HandleLinkAsync(message, channel, postId);
                }
                catch (Exception ex)
                {
                    BotLogger.Error("Ошибка обработки ссылки Telegram t.me/{Channel}/{Post}: {Message}", channel, postId, ex.Message);
                }
            }

            // Своё оформление показали — стандартное превью Discord больше не нужно
            if (replied)
            {
                await SuppressSourceEmbedsAsync(message);
            }
        });
    }

    /// <summary>
    /// Находит ссылки на посты публичных каналов. Приватные (t.me/c/...) не подходят:
    /// их виджет требует авторизации.
    /// </summary>
    internal static IReadOnlyList<(string Channel, string PostId)> FindLinks(string text)
    {
        var result = new List<(string Channel, string PostId)>();

        foreach (Match match in PostLinkRegex().Matches(text))
        {
            var link = (match.Groups[1].Value, match.Groups[2].Value);

            if (!result.Contains(link))
            {
                result.Add(link);
            }

            if (result.Count == MaxLinksPerMessage)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Обрабатывает одну ссылку. Возвращает true, если ответ с медиа отправлен.
    /// </summary>
    private static async Task<bool> HandleLinkAsync(SocketUserMessage message, string channel, string postId)
    {
        var post = await TelegramPostClient.TryGetPostAsync(channel, postId);

        if (post == null || post.Media.Count == 0)
        {
            return false;
        }

        var postUrl = TelegramPostClient.BuildPostUrl(channel, postId);
        var uploadLimit = GetUploadLimit(message);
        var attachments = new List<FileAttachment>();
        var fileNames = new List<string>();
        var oversized = new List<string>();
        string? thumbnailUrl = null;

        try
        {
            foreach (var media in post.Media.Take(MaxAttachments))
            {
                var download = await TelegramPostClient.TryDownloadAsync(media.Url, uploadLimit);

                if (download == null)
                {
                    continue;
                }

                // Не влезло в лимит Discord — покажем превью и ссылку вместо файла
                if (download.Content == null)
                {
                    oversized.Add(FormatSize(download.SizeBytes));
                    thumbnailUrl ??= media.ThumbnailUrl;
                    continue;
                }

                var fileName = BuildFileName(media);
                attachments.Add(new FileAttachment(download.Content, fileName));
                fileNames.Add(fileName);
            }

            if (attachments.Count == 0 && oversized.Count == 0)
            {
                return false;
            }

            var reference = new MessageReference(message.Id, failIfNotExists: false);

            if (attachments.Count > 0)
            {
                // Components V2: медиа лежит внутри цветного контейнера. В таком сообщении
                // запрещены content и embeds, поэтому весь текст уходит в компоненты
                await message.Channel.SendFilesAsync(
                    attachments,
                    components: BuildContainer(post, postUrl, fileNames, oversized),
                    flags: MessageFlags.ComponentsV2,
                    allowedMentions: AllowedMentions.None,
                    messageReference: reference);
            }
            else
            {
                // Скачать было нечего — остаётся обычный embed с превью и ссылкой
                await message.Channel.SendMessageAsync(
                    embed: BuildEmbed(post, postUrl, thumbnailUrl, oversized),
                    allowedMentions: AllowedMentions.None,
                    messageReference: reference);
            }

            BotLogger.Information("Telegram: пост t.me/{Channel}/{Post} — отправлено файлов {Count}", channel, postId, attachments.Count);
            return true;
        }
        finally
        {
            foreach (var attachment in attachments)
            {
                attachment.Dispose();
            }
        }
    }

    /// <summary>
    /// Собирает контейнер Components V2: цветная полоса, заголовок с подписью, галерея медиа.
    /// В отличие от embed'а сюда можно положить видео — оно проигрывается внутри контейнера.
    /// </summary>
    private static MessageComponent BuildContainer(
        TelegramPostClient.TelegramPost post, string postUrl, IList<string> fileNames, IList<string> oversized)
    {
        var container = new ContainerBuilder().WithAccentColor(TelegramBlue);
        var header = BuildHeaderText(post, postUrl);

        if (header != null)
        {
            container.AddComponent(new TextDisplayBuilder().WithContent(header));
        }

        var gallery = new MediaGalleryBuilder();

        foreach (var fileName in fileNames)
        {
            gallery.AddItem(new MediaGalleryItemProperties(new UnfurledMediaItemProperties($"attachment://{fileName}")));
        }

        container.AddComponent(gallery);

        foreach (var size in oversized)
        {
            container.AddComponent(new TextDisplayBuilder().WithContent(BotMessages.TelegramTooBig(size, postUrl)));
        }

        // «-# » — мелкий текст Discord, заменяет футер embed'а
        container.AddComponent(new TextDisplayBuilder().WithContent($"-# {BotMessages.TelegramFooter()}"));

        return new ComponentBuilderV2().AddComponent(container).Build();
    }

    /// <summary>
    /// Заголовок контейнера: имя канала ссылкой и подпись поста цитатой.
    /// </summary>
    private static string? BuildHeaderText(TelegramPostClient.TelegramPost post, string postUrl)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(post.ChannelName))
        {
            // Скобки в имени канала разорвали бы markdown-ссылку
            var name = MarkdownLinkCharsRegex().Replace(post.ChannelName, string.Empty);
            lines.Add($"### [{name}]({postUrl})");
        }

        if (post.Caption != null)
        {
            lines.Add(PrepareCaption(post.Caption));
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private static Embed BuildEmbed(TelegramPostClient.TelegramPost post, string postUrl, string? imageUrl, IList<string> oversized)
    {
        var embed = new EmbedBuilder()
            .WithColor(TelegramBlue)
            .WithUrl(postUrl)
            .WithFooter(BotMessages.TelegramFooter());

        if (!string.IsNullOrWhiteSpace(post.ChannelName))
        {
            embed.WithAuthor(post.ChannelName, url: postUrl);
        }

        if (imageUrl != null)
        {
            embed.WithImageUrl(imageUrl);
        }

        var description = new StringBuilder();

        if (post.Caption != null)
        {
            description.Append(PrepareCaption(post.Caption));
        }

        foreach (var size in oversized)
        {
            if (description.Length > 0)
            {
                description.Append("\n\n");
            }

            description.Append(BotMessages.TelegramTooBig(size, postUrl));
        }

        if (description.Length > 0)
        {
            embed.WithDescription(description.ToString());
        }

        return embed.Build();
    }

    /// <summary>
    /// Убирает стандартное превью Discord из исходного сообщения: его заменил наш embed.
    /// Само превью не удаляется, а помечается флагом SuppressEmbeds — единственное, что
    /// Discord позволяет боту сделать с чужим сообщением, и только с правом «Управление сообщениями».
    /// </summary>
    private static async Task SuppressSourceEmbedsAsync(SocketUserMessage message)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        if (guild != null && message.Channel is IGuildChannel guildChannel &&
            !guild.CurrentUser.GetPermissions(guildChannel).ManageMessages)
        {
            BotLogger.Warning("Нет права управлять сообщениями в #{Channel} — превью Telegram осталось", message.Channel.Name);
            return;
        }

        try
        {
            var flags = (message.Flags ?? MessageFlags.None) | MessageFlags.SuppressEmbeds;
            await message.ModifyAsync(properties => properties.Flags = flags);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось убрать превью в сообщении {MessageId}: {Message}", message.Id, ex.Message);
        }
    }

    /// <summary>
    /// Готовит подпись поста к вставке: обрезает по лимиту компонента.
    /// </summary>
    internal static string PrepareCaption(string text)
    {
        if (text.Length > MaxCaptionLength)
        {
            return string.Concat(text.AsSpan(0, MaxCaptionLength), "…");
        }

        return text;
    }

    /// <summary>
    /// Максимальный размер вложения зависит от уровня буста сервера.
    /// </summary>
    private static ulong GetUploadLimit(SocketUserMessage message)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;
        return guild?.MaxUploadLimit ?? FallbackUploadLimit;
    }

    /// <summary>
    /// Собирает безопасное имя файла: Telegram часто отдаёт путь без расширения.
    /// </summary>
    internal static string BuildFileName(TelegramPostClient.TelegramMedia media)
    {
        var name = string.Empty;

        if (Uri.TryCreate(media.Url, UriKind.Absolute, out var uri))
        {
            name = UnsafeFileCharsRegex().Replace(Path.GetFileName(uri.AbsolutePath), string.Empty);
        }

        if (name.Length > MaxFileNameLength)
        {
            name = name[^MaxFileNameLength..];
        }

        if (name.Length == 0 || !name.Contains('.'))
        {
            name = media.IsVideo ? "telegram.mp4" : "telegram.jpg";
        }

        return name;
    }

    private static string FormatSize(long bytes) =>
        $"{bytes / 1024d / 1024d:F1} МБ";

    [GeneratedRegex(@"https?://(?:t\.me|telegram\.me)/(?:s/)?([A-Za-z][A-Za-z0-9_]{3,31})/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PostLinkRegex();

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeFileCharsRegex();

    [GeneratedRegex(@"[\[\]()]")]
    private static partial Regex MarkdownLinkCharsRegex();
}
