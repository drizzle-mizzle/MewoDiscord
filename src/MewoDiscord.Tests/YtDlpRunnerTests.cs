using System.Text.Json;

using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты разбора ответа yt-dlp, выбора качества под лимит и сборки аргументов.
/// Сам yt-dlp не запускается и в сеть никто не ходит: проверяется то, что решает код.
/// </summary>
public class YtDlpRunnerTests
{
    /// <summary>
    /// Ролик на сто секунд: раздельные видео- и аудиопотоки, как их отдаёт YouTube,
    /// плюс один цельный поток на 360p.
    /// </summary>
    private const string Fixture =
        """
        {
          "id": "dQw4w9WgXcQ",
          "title": "Клип",
          "duration": 100,
          "is_live": false,
          "live_status": "not_live",
          "age_limit": 0,
          "formats": [
            {"format_id":"140","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","filesize":1600000,"protocol":"https"},
            {"format_id":"251","ext":"webm","vcodec":"none","acodec":"opus","filesize":1000000,"protocol":"https"},
            {"format_id":"137","ext":"mp4","vcodec":"avc1.640028","acodec":"none","height":1080,"filesize":40000000,"protocol":"https"},
            {"format_id":"248","ext":"webm","vcodec":"vp9","acodec":"none","height":1080,"filesize":30000000,"protocol":"https"},
            {"format_id":"136","ext":"mp4","vcodec":"avc1.4d401f","acodec":"none","height":720,"filesize":18000000,"protocol":"https"},
            {"format_id":"18","ext":"mp4","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"filesize":6000000,"protocol":"https"}
          ]
        }
        """;

    private const long Limit50Mb = 50L * 1024 * 1024;

    private static YtDlpRunner.VideoMeta Meta()
    {
        var meta = YtDlpRunner.ParseMeta(Fixture);
        Assert.NotNull(meta);

        return meta;
    }

    [Fact]
    public void Media_ФорматыРазбираютсяИзJsonYtDlp()
    {
        var meta = Meta();

        Assert.Equal("dQw4w9WgXcQ", meta.Id);
        Assert.Equal(100, meta.DurationSeconds);
        Assert.False(meta.IsLiveOrUpcoming);
        Assert.Equal(6, meta.Formats.Count);

        var audio = meta.Formats[0];
        Assert.False(audio.HasVideo);
        Assert.True(audio.HasAudio);

        var video = meta.Formats[2];
        Assert.True(video.HasVideo);
        Assert.False(video.HasAudio);
        Assert.Equal(1080, video.Height);

        // Битый ответ почти всегда означает, что yt-dlp пора обновить
        Assert.Null(YtDlpRunner.ParseMeta("не json"));
        Assert.Null(YtDlpRunner.ParseMeta("{}"));
    }

    [Fact]
    public void Media_РазмерОценивается()
    {
        using var document = JsonDocument.Parse(
            """
            [
              {"filesize":12345678},
              {"filesize_approx":7654321},
              {"tbr":1500},
              {"ext":"mp4"}
            ]
            """);

        var formats = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(12_345_678, YtDlpRunner.EstimateBytes(formats[0], 100));
        Assert.Equal(7_654_321, YtDlpRunner.EstimateBytes(formats[1], 100));

        // tbr — десятичные килобиты в секунду: 1500 кбит/с за 100 с
        Assert.Equal(18_750_000, YtDlpRunner.EstimateBytes(formats[2], 100));

        // Размер неизвестен — ноль, а не догадка
        Assert.Equal(0, YtDlpRunner.EstimateBytes(formats[3], 100));
        Assert.Equal(0, YtDlpRunner.EstimateBytes(formats[2], 0));
    }

    [Fact]
    public void Media_ЛестницаКачествСкладываетВидеоИЗвук()
    {
        var ladder = YtDlpRunner.BuildLadder(Meta());

        Assert.Equal(3, ladder.Count);
        Assert.Equal([1080, 720, 360], ladder.Select(r => r.Height));

        // На 1080 лежат avc1 (40 МБ) и vp9 (30 МБ) — берём лёгкий, плюс звук
        Assert.Equal(31_600_000, ladder[0].EstimatedBytes);
        Assert.Equal(19_600_000, ladder[1].EstimatedBytes);

        // Цельный поток скачивается как есть — звук в нём уже учтён
        Assert.Equal(6_000_000, ladder[2].EstimatedBytes);
    }

    [Theory]
    [InlineData(50, 1080, false, false)]
    [InlineData(25, 720, true, false)]
    [InlineData(5, 360, true, true)]
    public void Media_ВыборКачестваПодЛимит(int limitMb, int expectedHeight, bool reduced, bool overLimit)
    {
        var choice = YtDlpRunner.Choose(
            YtDlpRunner.BuildLadder(Meta()),
            limitMb * 1024L * 1024,
            hardCap: 2L * 1024 * 1024 * 1024);

        Assert.NotNull(choice);
        Assert.Equal(expectedHeight, choice.Height);
        Assert.Equal(1080, choice.BestHeight);
        Assert.Equal(reduced, choice.Reduced);
        Assert.Equal(overLimit, choice.OverLimit);
    }

    [Fact]
    public void Media_СлишкомТяжёлоеВидеоНеБерём()
    {
        // Ни одно качество не влезает в лимит, а минимальное тяжелее потолка —
        // отказ до всякого качания
        Assert.Null(YtDlpRunner.Choose(
            YtDlpRunner.BuildLadder(Meta()),
            1L * 1024 * 1024,
            hardCap: 5_000_000));

        // Тот же лимит, но потолок выше минимального качества — берёмся и пережимаем
        Assert.NotNull(YtDlpRunner.Choose(
            YtDlpRunner.BuildLadder(Meta()),
            1L * 1024 * 1024,
            hardCap: 2L * 1024 * 1024 * 1024));

        Assert.Null(YtDlpRunner.Choose([], Limit50Mb, hardCap: 2L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Media_ОтрезокПоднимаетДоступноеКачество()
    {
        // Качается десятая часть ролика, поэтому в тот же лимит влезает максимум,
        // а не 360p: это главный выигрыш от --download-sections
        var choice = YtDlpRunner.Choose(
            YtDlpRunner.BuildLadder(Meta()),
            5L * 1024 * 1024,
            hardCap: 2L * 1024 * 1024 * 1024,
            portion: 0.1);

        Assert.NotNull(choice);
        Assert.Equal(1080, choice.Height);
        Assert.False(choice.Reduced);
        Assert.Equal(3_160_000, choice.EstimatedBytes);
    }

    [Fact]
    public void Media_ЖивойЭфирУзнаётсяПоМетаданным()
    {
        var live = YtDlpRunner.ParseMeta("""{"id":"x","title":"t","duration":0,"is_live":true,"formats":[]}""");
        Assert.NotNull(live);
        Assert.True(live.IsLiveOrUpcoming);

        var upcoming = YtDlpRunner.ParseMeta("""{"id":"x","title":"t","live_status":"is_upcoming","formats":[]}""");
        Assert.NotNull(upcoming);
        Assert.True(upcoming.IsLiveOrUpcoming);
    }

    [Theory]
    [InlineData("ERROR: [youtube] x: Sign in to confirm you're not a bot. Use --cookies for authentication.", YtDlpRunner.YtDlpFailure.BotCheck)]
    [InlineData("ERROR: [youtube] x: Sign in to confirm your age. This video may be inappropriate for some users.", YtDlpRunner.YtDlpFailure.AgeRestricted)]
    [InlineData("ERROR: [youtube] x: Private video. Sign in if you've been granted access to this video", YtDlpRunner.YtDlpFailure.Private)]
    [InlineData("ERROR: [youtube] x: Video unavailable. This video is not available in your country", YtDlpRunner.YtDlpFailure.GeoBlocked)]
    [InlineData("ERROR: [youtube] x: This live event will begin in 3 hours.", YtDlpRunner.YtDlpFailure.Live)]
    [InlineData("ERROR: [youtube] x: Video unavailable. This video has been removed by the uploader", YtDlpRunner.YtDlpFailure.Unavailable)]
    [InlineData("ERROR: [youtube] x: Join this channel to get access to members-only content", YtDlpRunner.YtDlpFailure.Unavailable)]
    [InlineData("WARNING: [youtube] x: Signature solving failed: Some formats may be missing. Ensure you have a supported JavaScript runtime\nERROR: [youtube] x: The page needs to be reloaded.", YtDlpRunner.YtDlpFailure.JsRuntime)]
    [InlineData("ERROR: [youtube] x: The page needs to be reloaded.", YtDlpRunner.YtDlpFailure.JsRuntime)]
    [InlineData("WARNING: [youtube] x: n challenge solving failed: Some formats may be missing", YtDlpRunner.YtDlpFailure.JsRuntime)]
    [InlineData("WARNING: [youtube] x: nsig extraction failed: Some formats may be missing", YtDlpRunner.YtDlpFailure.Outdated)]
    [InlineData("ERROR: [youtube] x: Unable to extract yt initial data; please report this issue", YtDlpRunner.YtDlpFailure.Outdated)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", YtDlpRunner.YtDlpFailure.Failed)]
    [InlineData("", YtDlpRunner.YtDlpFailure.Failed)]
    public void Media_ОшибкаYtDlpКлассифицируется(string stderr, YtDlpRunner.YtDlpFailure expected)
    {
        Assert.Equal(expected, YtDlpRunner.Classify(stderr));
    }

    [Fact]
    public void Media_АргументыКачанияСобираютсяКодом()
    {
        var arguments = YtDlpRunner.BuildDownloadArguments(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            new YtDlpRunner.Choice(720, 1080, 19_600_000, true, false, false),
            "/work/source.%(ext)s",
            ffmpegDirectory: null,
            maxFileSizeBytes: 2_000_000_000,
            section: null);

        // Плейлист по ссылке на видео не должен утащить за собой пятьсот роликов
        Assert.Contains("--no-playlist", arguments);

        // Иначе yt-dlp прочитает /etc/yt-dlp.conf и молча переопределит наши аргументы
        Assert.Contains("--ignore-config", arguments);

        // Разделитель: после него аргумент не может быть принят за опцию
        Assert.Equal("--", arguments[^2]);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", arguments[^1]);

        // Имя файла строит код, а не заголовок видео
        Assert.Contains("/work/source.%(ext)s", arguments);

        // --print сам по себе включает симуляцию
        Assert.Contains("--no-simulate", arguments);

        // С .part недокачанный кусок носит чужое имя и не может уехать в чат
        Assert.DoesNotContain("--no-part", arguments);

        Assert.Contains("--max-filesize", arguments);
        Assert.Contains("2000000000", arguments);
        Assert.Contains(arguments, a => a.Contains("height<=720", StringComparison.Ordinal));
    }

    [Fact]
    public void Media_ОтрезокПревращаетсяВDownloadSections()
    {
        var shortCut = YtDlpRunner.BuildDownloadArguments(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            new YtDlpRunner.Choice(720, 720, 100, false, false, false),
            "/work/source.%(ext)s",
            null,
            2_000_000_000,
            (64, 100));

        Assert.Contains("--download-sections", shortCut);
        Assert.Contains("*64-100", shortCut);

        // Точный рез — это перекодирование вокруг границ: коротким отрезкам можно
        Assert.Contains("--force-keyframes-at-cuts", shortCut);

        var longCut = YtDlpRunner.BuildDownloadArguments(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            new YtDlpRunner.Choice(720, 720, 100, false, false, false),
            "/work/source.%(ext)s",
            null,
            2_000_000_000,
            (0, YtDlpRunner.MaxAccurateCutSeconds + 60));

        Assert.Contains("--download-sections", longCut);
        Assert.DoesNotContain("--force-keyframes-at-cuts", longCut);

        // У потока из манифеста отрезок вырезать нельзя — качаем целиком, режет ffmpeg
        var manifest = YtDlpRunner.BuildDownloadArguments(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            new YtDlpRunner.Choice(720, 720, 100, false, false, Manifest: true),
            "/work/source.%(ext)s",
            null,
            2_000_000_000,
            (64, 100));

        Assert.DoesNotContain("--download-sections", manifest);
    }

    [Fact]
    public void Media_ДополнительныеАргументыРазбиваютсяПоПробельным()
    {
        Assert.Equal(
            ["--extractor-args", "youtube:player_client=tv,web_safari"],
            YtDlpRunner.SplitExtraArgs("--extractor-args youtube:player_client=tv,web_safari"));

        // Многострочное значение ini склеивается через перевод строки: разбивать
        // только по пробелу значило бы отдать yt-dlp один битый аргумент
        Assert.Equal(
            ["--extractor-args", "youtube:player_client=tv", "--sleep-requests", "1"],
            YtDlpRunner.SplitExtraArgs("--extractor-args youtube:player_client=tv\n--sleep-requests 1"));

        Assert.Empty(YtDlpRunner.SplitExtraArgs(string.Empty));
        Assert.Empty(YtDlpRunner.SplitExtraArgs("   \n  "));
    }

    [Fact]
    public void Media_РазведкаНичегоНеКачает()
    {
        var arguments = YtDlpRunner.BuildProbeArguments("https://www.youtube.com/watch?v=dQw4w9WgXcQ");

        Assert.Contains("--skip-download", arguments);
        Assert.Contains("--dump-single-json", arguments);
        Assert.Contains("--no-playlist", arguments);
        Assert.Equal("--", arguments[^2]);
    }
}
