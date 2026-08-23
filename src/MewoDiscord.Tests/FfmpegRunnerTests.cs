using MewoDiscord.AiActionsProcessors;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты сборки аргументов ffmpeg и разбора плана от модели.
/// Сам ffmpeg не запускается: проверяется то, что решает код, а не внешняя программа.
/// </summary>
public class FfmpegRunnerTests
{
    private static readonly FfmpegRunner.MediaInfo Source = new(1920, 1080, 30);

    private static string Args(FfmpegRunner.MediaPlan plan, string format = "mp4", FfmpegRunner.MediaInfo? info = null) =>
        string.Join(' ', FfmpegRunner.BuildArguments(plan, info ?? Source, format, "in.mp4", "out." + format));

    [Fact]
    public void Media_ОбрезкаПоВремениСчитаетсяДлительностью()
    {
        // -t однозначен рядом с -ss, в отличие от -to, у которого смещается точка отсчёта
        var args = Args(new FfmpegRunner.MediaPlan(Start: 3, End: 7.5));

        Assert.Contains("-ss 3", args);
        Assert.Contains("-t 4.5", args);
    }

    [Fact]
    public void Media_ДлительностьРежетсяПоПотолку()
    {
        var args = Args(new FfmpegRunner.MediaPlan(Start: 0, End: 300));

        Assert.Contains($"-t {FfmpegRunner.MaxOutputSeconds}", args);
        Assert.DoesNotContain("-t 300", args);
    }

    [Fact]
    public void Media_КропЗажимаетсяВГраницыКадра()
    {
        var clamped = FfmpegRunner.ClampCrop(new FfmpegRunner.CropBox(1800, 1000, 500, 500), Source);

        Assert.NotNull(clamped);
        Assert.Equal(120, clamped.Width);
        Assert.Equal(80, clamped.Height);

        // Отрицательные координаты модель тоже иногда присылает
        var negative = FfmpegRunner.ClampCrop(new FfmpegRunner.CropBox(-50, -50, 200, 200), Source);
        Assert.NotNull(negative);
        Assert.Equal(0, negative.X);
        Assert.Equal(0, negative.Y);
    }

    [Fact]
    public void Media_КропИдётДоМасштабирования()
    {
        var args = Args(new FfmpegRunner.MediaPlan(Crop: new FfmpegRunner.CropBox(0, 0, 1000, 800), Width: 320));

        var crop = args.IndexOf("crop=", StringComparison.Ordinal);
        var scale = args.IndexOf("scale=", StringComparison.Ordinal);

        Assert.True(crop >= 0 && scale > crop, $"кроп должен идти перед scale: {args}");
    }

    [Fact]
    public void Media_ШиринаНеПревышаетПотолок()
    {
        var args = Args(new FfmpegRunner.MediaPlan(Width: 4000));

        Assert.Contains($"scale={FfmpegRunner.MaxWidth}:-2", args);
    }

    [Fact]
    public void Media_ГифкаСобираетсяСПалитрой()
    {
        var args = Args(new FfmpegRunner.MediaPlan(Format: "gif"), "gif");

        // Без palettegen/paletteuse цвета в гифке получаются грязными
        Assert.Contains("palettegen", args);
        Assert.Contains("paletteuse", args);

        // Частота задаётся всегда: исходные 60 fps раздувают гифку
        Assert.Contains($"fps={FfmpegRunner.MaxFps}", args);
        Assert.Contains("-an", args);
    }

    [Fact]
    public void Media_ФорматБерётсяИзБелогоСписка()
    {
        Assert.Equal("gif", FfmpegRunner.ResolveFormat("gif", "clip.mp4"));
        Assert.Equal("jpg", FfmpegRunner.ResolveFormat("jpeg", "clip.mp4"));

        // Формат не указан — остаётся исходный
        Assert.Equal("mp4", FfmpegRunner.ResolveFormat(null, "clip.mp4"));

        // Всё, чего нет в списке, отбрасывается: имя формата уходит в аргументы
        Assert.Null(FfmpegRunner.ResolveFormat("exe", "clip.mp4"));
        Assert.Null(FfmpegRunner.ResolveFormat("../../etc/passwd", "clip.mp4"));
    }

    [Fact]
    public void Media_ПланРазбираетсяИзОтветаМодели()
    {
        var plan = ConvertMedia.ParsePlan("""
            ```json
            {"format":"gif","start":2,"end":6,"crop":{"x":10,"y":20,"w":300,"h":200},"width":480,"fps":15}
            ```
            """);

        Assert.NotNull(plan);
        Assert.Equal("gif", plan.Format);
        Assert.Equal(2, plan.Start);
        Assert.Equal(6, plan.End);
        Assert.Equal(480, plan.Width);
        Assert.Equal(15, plan.Fps);
        Assert.NotNull(plan.Crop);
        Assert.Equal(300, plan.Crop.Width);
    }

    [Fact]
    public void Media_ЧислаСтрокойТожеПонимаются()
    {
        var plan = ConvertMedia.ParsePlan("""{"start":"1.5","width":"320"}""");

        Assert.NotNull(plan);
        Assert.Equal(1.5, plan.Start);
        Assert.Equal(320, plan.Width);
    }

    [Fact]
    public void Media_ПустойИлиБитыйПланНеВыполняется()
    {
        Assert.True(ConvertMedia.ParsePlan("{}")!.IsEmpty);
        Assert.Null(ConvertMedia.ParsePlan("не понял, о чём речь"));
        Assert.Null(ConvertMedia.ParsePlan("{битый json"));
        Assert.False(new FfmpegRunner.MediaPlan(Format: "gif").IsEmpty);
    }

    [Fact]
    public void Media_РазмерыЧитаютсяИзОтветаFfprobe()
    {
        var info = FfmpegRunner.ParseProbe("""
            {"programs":[],"streams":[{"width":640,"height":480}],"format":{"duration":"12.500000"}}
            """);

        Assert.NotNull(info);
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
        Assert.Equal(12.5, info.DurationSeconds);

        // Видеопотока нет — значит это не то, с чем мы работаем
        Assert.Null(FfmpegRunner.ParseProbe("""{"streams":[]}"""));
    }
}
