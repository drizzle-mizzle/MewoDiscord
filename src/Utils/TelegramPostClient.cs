using System.Net;
using System.Text.RegularExpressions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Чтение публичных постов Telegram через виджет-страницу «t.me/канал/пост?embed=1».
/// Bot API отдаёт только каналы, куда добавлен бот, поэтому разбираем HTML виджета.
/// Работает лишь с публичными каналами: приватные ссылки вида t.me/c/... не поддерживаются.
/// </summary>
public static partial class TelegramPostClient
{
    private const string EmbedUrlFormat = "https://t.me/{0}/{1}?embed=1";
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
            return ParsePost(html);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось получить пост Telegram {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Разбирает HTML виджета. Внутренний метод — на нём держатся тесты парсинга.
    /// </summary>
    internal static SocialPost? ParsePost(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var media = new List<SocialMedia>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var thumbnail = FirstGroup(VideoThumbRegex(), html);

        foreach (Match match in VideoRegex().Matches(html))
        {
            AddMedia(media, seen, new SocialMedia(WebUtility.HtmlDecode(match.Groups[1].Value), IsVideo: true, thumbnail));
        }

        foreach (Match match in PhotoRegex().Matches(html))
        {
            AddMedia(media, seen, new SocialMedia(WebUtility.HtmlDecode(match.Groups[1].Value), IsVideo: false, ThumbnailUrl: null));
        }

        // Видео без прямой ссылки (длинное или защищённое) — остаётся только превью
        if (media.Count == 0 && thumbnail != null)
        {
            media.Add(new SocialMedia(thumbnail, IsVideo: false, thumbnail));
        }

        var channelName = FirstGroup(OwnerNameRegex(), html);
        var caption = ExtractCaption(html);

        if (media.Count == 0 && caption == null)
        {
            return null;
        }

        return new SocialPost(channelName, caption, media);
    }

    /// <summary>
    /// Кладёт файл в список, пропуская повтор по адресу: один и тот же файл дважды
    /// в посте — это дубль вёрстки, а не второе вложение.
    /// </summary>
    private static void AddMedia(List<SocialMedia> media, HashSet<string> seen, SocialMedia item)
    {
        if (seen.Add(item.Url))
        {
            media.Add(item);
        }
    }

    /// <summary>
    /// Достаёт текст поста, разворачивая переносы строк и HTML-сущности.
    /// </summary>
    private static string? ExtractCaption(string html)
    {
        var raw = FirstGroup(CaptionRegex(), html);

        if (raw == null)
        {
            return null;
        }

        var text = LineBreakRegex().Replace(raw, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Trim();

        return text.Length > 0 ? text : null;
    }

    private static string? FirstGroup(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    #region Регулярки разбора виджета

    /// <summary>
    /// Видео, не заполняющее плеер (вертикальное), Telegram кладёт на размытую подложку —
    /// копию того же файла с классом js-message_video_blured. Это оформление, а не второй
    /// файл: без отсечки такое видео уезжало в Discord дважды.
    /// </summary>
    [GeneratedRegex(@"<video(?![^>]*js-message_video_blured)[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex VideoRegex();

    [GeneratedRegex(@"tgme_widget_message_photo_wrap[^""]*""[^>]*background-image:\s*url\('([^']+)'\)", RegexOptions.IgnoreCase)]
    private static partial Regex PhotoRegex();

    [GeneratedRegex(@"tgme_widget_message_video_thumb[^""]*""[^>]*background-image:\s*url\('([^']+)'\)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoThumbRegex();

    [GeneratedRegex(@"<div class=""tgme_widget_message_text[^""]*js-message_text[^""]*""[^>]*>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CaptionRegex();

    [GeneratedRegex(@"tgme_widget_message_owner_name""[^>]*>\s*(?:<span[^>]*>)?([^<]+)", RegexOptions.IgnoreCase)]
    private static partial Regex OwnerNameRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    #endregion
}
