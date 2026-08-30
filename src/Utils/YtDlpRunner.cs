using System.Globalization;
using System.Text.Json;

using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Разведка и скачивание видео через yt-dlp. Адрес строится кодом из проверенного
/// идентификатора (<see cref="YoutubeLinks.WatchUrl"/>) — исходная строка пользователя
/// в аргументы не попадает; всё остальное собирается здесь из чисел и заготовленных
/// строк.
/// Разведка отвечает на главный вопрос до траты байтов: в каком качестве видео влезет
/// в лимит вложения и стоит ли вообще начинать.
/// </summary>
public static class YtDlpRunner
{
    /// <summary>
    /// Запас между оценкой размера и фактом: точного размера yt-dlp часто не знает,
    /// а к файлу ещё добавляются накладные расходы контейнера после склейки.
    /// </summary>
    internal const double FitSafetyFactor = 0.93;

    /// <summary>
    /// До какой длины отрезка просим yt-dlp резать точно по кадру: точный рез — это
    /// перекодирование вокруг границ, и на сорокаминутном куске оно стоит сорок минут.
    /// </summary>
    internal const double MaxAccurateCutSeconds = 300;

    /// <summary>
    /// Сколько символов вывода yt-dlp писать в лог при отказе. Интересен всегда конец,
    /// но не одна последняя строка: причина часто в предупреждении перед ней.
    /// </summary>
    private const int TailLength = 800;

    private const int ProbeTimeoutSeconds = 90;

    private const int VersionTimeoutSeconds = 15;

    /// <summary>
    /// Как часто сторож качания смотрит на рабочий каталог.
    /// </summary>
    private const int WatchdogIntervalSeconds = 3;

    /// <summary>
    /// Сколько секунд без прироста считать зависанием. Плоского таймаута мало в обе
    /// стороны: законные два гигабайта по скромному каналу — это двадцать минут,
    /// а зависшее качание не растёт вовсе.
    /// </summary>
    private const int StallSeconds = 120;

    /// <summary>
    /// Имя, под которым сохраняется исходник. Из заголовка видео имя файла не строим:
    /// там бывают юникод, слэши и двести символов.
    /// </summary>
    private const string SourceStem = "source";

    /// <summary>
    /// Причина отказа. Всё это приезжает только текстом в stderr, поэтому его
    /// приходится разбирать.
    /// </summary>
    public enum YtDlpFailure
    {
        None,
        Live,
        AgeRestricted,
        BotCheck,
        Private,
        GeoBlocked,
        Unavailable,
        Outdated,

        /// <summary>
        /// В системе нет JavaScript-рантайма, которым yt-dlp считает подпись YouTube.
        /// </summary>
        JsRuntime,

        /// <summary>
        /// Файл перерос потолок прямо во время качания: размеров потоков yt-dlp
        /// заранее не знал, и оценка ступени оказалась заниженной.
        /// </summary>
        TooBig,

        /// <summary>
        /// На диске кончилось место — качание прервал сторож.
        /// </summary>
        NoRoom,
        Failed
    }

    public record FormatInfo(
        string? Extension,
        string? VideoCodec,
        string? AudioCodec,
        int Height,
        long Bytes,
        bool SizeKnown,
        string? Protocol)
    {
        public bool HasVideo => VideoCodec is not (null or "none");

        public bool HasAudio => AudioCodec is not (null or "none");

        /// <summary>
        /// Поток из манифеста: у него нет размера, и отрезком его скачать нельзя.
        /// </summary>
        public bool IsManifest => Protocol?.StartsWith("m3u8", StringComparison.OrdinalIgnoreCase) == true;
    }

    public record VideoMeta(
        string Id,
        string Title,
        double DurationSeconds,
        bool IsLive,
        string? LiveStatus,
        int AgeLimit,
        IReadOnlyList<FormatInfo> Formats)
    {
        /// <summary>
        /// Эфир, идущий или ещё не начавшийся. Качать такое нельзя: скачивание
        /// не закончится никогда и будет расти, пока не упрётся в сторожа.
        /// </summary>
        public bool IsLiveOrUpcoming => IsLive || LiveStatus is "is_live" or "is_upcoming";
    }

    /// <summary>
    /// Ступень лестницы качеств: высота кадра и оценка веса в байтах.
    /// </summary>
    public record Rung(int Height, long EstimatedBytes, bool SizeKnown, bool Manifest);

    /// <summary>
    /// Выбранное качество. Reduced — взяли не максимум из доступных, это повод
    /// сказать пользователю. OverLimit — не влезает даже так, дальше пережимаем.
    /// </summary>
    public record Choice(int Height, int BestHeight, long EstimatedBytes, bool Reduced, bool OverLimit, bool Manifest);

    /// <summary>
    /// Спрашивает у YouTube метаданные и список форматов, ничего не скачивая.
    /// </summary>
    public static async Task<(VideoMeta? Meta, YtDlpFailure Failure)> ProbeAsync(string videoId)
    {
        if (!YoutubeLinks.IsValidVideoId(videoId))
        {
            return (null, YtDlpFailure.Failed);
        }

        try
        {
            var arguments = BuildProbeArguments(YoutubeLinks.WatchUrl(videoId));

            var result = await ProcessRunner.RunAsync(
                AppConfig.MediaSettings.YtDlpPath,
                arguments,
                TimeSpan.FromSeconds(ProbeTimeoutSeconds));

            if (!result.Ok)
            {
                var failure = result.TimedOut ? YtDlpFailure.Failed : Classify(result.StandardError);
                LogFailure("разведка", videoId, failure, arguments, result.StandardError);

                return (null, failure);
            }

            var meta = ParseMeta(result.StandardOutput);

            return meta == null ? (null, YtDlpFailure.Outdated) : (meta, YtDlpFailure.None);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось запустить yt-dlp: {Message}", ex.Message);
            return (null, YtDlpFailure.Failed);
        }
    }

    /// <summary>
    /// Качает выбранное качество в рабочий каталог и возвращает путь к готовому файлу.
    /// Отрезок, если он задан, качается отрезком: для YouTube это запрос нужного куска,
    /// а не всего видео.
    /// </summary>
    public static async Task<(string? Path, YtDlpFailure Failure)> DownloadAsync(
        string videoId,
        Choice choice,
        MediaWorkspace workspace,
        (double Start, double End)? section,
        long hardCapBytes)
    {
        if (!YoutubeLinks.IsValidVideoId(videoId))
        {
            return (null, YtDlpFailure.Failed);
        }

        try
        {
            var arguments = BuildDownloadArguments(
                YoutubeLinks.WatchUrl(videoId),
                choice,
                workspace.PathFor(SourceStem + ".%(ext)s"),
                FfmpegDirectory(),
                hardCapBytes,
                section);

            // Сторож убивает процесс по трём разным причинам, а наружу это приезжает
            // одинаковым «прервано» — вердикт нужен, чтобы назвать настоящую
            YtDlpFailure? watchdogVerdict = null;

            var result = await ProcessRunner.RunAsync(
                AppConfig.MediaSettings.YtDlpPath,
                arguments,
                TimeSpan.FromMinutes(AppConfig.MediaSettings.DownloadTimeoutMinutes),
                workspace.FullPath,
                token => WatchAsync(workspace, hardCapBytes, verdict => watchdogVerdict = verdict, token));

            if (!result.Ok)
            {
                var failure = result.TimedOut
                    ? watchdogVerdict ?? YtDlpFailure.Failed
                    : Classify(result.StandardError);
                LogFailure("качание", videoId, failure, arguments, result.StandardError);

                return (null, failure);
            }

            var path = LocateResult(workspace, result.StandardOutput);

            return path == null ? (null, YtDlpFailure.Failed) : (path, YtDlpFailure.None);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка скачивания видео: {Message}", ex.Message);
            return (null, YtDlpFailure.Failed);
        }
    }

    /// <summary>
    /// Версия yt-dlp для лога при старте. YouTube ломает его примерно раз в месяц,
    /// и на вопрос «почему перестало работать» должен быть ответ в журнале.
    /// </summary>
    public static async Task<string?> VersionAsync()
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                AppConfig.MediaSettings.YtDlpPath,
                ["--version"],
                TimeSpan.FromSeconds(VersionTimeoutSeconds));

            return result.Ok ? result.StandardOutput.Trim() : null;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("yt-dlp не отвечает: {Message}", ex.Message);
            return null;
        }
    }

    #region Internals

    internal static List<string> BuildProbeArguments(string url)
    {
        var arguments = new List<string>
        {
            "--dump-single-json",
            "--skip-download",
            "--no-playlist",

            // --no-warnings здесь нет намеренно: причина отказа у yt-dlp часто живёт
            // в предупреждении, а не в финальной строке ошибки
            "--no-progress",

            // Без этого yt-dlp читает /etc/yt-dlp.conf и молча переопределяет
            // всё, что мы тут аккуратно собрали
            "--ignore-config",
            "--socket-timeout", "15",
            "--retries", "3"
        };

        AppendCommonOptions(arguments);
        arguments.AddRange(["--", url]);

        return arguments;
    }

    internal static List<string> BuildDownloadArguments(
        string url,
        Choice choice,
        string outputTemplate,
        string? ffmpegDirectory,
        long maxFileSizeBytes,
        (double Start, double End)? section)
    {
        var arguments = new List<string>
        {
            "--no-playlist",

            // Предупреждения не глушим — см. комментарий в разведке
            "--no-progress",
            "--ignore-config",
            "--no-mtime",
            "--retries", "5",
            "--fragment-retries", "5",
            "--socket-timeout", "20",
            "--concurrent-fragments", "4",
            "--max-filesize", maxFileSizeBytes.ToString(CultureInfo.InvariantCulture),
            "-f", BuildSelector(choice.Height),
            "-S", "res,codec:h264:aac,ext:mp4:m4a,br",
            "--merge-output-format", "mp4",
            "-o", outputTemplate,

            // --print сам по себе включает симуляцию: без --no-simulate ничего не скачается
            "--no-simulate",
            "--print", "after_move:filepath"
        };

        if (ffmpegDirectory != null)
        {
            arguments.AddRange(["--ffmpeg-location", ffmpegDirectory]);
        }

        // Отрезок из манифеста вырезать нельзя — такое видео качаем целиком,
        // а режет уже ffmpeg
        if (section != null && !choice.Manifest)
        {
            arguments.AddRange(["--download-sections", FormatSection(section.Value)]);

            if (section.Value.End - section.Value.Start <= MaxAccurateCutSeconds)
            {
                arguments.Add("--force-keyframes-at-cuts");
            }
        }

        AppendCommonOptions(arguments);
        arguments.AddRange(["--", url]);

        return arguments;
    }

    /// <summary>
    /// Селектор качества по высоте кадра, а не закреплённый format_id: идентификаторы
    /// переезжают между разведкой и качанием под опытами YouTube, а «запрошенный формат
    /// недоступен» — исход хуже погрешности в оценке размера.
    /// Порядок предпочтений: H.264 с AAC (их играет любой клиент Discord), затем любая
    /// пара на той же высоте, затем цельный поток, и уже в последнюю очередь — что есть.
    /// </summary>
    internal static string BuildSelector(int height) =>
        $"bv*[height<={height}][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<={height}]+ba/"
        + $"b[height<={height}]/wv*+ba/w";

    internal static string FormatSection((double Start, double End) section) =>
        "*" + Number(section.Start) + "-" + Number(section.End);

    private static void AppendCommonOptions(List<string> arguments)
    {
        var cookies = AppConfig.MediaSettings.YoutubeCookiesFile;

        if (cookies.Length > 0)
        {
            arguments.AddRange(["--cookies", cookies]);
        }

        arguments.AddRange(SplitExtraArgs(AppConfig.MediaSettings.YtDlpExtraArgs));
    }

    /// <summary>
    /// Разбивает строку дополнительных аргументов из конфига. Разделитель — любой
    /// пробельный символ: многострочное значение ini склеивается через перевод строки.
    /// Кавычки не снимаются: аргументы уходят списком, а не строкой в оболочку.
    /// </summary>
    internal static string[] SplitExtraArgs(string value) =>
        value.Split(
            [' ', '\t', '\n', '\r'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// yt-dlp зовёт ffmpeg сам, чтобы склеить раздельные видео- и аудиопотоки YouTube.
    /// Если путь к ffmpeg задан в конфиге, показываем каталог, где его искать.
    /// </summary>
    private static string? FfmpegDirectory()
    {
        var path = AppConfig.FfmpegPath;

        return path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetDirectoryName(path)
            : null;
    }

    /// <summary>
    /// Разбирает ответ --dump-single-json. null — ответ не разобрался, а это почти всегда
    /// означает, что yt-dlp пора обновить.
    /// </summary>
    internal static VideoMeta? ParseMeta(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out _))
            {
                return null;
            }

            var duration = JsonRead.Number(root, "duration");
            var formats = new List<FormatInfo>();

            if (root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var format in list.EnumerateArray())
                {
                    var bytes = EstimateBytes(format, duration);

                    formats.Add(new FormatInfo(
                        JsonRead.Text(format, "ext"),
                        JsonRead.Text(format, "vcodec"),
                        JsonRead.Text(format, "acodec"),
                        (int)JsonRead.Number(format, "height"),
                        bytes,
                        bytes > 0,
                        JsonRead.Text(format, "protocol")));
                }
            }

            return new VideoMeta(
                JsonRead.Text(root, "id") ?? string.Empty,
                JsonRead.Text(root, "title") ?? string.Empty,
                duration,
                root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True,
                JsonRead.Text(root, "live_status"),
                (int)JsonRead.Number(root, "age_limit"),
                formats);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Вес потока. Точного размера yt-dlp часто не знает, тогда считаем из битрейта:
    /// tbr — это десятичные килобиты в секунду, то есть тысяча бит, а не 1024.
    /// Ноль означает «размер неизвестен» и в проверке «влезает ли» считается
    /// бесконечностью: утверждать, что неизвестное влезет, нельзя.
    /// </summary>
    internal static long EstimateBytes(JsonElement format, double durationSeconds)
    {
        var exact = JsonRead.Number(format, "filesize");

        if (exact > 0)
        {
            return (long)exact;
        }

        var approximate = JsonRead.Number(format, "filesize_approx");

        if (approximate > 0)
        {
            return (long)approximate;
        }

        var bitrate = JsonRead.Number(format, "tbr");

        return bitrate > 0 && durationSeconds > 0 ? (long)(bitrate * 1000 / 8 * durationSeconds) : 0;
    }

    /// <summary>
    /// Лестница качеств: по ступени на каждую доступную высоту кадра, вес — вместе
    /// со звуком. У YouTube видео и звук лежат раздельно и склеиваются при скачивании,
    /// поэтому складывать их обязательно, иначе оценка систематически занижена.
    /// </summary>
    internal static IReadOnlyList<Rung> BuildLadder(VideoMeta meta)
    {
        var audioOnly = meta.Formats.Where(f => !f.HasVideo && f.HasAudio).ToList();

        // Селектор предпочитает m4a с AAC, поэтому и оцениваем по нему:
        // иначе считали бы по opus, который заметно легче
        var audio = audioOnly.Where(f => f.Extension == "m4a").MaxBy(f => f.Bytes)
            ?? audioOnly.MaxBy(f => f.Bytes);

        var audioBytes = audio?.Bytes ?? 0;
        var audioKnown = audio?.SizeKnown ?? false;
        var rungs = new List<Rung>();

        foreach (var height in meta.Formats.Where(f => f.HasVideo && f.Height > 0).Select(f => f.Height).Distinct())
        {
            var candidates = new List<Rung>();

            foreach (var format in meta.Formats.Where(f => f.HasVideo && f.Height == height))
            {
                // Цельный поток скачивается как есть — звук в нём уже учтён
                var bytes = format.HasAudio ? format.Bytes : format.Bytes + audioBytes;
                var known = format.SizeKnown && (format.HasAudio || audioKnown);

                candidates.Add(new Rung(height, bytes, known, format.IsManifest));
            }

            // На одной высоте лежат avc1, vp9 и av1: берём самый лёгкий известный,
            // а если известных нет — любой
            var best = candidates.Where(r => r.SizeKnown).MinBy(r => r.EstimatedBytes) ?? candidates[0];

            rungs.Add(best);
        }

        return rungs.OrderByDescending(r => r.Height).ToList();
    }

    /// <summary>
    /// Выбирает качество под лимит вложения. portion — какая доля видео нужна:
    /// у обрезки качается только отрезок, и оценка масштабируется вместе с ним.
    /// Ступени из манифеста — исключение: отрезок им не вырезать, они всегда качаются
    /// целиком, и заниженная оценка обманула бы и проверку места, и выбор качества.
    /// null — не влезает даже минимальное качество, и оно заведомо больше потолка.
    /// </summary>
    internal static Choice? Choose(IReadOnlyList<Rung> ladder, long uploadLimit, long hardCap, double portion = 1)
    {
        if (ladder.Count == 0)
        {
            return null;
        }

        var scale = Math.Clamp(portion, 0, 1);
        var target = (long)(uploadLimit * FitSafetyFactor);
        var best = ladder[0];

        foreach (var rung in ladder)
        {
            var bytes = Estimate(rung, scale);

            if (rung.SizeKnown && bytes <= target)
            {
                return new Choice(rung.Height, best.Height, bytes, rung.Height != best.Height, false, rung.Manifest);
            }
        }

        var minimal = ladder[^1];
        var minimalBytes = Estimate(minimal, scale);

        // Неизвестный размер не повод отказывать заранее: от разрастания защищают
        // --max-filesize и сторож качания
        if (minimal.SizeKnown && minimalBytes > hardCap)
        {
            return null;
        }

        return new Choice(minimal.Height, best.Height, minimalBytes, true, true, minimal.Manifest);
    }

    /// <summary>
    /// Сколько байт ждать от ступени с учётом доли, которая нужна. Манифест качается
    /// целиком независимо от отрезка — доля к нему не применяется.
    /// </summary>
    private static long Estimate(Rung rung, double scale) =>
        rung.Manifest ? rung.EstimatedBytes : (long)(rung.EstimatedBytes * scale);

    /// <summary>
    /// Превращает текст ошибки yt-dlp в причину отказа.
    /// </summary>
    internal static YtDlpFailure Classify(string stderr)
    {
        var text = stderr.ToLowerInvariant();

        // Именно «not a bot», а не «sign in to confirm»: с того же оборота начинается
        // и проверка возраста, и она уехала бы сюда
        if (Has(text, "not a bot"))
        {
            return YtDlpFailure.BotCheck;
        }

        if (Has(text, "confirm your age", "age-restricted", "inappropriate for some users", "age verification"))
        {
            return YtDlpFailure.AgeRestricted;
        }

        if (Has(text, "private video", "is private"))
        {
            return YtDlpFailure.Private;
        }

        if (Has(text, "in your country", "geo restrict", "not available from your location"))
        {
            return YtDlpFailure.GeoBlocked;
        }

        // Обороты целиком, а не «is live»: то совпало бы с «th-is live-stream»
        if (Has(text, "live event will begin", "premieres in", "live stream recording is not available"))
        {
            return YtDlpFailure.Live;
        }

        if (Has(text, "video unavailable", "has been removed", "does not exist", "members-only", "no longer available"))
        {
            return YtDlpFailure.Unavailable;
        }

        // Нет движка, которым считается подпись YouTube. Проверяется до Outdated:
        // «не смог разобрать» тут следствие, а не причина, и совет обновить yt-dlp
        // увёл бы в сторону от настоящего лечения — поставить рантайм
        if (Has(text, "signature solving failed", "challenge solving failed",
            "the page needs to be reloaded", "javascript runtime"))
        {
            return YtDlpFailure.JsRuntime;
        }

        // Проверяется последним: «не смог разобрать» бывает и следствием причин выше,
        // и только само по себе означает, что пора обновляться
        if (Has(text, "nsig extraction failed", "please report this issue", "unable to extract", "signature extraction"))
        {
            return YtDlpFailure.Outdated;
        }

        return YtDlpFailure.Failed;
    }

    private static bool Has(string text, params string[] markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));

    /// <summary>
    /// Находит скачанный файл. Сначала верим тому, что yt-dlp сам назвал итоговым путём,
    /// потом ищем по фиксированному имени. Огрызки (.part, .ytdl) отбрасываются:
    /// обрезанное видео не должно уехать в чат ни при каких обстоятельствах.
    /// </summary>
    private static string? LocateResult(MediaWorkspace workspace, string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (IsInsideWorkspace(workspace, line) && File.Exists(line) && !IsLeftover(line))
            {
                return line;
            }
        }

        var found = Directory.GetFiles(workspace.FullPath, SourceStem + ".*")
            .Where(path => !IsLeftover(path))
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();

        if (found == null)
        {
            BotLogger.Warning("yt-dlp отработал, но файла в {Directory} нет", workspace.FullPath);
        }

        return found;
    }

    private static bool IsInsideWorkspace(MediaWorkspace workspace, string path)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(workspace.FullPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Недокачанный кусок или служебный файл. С .part (это поведение по умолчанию,
    /// поэтому --no-part мы не передаём) убитое качание оставляет файл с чужим именем,
    /// и отличить его от готового можно по расширению.
    /// </summary>
    private static bool IsLeftover(string path)
    {
        var name = Path.GetFileName(path);

        return name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Сторож качания. --max-filesize — оговорка, а не гарантия: он не работает,
    /// когда размер потока неизвестен, и применяется к потокам по отдельности.
    /// Настоящий потолок держит этот цикл.
    /// </summary>
    private static async Task WatchAsync(
        MediaWorkspace workspace,
        long hardCapBytes,
        Action<YtDlpFailure> report,
        CancellationToken token)
    {
        var lastSize = -1L;
        var stagnant = 0;

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(WatchdogIntervalSeconds), token);

            var size = workspace.UsedBytes;

            if (size > hardCapBytes)
            {
                BotLogger.Warning("Качание переросло потолок ({Size} байт) — прерываю", size);
                report(YtDlpFailure.TooBig);
                return;
            }

            if (!workspace.HasRoomFor(0))
            {
                BotLogger.Warning("Место на диске кончилось — прерываю качание");
                report(YtDlpFailure.NoRoom);
                return;
            }

            if (size == lastSize)
            {
                stagnant += WatchdogIntervalSeconds;

                if (stagnant >= StallSeconds)
                {
                    BotLogger.Warning("Качание не растёт {Seconds} с — считаю зависшим", stagnant);
                    report(YtDlpFailure.Failed);
                    return;
                }
            }
            else
            {
                stagnant = 0;
                lastSize = size;
            }
        }
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Пишет в лог отказ вместе со списком аргументов. Аргументы обязательны: без них
    /// по одному тексту ошибки не отличить «yt-dlp не может» от «мы попросили не то»,
    /// а разница между разведкой из бота и той же ссылкой из консоли — как раз в них.
    /// Секретов здесь нет: адрес собран из идентификатора видео, остальное — конфиг,
    /// который писал администратор.
    /// </summary>
    private static void LogFailure(
        string stage,
        string videoId,
        YtDlpFailure failure,
        IReadOnlyList<string> arguments,
        string stderr) =>
        BotLogger.Warning(
            "yt-dlp: {Stage} {Id} не удалась ({Failure})\nАргументы: {Arguments}\nВывод: {Error}",
            stage,
            videoId,
            failure,
            string.Join(' ', arguments),
            Tail(stderr));

    /// <summary>
    /// Хвост вывода для лога: yt-dlp бывает многословен, а интересен всегда конец.
    /// </summary>
    private static string Tail(string text)
    {
        var trimmed = text.Trim();

        return trimmed.Length <= TailLength ? trimmed : trimmed[^TailLength..];
    }

    #endregion
}
