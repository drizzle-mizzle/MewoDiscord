using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

/// <summary>
/// Общий ответ на ссылку из соцсети: качает медиа поста и отвечает на исходное сообщение
/// контейнером Components V2. У источников своё только три вещи — как найти ссылки в тексте,
/// как достать по ним пост и чем подписаться; всё остальное живёт здесь.
/// Контейнер выбран вместо embed'а потому, что видео внутри embed'а Discord ботам не отдаёт
/// (поле video — только для входящих). Такое сообщение не может нести content и embeds,
/// поэтому весь текст уходит в компоненты.
/// </summary>
public static partial class PostMediaHandler
{
    /// <summary>
    /// Сколько ссылок из одного сообщения обрабатываем: дальше это уже не пересказ поста,
    /// а флуд вложениями.
    /// </summary>
    internal const int MaxLinksPerMessage = 3;

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
    /// Сколько постов обрабатываем одновременно. Работа фоновая и незаказанная —
    /// пусть ждёт, а не соревнуется за память и канал с остальным ботом.
    /// </summary>
    private static readonly SemaphoreSlim _slots = new(2, 2);

    /// <summary>
    /// Оформление ответа: у каждого источника своя акцентная полоса, подпись, иконка
    /// и текст про невлезший файл.
    /// </summary>
    public record PostStyle(
        Color Accent, string Slug, Func<string> Footer, Func<Emote?> Icon, Func<string, string, string> TooBig);

    /// <summary>
    /// Ссылка на пост и способ его достать. Потолок вложения приходит в FetchAsync, потому что
    /// у источника с лесенкой качеств от него зависит, какой файл вообще брать.
    /// </summary>
    public record PostRequest(string Url, Func<ulong, Task<SocialPost?>> FetchAsync);

    /// <summary>
    /// Запускает обработку в фоне: скачивание медиа не должно задерживать остальную
    /// обработку сообщений канала, которая идёт под общим замком.
    /// </summary>
    public static void HandleInBackground(SocketUserMessage message, IReadOnlyList<PostRequest> requests, PostStyle style)
    {
        if (requests.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // Обработка идёт на любое сообщение со ссылкой, без всякой просьбы, и каждая
            // держит в памяти скачанные файлы: без общего потолка десяток ссылок подряд
            // множит эти буферы линейно. Очередь тут уместна — ждать своей очереди
            // секунды никому не мешает
            await _slots.WaitAsync();

            try
            {
                var replied = false;

                foreach (var request in requests)
                {
                    try
                    {
                        replied |= await HandleRequestAsync(message, request, style);
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Error("Ошибка обработки ссылки {Url}: {Message}", request.Url, ex.Message);
                    }
                }

                // Своё оформление показали — стандартное превью Discord больше не нужно
                if (replied)
                {
                    await SuppressSourceEmbedsAsync(message);
                }
            }
            finally
            {
                _slots.Release();
            }
        });
    }

    /// <summary>
    /// Обрабатывает одну ссылку. Возвращает true, если ответ с медиа отправлен.
    /// </summary>
    private static async Task<bool> HandleRequestAsync(SocketUserMessage message, PostRequest request, PostStyle style)
    {
        var uploadLimit = DiscordLimits.UploadLimit(message, FallbackUploadLimit);
        var post = await request.FetchAsync(uploadLimit);

        if (post == null || post.Media.Count == 0)
        {
            return false;
        }

        var attachments = new List<FileAttachment>();
        var fileNames = new List<string>();
        var oversized = new List<string>();
        string? thumbnailUrl = null;

        // Лимит Discord считается на сообщение целиком, а не на файл: у поста из четырёх
        // картинок проверка каждой по отдельности пропустила бы то, что вместе не влезает
        var budget = uploadLimit;

        try
        {
            foreach (var media in post.Media.Take(MaxAttachments))
            {
                var download = await SocialMediaHttp.TryDownloadAsync(media.Url, budget);

                if (download == null)
                {
                    continue;
                }

                // Не влезло в лимит Discord — покажем превью и ссылку вместо файла
                if (download.Content == null)
                {
                    oversized.Add(DiscordLimits.FormatSize(download.SizeBytes));
                    thumbnailUrl ??= media.ThumbnailUrl;
                    continue;
                }

                var fileName = BuildFileName(media, style.Slug);
                attachments.Add(new FileAttachment(download.Content, fileName));
                fileNames.Add(fileName);
                budget -= (ulong)download.SizeBytes;
            }

            if (attachments.Count == 0 && oversized.Count == 0)
            {
                return false;
            }

            var reference = new MessageReference(message.Id, failIfNotExists: false);

            if (attachments.Count > 0)
            {
                // Components V2: медиа лежит внутри цветного контейнера
                await message.Channel.SendFilesAsync(
                    attachments,
                    components: BuildContainer(post, request.Url, fileNames, oversized, style),
                    flags: MessageFlags.ComponentsV2,
                    allowedMentions: AllowedMentions.None,
                    messageReference: reference);
            }
            else
            {
                // Скачать было нечего — остаётся обычный embed с превью и ссылкой
                await message.Channel.SendMessageAsync(
                    embed: BuildEmbed(post, request.Url, thumbnailUrl, oversized, style),
                    allowedMentions: AllowedMentions.None,
                    messageReference: reference);
            }

            BotLogger.Information("Пост {Url} — отправлено файлов {Count}", request.Url, attachments.Count);
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
        SocialPost post, string postUrl, IList<string> fileNames, IList<string> oversized, PostStyle style)
    {
        var container = new ContainerBuilder().WithAccentColor(style.Accent);
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
            container.AddComponent(new TextDisplayBuilder().WithContent(style.TooBig(size, postUrl)));
        }

        container.AddComponent(new TextDisplayBuilder()
            .WithContent(BuildFooterText(style.Footer(), style.Icon(), post.PublishedAt)));

        return new ComponentBuilderV2().AddComponent(container).Build();
    }

    /// <summary>
    /// Заголовок контейнера: ссылка на пост, показываемое имя автора под ней и текст поста.
    /// Подписью ссылки всегда идёт логин — он из латиницы и цифр, поэтому ссылка рисуется
    /// у любого автора. Имя живёт отдельной строкой: в нём бывают эмодзи, а с ними Discord
    /// ссылку не рисует вовсе. Логина нет (такое бывает только при неполном ответе) —
    /// подписью становится имя, вычищенное по тем же правилам.
    /// </summary>
    internal static string? BuildHeaderText(SocialPost post, string postUrl)
    {
        var lines = new List<string>();
        var label = BuildLinkLabel(post.AuthorHandle ?? post.AuthorName);

        if (label != null)
        {
            lines.Add($"### [{label}]({postUrl})");
        }

        var name = BuildDisplayName(post.AuthorName, label);

        if (name != null)
        {
            lines.Add($"**{name}**");
        }

        if (post.Caption != null)
        {
            lines.Add(PrepareCaption(post.Caption));
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    /// <summary>
    /// Показываемое имя автора — как есть, вместе с эмодзи: это обычный текст, а не подпись
    /// ссылки. Имя, которое ничем не отличается от логина, второй раз не показываем.
    /// </summary>
    private static string? BuildDisplayName(string? authorName, string? label)
    {
        var name = authorName?.Trim();

        if (string.IsNullOrEmpty(name) || name == label || $"@{name}" == label)
        {
            return null;
        }

        return name;
    }

    /// <summary>
    /// Подпись контейнера. «-# » — мелкий текст Discord, заменяющий футер embed'а; иконку
    /// сюда можно поставить только эмодзи, слота для картинки у компонента нет.
    /// Дата уходит меткой времени Discord, а не готовой строкой: так каждый читает её
    /// в своей зоне и на своём языке — ровно как в превью, которое мы погасили.
    /// </summary>
    internal static string BuildFooterText(string footer, Emote? icon, DateTimeOffset? publishedAt)
    {
        var text = icon == null ? footer : $"{icon} {footer}";

        if (publishedAt != null)
        {
            text += $" • <t:{publishedAt.Value.ToUnixTimeSeconds()}:f>";
        }

        return $"-# {text}";
    }

    private static Embed BuildEmbed(
        SocialPost post, string postUrl, string? imageUrl, IList<string> oversized, PostStyle style)
    {
        var embed = new EmbedBuilder()
            .WithColor(style.Accent)
            .WithUrl(postUrl)
            .WithFooter(style.Footer(), BotEmotes.IconUrl(style.Icon()));

        if (post.PublishedAt != null)
        {
            // У embed'а под дату свой слот — там она выглядит ровно как в погашенном превью
            embed.WithTimestamp(post.PublishedAt.Value);
        }

        var author = post.AuthorName ?? post.AuthorHandle;

        if (!string.IsNullOrWhiteSpace(author))
        {
            embed.WithAuthor(author, url: postUrl);
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

            description.Append(style.TooBig(size, postUrl));
        }

        if (description.Length > 0)
        {
            embed.WithDescription(description.ToString());
        }

        return embed.Build();
    }

    /// <summary>
    /// Убирает стандартное превью Discord из исходного сообщения: его заменил наш ответ.
    /// Само превью не удаляется, а помечается флагом SuppressEmbeds — единственное, что
    /// Discord позволяет боту сделать с чужим сообщением, и только с правом «Управление сообщениями».
    /// </summary>
    private static async Task SuppressSourceEmbedsAsync(SocketUserMessage message)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        if (guild != null && message.Channel is IGuildChannel guildChannel &&
            !guild.CurrentUser.GetPermissions(guildChannel).ManageMessages)
        {
            BotLogger.Warning("Нет права управлять сообщениями в #{Channel} — превью осталось", message.Channel.Name);
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
    /// Готовит имя автора к подписи скрытой ссылки. Эмодзи из подписи вычёркиваются:
    /// с ними Discord ссылку не отрисовывает вовсе — показывает сырую разметку
    /// со скобками и адресом. Квадратная скобка оборвала бы подпись по той же причине.
    /// Когда от имени остаётся только логин в скобках, скобки снимаются: они были при имени,
    /// а имени больше нет. Вложенные скобки при этом не трогаем — «(Аня) (@anya)»
    /// развернулось бы в мусор.
    /// Не осталось ничего (имя было из одних эмодзи, а логина нет) — ссылки не будет:
    /// подпись из пустоты Discord тоже покажет сырой разметкой.
    /// </summary>
    internal static string? BuildLinkLabel(string? authorName)
    {
        if (string.IsNullOrWhiteSpace(authorName))
        {
            return null;
        }

        var label = RemoveEmoji(authorName);
        label = MarkdownLinkCharsRegex().Replace(label, string.Empty);
        label = ExtraSpacesRegex().Replace(label, " ").Trim();

        if (label.StartsWith('(') && label.EndsWith(')') && label.IndexOf(')') == label.Length - 1)
        {
            label = label[1..^1].Trim();
        }

        return label.Length > 0 ? label : null;
    }

    /// <summary>
    /// Вычёркивает эмодзи. Считаем по кодовым точкам, а не регуляркой: почти все эмодзи
    /// лежат за пределами основной плоскости, а .NET сопоставляет регулярку по половинкам
    /// суррогатной пары, и категория «значок» у них не совпадает ни с чем.
    /// </summary>
    private static string RemoveEmoji(string text)
    {
        var result = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsEmoji(rune))
            {
                result.Append(rune);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Эмодзи — это сам значок (в том числе половинка флага: 🇬🇷 состоит из двух),
    /// рамка клавиши, а также склейка: селектор начертания FE0F, нулевой соединитель 200D
    /// и тон кожи. Тон кожи ловится диапазоном, а не категорией: в ней же лежат «^» и «`».
    /// </summary>
    private static bool IsEmoji(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.OtherSymbol or UnicodeCategory.EnclosingMark ||
               rune.Value is 0x200D or 0xFE0F ||
               rune.Value is >= 0x1F3FB and <= 0x1F3FF;
    }

    /// <summary>
    /// Готовит текст поста к вставке: обрезает по лимиту компонента.
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
    /// Собирает безопасное имя файла: соцсети часто отдают путь без расширения,
    /// а у X к нему ещё цепляется query с токеном. slug — как назвать файл, если из адреса
    /// имени не вышло: по нему в папке загрузок видно, откуда файл.
    /// </summary>
    internal static string BuildFileName(SocialMedia media, string slug)
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
            name = media.IsVideo ? $"{slug}.mp4" : $"{slug}.jpg";
        }

        return name;
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeFileCharsRegex();

    [GeneratedRegex(@"[\[\]]")]
    private static partial Regex MarkdownLinkCharsRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ExtraSpacesRegex();
}
