using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Чтение публичных постов Telegram через виджет-страницу «t.me/канал/пост?embed=1».
/// Bot API отдаёт только каналы, куда добавлен бот, поэтому разбираем HTML виджета
/// селекторами: разметку Telegram меняет без предупреждения, а регулярка вдобавок
/// зависела от порядка атрибутов в теге и ломалась молча.
/// Работает лишь с публичными каналами: приватные ссылки вида t.me/c/... не поддерживаются.
/// </summary>
public static partial class TelegramPostClient
{
    private const string EmbedUrlFormat = "https://t.me/{0}/{1}?embed=1";

    /// <summary>
    /// Разборщик HTML. Состояния не держит, поэтому одного хватает на всех.
    /// </summary>
    private static readonly HtmlParser _parser = new();
    private const string PostUrlFormat = "https://t.me/{0}/{1}";

    /// <summary>
    /// Ссылка на сам пост (без embed) — для кнопки «открыть в Telegram».
    /// </summary>
    public static string BuildPostUrl(string channel, string postId) =>
        string.Format(PostUrlFormat, channel, postId);

    /// <summary>
    /// Загружает и разбирает пост. Возвращает null, если пост недоступен.
    /// </summary>
    public static async Task<SocialPost?> TryGetPostAsync(string channel, string postId)
    {
        var url = string.Format(EmbedUrlFormat, channel, postId);

        try
        {
            var html = await SocialMediaHttp.Http.GetStringAsync(url);
            var post = ParsePost(html);

            // Страница пришла, а разобрать не вышло — скорее всего Telegram сменил
            // вёрстку виджета. Без записи в лог это неотличимо от «не увидел ссылку»
            if (post == null && !string.IsNullOrWhiteSpace(html))
            {
                BotLogger.Warning("Пост Telegram {Url} не разобрался: вёрстка виджета изменилась?", url);
            }

            return post;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось получить пост Telegram {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Разбирает HTML виджета.
    /// </summary>
    internal static SocialPost? ParsePost(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var document = _parser.ParseDocument(html);

        var media = new List<SocialMedia>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var thumbnail = BackgroundUrl(document.QuerySelector(".tgme_widget_message_video_thumb"));

        // Видео, не заполняющее плеер (вертикальное), Telegram кладёт на размытую подложку —
        // копию того же файла с классом js-message_video_blured. Это оформление, а не второй
        // файл: без отсечки такое видео уезжало в Discord дважды
        foreach (var video in document.QuerySelectorAll("video[src]:not(.js-message_video_blured)"))
        {
            var source = video.GetAttribute("src");

            if (!string.IsNullOrWhiteSpace(source))
            {
                AddMedia(media, seen, new SocialMedia(source, IsVideo: true, thumbnail));
            }
        }

        foreach (var photo in document.QuerySelectorAll(".tgme_widget_message_photo_wrap"))
        {
            var source = BackgroundUrl(photo);

            if (source != null)
            {
                AddMedia(media, seen, new SocialMedia(source, IsVideo: false, ThumbnailUrl: null));
            }
        }

        // Видео без прямой ссылки (длинное или защищённое) — остаётся только превью
        if (media.Count == 0 && thumbnail != null)
        {
            media.Add(new SocialMedia(thumbnail, IsVideo: false, thumbnail));
        }

        var owner = document.QuerySelector("a.tgme_widget_message_owner_name");
        var channelName = Trimmed(owner?.TextContent);
        var caption = ExtractCaption(document);

        if (media.Count == 0 && caption == null)
        {
            return null;
        }

        var channel = ExtractLogin(owner?.GetAttribute("href"));

        return new SocialPost(
            channelName,
            channel == null ? null : $"@{channel}",
            caption,
            media,
            ReadDate(document));
    }

    /// <summary>
    /// Время публикации; не нашли или не разобрали — в подписи её просто не будет.
    /// </summary>
    private static DateTimeOffset? ReadDate(IDocument document)
    {
        var raw = document.QuerySelector(".tgme_widget_message_date time")?.GetAttribute("datetime");

        return raw != null && DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// Адрес из инлайнового background-image: виджет пишет его как url('…').
    /// Отдельного атрибута с адресом у фотографий и превью нет.
    /// </summary>
    private static string? BackgroundUrl(IElement? element)
    {
        var style = element?.GetAttribute("style");
        var start = style?.IndexOf("url('", StringComparison.OrdinalIgnoreCase) ?? -1;

        if (start < 0)
        {
            return null;
        }

        start += 5;
        var end = style!.IndexOf('\'', start);

        return end > start ? style[start..end] : null;
    }

    /// <summary>
    /// Логин канала из ссылки на него же: в имени он не написан, а подписью нашей ссылки
    /// идёт именно логин.
    /// </summary>
    private static string? ExtractLogin(string? href)
    {
        const string prefix = "https://t.me/";

        if (href == null || !href.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = href[prefix.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var login = end < 0 ? rest : rest[..end];

        return login.Length > 0 && login.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? login : null;
    }

    /// <summary>
    /// Кладёт файл в список, пропуская повтор по адресу: один и тот же файл дважды —
    /// это дубль вёрстки. Страховка на случай переименования класса размытой подложки.
    /// </summary>
    private static void AddMedia(List<SocialMedia> media, HashSet<string> seen, SocialMedia item)
    {
        if (seen.Add(item.Url))
        {
            media.Add(item);
        }
    }

    /// <summary>
    /// Достаёт текст поста, разворачивая переносы строк и HTML-сущности. Разметку внутри
    /// берём сырой: br превращается в перенос, остальные теги выбрасываются.
    /// </summary>
    private static string? ExtractCaption(IDocument document)
    {
        var raw = document.QuerySelector(".tgme_widget_message_text.js-message_text")?.InnerHtml;

        if (raw == null)
        {
            return null;
        }

        var text = LineBreakRegex().Replace(raw, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Trim();

        return text.Length > 0 ? text : null;
    }

    private static string? Trimmed(string? value)
    {
        var text = value?.Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
}
