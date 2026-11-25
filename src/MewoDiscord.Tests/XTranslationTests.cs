using MewoDiscord.Handlers;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Автономные проверки правил появления реакций и строгого разбора семантического гейта.
/// Discord, прокси и генерация здесь не вызываются.
/// </summary>
public class XTranslationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   \n", false)]
    [InlineData("Текст поста", true)]
    public void X_РеакцияТекстаТолькоПриПодписи(string? caption, bool expected)
    {
        var post = Post(caption, [Photo(1)]);

        Assert.Equal(expected, XTranslationHandler.HasTranslatableText(post));
    }

    [Fact]
    public void X_РеакцияИзображенийТолькоУРеальныхВложений()
    {
        var post = Post(null, [Photo(1)]);

        Assert.True(XTranslationHandler.CanTranslateImages(post, hasAttachments: true));
        Assert.False(XTranslationHandler.CanTranslateImages(post, hasAttachments: false));
    }

    [Fact]
    public void X_РеакцияИзображенийНеНужнаБезКартинокИНаВидео()
    {
        Assert.False(XTranslationHandler.CanTranslateImages(Post(null, []), hasAttachments: true));
        Assert.False(XTranslationHandler.CanTranslateImages(Post(null, [Video(1)]), hasAttachments: true));
        Assert.False(XTranslationHandler.CanTranslateImages(Post(null, [Photo(1), Video(2)]), hasAttachments: true));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void X_ПереводИзображенийОграниченШестью(int count, bool expected)
    {
        var post = Post(null, Enumerable.Range(1, count).Select(Photo).ToList());

        Assert.Equal(expected, XTranslationHandler.CanTranslateImages(post, hasAttachments: true));
    }

    [Fact]
    public void X_СемантическийГейтВыбираетТолькоТекстДляПеревода()
    {
        const string reply = """
            {
              "images": [
                { "index": 1, "has_text": true, "needs_translation": true },
                { "index": 2, "has_text": false, "needs_translation": false },
                { "index": 3, "has_text": true, "needs_translation": false }
              ]
            }
            """;

        Assert.Equal([1], XTranslationHandler.ParseImageAssessments(reply, 3));
    }

    [Fact]
    public void X_СемантическийГейтПринимаетMarkdownИНесколькоКандидатов()
    {
        const string reply = """
            ```json
            {"images":[
              {"index":2,"has_text":true,"needs_translation":true},
              {"index":1,"has_text":true,"needs_translation":true}
            ]}
            ```
            """;

        Assert.Equal([1, 2], XTranslationHandler.ParseImageAssessments(reply, 2));
    }

    [Fact]
    public void X_СемантическийГейтМожетОтклонитьВсеКартинки()
    {
        const string reply = """
            {"images":[{"index":1,"has_text":false,"needs_translation":false}]}
            """;

        var approved = XTranslationHandler.ParseImageAssessments(reply, 1);

        Assert.NotNull(approved);
        Assert.Empty(approved);
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{}")]
    [InlineData("{\"images\":[]}")] // нет обязательной записи
    [InlineData("{\"images\":[{\"index\":1,\"has_text\":true}]}")] // нет второго признака
    [InlineData("{\"images\":[{\"index\":1,\"has_text\":\"да\",\"needs_translation\":true}]}")]
    [InlineData("{\"images\":[{\"index\":2,\"has_text\":true,\"needs_translation\":true}]}")]
    [InlineData("{\"images\":[{\"index\":1,\"has_text\":true,\"needs_translation\":true},{\"index\":1,\"has_text\":true,\"needs_translation\":true}]}")]
    public void X_СемантическийГейтЗакрываетсяПриНекорректномОтвете(string reply)
    {
        Assert.Null(XTranslationHandler.ParseImageAssessments(reply, 1));
    }

    [Fact]
    public void X_СемантическийГейтТребуетЗаписиДляКаждойКартинки()
    {
        const string reply = """
            {"images":[{"index":1,"has_text":true,"needs_translation":true}]}
            """;

        Assert.Null(XTranslationHandler.ParseImageAssessments(reply, 2));
    }

    private static SocialPost Post(string? caption, IReadOnlyList<SocialMedia> media) =>
        new("Автор", "@author", caption, media, PublishedAt: null);

    private static SocialMedia Photo(int index) =>
        new($"https://pbs.twimg.com/media/{index}.jpg", IsVideo: false, ThumbnailUrl: null);

    private static SocialMedia Video(int index) =>
        new($"https://video.twimg.com/{index}.mp4", IsVideo: true, ThumbnailUrl: null);
}
