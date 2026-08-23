using MewoDiscord.Helpers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты распознавания ссылок на YouTube и условия гейта «есть ссылка и есть просьба».
/// Всё чистым кодом: гейт работает до похода в ИИ и в сеть не ходит.
/// </summary>
public class YoutubeLinksTests
{
    private const string Id = "dQw4w9WgXcQ";

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("http://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=AbCdEfGhIjKl")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s")]
    [InlineData("https://www.youtube.com/watch?list=PLabcdefgh&v=dQw4w9WgXcQ")]
    [InlineData("<https://youtu.be/dQw4w9WgXcQ>")]
    [InlineData("[клип](https://youtu.be/dQw4w9WgXcQ)")]
    [InlineData("вот тут https://youtu.be/dQw4w9WgXcQ смотри")]
    public void Media_СсылкаНаYoutubeНаходитсяВЛюбомФормате(string text)
    {
        Assert.Equal(Id, YoutubeLinks.FirstVideoId(text));
    }

    [Theory]
    [InlineData("https://www.youtube.com/@somechannel")]
    [InlineData("https://www.youtube.com/playlist?list=PLabcdefghijklmnop")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/short")]
    [InlineData("https://vimeo.com/123456789")]
    [InlineData("просто текст без ссылок")]
    public void Media_ЧужиеИНеполныеСсылкиНеЛовятся(string text)
    {
        Assert.Null(YoutubeLinks.FirstVideoId(text));
    }

    [Fact]
    public void Media_ИдентификаторНеПродолжаетсяЗаОдиннадцатьСимволов()
    {
        // Без запрета буквы следом совпали бы первые одиннадцать символов,
        // и бот скачал бы совершенно другое видео
        Assert.Null(YoutubeLinks.FirstVideoId("https://www.youtube.com/watch?v=dQw4w9WgXcQEXTRA"));
    }

    [Theory]
    [InlineData("скачай https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("обрежь с 1:04 по 1:40 youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("вырежи звуковую дорожку https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", false)]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ", false)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=AbCdEfGhIjKl", false)]
    [InlineData("<https://youtu.be/dQw4w9WgXcQ>", false)]
    [InlineData("!!! https://youtu.be/dQw4w9WgXcQ", false)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ 🔥", false)]
    [InlineData("<:kek:123456789> https://youtu.be/dQw4w9WgXcQ", false)]
    [InlineData("<@123456789> https://youtu.be/dQw4w9WgXcQ", false)]
    [InlineData("скачай https://www.youtube.com/@somechannel", false)]
    [InlineData("скачай вот это", false)]
    public void Media_ГейтТребуетТекстаКромеСсылки(string text, bool expected)
    {
        Assert.Equal(expected, YoutubeLinks.HasRequestBesidesLink(text));
    }

    [Fact]
    public void Media_ХвостСсылкиНеСчитаетсяПросьбой()
    {
        // Параметры после идентификатора вычёркиваются вместе со ссылкой: иначе
        // «si=AbCdEfGh» сошло бы за текст запроса
        Assert.False(YoutubeLinks.HasRequestBesidesLink("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s&feature=share"));
    }

    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("_-abcDEF123", true)]
    [InlineData("dQw4w9WgXc", false)]
    [InlineData("dQw4w9WgXcQQ", false)]
    [InlineData("../../etc/p", false)]
    [InlineData("-f arbitrary", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Media_ИдентификаторВидеоВалидируется(string? id, bool expected)
    {
        Assert.Equal(expected, YoutubeLinks.IsValidVideoId(id));
    }

    [Fact]
    public void Media_АдресДляYtDlpСтроитсяКодом()
    {
        // От исходной строки пользователя не остаётся ничего, кроме проверенного
        // идентификатора: ни плейлиста, ни отметки времени, ни чужой схемы
        var id = YoutubeLinks.FirstVideoId("https://youtu.be/dQw4w9WgXcQ?si=abc&list=PLevil");

        Assert.NotNull(id);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", YoutubeLinks.WatchUrl(id));
    }
}
