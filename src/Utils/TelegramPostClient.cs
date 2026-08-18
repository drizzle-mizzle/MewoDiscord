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
    private const int RequestTimeoutSeconds = 20;

    /// <summary>
    /// Телеграм отдаёт виджет только «браузерным» клиентам.
    /// </summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Медиафайл поста.
    /// </summary>
    public record TelegramMedia(string Url, bool IsVideo, string? ThumbnailUrl);

    /// <summary>
    /// Разобранный пост Telegram.
    /// </summary>
    public record TelegramPost(string? ChannelName, string? Caption, IReadOnlyList<TelegramMedia> Media);

    /// <summary>
    /// Скачанный медиафайл. Content == null означает, что файл не влез в лимит Discord.
    /// </summary>
    public record MediaDownload(MemoryStream? Content, long SizeBytes);

    /// <summary>
    /// Ссылка на сам пост (без embed) — для кнопки «открыть в Telegram».
    /// </summary>
    public static string BuildPostUrl(string channel, string postId) =>
        string.Format(PostUrlFormat, channel, postId);

    /// <summary>
    /// Загружает и разбирает пост. Возвращает null, если пост недоступен.
    /// </summary>
    public static async Task<TelegramPost?> TryGetPostAsync(string channel, string postId)
    {
        var url = string.Format(EmbedUrlFormat, channel, postId);

        try
        {
            var html = await Http.GetStringAsync(url);
            return ParsePost(html);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось получить пост Telegram {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Скачивает медиа, если оно укладывается в maxBytes.
    /// Возвращает null при ошибке сети; при превышении лимита — размер без содержимого.
    /// </summary>
    public static async Task<MediaDownload?> TryDownloadAsync(string url, ulong maxBytes)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var declaredSize = response.Content.Headers.ContentLength;

            if (declaredSize > (long)maxBytes)
            {
                return new MediaDownload(null, declaredSize.Value);
            }

            await using var source = await response.Content.ReadAsStreamAsync();
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;

            while (true)
            {
                var read = await source.ReadAsync(chunk);

                if (read == 0)
                {
                    break;
                }

                total += read;

                // Длина могла не прийти в заголовках — обрываем по факту
                if (total > (long)maxBytes)
                {
                    await buffer.DisposeAsync();
                    return new MediaDownload(null, total);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read));
            }

            buffer.Position = 0;
            return new MediaDownload(buffer, total);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось скачать медиа Telegram {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Разбирает HTML виджета. Внутренний метод — на нём держатся тесты парсинга.
    /// </summary>
    internal static TelegramPost? ParsePost(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var media = new List<TelegramMedia>();
        var thumbnail = FirstGroup(VideoThumbRegex(), html);

        foreach (Match match in VideoRegex().Matches(html))
        {
            media.Add(new TelegramMedia(WebUtility.HtmlDecode(match.Groups[1].Value), IsVideo: true, thumbnail));
        }

        foreach (Match match in PhotoRegex().Matches(html))
        {
            media.Add(new TelegramMedia(WebUtility.HtmlDecode(match.Groups[1].Value), IsVideo: false, ThumbnailUrl: null));
        }

        // Видео без прямой ссылки (длинное или защищённое) — остаётся только превью
        if (media.Count == 0 && thumbnail != null)
        {
            media.Add(new TelegramMedia(thumbnail, IsVideo: false, thumbnail));
        }

        var channelName = FirstGroup(OwnerNameRegex(), html);
        var caption = ExtractCaption(html);

        if (media.Count == 0 && caption == null)
        {
            return null;
        }

        return new TelegramPost(channelName, caption, media);
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

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler();
        var proxy = AppConfig.TelegramProxy;

        // Telegram заблокирован у части провайдеров — тогда нужен прокси из конфига
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try
            {
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
            }
            catch (Exception ex)
            {
                // Опечатка в адресе не должна ронять всю фичу — идём напрямую
                BotLogger.Error("Некорректный TelegramProxy «{Proxy}»: {Message}", proxy, ex.Message);
            }
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    #region Регулярки разбора виджета

    [GeneratedRegex(@"<video[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase)]
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
