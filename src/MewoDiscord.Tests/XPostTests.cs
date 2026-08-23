using MewoDiscord.Handlers;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты разбора ответа syndication-ручки X и поиска ссылок. Автономны: сеть не нужна,
/// JSON взят фикстурами по форме настоящего ответа «tweet-result».
/// Выбор качества (<see cref="XPostClient.PickQualityAsync"/>) здесь проверяется только там,
/// где вариант один: с лесенкой он спрашивает размеры у CDN, а тесты в сеть не ходят.
/// </summary>
public class XPostTests
{
    /// <summary>
    /// Пост с видео: рядом с mp4 в лесенке лежит плейлист m3u8, а варианты идут вразнобой.
    /// </summary>
    private const string VideoPostJson = """
        {
          "id_str": "2090877991228264814",
          "text": "Ракета &amp; телескоп https://t.co/nCZCpF1MIO https://t.co/mEdIaLnKs",
          "entities": { "urls": [
            { "url": "https://t.co/nCZCpF1MIO", "expanded_url": "https://go.nasa.gov/45FxOK8" } ] },
          "user": { "name": "NASA", "screen_name": "NASA" },
          "mediaDetails": [ {
            "type": "video",
            "url": "https://t.co/mEdIaLnKs",
            "media_url_https": "https://pbs.twimg.com/media/poster.jpg",
            "video_info": {
              "duration_millis": 28361,
              "variants": [
                { "content_type": "application/x-mpegURL", "url": "https://video.twimg.com/pl/list.m3u8" },
                { "bitrate": 288000, "content_type": "video/mp4", "url": "https://video.twimg.com/vid/480x270/low.mp4" },
                { "bitrate": 2176000, "content_type": "video/mp4", "url": "https://video.twimg.com/vid/1280x720/high.mp4" },
                { "bitrate": 832000, "content_type": "video/mp4", "url": "https://video.twimg.com/vid/640x360/mid.mp4" } ] } } ]
        }
        """;

    /// <summary>
    /// Пост с гифкой: у X это тот же mp4, только типом animated_gif и одним вариантом.
    /// Текста в посте нет — только ссылка на само медиа.
    /// </summary>
    private const string GifPostJson = """
        {
          "id_str": "2091190297782841527",
          "text": "https://t.co/eSMjjFbBR5",
          "entities": { "urls": [] },
          "user": { "name": "M Ameen", "screen_name": "ReZero_Ameen" },
          "mediaDetails": [ {
            "type": "animated_gif",
            "url": "https://t.co/eSMjjFbBR5",
            "media_url_https": "https://pbs.twimg.com/tweet_video_thumb/HQVmd6lWgAAEQGr.jpg",
            "video_info": { "variants": [
              { "bitrate": 0, "content_type": "video/mp4", "url": "https://video.twimg.com/tweet_video/HQVmd6lWgAAEQGr.mp4" } ] } } ]
        }
        """;

    private const string PhotosPostJson = """
        {
          "id_str": "2090487779268407625",
          "text": "Две картинки https://t.co/jDZCF46SoS",
          "entities": { "urls": [] },
          "user": { "name": "Тестовый автор", "screen_name": "test_user" },
          "mediaDetails": [
            { "type": "photo", "url": "https://t.co/jDZCF46SoS",
              "media_url_https": "https://pbs.twimg.com/media/first.jpg" },
            { "type": "photo", "url": "https://t.co/jDZCF46SoS",
              "media_url_https": "https://pbs.twimg.com/media/second.jpg" } ]
        }
        """;

    [Fact]
    public void X_ВидеоБерётТолькоMp4ОтЛучшегоКХудшему()
    {
        var post = XPostClient.ParsePost(VideoPostJson);

        Assert.NotNull(post);
        var media = Assert.Single(post.Media);
        Assert.True(media.IsVideo);
        Assert.Equal(
            [
                "https://video.twimg.com/vid/1280x720/high.mp4",
                "https://video.twimg.com/vid/640x360/mid.mp4",
                "https://video.twimg.com/vid/480x270/low.mp4"
            ],
            media.Variants);
    }

    [Fact]
    public void X_ПревьюВидеоБерётсяИзMediaUrl()
    {
        var post = XPostClient.ParsePost(VideoPostJson);

        Assert.NotNull(post);
        Assert.Equal("https://pbs.twimg.com/media/poster.jpg", Assert.Single(post.Media).ThumbnailUrl);
    }

    [Fact]
    public void X_ПодписьРазворачиваетСсылкиИУбираетСсылкуНаМедиа()
    {
        var post = XPostClient.ParsePost(VideoPostJson);

        Assert.NotNull(post);
        Assert.Equal("Ракета & телескоп https://go.nasa.gov/45FxOK8", post.Caption);
    }

    [Fact]
    public void X_АвторСЛогином()
    {
        var post = XPostClient.ParsePost(VideoPostJson);

        Assert.NotNull(post);
        Assert.Equal("NASA (@NASA)", post.AuthorName);
    }

    [Fact]
    public async Task X_ГифкаЭтоВидеоБезПодписи()
    {
        var ladder = XPostClient.ParsePost(GifPostJson);

        Assert.NotNull(ladder);
        Assert.Null(ladder.Caption);

        // Вариант один — в сеть за размерами ходить не нужно
        var post = await XPostClient.PickQualityAsync(ladder, maxBytes: 50 * 1024 * 1024);
        var media = Assert.Single(post.Media);
        Assert.True(media.IsVideo);
        Assert.Equal("https://video.twimg.com/tweet_video/HQVmd6lWgAAEQGr.mp4", media.Url);
    }

    [Fact]
    public async Task X_ФотоРазбираютсяКакНеВидеоИПоПорядку()
    {
        var ladder = XPostClient.ParsePost(PhotosPostJson);

        Assert.NotNull(ladder);

        var post = await XPostClient.PickQualityAsync(ladder, maxBytes: 50 * 1024 * 1024);
        Assert.Equal(2, post.Media.Count);
        Assert.All(post.Media, media => Assert.False(media.IsVideo));
        Assert.Equal("https://pbs.twimg.com/media/first.jpg", post.Media[0].Url);
        Assert.Equal("https://pbs.twimg.com/media/second.jpg", post.Media[1].Url);
        Assert.Equal("Две картинки", post.Caption);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("не json вовсе")]
    [InlineData("""{ "id_str": "1", "text": "", "mediaDetails": [] }""")]
    public void X_ПустойИлиЧужойОтветНеПост(string json)
    {
        Assert.Null(XPostClient.ParsePost(json));
    }

    [Fact]
    public void X_НеизвестныйТипМедиаПропускается()
    {
        var json = """
            { "id_str": "1", "text": "текст", "user": { "name": "Кто-то", "screen_name": "someone" },
              "mediaDetails": [ { "type": "broadcast", "media_url_https": "https://pbs.twimg.com/x.jpg" } ] }
            """;

        var post = XPostClient.ParsePost(json);

        Assert.NotNull(post);
        Assert.Empty(post.Media);
        Assert.Equal("текст", post.Caption);
    }

    [Theory]
    [InlineData("глянь https://x.com/ReZero_Ameen/status/2091190297782841527?s=20 огонь", "ReZero_Ameen", "2091190297782841527")]
    [InlineData("https://twitter.com/NASA/status/2090877991228264814", "NASA", "2090877991228264814")]
    [InlineData("https://www.x.com/i/web/status/1234567890", "i/web", "1234567890")]
    [InlineData("https://mobile.twitter.com/user/statuses/77", "user", "77")]
    public void X_СсылкаНаПостНаходится(string text, string author, string statusId)
    {
        var links = XMediaHandler.FindLinks(text);

        var link = Assert.Single(links);
        Assert.Equal(author, link.Author);
        Assert.Equal(statusId, link.StatusId);
    }

    [Theory]
    [InlineData("https://x.com/NASA")]                              // профиль без поста
    [InlineData("https://example.com/x.com/NASA/status/1")]         // чужой домен
    [InlineData("https://x.com/NASA/status/abc")]                   // не число
    public void X_НеподходящиеСсылкиИгнорируются(string text)
    {
        Assert.Empty(XMediaHandler.FindLinks(text));
    }

    [Fact]
    public void X_ПовторыСхлопываютсяПоИдентификатору()
    {
        var text = "https://x.com/one/status/1 https://twitter.com/two/status/1 " +
                   "https://x.com/three/status/2 https://x.com/four/status/3 https://x.com/five/status/4";

        var links = XMediaHandler.FindLinks(text);

        Assert.Equal(3, links.Count);
        Assert.Equal(["1", "2", "3"], links.Select(link => link.StatusId));
    }

    [Theory]
    [InlineData("2091190297782841527")]
    [InlineData("0")]
    [InlineData("не число")]
    public void X_ТокенНикогдаНеПустой(string statusId)
    {
        var token = XPostClient.BuildToken(statusId);

        Assert.NotEmpty(token);
        Assert.DoesNotContain('0', token);
    }
}
