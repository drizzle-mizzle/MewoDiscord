using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты расчёта пережатия. Ничего не кодируется: проверяется арифметика,
/// по которой выбирается битрейт, разрешение и шаг следующего круга.
/// </summary>
public class MediaShrinkTests
{
    private const long TenMb = 10L * 1024 * 1024;

    private static readonly FfmpegRunner.MediaInfo _fullHd = new(
        1920,
        1080,
        3600,
        Video: new FfmpegRunner.VideoStreamInfo("h264", 1920, 1080, 30, 4_000_000),
        Audio: new FfmpegRunner.AudioStreamInfo("aac", 2, 44100, 128_000));

    [Fact]
    public void Media_ЦелевойБитрейтСчитаетсяИзРазмераИДлины()
    {
        // Десять мегабайт на 36 секунд — это около 2.3 Мбит/с на всё вместе
        var plan = MediaShrink.PlanVideo(_fullHd, 36, TenMb);

        Assert.NotNull(plan);
        Assert.InRange(plan.VideoBitrateBps + plan.AudioBitrateBps, 2_200_000, 2_400_000);

        // Под такой битрейт 1080p не тянет по плотности битов на пиксель, а 720p тянет
        Assert.Equal(720, plan.Height);
        Assert.Equal(1280, plan.Width);
        Assert.Equal(30, plan.Fps);
    }

    [Fact]
    public void Media_РазрешениеПадаетВместеСБитрейтом()
    {
        var plan = MediaShrink.PlanVideo(_fullHd, 600, 25L * 1024 * 1024);

        Assert.NotNull(plan);

        // Десять минут в двадцать пять мегабайт — это уже не 720p и тем более не 1080p
        Assert.True(plan.Height <= 480, $"ожидалось не выше 480p, получено {plan.Height}p");
        Assert.True(plan.Height > 0);

        // Стороны обязаны быть чётными, иначе кодеки видео ругаются
        Assert.Equal(0, plan.Width % 2);
        Assert.Equal(0, plan.Height % 2);
    }

    [Fact]
    public void Media_ЧастотаКадровНеВышеТридцати()
    {
        var sixty = _fullHd with
        {
            Video = new FfmpegRunner.VideoStreamInfo("h264", 1920, 1080, 60, 8_000_000)
        };

        var plan = MediaShrink.PlanVideo(sixty, 36, TenMb);

        Assert.NotNull(plan);

        // Шестьдесят кадров вдвое режут плотность битрейта задаром
        Assert.Equal(30, plan.Fps);
    }

    [Fact]
    public void Media_СлишкомДлинноеВидеоНеЖмётся()
    {
        // Три часа в десять мегабайт — это семь килобит в секунду, то есть ничего
        Assert.Null(MediaShrink.PlanVideo(_fullHd, 10800, TenMb));
        Assert.False(MediaShrink.CanFitVideo(10800, TenMb));

        // А тридцать шесть секунд в те же десять мегабайт — вполне
        Assert.True(MediaShrink.CanFitVideo(36, TenMb));
    }

    [Fact]
    public void Media_СледующаяПопыткаКорректируетсяПоФакту()
    {
        var previous = new FfmpegRunner.EncodeSettings(3_000_000, 128_000, 1280, 720, 30);
        var next = MediaShrink.CorrectVideo(previous, 14L * 1024 * 1024, TenMb, _fullHd);

        Assert.NotNull(next);

        // Промах на треть — значит и битрейт вниз примерно на треть, а не слепые 20 %
        Assert.InRange(next.VideoBitrateBps, 2_000_000, 2_200_000);
    }

    [Fact]
    public void Media_ДикийЗамерНеУтаскиваетСледующийКруг()
    {
        var previous = new FfmpegRunner.EncodeSettings(3_000_000, 128_000, 1280, 720, 30);
        var next = MediaShrink.CorrectVideo(previous, 100L * 1024 * 1024, TenMb, _fullHd);

        Assert.NotNull(next);

        // Без зажима снизу один кривой замер отправил бы следующий круг в кашу
        Assert.InRange(next.VideoBitrateBps, 1_400_000, 1_500_000);
    }

    [Fact]
    public void Media_ГифкаУменьшаетсяСначалаПоЧастотеКадров()
    {
        var gif = new FfmpegRunner.MediaInfo(
            640,
            360,
            10,
            Video: new FfmpegRunner.VideoStreamInfo("gif", 640, 360, 20, 0));

        // Вдвое с лишним — хватает одних кадров, ширину трогать незачем
        var half = MediaShrink.Plan(MediaShrink.ShrinkKind.Gif, gif, 10, 8L * 1024 * 1024, 20L * 1024 * 1024);

        Assert.NotNull(half);
        Assert.Equal(640, half.Width);
        Assert.True(half.Fps < 20);
    }

    [Fact]
    public void Media_ГифкаОтдаётШиринуПоследней()
    {
        var gif = new FfmpegRunner.MediaInfo(
            640,
            360,
            10,
            Video: new FfmpegRunner.VideoStreamInfo("gif", 640, 360, 20, 0));

        // Впятеро одними кадрами не выбрать: подключаются палитра и только потом ширина
        var deep = MediaShrink.Plan(MediaShrink.ShrinkKind.Gif, gif, 10, 8L * 1024 * 1024, 40L * 1024 * 1024);

        Assert.NotNull(deep);
        Assert.Equal(MediaShrink.MinGifFps, deep.Fps);
        Assert.True(deep.Colors is > 0 and <= 64);
        Assert.True(deep.Width < 640);
        Assert.True(deep.Width >= MediaShrink.MinGifWidth);
    }

    [Fact]
    public void Media_ГифкаНижеПоловДальшеНеЖмётся()
    {
        var previous = new FfmpegRunner.EncodeSettings(Width: MediaShrink.MinGifWidth, Fps: MediaShrink.MinGifFps, Colors: 32);

        // Шагать больше некуда, а результат всё ещё велик — честнее отказать
        Assert.Null(MediaShrink.Correct(
            MediaShrink.ShrinkKind.Gif,
            previous,
            20L * 1024 * 1024,
            1L * 1024 * 1024,
            new FfmpegRunner.MediaInfo(640, 360, 10),
            10));
    }

    [Fact]
    public void Media_ЗвукСчитаетсяТочно()
    {
        // Пять минут в восемь мегабайт — это 223 кбит/с, берём верхнюю ступень
        var plan = MediaShrink.PlanAudio(300, 8L * 1024 * 1024);

        Assert.NotNull(plan);
        Assert.Equal(128_000, plan.AudioBitrateBps);
        Assert.Equal(2, plan.Channels);
    }

    [Fact]
    public void Media_ЗвукНеОпускаетсяНижеПорога()
    {
        // Три часа в восемь мегабайт — шесть килобит в секунду, речь неразборчива
        Assert.Null(MediaShrink.PlanAudio(10800, 8L * 1024 * 1024));
    }

    [Fact]
    public void Media_КругЗвукаОбязанБытьШагомВниз()
    {
        var previous = new FfmpegRunner.EncodeSettings(AudioBitrateBps: 128_000, Channels: 2);

        var next = MediaShrink.Correct(
            MediaShrink.ShrinkKind.Audio,
            previous,
            10L * 1024 * 1024,
            8L * 1024 * 1024,
            new FfmpegRunner.MediaInfo(0, 0, 300),
            300);

        Assert.NotNull(next);
        Assert.True(next.AudioBitrateBps < previous.AudioBitrateBps);
    }
}
