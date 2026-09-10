using System.Globalization;
using System.Text.Json;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Сведения о видео YouTube для довеска к родному превью Discord: название, голоса,
/// просмотры и дата публикации.
/// Главный источник — YouTube Data API (ключ <see cref="AppConfig.YoutubeApiKey"/>):
/// официальный, без проверки на робота, с точными лайками, просмотрами и датой.
/// Без ключа — страница просмотра, из разметки schema.org. Она работает с домашнего IP,
/// а IP дата-центра YouTube отдаёт урезанную страницу без названия и даты.
/// Дизлайки — всегда Return YouTube Dislike: YouTube их наружу не показывает, а RYD
/// оценивает. Он же — запасной источник лайков и просмотров.
/// oEmbed — запасной источник названия, когда страница не разобралась.
/// </summary>
public static class YoutubeVideoClient
{
    private const string VotesUrlFormat = "https://returnyoutubedislikeapi.com/votes?videoId={0}";

    private const string OEmbedUrlFormat = "https://www.youtube.com/oembed?format=json&url={0}";

    private const string ApiUrlFormat = "https://www.googleapis.com/youtube/v3/videos?part=snippet,statistics&id={0}";

    /// <summary>
    /// Ключ API уходит заголовком, а не параметром адреса: адреса попадают в логи и исключения.
    /// </summary>
    private const string ApiKeyHeader = "X-Goog-Api-Key";

    /// <summary>
    /// Отказ от персонализации: без него из европейского дата-центра вместо страницы видео
    /// приезжает страница согласия с куки.
    /// </summary>
    private const string ConsentCookie = "SOCS=CAI";

    /// <summary>
    /// Язык задаём явно: иначе YouTube выберет его по стране сервера и может отдать
    /// перевод названия от автора — не то название, что в превью Discord.
    /// </summary>
    private const string PageLanguage = "en-US,en;q=0.9";

    /// <summary>
    /// Разборщик HTML. Состояния не держит, поэтому одного хватает на всех.
    /// </summary>
    private static readonly HtmlParser _parser = new();

    /// <summary>
    /// Что известно о видео. Название есть всегда: без него видео не считается найденным.
    /// Числа из разных источников, и любое может не прийти.
    /// </summary>
    public record VideoInfo(
        string Id, string Title, long? Likes, long? Dislikes, long? Views, DateTimeOffset? PublishedAt);

    /// <summary>
    /// Ответ RYD: голоса и просмотры одним ответом, поэтому либо есть все, либо ничего.
    /// </summary>
    public record Votes(long Likes, long Dislikes, long Views);

    internal record PageInfo(string? Title, DateTimeOffset? PublishedAt);

    /// <summary>
    /// Ответ Data API. Лайков нет, когда автор их скрыл.
    /// </summary>
    internal record ApiVideo(string Title, DateTimeOffset? PublishedAt, long? Views, long? Likes);

    /// <summary>
    /// Собирает сведения о видео. null — видео не нашлось: его не назвали ни Data API,
    /// ни страница, ни oEmbed. RYD доказательством не служит — на выдуманный идентификатор
    /// он отвечает нулями, а не ошибкой, и превью с нулями вышло бы у удалённого видео.
    /// </summary>
    public static async Task<VideoInfo?> TryGetAsync(string videoId)
    {
        if (!YoutubeLinks.IsValidVideoId(videoId))
        {
            return null;
        }

        var votesTask = TryGetVotesAsync(videoId);
        var apiKey = AppConfig.YoutubeApiKey;

        if (apiKey.Length > 0)
        {
            var (answered, video) = await TryGetFromApiAsync(videoId, apiKey);

            // Data API ответил — ему и верим, в том числе в том, что видео нет
            if (answered)
            {
                var votes = await votesTask;

                return video == null
                    ? null
                    : new VideoInfo(
                        videoId,
                        video.Title,
                        video.Likes ?? votes?.Likes,
                        votes?.Dislikes,
                        video.Views ?? votes?.Views,
                        video.PublishedAt);
            }
        }

        var page = await TryGetPageAsync(videoId);
        var title = page?.Title ?? await TryGetOEmbedTitleAsync(videoId);
        var fallbackVotes = await votesTask;

        return title == null
            ? null
            : new VideoInfo(
                videoId,
                title,
                fallbackVotes?.Likes,
                fallbackVotes?.Dislikes,
                fallbackVotes?.Views,
                page?.PublishedAt);
    }

    /// <summary>
    /// Спрашивает Data API. answered == false — API недоступен (сеть, квота, неверный ключ),
    /// и тогда идём прежним путём. answered == true и video == null — видео нет.
    /// </summary>
    private static async Task<(bool Answered, ApiVideo? Video)> TryGetFromApiAsync(string videoId, string apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(ApiUrlFormat, videoId));
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);

            using var response = await SocialMediaHttp.Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                BotLogger.Warning(
                    "YouTube Data API не ответил для {Id}: {Status} {Reason}",
                    videoId,
                    (int)response.StatusCode,
                    ParseApiError(body) ?? string.Empty);

                return (false, null);
            }

            var video = ParseApiVideo(body);

            if (video == null)
            {
                BotLogger.Warning("YouTube Data API не знает видео {Id}", videoId);
            }

            return (true, video);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось спросить YouTube Data API о {Id}: {Message}", videoId, ex.Message);
            return (false, null);
        }
    }

    private static async Task<PageInfo?> TryGetPageAsync(string videoId)
    {
        try
        {
            var (html, finalUrl) = await GetFromYoutubeAsync(YoutubeLinks.WatchUrl(videoId));
            var page = ParsePage(html, videoId);

            // Пропавшая дата без записи в лог неотличима от «так и задумано». Главные улики —
            // куда привели переадресации и заголовок пришедшей страницы: вместо страницы
            // видео YouTube бывает отдаёт согласие с куки или проверку на робота
            if (page?.PublishedAt == null)
            {
                BotLogger.Warning(
                    "Страница видео YouTube {Id} пришла без даты: {Url}, {Length} символов, заголовок «{Title}»",
                    videoId,
                    finalUrl?.ToString() ?? "?",
                    html.Length,
                    _parser.ParseDocument(html).Title ?? string.Empty);
            }

            return page;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось получить страницу видео YouTube {Id}: {Message}", videoId, ex.Message);
            return null;
        }
    }

    private static async Task<Votes?> TryGetVotesAsync(string videoId)
    {
        try
        {
            var votes = ParseVotes(await SocialMediaHttp.Http.GetStringAsync(string.Format(VotesUrlFormat, videoId)));

            if (votes == null)
            {
                BotLogger.Warning("Ответ Return YouTube Dislike для {Id} не разобрался", videoId);
            }

            return votes;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Return YouTube Dislike не ответил для {Id}: {Message}", videoId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Запасной источник названия: зовём, только когда страница его не дала.
    /// Удалённое, приватное и закрытое для встраивания видео oEmbed отдаёт ошибкой.
    /// </summary>
    private static async Task<string?> TryGetOEmbedTitleAsync(string videoId)
    {
        var url = string.Format(OEmbedUrlFormat, Uri.EscapeDataString(YoutubeLinks.WatchUrl(videoId)));

        try
        {
            return ParseOEmbedTitle((await GetFromYoutubeAsync(url)).Body);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("oEmbed не отдал видео YouTube {Id}: {Message}", videoId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Запрос к самому YouTube. Вместе с телом отдаёт адрес, куда в итоге привели
    /// переадресации: по нему видно, что вместо страницы видео подсунули согласие с куки.
    /// </summary>
    private static async Task<(string Body, Uri? FinalUrl)> GetFromYoutubeAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
        request.Headers.TryAddWithoutValidation("Accept-Language", PageLanguage);

        using var response = await SocialMediaHttp.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadAsStringAsync(), response.RequestMessage?.RequestUri);
    }

    /// <summary>
    /// Разбирает ответ videos.list. null — видео нет: удалено, приватное или идентификатор
    /// выдуман. Числа Data API отдаёт строками — их разбирает <see cref="JsonRead.Number"/>.
    /// </summary>
    internal static ApiVideo? ParseApiVideo(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                return null;
            }

            var item = items[0];

            if (!item.TryGetProperty("snippet", out var snippet))
            {
                return null;
            }

            var title = JsonRead.Text(snippet, "title")?.Trim();

            if (string.IsNullOrEmpty(title))
            {
                return null;
            }

            long? views = null;
            long? likes = null;

            if (item.TryGetProperty("statistics", out var statistics))
            {
                views = Count(statistics, "viewCount");
                likes = Count(statistics, "likeCount");
            }

            return new ApiVideo(title, ParseDate(JsonRead.Text(snippet, "publishedAt")), views, likes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Причина отказа Data API из тела ошибки: неверный ключ, кончилась квота, API не включён
    /// в проекте. Ключа в тексте нет — его можно писать в лог.
    /// </summary>
    internal static string? ParseApiError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
                ? JsonRead.Text(error, "message")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Разбирает страницу просмотра. null — это страница не того видео или не страница видео
    /// вовсе: вместо неё YouTube бывает подсовывает согласие с куки или проверку на робота,
    /// и тогда их заголовок сошёл бы за название. Своё видео узнаём по каноничной ссылке
    /// или по идентификатору в разметке schema.org.
    /// </summary>
    internal static PageInfo? ParsePage(string html, string videoId)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var document = _parser.ParseDocument(html);

        if (!IsPageOf(document, videoId))
        {
            return null;
        }

        var title = Content(document, "meta[name='title']") ?? Content(document, "meta[property='og:title']");
        var published = ParseDate(
            Content(document, "meta[itemprop='datePublished']") ?? Content(document, "meta[itemprop='uploadDate']"));

        return title == null && published == null ? null : new PageInfo(title, published);
    }

    /// <summary>
    /// Разбирает ответ RYD. Лайки и просмотры в нём настоящие, с YouTube, только с запаздыванием,
    /// а дизлайки — оценка.
    /// </summary>
    internal static Votes? ParseVotes(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var likes = JsonRead.Number(root, "likes", -1);
            var dislikes = JsonRead.Number(root, "dislikes", -1);
            var views = JsonRead.Number(root, "viewCount", -1);

            return likes < 0 || dislikes < 0 || views < 0
                ? null
                : new Votes((long)likes, (long)dislikes, (long)views);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ParseOEmbedTitle(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = root.ValueKind == JsonValueKind.Object ? JsonRead.Text(root, "title")?.Trim() : null;

            return string.IsNullOrEmpty(title) ? null : title;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? Count(JsonElement element, string name)
    {
        var value = JsonRead.Number(element, name, -1);

        return value < 0 ? null : (long)value;
    }

    private static bool IsPageOf(IDocument document, string videoId)
    {
        var canonical = document.QuerySelector("link[rel='canonical']")?.GetAttribute("href");

        return Content(document, "meta[itemprop='identifier']") == videoId
            || canonical?.Contains(videoId, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Значение атрибута content; пустое считается отсутствующим.
    /// </summary>
    private static string? Content(IDocument document, string selector)
    {
        var value = document.QuerySelector(selector)?.GetAttribute("content")?.Trim();

        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Дата — ISO-время: со смещением у страницы («2009-10-24T23:57:33-07:00»), в UTC у Data API.
    /// Не разобралась — обойдёмся без даты, в подписи её просто не будет.
    /// </summary>
    private static DateTimeOffset? ParseDate(string? text) =>
        text != null && DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
