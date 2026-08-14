using MewoDiscord.Handlers;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты разбора виджета Telegram и поиска ссылок. Автономны: сеть не нужна,
/// HTML взят фикстурами по структуре страницы «t.me/канал/пост?embed=1».
/// </summary>
public class TelegramPostTests
{
    /// <summary>
    /// Пост с видео: рядом лежат аватар канала и превью — их нельзя спутать с медиа.
    /// </summary>
    private const string VideoPostHtml = """
        <div class="tgme_widget_message_user">
          <a href="https://t.me/test_channel"><i class="tgme_widget_message_user_photo">
            <img src="https://cdn4.telesco.pe/file/avatar.jpg"></i></a>
        </div>
        <div class="tgme_widget_message_author accent_color">
          <a class="tgme_widget_message_owner_name" href="https://t.me/test_channel"><span dir="auto">Тестовый канал</span></a>
        </div>
        <div class="tgme_widget_message_video_player js-message_video_player">
          <video src="https://cdn4.telesco.pe/file/clip.mp4" class="tgme_widget_message_video js-message_video"></video>
          <i class="tgme_widget_message_video_thumb" style="background-image:url('https://cdn4.telesco.pe/file/thumb.jpg')"></i>
        </div>
        <div class="tgme_widget_message_text js-message_text">Первая строка<br/>вторая &amp; третья</div>
        """;

    private const string PhotoPostHtml = """
        <div class="tgme_widget_message_author accent_color">
          <a class="tgme_widget_message_owner_name" href="https://t.me/test_channel"><span dir="auto">Тестовый канал</span></a>
        </div>
        <a class="tgme_widget_message_photo_wrap js-message_photo"
           style="width:100%;background-image:url('https://cdn4.telesco.pe/file/picture.jpg')"
           href="https://t.me/test_channel/7"></a>
        """;

    private const string TextOnlyPostHtml = """
        <div class="tgme_widget_message_author accent_color">
          <a class="tgme_widget_message_owner_name" href="https://t.me/test_channel"><span dir="auto">Тестовый канал</span></a>
        </div>
        <div class="tgme_widget_message_text js-message_text">Просто текст без картинок</div>
        """;

    [Fact]
    public void Telegram_ВидеоРазбираетсяСПревьюИПодписью()
    {
        var post = TelegramPostClient.ParsePost(VideoPostHtml);

        Assert.NotNull(post);
        var media = Assert.Single(post.Media);
        Assert.True(media.IsVideo);
        Assert.Equal("https://cdn4.telesco.pe/file/clip.mp4", media.Url);
        Assert.Equal("https://cdn4.telesco.pe/file/thumb.jpg", media.ThumbnailUrl);
        Assert.Equal("Первая строка\nвторая & третья", post.Caption);
        Assert.Equal("Тестовый канал", post.ChannelName);
    }

    [Fact]
    public void Telegram_АватарКаналаНеСчитаетсяМедиа()
    {
        var post = TelegramPostClient.ParsePost(VideoPostHtml);

        Assert.NotNull(post);
        Assert.DoesNotContain(post.Media, m => m.Url.Contains("avatar"));
    }

    [Fact]
    public void Telegram_ФотоРазбираетсяКакНеВидео()
    {
        var post = TelegramPostClient.ParsePost(PhotoPostHtml);

        Assert.NotNull(post);
        var media = Assert.Single(post.Media);
        Assert.False(media.IsVideo);
        Assert.Equal("https://cdn4.telesco.pe/file/picture.jpg", media.Url);
    }

    [Fact]
    public void Telegram_ПостБезМедиаНеДаётФайлов()
    {
        var post = TelegramPostClient.ParsePost(TextOnlyPostHtml);

        Assert.NotNull(post);
        Assert.Empty(post.Media);
    }

    [Theory]
    [InlineData("смотри что нашёл https://t.me/hz_xman/6034 огонь", "hz_xman", "6034")]
    [InlineData("https://t.me/s/some_channel/42", "some_channel", "42")]
    [InlineData("http://telegram.me/another_one/7", "another_one", "7")]
    public void Telegram_СсылкаНаПостНаходится(string text, string channel, string postId)
    {
        var links = TelegramMediaHandler.FindLinks(text);

        var link = Assert.Single(links);
        Assert.Equal(channel, link.Channel);
        Assert.Equal(postId, link.PostId);
    }

    [Theory]
    [InlineData("https://t.me/c/1234567890/55")]          // приватный канал — виджета нет
    [InlineData("https://t.me/hz_xman")]                   // канал без поста
    [InlineData("https://example.com/t.me/channel/1")]     // чужой домен
    public void Telegram_НеподходящиеСсылкиИгнорируются(string text)
    {
        var links = TelegramMediaHandler.FindLinks(text);

        Assert.Empty(links);
    }

    [Fact]
    public void Telegram_ПовторыСхлопываютсяИЛимитируются()
    {
        var text = "https://t.me/one_channel/1 https://t.me/one_channel/1 " +
                   "https://t.me/two_channel/2 https://t.me/three_channel/3 https://t.me/four_channel/4";

        var links = TelegramMediaHandler.FindLinks(text);

        Assert.Equal(3, links.Count);
        Assert.Equal("one_channel", links[0].Channel);
    }

    [Fact]
    public void Telegram_КороткаяПодписьПередаётсяКакЕсть()
    {
        Assert.Equal("первая\nвторая", TelegramMediaHandler.PrepareCaption("первая\nвторая"));
    }

    [Fact]
    public void Telegram_ДлиннаяПодписьОбрезается()
    {
        var caption = TelegramMediaHandler.PrepareCaption(new string('а', 5000));

        Assert.EndsWith("…", caption);
        Assert.True(caption.Length < 4000, $"длина {caption.Length} не влезает в лимит компонента");
    }

    [Theory]
    [InlineData("https://cdn4.telesco.pe/file/clip.mp4", true, "clip.mp4")]
    [InlineData("https://cdn4.telesco.pe/file/picture.jpg", false, "picture.jpg")]
    [InlineData("https://cdn4.telesco.pe/file/no-extension-token", true, "telegram.mp4")]
    [InlineData("https://cdn4.telesco.pe/file/no-extension-token", false, "telegram.jpg")]
    public void Telegram_ИмяФайлаБезопасно(string url, bool isVideo, string expected)
    {
        var media = new TelegramPostClient.TelegramMedia(url, isVideo, ThumbnailUrl: null);

        Assert.Equal(expected, TelegramMediaHandler.BuildFileName(media));
    }
}
