using System.Globalization;
using System.Text.Json;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Сведения о видео YouTube для довеска к родному превью Discord: название, голоса,
/// просмотры и дата публикации. Ключей не требует ни один источник, и каждый отвечает за своё.
/// Название и дату отдаёт страница просмотра — из разметки schema.org, которую YouTube
/// держит для поисковиков. Голоса и просмотры — Return YouTube Dislike: дизлайков YouTube
/// наружу не показывает, а RYD их оценивает и заодно отдаёт лайки с просмотрами.
/// oEmbed — запасной источник названия на случай, когда страница не разобралась.
/// </summary>
public static class YoutubeVideoClient
{
    private const string VotesUrlFormat = "https://returnyoutubedislikeapi.com/votes?videoId={0}";

    private const string OEmbedUrlFormat = "https://www.youtube.com/oembed?format=json&url={0}";

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
    /// </summary>
    public record VideoInfo(string Id, string Title, Votes? Votes, DateTimeOffset? PublishedAt);

    /// <summary>
    /// Голоса и просмотры приходят одним ответом, поэтому и живут вместе: либо есть все, либо ничего.
    /// </summary>
    public record Votes(long Likes, long Dislikes, long Views);

    internal record PageInfo(string? Title, DateTimeOffset? PublishedAt);

    /// <summary>
    /// Собирает сведения о видео. null — видео не нашлось: его не назвали ни страница,
    /// ни oEmbed. RYD доказательством не служит — на выдуманный идентификатор он отвечает
    /// нулями, а не ошибкой, и превью с нулями вышло бы у удалённого видео.
    /// </summary>
    public static async Task<VideoInfo?> TryGetAsync(string videoId)
    {
        if (!YoutubeLinks.IsValidVideoId(videoId))
        {
            return null;
        }

        var pageTask = TryGetPageAsync(videoId);
        var votesTask = TryGetVotesAsync(videoId);
        await Task.WhenAll(pageTask, votesTask);

        var page = await pageTask;
        var title = page?.Title ?? await TryGetOEmbedTitleAsync(videoId);

        return title == null ? null : new VideoInfo(videoId, title, await votesTask, page?.PublishedAt);
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
    /// а дизлайки — оценка. Нет хотя бы одного числа — не показываем ни одного: строка
    /// из разнородных источников врала бы о соотношении.
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
    /// Дата в разметке — ISO-время со смещением («2009-10-24T23:57:33-07:00»).
    /// Не разобралась — обойдёмся без даты, в подписи её просто не будет.
    /// </summary>
    private static DateTimeOffset? ParseDate(string? text) =>
        text != null && DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
