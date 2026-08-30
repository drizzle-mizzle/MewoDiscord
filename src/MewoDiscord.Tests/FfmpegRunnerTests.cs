using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты сборки аргументов ffmpeg и разбора плана от модели.
/// Сам ffmpeg не запускается: проверяется то, что решает код, а не внешняя программа.
/// </summary>
public class FfmpegRunnerTests
{
    private static readonly FfmpegRunner.MediaInfo Source = new(1920, 1080, 30);

    private static string Args(
        FfmpegRunner.MediaPlan plan,
        string format = "mp4",
        FfmpegRunner.MediaInfo? info = null,
        FfmpegRunner.MediaLimits? limits = null) =>
        string.Join(' ', FfmpegRunner.BuildArguments(plan, info ?? Source, format, "in.mp4", "out." + format, limits));

    [Fact]
    public void Media_ОбрезкаПоВремениСчитаетсяДлительностью()
    {
        // -t однозначен рядом с -ss, в отличие от -to, у которого смещается точка отсчёта
        var args = Args(new FfmpegRunner.MediaPlan(Start: 3, End: 7.5));

        Assert.Contains("-ss 3", args);
        Assert.Contains("-t 4.5", args);
    }

    [Fact]
    public void Media_ДлительностьГифкиРежетсяПоПотолку()
    {
        var args = Args(new FfmpegRunner.MediaPlan(Start: 0, End: 300), "gif");

        Assert.Contains($"-t {FfmpegRunner.MaxGifSeconds}", args);
        Assert.DoesNotContain("-t 300", args);
    }

    [Fact]
    public void Media_ДлинаСкачанногоВидеоНеРежется()
    {
        // Потолок гифки на скачанное видео не распространяется: «обрежь с 1:04 по 1:40» —
        // это 36 секунд, и молча вернуть пятнадцать было бы враньём
        var args = Args(
            new FfmpegRunner.MediaPlan(Start: 64, End: 100),
            "mp4",
            new FfmpegRunner.MediaInfo(1920, 1080, 600),
            FfmpegRunner.MediaLimits.Download("mp4", 600));

        Assert.Contains("-ss 64", args);
        Assert.Contains("-t 36", args);
    }

    [Fact]
    public void Media_КонвертацияДлинногоВидеоУсекаетсяПотолком()
    {
        // Потолок обработки клипа из чата — пять минут; восьмиминутное видео обрежется,
        // и об этом обязана появиться сноска (сам факт усечения проверяется отдельно)
        var args = Args(new FfmpegRunner.MediaPlan(), "mp4", new FfmpegRunner.MediaInfo(1920, 1080, 480));

        Assert.Contains("-t 300", args);
    }

    [Theory]
    [InlineData(0.04, "png")]   // ffprobe отдаёт для одиночного кадра сороковую долю секунды
    [InlineData(0, "png")]
    [InlineData(30, "mp4")]
    public void Media_ФорматПоУмолчаниюЗависитОтНеподвижности(double duration, string expected)
    {
        // Фото в формате вне белого списка (.heic с телефона) обязано стать картинкой,
        // а не одно-кадровым видео
        var format = FfmpegRunner.ResolveFormat(null, "photo.heic", duration >= FfmpegRunner.MaxStillSeconds);

        Assert.Equal(expected, format);
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
    public void Media_ШиринаГифкиНеПревышаетПотолок()
    {
        var args = Args(new FfmpegRunner.MediaPlan(Width: 4000), "gif");

        Assert.Contains($"scale={FfmpegRunner.MaxGifWidth}:-2", args);
    }

    [Fact]
    public void Media_ГифкаСобираетсяСПалитрой()
    {
        var args = Args(new FfmpegRunner.MediaPlan(Format: "gif"), "gif");

        // Без palettegen/paletteuse цвета в гифке получаются грязными
        Assert.Contains("palettegen", args);
        Assert.Contains("paletteuse", args);

        // Частота задаётся всегда: исходные 60 fps раздувают гифку
        Assert.Contains($"fps={FfmpegRunner.MaxGifFps}", args);
        Assert.Contains("-an", args);
    }

    [Fact]
    public void Media_ЧастотаКадровИдётПередМасштабированием()
    {
        // Иначе scale обрабатывает все шестьдесят кадров в секунду вместо пятнадцати
        var args = Args(new FfmpegRunner.MediaPlan(Width: 320, Fps: 15));

        var fps = args.IndexOf("fps=", StringComparison.Ordinal);
        var scale = args.IndexOf("scale=", StringComparison.Ordinal);

        Assert.True(fps >= 0 && scale > fps, $"fps должен идти перед scale: {args}");
    }

    [Fact]
    public void Media_ЦветовоеПространствоПриводитсяКYuv420p()
    {
        // Десятибитный исходник с YouTube иначе даёт файл, который Discord показывает
        // чёрным прямоугольником
        Assert.Contains("-pix_fmt yuv420p", Args(new FfmpegRunner.MediaPlan(Width: 640)));
        Assert.Contains("+faststart", Args(new FfmpegRunner.MediaPlan(Width: 640)));
    }

    [Fact]
    public void Media_ЗвуковаяДорожкаВырезаетсяБезВидео()
    {
        var args = Args(new FfmpegRunner.MediaPlan(AudioOnly: true, Format: "mp3"), "mp3");

        Assert.Contains("-vn", args);
        Assert.Contains("-map 0:a:0", args);
        Assert.Contains("libmp3lame", args);
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void Media_РодныйКонтейнерЗвукаКопируетсяБезПерекодирования()
    {
        var withAac = new FfmpegRunner.MediaInfo(
            0,
            0,
            30,
            Audio: new FfmpegRunner.AudioStreamInfo("aac", 2, 44100, 128_000));

        Assert.Equal("m4a", FfmpegRunner.ResolveAudioFormat(null, withAac.Audio));
        Assert.Contains("-c:a copy", Args(new FfmpegRunner.MediaPlan(AudioOnly: true), "m4a", withAac));

        // Формат вне белого списка не проходит: имя уходит в аргументы и в имя файла
        Assert.Null(FfmpegRunner.ResolveAudioFormat("wav", withAac.Audio));
    }

    [Fact]
    public void Media_ПережатиеБезИзмененийКопируетПотоки()
    {
        var args = string.Join(
            ' ',
            FfmpegRunner.BuildEncodeArguments(
                new FfmpegRunner.EncodeSettings(CopyStreams: true),
                new FfmpegRunner.MediaPlan(),
                Source,
                "mp4",
                "in.mp4",
                "out.mp4"));

        Assert.Contains("-c copy", args);
        Assert.DoesNotContain("libx264", args);
    }

    [Fact]
    public void Media_ПережатиеВидеоЦелитсяВБитрейт()
    {
        var args = string.Join(
            ' ',
            FfmpegRunner.BuildEncodeArguments(
                new FfmpegRunner.EncodeSettings(1_500_000, 96_000, 1280, 720, 30),
                new FfmpegRunner.MediaPlan(),
                Source,
                "mp4",
                "in.mp4",
                "out.mp4"));

        Assert.Contains("-b:v 1500000", args);
        Assert.Contains("-b:a 96000", args);
        Assert.Contains("scale=1280:720", args);
        Assert.Contains("fps=30", args);

        // Круг сжатия всегда читает исходник, а не результат прошлого круга
        Assert.Contains("-i in.mp4", args);
    }

    [Fact]
    public void Media_ФорматБерётсяИзБелогоСписка()
    {
        Assert.Equal("gif", FfmpegRunner.ResolveFormat("gif", "clip.mp4", animated: true));
        Assert.Equal("jpg", FfmpegRunner.ResolveFormat("jpeg", "clip.mp4", animated: true));

        // Формат не указан — остаётся исходный
        Assert.Equal("mp4", FfmpegRunner.ResolveFormat(null, "clip.mp4", animated: true));
        Assert.Equal("webp", FfmpegRunner.ResolveFormat(null, "pic.webp", animated: false));

        // Исходный контейнер вне списка — отказывать в обрезке из-за этого глупо,
        // берём разумный по умолчанию
        Assert.Equal("mp4", FfmpegRunner.ResolveFormat(null, "clip.mov", animated: true));
        Assert.Equal("mp4", FfmpegRunner.ResolveFormat(null, "clip.mkv", animated: true));
        Assert.Equal("png", FfmpegRunner.ResolveFormat(null, "photo.heic", animated: false));

        // Запрошенный явно формат обязан быть в списке: имя уходит в аргументы
        Assert.Null(FfmpegRunner.ResolveFormat("exe", "clip.mp4", animated: true));
        Assert.Null(FfmpegRunner.ResolveFormat("../../etc/passwd", "clip.mp4", animated: true));
    }

    [Fact]
    public void Media_ПланРазбираетсяИзОтветаМодели()
    {
        var plan = MediaPlanParser.Parse("""
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
        var plan = MediaPlanParser.Parse("""{"start":"1.5","width":"320"}""");

        Assert.NotNull(plan);
        Assert.Equal(1.5, plan.Start);
        Assert.Equal(320, plan.Width);
    }

    [Fact]
    public void Media_ЗвуковойПланУзнаётся()
    {
        Assert.True(MediaPlanParser.Parse("""{"audio":true}""")!.AudioOnly);
        Assert.True(MediaPlanParser.Parse("""{"audio":"true"}""")!.AudioOnly);
        Assert.False(MediaPlanParser.Parse("""{"audio":false}""")!.AudioOnly);
        Assert.False(MediaPlanParser.Parse("{}")!.AudioOnly);
    }

    [Fact]
    public void Media_ТолькоОбрезкаОтличаетсяОтОстальныхПланов()
    {
        // Такой план умеет выполнить сам yt-dlp, скачав один отрезок
        Assert.True(new FfmpegRunner.MediaPlan(Start: 5, End: 10).IsTrimOnly);
        Assert.False(new FfmpegRunner.MediaPlan(Start: 5, End: 10, Format: "gif").IsTrimOnly);
        Assert.False(new FfmpegRunner.MediaPlan(Start: 5, AudioOnly: true).IsTrimOnly);
        Assert.False(new FfmpegRunner.MediaPlan().IsTrimOnly);
    }

    [Fact]
    public void Media_ПустойИлиБитыйПланНеВыполняется()
    {
        Assert.True(MediaPlanParser.Parse("{}")!.IsEmpty);
        Assert.Null(MediaPlanParser.Parse("не понял, о чём речь"));
        Assert.Null(MediaPlanParser.Parse("{битый json"));
        Assert.False(new FfmpegRunner.MediaPlan(Format: "gif").IsEmpty);
        Assert.False(new FfmpegRunner.MediaPlan(AudioOnly: true).IsEmpty);
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

        // Ни видео, ни звука — значит это не то, с чем мы работаем
        Assert.Null(FfmpegRunner.ParseProbe("""{"streams":[]}"""));
    }

    [Fact]
    public void Media_ПотокиЧитаютсяИзОтветаFfprobe()
    {
        var info = FfmpegRunner.ParseProbe("""
            {"streams":[
              {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,
               "avg_frame_rate":"30000/1001","bit_rate":"4500000"},
              {"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"44100","bit_rate":"128000"}],
             "format":{"duration":"61.5","bit_rate":"4628000","format_name":"mov,mp4,m4a","size":"35000000"}}
            """);

        Assert.NotNull(info);
        Assert.NotNull(info.Video);
        Assert.Equal("h264", info.Video.Codec);
        Assert.Equal(4_500_000, info.Video.BitrateBps);

        // Частота приезжает рациональной строкой
        Assert.Equal(29.97, info.Video.Fps, 2);

        Assert.NotNull(info.Audio);
        Assert.Equal("aac", info.Audio.Codec);
        Assert.Equal(2, info.Audio.Channels);
        Assert.Equal(128_000, info.Audio.BitrateBps);
        Assert.Equal(35_000_000, info.SizeBytes);
    }

    [Fact]
    public void Media_ОбложкаЗвуковогоФайлаНеСчитаетсяВидео()
    {
        // У mp3 с обложкой ffprobe показывает видеопоток, но кадров в нём нет
        var info = FfmpegRunner.ParseProbe("""
            {"streams":[
              {"codec_type":"video","codec_name":"mjpeg","width":600,"height":600,"avg_frame_rate":"0/0"},
              {"codec_type":"audio","codec_name":"mp3","channels":2,"sample_rate":"44100","bit_rate":"192000"}],
             "format":{"duration":"180.0","format_name":"mp3","size":"4320000"}}
            """);

        Assert.NotNull(info);
        Assert.Null(info.Video);
        Assert.NotNull(info.Audio);
        Assert.Equal(0, info.Width);
    }

    [Fact]
    public void Media_БитрейтВидеоДостаётсяИзКонтейнераЕслиЕгоНетУПотока()
    {
        var info = FfmpegRunner.ParseProbe("""
            {"streams":[
              {"codec_type":"video","codec_name":"vp9","width":1280,"height":720,"avg_frame_rate":"30/1"},
              {"codec_type":"audio","codec_name":"opus","channels":2,"sample_rate":"48000","bit_rate":"128000"}],
             "format":{"duration":"100.0","format_name":"matroska,webm","size":"12500000"}}
            """);

        Assert.NotNull(info);
        Assert.NotNull(info.Video);

        // 12 500 000 байт за 100 секунд — это миллион бит/с всего, минус звук
        Assert.Equal(872_000, info.Video.BitrateBps);
    }
}
