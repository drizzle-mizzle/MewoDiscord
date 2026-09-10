using Discord;

using MewoDiscord.Handlers;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты довеска к превью YouTube: разбор страницы просмотра, ответов RYD и oEmbed
/// и решение, что показывать. В сеть никто не ходит — всё на фикстурах.
/// </summary>
public class YoutubePreviewTests
{
    private const string Id = "dQw4w9WgXcQ";

    private const string Title = "Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)";

    /// <summary>
    /// Выжимка из настоящей страницы просмотра: заголовки для соцсетей, каноничная ссылка
    /// и разметка schema.org.
    /// </summary>
    private const string Page =
        """
        <html><head>
        <title>Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster) - YouTube</title>
        <meta name="title" content="Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)">
        <meta property="og:title" content="Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)">
        <link rel="canonical" href="https://www.youtube.com/watch?v=dQw4w9WgXcQ">
        </head><body>
        <div itemscope itemtype="http://schema.org/VideoObject">
        <meta itemprop="name" content="Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)">
        <meta itemprop="identifier" content="dQw4w9WgXcQ">
        <meta itemprop="datePublished" content="2009-10-24T23:57:33-07:00">
        <meta itemprop="uploadDate" content="2009-10-24T23:57:33-07:00">
        </div>
        </body></html>
        """;

    private const string Votes =
        """
        {"id":"dQw4w9WgXcQ","dateCreated":"2022-04-09T22:01:38.222268Z","likes":19380748,"rawDislikes":6648,
        "rawLikes":128723,"dislikes":518584,"rating":4.895758510888707,"viewCount":1813879923,"deleted":false}
        """;

    [Fact]
    public void Media_СтраницаВидеоДаётНазваниеИДату()
    {
        var page = YoutubeVideoClient.ParsePage(Page, Id);

        Assert.NotNull(page);
        Assert.Equal(Title, page.Title);
        Assert.Equal(new DateTimeOffset(2009, 10, 24, 23, 57, 33, TimeSpan.FromHours(-7)), page.PublishedAt);
    }

    [Fact]
    public void Media_ЧужаяСтраницаНеСходитЗаВидео()
    {
        // Согласие с куки вместо страницы видео: его заголовок сошёл бы за название
        const string consent =
            """
            <html><head><title>Before you continue to YouTube</title>
            <meta property="og:title" content="Before you continue to YouTube"></head></html>
            """;

        Assert.Null(YoutubeVideoClient.ParsePage(consent, Id));

        // Страница другого видео
        Assert.Null(YoutubeVideoClient.ParsePage(Page, "aaaaaaaaaaa"));

        Assert.Null(YoutubeVideoClient.ParsePage(string.Empty, Id));
    }

    [Fact]
    public void Media_СвоёВидеоУзнаётсяИПоКаноничнойСсылке()
    {
        // Без разметки schema.org, но с каноничной ссылкой вида shorts — и без даты:
        // её отсутствие не повод терять название
        const string shorts =
            """
            <html><head>
            <meta name="title" content="Tom &amp; Jerry">
            <link rel="canonical" href="https://www.youtube.com/shorts/dQw4w9WgXcQ">
            </head></html>
            """;

        var page = YoutubeVideoClient.ParsePage(shorts, Id);

        Assert.NotNull(page);
        Assert.Equal("Tom & Jerry", page.Title);
        Assert.Null(page.PublishedAt);
    }

    [Fact]
    public void Media_ГолосаРазбираютсяИзОтветаRyd()
    {
        var votes = YoutubeVideoClient.ParseVotes(Votes);

        Assert.NotNull(votes);
        Assert.Equal(19_380_748, votes.Likes);
        Assert.Equal(518_584, votes.Dislikes);
        Assert.Equal(1_813_879_923, votes.Views);

        // Нет хотя бы одного числа — нет строки вовсе, а не строка с дыркой
        Assert.Null(YoutubeVideoClient.ParseVotes("""{"id":"dQw4w9WgXcQ","likes":10,"viewCount":100}"""));
        Assert.Null(YoutubeVideoClient.ParseVotes("не json"));
        Assert.Null(YoutubeVideoClient.ParseVotes("[]"));
    }

    [Fact]
    public void Media_НазваниеИзOEmbed()
    {
        Assert.Equal(Title, YoutubeVideoClient.ParseOEmbedTitle($$"""{"title":"{{Title}}","type":"video"}"""));
        Assert.Null(YoutubeVideoClient.ParseOEmbedTitle("""{"title":"  ","type":"video"}"""));
        Assert.Null(YoutubeVideoClient.ParseOEmbedTitle("Unauthorized"));
    }

    [Fact]
    public void Media_ВидеоИзDataApi()
    {
        // Числа Data API отдаёт строками
        var video = YoutubeVideoClient.ParseApiVideo(
            $$$"""
            {"items":[{"id":"{{{Id}}}","snippet":{"publishedAt":"2009-10-25T06:57:33Z","title":"{{{Title}}}"},
            "statistics":{"viewCount":"1814007818","likeCount":"19381264","commentCount":"2400000"}}]}
            """);

        Assert.NotNull(video);
        Assert.Equal(Title, video.Title);
        Assert.Equal(new DateTimeOffset(2009, 10, 25, 6, 57, 33, TimeSpan.Zero), video.PublishedAt);
        Assert.Equal(1_814_007_818, video.Views);
        Assert.Equal(19_381_264, video.Likes);
    }

    [Fact]
    public void Media_DataApiБезЛайковИБезВидео()
    {
        // Автор скрыл лайки — их нет, а не ноль
        var hidden = YoutubeVideoClient.ParseApiVideo(
            """{"items":[{"snippet":{"title":"Видео"},"statistics":{"viewCount":"10"}}]}""");

        Assert.NotNull(hidden);
        Assert.Null(hidden.Likes);
        Assert.Equal(10, hidden.Views);
        Assert.Null(hidden.PublishedAt);

        // Удалённое или выдуманное видео — пустой список, а не ошибка
        Assert.Null(YoutubeVideoClient.ParseApiVideo("""{"kind":"youtube#videoListResponse","items":[]}"""));
        Assert.Null(YoutubeVideoClient.ParseApiVideo("не json"));

        Assert.Equal(
            "API key not valid. Please pass a valid API key.",
            YoutubeVideoClient.ParseApiError(
                """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key."}}"""));
    }

    [Fact]
    public void Media_ПолноеНазваниеТолькоКогдаПревьюЕгоОбрезало()
    {
        // Так Discord кладёт в embed длинное название: оборванным, с тремя точками
        Assert.True(YoutubePreviewHandler.NeedsFullTitle(
            "Очень длинное название видео, которое не влезло в превью",
            "Очень длинное название видео, котор..."));

        // Длинное, но целиком — дублировать незачем
        Assert.False(YoutubePreviewHandler.NeedsFullTitle(Title, Title));

        // Превью нет вовсе — название больше нигде не видно
        Assert.True(YoutubePreviewHandler.NeedsFullTitle("Коротко", nativeTitle: null));
    }

    [Fact]
    public void Media_ЗаголовокПревьюНаходитсяПоВидео()
    {
        var embeds = new[]
        {
            new EmbedBuilder().WithUrl($"https://www.youtube.com/watch?v={Id}").WithTitle("Rick Astley - Never...").Build(),
            new EmbedBuilder().WithUrl("https://x.com/user/status/1").WithTitle("Чужое превью").Build(),

            // Превью без заголовка названия не показывает — как будто его нет
            new EmbedBuilder().WithUrl("https://www.youtube.com/watch?v=aaaaaaaaaaa").WithDescription("…").Build()
        };

        var titles = YoutubePreviewHandler.NativeTitles(embeds, [Id, "aaaaaaaaaaa"]);

        Assert.Single(titles);
        Assert.Equal("Rick Astley - Never...", titles[Id]);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1 000")]
    [InlineData(1_813_879_923, "1 813 879 923")]
    public void Media_ЧислаРазбиваютсяПоРазрядам(long count, string expected)
    {
        // Между разрядами неразрывный пробел: число не разорвётся переносом строки
        Assert.Equal(expected.Replace(' ', '\u00A0'), YoutubePreviewHandler.FormatCount(count));
    }
}
