using MewoDiscord.Handlers;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты общего движка ответов на посты: заголовок со ссылкой, подпись, дата и имя файла.
/// Источник тут ни при чём — движок один на Telegram и X, и часть случаев пришла как раз
/// из X. Префикс имён исторический (Telegram_): по нему тесты попадают в офлайн-прогон,
/// а менять его значило бы править фильтр из CLAUDE.md ради косметики.
/// </summary>
public class PostMediaHandlerTests
{
    [Fact]
    public void Telegram_КороткаяПодписьПередаётсяКакЕсть()
    {
        Assert.Equal("первая\nвторая", PostMediaHandler.PrepareCaption("первая\nвторая"));
    }

    [Fact]
    public void Telegram_ДлиннаяПодписьОбрезается()
    {
        var caption = PostMediaHandler.PrepareCaption(new string('а', 5000));

        Assert.EndsWith("…", caption);
        Assert.True(caption.Length < 4000, $"длина {caption.Length} не влезает в лимит компонента");
    }

    /// <summary>
    /// Заголовок общий для всех источников: ссылка подписана логином, имя идёт строкой ниже.
    /// </summary>
    [Fact]
    public void Telegram_ЗаголовокСсылаетсяЛогиномАИмяНиже()
    {
        var post = new SocialPost("🐝🇬🇷", "@bee_fumo", "Yuri things..", [], PublishedAt: null);

        var header = PostMediaHandler.BuildHeaderText(post, "https://x.com/bee_fumo/status/1");

        Assert.Equal("### [@bee_fumo](https://x.com/bee_fumo/status/1)\n**🐝🇬🇷**\nYuri things..", header);
    }

    [Fact]
    public void Telegram_ИмяРавноеЛогинуВторойРазНеПишется()
    {
        var post = new SocialPost("bee_fumo", "@bee_fumo", null, [], PublishedAt: null);

        var header = PostMediaHandler.BuildHeaderText(post, "https://x.com/bee_fumo/status/1");

        Assert.Equal("### [@bee_fumo](https://x.com/bee_fumo/status/1)", header);
    }

    /// <summary>
    /// Логина нет — подписью становится имя, и вот тут эмодзи из него вычёркиваются.
    /// </summary>
    [Fact]
    public void Telegram_БезЛогинаПодписьюИдётИмя()
    {
        var post = new SocialPost("Тестовый канал", null, null, [], PublishedAt: null);

        var header = PostMediaHandler.BuildHeaderText(post, "https://t.me/test_channel/5");

        Assert.Equal("### [Тестовый канал](https://t.me/test_channel/5)", header);
    }

    [Fact]
    public void Telegram_ДатаВПодписиУходитМеткойВремени()
    {
        var footer = PostMediaHandler.BuildFooterText(
            "Telegram", icon: null, new DateTimeOffset(2026, 8, 24, 19, 14, 14, TimeSpan.Zero));

        Assert.Equal("-# Telegram • <t:1787598854:f>", footer);
    }

    [Fact]
    public void Telegram_БезДатыПодписьПрежняя()
    {
        Assert.Equal("-# Telegram", PostMediaHandler.BuildFooterText("Telegram", icon: null, publishedAt: null));
    }

    /// <summary>
    /// Подпись ссылки общая для всех источников, но случай пришёл из X: у автора с эмодзи
    /// в имени Discord показывал сырую разметку вместо ссылки.
    /// </summary>
    [Theory]
    [InlineData("Khyle. (@khyleri)", "Khyle. (@khyleri)")]
    [InlineData("🐝🇬🇷 (@bee_fumo)", "@bee_fumo")]
    [InlineData("Anna 🌸 Smith (@anna)", "Anna Smith (@anna)")]
    [InlineData("Тест 👨‍👩‍👧 (@test)", "Тест (@test)")]
    [InlineData("[NASA] (@NASA)", "NASA (@NASA)")]
    [InlineData("(Аня) (@anya)", "(Аня) (@anya)")]
    public void Telegram_ПодписьСсылкиБезЭмодзи(string author, string expected)
    {
        Assert.Equal(expected, PostMediaHandler.BuildLinkLabel(author));
    }

    [Theory]
    [InlineData("🐝🇬🇷")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Telegram_ПодписьБезБуквНеСсылка(string? author)
    {
        Assert.Null(PostMediaHandler.BuildLinkLabel(author));
    }

    [Theory]
    [InlineData("https://cdn4.telesco.pe/file/clip.mp4", true, "clip.mp4")]
    [InlineData("https://cdn4.telesco.pe/file/picture.jpg", false, "picture.jpg")]
    [InlineData("https://cdn4.telesco.pe/file/no-extension-token", true, "telegram.mp4")]
    [InlineData("https://cdn4.telesco.pe/file/no-extension-token", false, "telegram.jpg")]
    public void Telegram_ИмяФайлаБезопасно(string url, bool isVideo, string expected)
    {
        var media = new SocialMedia(url, isVideo, ThumbnailUrl: null);

        Assert.Equal(expected, PostMediaHandler.BuildFileName(media, "telegram"));
    }
}
