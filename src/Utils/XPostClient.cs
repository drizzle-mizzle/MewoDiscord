using System.Net;
using System.Text.Json;

using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Чтение постов X через syndication-ручку «cdn.syndication.twimg.com/tweet-result» — ту самую,
/// которой X рисует встроенные твиты на чужих сайтах. Ключей и авторизации не требует и отдаёт
/// JSON: автора, текст и все медиа поста, а для видео — лесенку mp4-вариантов.
/// Удалённые и защищённые посты она не отдаёт (404), а закрытые для анонима — например,
/// у аккаунтов с пометкой «чувствительные медиа» — отдаёт «надгробием» вместо поста.
/// На такой отказ идём вторым путём, в читалку FxTwitter; не вышло и там — бот молчит,
/// и в чате остаётся обычное превью Discord.
/// </summary>
public static class XPostClient
{
    private const string SyndicationUrlFormat =
        "https://cdn.syndication.twimg.com/tweet-result?id={0}&token={1}&lang=en";

    /// <summary>
    /// Запасная читалка. Ключей тоже не требует, а посты, закрытые для анонима, отдаёт:
    /// syndication — витрина для встроенных твитов, и такое она показывать не станет
    /// никогда, сколько ни подбирай токен.
    /// </summary>
    private const string FxApiUrlFormat = "https://api.fxtwitter.com/status/{0}";

    private const string PostUrlFormat = "https://x.com/{0}/status/{1}";

    /// <summary>
    /// Медиа до выбора качества: у видео X держит лесенку mp4-вариантов, от лучшего к худшему.
    /// У фото и гифки в лесенке всегда один файл.
    /// </summary>
    internal record MediaLadder(bool IsVideo, string? ThumbnailUrl, IReadOnlyList<string> Variants);

    /// <summary>
    /// Пост до выбора качества.
    /// </summary>
    internal record LadderPost(string? AuthorName, string? Caption, IReadOnlyList<MediaLadder> Media);

    /// <summary>
    /// Ссылка на сам пост — для заголовка и сообщения о невлезшем файле.
    /// </summary>
    public static string BuildPostUrl(string author, string statusId) =>
        string.Format(PostUrlFormat, author, statusId);

    /// <summary>
    /// Загружает и разбирает пост. maxBytes — потолок вложения: по нему выбирается качество
    /// видео, потому что качать лучшее, чтобы затем выбросить, незачем.
    /// </summary>
    public static async Task<SocialPost?> TryGetPostAsync(string statusId, ulong maxBytes)
    {
        var ladder = await TryGetLadderAsync(statusId);

        return ladder == null ? null : await PickQualityAsync(ladder, maxBytes);
    }

    /// <summary>
    /// Достаёт пост: сперва штатной ручкой, а не вышло — читалкой. Причина отказа роли
    /// не играет, поэтому и не разбирается: «надгробие» приезжает обычным ответом 200,
    /// то есть неотличимо от мусора уже на разборе, а сеть и 404 ловятся тем же catch.
    /// Каждый отказ пишется в лог: молчание бота без единой строчки со стороны неотличимо
    /// от «не увидел ссылку», и причину искали бы в регулярке, а не в X.
    /// </summary>
    private static async Task<LadderPost?> TryGetLadderAsync(string statusId)
    {
        var url = string.Format(SyndicationUrlFormat, statusId, BuildToken(statusId));

        try
        {
            var json = await SocialMediaHttp.Http.GetStringAsync(url);
            var post = ParsePost(json);

            if (post != null)
            {
                return post;
            }

            BotLogger.Warning("Syndication не отдала пост X {Id} — идём в читалку", statusId);
        }
        catch (Exception ex)
        {
            BotLogger.Warning(
                "Не удалось получить пост X {Id}: {Message} — идём в читалку", statusId, ex.Message);
        }

        return await TryGetFxPostAsync(statusId);
    }

    /// <summary>
    /// Второй заход — к читалке. Логин в пути ей не нужен, и это кстати: у нас он бывает
    /// служебным (i/web), то есть ведёт в никуда.
    /// </summary>
    private static async Task<LadderPost?> TryGetFxPostAsync(string statusId)
    {
        try
        {
            var json = await SocialMediaHttp.Http.GetStringAsync(string.Format(FxApiUrlFormat, statusId));
            var post = ParseFxPost(json);

            if (post == null)
            {
                BotLogger.Warning("Читалка не отдала пост X {Id}", statusId);
            }

            return post;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось получить пост X {Id} через читалку: {Message}", statusId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Ручка требует непустой token, но значение не проверяет: в вебе туда кладут число,
    /// посчитанное из идентификатора поста средствами JavaScript, повторить которые в C#
    /// точь-в-точь нельзя. Считаем своё в том же алфавите — важны только непустота
    /// и постоянство для одного поста.
    /// </summary>
    internal static string BuildToken(string statusId)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

        if (!ulong.TryParse(statusId, out var id) || id == 0)
        {
            return "1";
        }

        var digits = new Stack<char>();

        while (id > 0)
        {
            digits.Push(alphabet[(int)(id % 36)]);
            id /= 36;
        }

        // Настоящий токен нули из себя выбрасывает — повторяем и это
        var token = new string(digits.ToArray()).Replace("0", string.Empty);
        return token.Length > 0 ? token : "1";
    }

    /// <summary>
    /// Разбирает ответ ручки. Внутренний метод — на нём держатся тесты разбора.
    /// </summary>
    internal static LadderPost? ParsePost(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id_str", out _))
            {
                return null;
            }

            var media = new List<MediaLadder>();

            if (root.TryGetProperty("mediaDetails", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in details.EnumerateArray())
                {
                    var ladder = ReadMedia(item);

                    if (ladder != null)
                    {
                        media.Add(ladder);
                    }
                }
            }

            var caption = ExtractCaption(root);

            if (media.Count == 0 && caption == null)
            {
                return null;
            }

            return new LadderPost(ReadAuthor(root, "user"), caption, media);
        }
        catch (JsonException ex)
        {
            BotLogger.Warning("Не удалось разобрать ответ X: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Сводит лесенки к конкретным файлам: для каждого видео берёт лучший вариант, который
    /// влезает в лимит. Битрейт из ответа для этого не годится — он завышает вес втрое,
    /// поэтому размер спрашиваем у CDN. Не влезло ничего — оставляем худший вариант:
    /// про него честно скажут «файл не влез», и это лучше молчания.
    /// </summary>
    internal static async Task<SocialPost> PickQualityAsync(LadderPost post, ulong maxBytes)
    {
        var media = new List<SocialMedia>();

        foreach (var ladder in post.Media)
        {
            var url = await PickVariantAsync(ladder, maxBytes);
            media.Add(new SocialMedia(url, ladder.IsVideo, ladder.ThumbnailUrl));
        }

        return new SocialPost(post.AuthorName, post.Caption, media);
    }

    private static async Task<string> PickVariantAsync(MediaLadder ladder, ulong maxBytes)
    {
        if (ladder.Variants.Count == 1)
        {
            return ladder.Variants[0];
        }

        foreach (var variant in ladder.Variants)
        {
            var size = await SocialMediaHttp.TryGetSizeAsync(variant);

            // Размера не назвали — берём как есть: скачивание всё равно оборвётся по потолку
            if (size == null || size <= (long)maxBytes)
            {
                return variant;
            }
        }

        return ladder.Variants[^1];
    }

    /// <summary>
    /// Разбирает одну запись mediaDetails. У фото файл лежит прямо в media_url_https,
    /// у видео и гифки там только превью, а сам файл — в лесенке вариантов.
    /// </summary>
    private static MediaLadder? ReadMedia(JsonElement item)
    {
        var type = ReadText(item, "type");
        var poster = ReadText(item, "media_url_https");

        if (type == "photo")
        {
            return poster == null ? null : new MediaLadder(IsVideo: false, ThumbnailUrl: null, [poster]);
        }

        // Гифки у X — это тоже mp4, только без звука: отдельного типа файла у них нет
        if (type != "video" && type != "animated_gif")
        {
            return null;
        }

        if (!item.TryGetProperty("video_info", out var info) ||
            !info.TryGetProperty("variants", out var variants) ||
            variants.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ladder = ReadMp4Ladder(variants);

        return ladder.Count == 0 ? null : new MediaLadder(IsVideo: true, poster, ladder);
    }

    /// <summary>
    /// Разбирает ответ читалки. Форма своя: пост лежит в «tweet», медиа — плоским списком
    /// «media.all», а текст приезжает уже развёрнутым и без ссылки на само медиа.
    /// Внутренний метод — на нём держатся тесты разбора.
    /// </summary>
    internal static LadderPost? ParseFxPost(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tweet", out var tweet) ||
                tweet.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var media = new List<MediaLadder>();

            if (tweet.TryGetProperty("media", out var mediaRoot) &&
                mediaRoot.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in all.EnumerateArray())
                {
                    var ladder = ReadFxMedia(item);

                    if (ladder != null)
                    {
                        media.Add(ladder);
                    }
                }
            }

            var text = ReadText(tweet, "text")?.Trim();
            var caption = string.IsNullOrEmpty(text) ? null : text;

            if (media.Count == 0 && caption == null)
            {
                return null;
            }

            return new LadderPost(ReadAuthor(tweet, "author"), caption, media);
        }
        catch (JsonException ex)
        {
            BotLogger.Warning("Не удалось разобрать ответ читалки: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Разбирает одну запись «media.all». У фото в url лежит сам файл, у видео и гифки —
    /// лучший вариант, а лесенка целиком рядом. Гифка и здесь тот же mp4, просто своим типом.
    /// </summary>
    private static MediaLadder? ReadFxMedia(JsonElement item)
    {
        var type = ReadText(item, "type");
        var url = ReadText(item, "url");

        if (type == "photo")
        {
            return url == null ? null : new MediaLadder(IsVideo: false, ThumbnailUrl: null, [url]);
        }

        if (type != "video" && type != "gif")
        {
            return null;
        }

        var ladder = item.TryGetProperty("variants", out var variants)
            ? ReadMp4Ladder(variants)
            : [];

        // Лесенки может и не быть — тогда остаётся тот единственный файл, который читалка
        // сама сочла лучшим: это хуже выбора по размеру, но лучше молчания
        if (ladder.Count == 0 && url != null)
        {
            ladder.Add(url);
        }

        return ladder.Count == 0 ? null : new MediaLadder(IsVideo: true, ReadText(item, "thumbnail_url"), ladder);
    }

    /// <summary>
    /// Собирает лесенку mp4 от лучшего к худшему. Форма вариантов у обоих источников одна:
    /// url, bitrate и content_type. Кроме mp4 в лесенке лежит плейлист m3u8 — собирать
    /// из него файл нечем и незачем.
    /// </summary>
    private static List<string> ReadMp4Ladder(JsonElement variants)
    {
        if (variants.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return variants.EnumerateArray()
            .Where(variant => ReadText(variant, "content_type") == "video/mp4")
            .OrderByDescending(variant => ReadNumber(variant, "bitrate"))
            .Select(variant => ReadText(variant, "url"))
            .OfType<string>()
            .ToList();
    }

    /// <summary>
    /// Текст поста: t.co-сокращения разворачиваем в настоящие адреса, а ссылку на само медиа
    /// выкидываем — она ведёт на этот же пост, который мы и так показываем.
    /// </summary>
    private static string? ExtractCaption(JsonElement root)
    {
        var text = ReadText(root, "text");

        if (text == null)
        {
            return null;
        }

        if (root.TryGetProperty("entities", out var entities) &&
            entities.TryGetProperty("urls", out var urls) &&
            urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in urls.EnumerateArray())
            {
                var shortUrl = ReadText(link, "url");
                var expanded = ReadText(link, "expanded_url");

                if (shortUrl != null && expanded != null)
                {
                    text = text.Replace(shortUrl, expanded, StringComparison.Ordinal);
                }
            }
        }

        if (root.TryGetProperty("mediaDetails", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in details.EnumerateArray())
            {
                var shortUrl = ReadText(item, "url");

                if (shortUrl != null)
                {
                    text = text.Replace(shortUrl, string.Empty, StringComparison.Ordinal);
                }
            }
        }

        // X отдаёт текст с экранированными &amp;, &lt; и &gt;
        text = WebUtility.HtmlDecode(text).Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// Автор в виде «Имя (@логин)»: одного имени мало — в X его кто угодно может повторить.
    /// Поля у обоих источников одни и те же, различается только имя объекта с автором.
    /// </summary>
    private static string? ReadAuthor(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var user))
        {
            return null;
        }

        var name = ReadText(user, "name");
        var login = ReadText(user, "screen_name");

        if (name == null)
        {
            return login == null ? null : $"@{login}";
        }

        return login == null ? name : $"{name} (@{login})";
    }

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
}
