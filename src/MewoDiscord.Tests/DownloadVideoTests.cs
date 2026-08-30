using MewoDiscord.AiActionsProcessors;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты арифметики скачивания: сверка длительности, разбор отрезка, имя файла и вид
/// результата. Всё это чистые функции — ни yt-dlp, ни сети не нужно.
/// </summary>
public class DownloadVideoTests
{
    [Theory]
    [InlineData(100, 100)]   // точное совпадение
    [InlineData(109, 100)]   // в пределах десяти процентов
    [InlineData(91, 100)]
    [InlineData(11, 10)]     // на коротком ролике работает допуск в две секунды
    [InlineData(9, 10)]
    [InlineData(5, 0)]       // длительность неизвестна — сверять не с чем
    public void Media_ДлительностьСчитаетсяСовпавшей(double actual, double expected)
    {
        Assert.True(DownloadVideo.DurationMatches(actual, expected));
    }

    [Theory]
    [InlineData(50, 100)]    // огрызок недокачанного файла
    [InlineData(200, 100)]   // приехал весь ролик вместо отрезка
    [InlineData(7, 10)]
    public void Media_ДлительностьСчитаетсяРазошедшейся(double actual, double expected)
    {
        Assert.False(DownloadVideo.DurationMatches(actual, expected));
    }

    [Fact]
    public void Media_ОтрезокИзМанифестаЖдётПолнуюДлительность()
    {
        // yt-dlp не режет манифест на качании — приедет весь ролик, и это удача,
        // а не провал: резать его будет ffmpeg уже у нас
        var expected = DownloadVideo.ExpectedSeconds(hasSection: true, manifest: true, fullSeconds: 600, outputSeconds: 30);

        Assert.Equal(600, expected);
        Assert.True(DownloadVideo.DurationMatches(600, expected));
    }

    [Fact]
    public void Media_ОбычныйОтрезокЖдётДлинуОтрезка()
    {
        var expected = DownloadVideo.ExpectedSeconds(hasSection: true, manifest: false, fullSeconds: 600, outputSeconds: 30);

        Assert.Equal(30, expected);
    }

    [Fact]
    public void Media_БезОтрезкаЖдётсяВесьРолик()
    {
        Assert.Equal(600, DownloadVideo.ExpectedSeconds(hasSection: false, manifest: true, fullSeconds: 600, outputSeconds: 600));
    }

    [Fact]
    public void Media_ОтрезокБезГраницНеОтрезок()
    {
        Assert.Null(DownloadVideo.ResolveSection(new FfmpegRunner.MediaPlan(), 100));
    }

    [Fact]
    public void Media_ОтрезокЗажимаетсяВДлительность()
    {
        var section = DownloadVideo.ResolveSection(new FfmpegRunner.MediaPlan(Start: 10, End: 500), 100);

        Assert.NotNull(section);
        Assert.Equal(10, section.Value.Start);
        Assert.Equal(100, section.Value.End);
    }

    [Fact]
    public void Media_ТолькоНачалоОтрезкаДоводитсяДоКонцаРолика()
    {
        var section = DownloadVideo.ResolveSection(new FfmpegRunner.MediaPlan(Start: 30), 100);

        Assert.NotNull(section);
        Assert.Equal(30, section.Value.Start);
        Assert.Equal(100, section.Value.End);
    }

    [Fact]
    public void Media_ТолькоКонецОтрезкаСчитаетсяОтНачала()
    {
        var section = DownloadVideo.ResolveSection(new FfmpegRunner.MediaPlan(End: 30), 100);

        Assert.NotNull(section);
        Assert.Equal(0, section.Value.Start);
        Assert.Equal(30, section.Value.End);
    }

    [Fact]
    public void Media_ПеревёрнутыйОтрезокОтбрасывается()
    {
        // Конец раньше начала — резать нечего, качаем целиком
        Assert.Null(DownloadVideo.ResolveSection(new FfmpegRunner.MediaPlan(Start: 80, End: 20), 100));
    }

    [Fact]
    public void Media_НачалоЗаКонцомРоликаНеДаётПустойОтрезок()
    {
        var section = DownloadVideo.ResolveSection(new FfmpegRunner.MediaPlan(Start: 500), 100);

        Assert.NotNull(section);
        Assert.True(section.Value.End > section.Value.Start);
    }

    [Theory]
    [InlineData("Обычное видео", "Обычное видео.mp4")]
    [InlineData("а/б\\в:г*д?е\"ж<з>и|к", "абвгдежзик.mp4")]
    [InlineData("////", "video.mp4")]
    [InlineData("", "video.mp4")]
    public void Media_ИмяФайлаВидеоБезопасно(string title, string expected)
    {
        Assert.Equal(expected, DownloadVideo.SafeName(title));
    }

    [Fact]
    public void Media_ДлинноеНазваниеРоликаОбрезается()
    {
        var name = DownloadVideo.SafeName(new string('я', 300));

        Assert.EndsWith(".mp4", name);
        Assert.True(name.Length < 100);
    }

    [Theory]
    [InlineData("gif", MediaShrink.ShrinkKind.Gif)]
    [InlineData("mp3", MediaShrink.ShrinkKind.Audio)]
    [InlineData("opus", MediaShrink.ShrinkKind.Audio)]
    [InlineData("mp4", MediaShrink.ShrinkKind.Video)]
    [InlineData("webm", MediaShrink.ShrinkKind.Video)]
    public void Media_ВидРезультатаОпределяетсяПоФормату(string format, MediaShrink.ShrinkKind expected)
    {
        Assert.Equal(expected, DownloadVideo.ResolveKind(format));
    }
}
