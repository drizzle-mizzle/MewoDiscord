using System.Globalization;
using System.Text.Json;

using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Запуск ffmpeg над файлом. Модель сюда не пишет ни строчки командной строки:
/// она отдаёт типизированный план (<see cref="MediaPlan"/>), а аргументы собирает код
/// из белого списка операций — иначе это было бы выполнение произвольных команд из вывода ИИ.
/// Операции нарочно простые: формат, обрезка по времени, кроп, размер, частота кадров,
/// вытаскивание звуковой дорожки.
/// Работа идёт по путям, а не по байтам: скачанное видео бывает на два гигабайта,
/// и держать его в памяти нельзя. Рабочий каталог приезжает
/// <see cref="MediaWorkspace"/> — он же доказывает, что вызывающий занял слот:
/// свободно плодить ffmpeg-процессы на маленьком сервере нельзя.
/// </summary>
public static class FfmpegRunner
{
    /// <summary>
    /// Максимальная длительность гифки. Больше в чат не нужно, а каждая лишняя
    /// секунда — это кадры, которые кто-то должен пережать.
    /// </summary>
    internal const double MaxGifSeconds = 15;

    /// <summary>
    /// Максимальная ширина гифки в пикселях.
    /// </summary>
    internal const int MaxGifWidth = 640;

    /// <summary>
    /// Максимальная частота кадров гифки.
    /// </summary>
    internal const int MaxGifFps = 20;

    /// <summary>
    /// Потолки для клипа, собранного из вложения в чате: обрезка присланного файла —
    /// операция на секунды, а не на минуты.
    /// </summary>
    internal const double MaxClipSeconds = 300;

    internal const int MaxClipWidth = 1280;

    internal const int MaxClipFps = 30;

    /// <summary>
    /// Потолки для скачанного видео: длину задаёт пользователь, ограничивает
    /// не время, а размер файла, поэтому по времени потолка нет — только по картинке.
    /// </summary>
    internal const int MaxDownloadWidth = 1920;

    internal const int MaxDownloadFps = 60;

    /// <summary>
    /// Потолок входного файла из чата. Совпадает с обычным лимитом вложений Discord —
    /// больше к нам всё равно не приедет.
    /// </summary>
    internal const int MaxInputBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Сколько ждать ffprobe: он только читает заголовки.
    /// </summary>
    private const int ProbeTimeoutSeconds = 60;

    /// <summary>
    /// Форматы, в которые разрешено конвертировать. Белый список, а не проверка
    /// на «плохое»: имя формата уходит в аргументы и в имя файла, и гадать
    /// о безопасности неизвестного не хочется.
    /// </summary>
    internal static readonly string[] AllowedFormats = ["gif", "mp4", "webm", "png", "jpg", "webp"];

    /// <summary>
    /// Форматы звуковой дорожки. Отдельным списком, а не вместе с остальными:
    /// иначе «сконвертируй картинку в mp3» прошло бы проверку.
    /// </summary>
    internal static readonly string[] AllowedAudioFormats = ["mp3", "m4a", "opus", "ogg"];

    /// <summary>
    /// Форматы без движения: результат — один кадр, и время к нему неприменимо.
    /// </summary>
    private static readonly string[] _stillFormats = ["png", "jpg", "webp"];

    /// <summary>
    /// План операции. Все поля необязательны: чего нет — того не делаем.
    /// Crop задаётся в пикселях исходника, поэтому модели заранее сообщают его размеры.
    /// </summary>
    public record MediaPlan(
        string? Format = null,
        double? Start = null,
        double? End = null,
        CropBox? Crop = null,
        int? Width = null,
        int? Fps = null,
        bool AudioOnly = false)
    {
        /// <summary>
        /// Есть ли в плане хоть одна операция: пустой план выполнять бессмысленно.
        /// </summary>
        public bool IsEmpty => Format == null && Start == null && End == null
            && Crop == null && Width == null && Fps == null && !AudioOnly;

        /// <summary>
        /// Нужна ли только обрезка по времени. Такой план умеет выполнить сам yt-dlp,
        /// скачав один отрезок вместо целого видео, — и тогда ffmpeg не нужен вовсе.
        /// </summary>
        public bool IsTrimOnly => (Start != null || End != null)
            && Format == null && Crop == null && Width == null && Fps == null && !AudioOnly;
    }

    public record CropBox(int X, int Y, int Width, int Height);

    /// <summary>
    /// Параметры видеодорожки для мета-подписи и математики пережатия.
    /// </summary>
    public record VideoStreamInfo(string Codec, int Width, int Height, double Fps, long BitrateBps);

    /// <summary>
    /// Параметры звуковой дорожки.
    /// </summary>
    public record AudioStreamInfo(string Codec, int Channels, int SampleRate, long BitrateBps);

    /// <summary>
    /// Размеры, длительность и параметры дорожек. Width, Height и DurationSeconds
    /// продублированы наверху записи намеренно: по ним считается кроп, и они должны
    /// оставаться доступными, когда видеодорожки нет вовсе (файл со звуком).
    /// </summary>
    public record MediaInfo(
        int Width,
        int Height,
        double DurationSeconds,
        VideoStreamInfo? Video = null,
        AudioStreamInfo? Audio = null,
        long SizeBytes = 0,
        string? ContainerName = null);

    /// <summary>
    /// Потолки одной операции. У гифки они жёстче, чем у клипа, а у скачанного видео
    /// длину задаёт пользователь — режет не время, а лимит вложения.
    /// </summary>
    public record MediaLimits(double MaxSeconds, int MaxWidth, int MaxFps)
    {
        /// <summary>
        /// Потолки для файла, присланного в чат.
        /// </summary>
        public static MediaLimits Chat(string format) => format == "gif"
            ? new MediaLimits(MaxGifSeconds, MaxGifWidth, MaxGifFps)
            : new MediaLimits(MaxClipSeconds, MaxClipWidth, MaxClipFps);

        /// <summary>
        /// Потолки для скачанного видео: по времени — сколько есть в исходнике.
        /// </summary>
        public static MediaLimits Download(string format, double sourceSeconds) => format == "gif"
            ? new MediaLimits(MaxGifSeconds, MaxGifWidth, MaxGifFps)
            : new MediaLimits(Math.Max(sourceSeconds, 1), MaxDownloadWidth, MaxDownloadFps);
    }

    /// <summary>
    /// Параметры пережатия под заданный размер. Какие поля значимы — зависит от формата:
    /// у видео это битрейты и разрешение, у гифки — ширина, кадры и палитра,
    /// у звука — битрейт и каналы. Считает их <see cref="MediaShrink"/>.
    /// </summary>
    public record EncodeSettings(
        long VideoBitrateBps = 0,
        long AudioBitrateBps = 0,
        int Width = 0,
        int Height = 0,
        int Fps = 0,
        int Colors = 0,
        int Channels = 2,
        bool CopyStreams = false);

    /// <summary>
    /// Результат — путь к файлу внутри рабочего каталога либо причина отказа
    /// для пользователя. Ошибки наружу не бросаются.
    /// <paramref name="TruncatedSeconds"/> — до скольких секунд потолок обработки урезал
    /// результат, о котором пользователь ничего такого не просил (0 — не урезал).
    /// </summary>
    public record MediaResult(string? FilePath, string? FileName, string? Error, double TruncatedSeconds = 0);

    /// <summary>
    /// Длительность неподвижной картинки. Ровный ноль не годится: ffprobe отдаёт
    /// для одиночного кадра около одной сороковой секунды, и строгая проверка считала бы
    /// обычное фото видео — а видео и картинка расходятся дальше по всей обработке.
    /// </summary>
    internal const double MaxStillSeconds = 1;

    /// <summary>
    /// Выполняет план над файлом. Имя результата строится от displayName —
    /// это имя, которое увидит пользователь, а не путь на диске.
    /// </summary>
    public static async Task<MediaResult> RunAsync(
        MediaWorkspace workspace,
        string inputPath,
        string displayName,
        MediaPlan plan,
        MediaInfo info,
        MediaLimits? limits = null)
    {
        var format = plan.AudioOnly
            ? ResolveAudioFormat(plan.Format, info.Audio)
            : ResolveFormat(plan.Format, displayName, info.DurationSeconds >= MaxStillSeconds);

        if (format == null)
        {
            return new MediaResult(null, null, BotMessages.MediaFormatNotSupported(plan.Format ?? "?"));
        }

        var caps = limits ?? MediaLimits.Chat(format);
        var outputPath = workspace.PathFor("result." + format);
        var arguments = BuildArguments(plan, info, format, inputPath, outputPath, limits);
        var result = await ExecuteAsync(workspace, arguments, outputPath, displayName, format);

        return result with { TruncatedSeconds = TruncatedBy(plan, info, caps, format) };
    }

    /// <summary>
    /// До скольких секунд потолок обработки урезал результат там, где обрезки не просили.
    /// 0 — не урезал или обрезку задал сам пользователь: про свой же отрезок ему говорить
    /// нечего, а вот молча потерянный хвост восьмиминутного видео выглядит поломкой.
    /// </summary>
    private static double TruncatedBy(MediaPlan plan, MediaInfo info, MediaLimits caps, string format)
    {
        // У неподвижного результата длительности нет вовсе: из видео берётся один кадр,
        // и говорить «взял только первые пять минут» про скриншот бессмысленно
        if (_stillFormats.Contains(format) || plan.Start.HasValue || plan.End.HasValue || info.DurationSeconds <= 0)
        {
            return 0;
        }

        return info.DurationSeconds > caps.MaxSeconds ? caps.MaxSeconds : 0;
    }

    /// <summary>
    /// Пережимает файл под заданные параметры. От <see cref="RunAsync"/> отличается тем,
    /// что параметры кодирования посчитаны заранее, а не выведены из плана: круг сжатия
    /// целится в конкретный размер. Выходной путь задаёт вызывающий — он же нумерует круги.
    /// </summary>
    public static async Task<MediaResult> EncodeAsync(
        MediaWorkspace workspace,
        string inputPath,
        string outputPath,
        string format,
        EncodeSettings settings,
        MediaPlan plan,
        MediaInfo info,
        string displayName)
    {
        var arguments = BuildEncodeArguments(settings, plan, info, format, inputPath, outputPath);

        return await ExecuteAsync(workspace, arguments, outputPath, displayName, format);
    }

    /// <summary>
    /// Спрашивает у ffprobe размеры, длительность и параметры дорожек.
    /// null — файл не читается как медиа.
    /// </summary>
    public static async Task<MediaInfo?> ProbeAsync(string path)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                AppConfig.FfprobePath,
                [
                    "-v", "error",
                    "-show_entries",
                    "stream=index,codec_type,codec_name,width,height,avg_frame_rate,r_frame_rate,bit_rate,channels,sample_rate"
                        + ":format=duration,bit_rate,format_name,size",
                    "-of", "json",
                    path
                ],
                TimeSpan.FromSeconds(ProbeTimeoutSeconds));

            return result.Ok ? ParseProbe(result.StandardOutput) : null;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("ffprobe не смог прочитать файл: {Message}", ex.Message);
            return null;
        }
    }

    #region Internals

    private static async Task<MediaResult> ExecuteAsync(
        MediaWorkspace workspace,
        IReadOnlyList<string> arguments,
        string outputPath,
        string displayName,
        string format)
    {
        try
        {
            var timeout = TimeSpan.FromMinutes(AppConfig.MediaSettings.EncodeTimeoutMinutes);
            var result = await ProcessRunner.RunAsync(AppConfig.FfmpegPath, arguments, timeout, workspace.FullPath);

            if (!result.Ok)
            {
                BotLogger.Warning(
                    "ffmpeg не справился: {Error}",
                    result.TimedOut ? "процесс не уложился в отведённое время" : result.StandardError.Trim());

                return new MediaResult(null, null, BotMessages.MediaFailed());
            }

            if (!File.Exists(outputPath))
            {
                return new MediaResult(null, null, BotMessages.MediaFailed());
            }

            return new MediaResult(outputPath, Path.GetFileNameWithoutExtension(displayName) + "." + format, null);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка обработки медиа: {Message}", ex.Message);
            return new MediaResult(null, null, BotMessages.MediaFailed());
        }
    }

    /// <summary>
    /// Разбирает json от ffprobe. null — нет ни видео-, ни звуковой дорожки,
    /// значит это не то, с чем мы работаем.
    /// </summary>
    internal static MediaInfo? ParseProbe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var duration = 0d;
            var containerBitrate = 0L;
            var size = 0L;
            string? container = null;

            if (root.TryGetProperty("format", out var format))
            {
                duration = JsonRead.Number(format, "duration");
                containerBitrate = (long)JsonRead.Number(format, "bit_rate");
                size = (long)JsonRead.Number(format, "size");
                container = JsonRead.Text(format, "format_name");
            }

            VideoStreamInfo? video = null;
            AudioStreamInfo? audio = null;

            foreach (var stream in streams.EnumerateArray())
            {
                var type = JsonRead.Text(stream, "codec_type");

                if (video == null && IsVideoStream(stream, type))
                {
                    video = ReadVideo(stream);
                }
                else if (audio == null && type == "audio")
                {
                    audio = ReadAudio(stream);
                }
            }

            if (video == null && audio == null)
            {
                return null;
            }

            // Битрейт дорожки часто отсутствует (webm, matroska) — тогда считаем
            // от контейнера, а в последнюю очередь от размера файла
            if (containerBitrate == 0 && size > 0 && duration > 0)
            {
                containerBitrate = (long)(size * 8 / duration);
            }

            if (video is { BitrateBps: 0 } && containerBitrate > 0)
            {
                video = video with { BitrateBps = Math.Max(containerBitrate - (audio?.BitrateBps ?? 0), 0) };
            }

            return new MediaInfo(
                video?.Width ?? 0,
                video?.Height ?? 0,
                duration,
                video,
                audio,
                size,
                container);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Видеодорожка ли это. Обложка mp3 тоже приезжает видеопотоком, но её частота
    /// кадров нулевая — это картинка, а не дорожка, и считать файл видео из-за неё нельзя.
    /// Поток без codec_type, но с размерами, считается видео: так выглядит ответ
    /// урезанного запроса к ffprobe.
    /// </summary>
    private static bool IsVideoStream(JsonElement stream, string? type)
    {
        var hasSize = stream.TryGetProperty("width", out _) && stream.TryGetProperty("height", out _);

        if (type == null)
        {
            return hasSize;
        }

        return type == "video" && hasSize && ReadFrameRate(stream) > 0;
    }

    private static VideoStreamInfo ReadVideo(JsonElement stream) => new(
        JsonRead.Text(stream, "codec_name") ?? "?",
        (int)JsonRead.Number(stream, "width"),
        (int)JsonRead.Number(stream, "height"),
        ReadFrameRate(stream),
        (long)JsonRead.Number(stream, "bit_rate"));

    private static AudioStreamInfo ReadAudio(JsonElement stream) => new(
        JsonRead.Text(stream, "codec_name") ?? "?",
        (int)JsonRead.Number(stream, "channels"),
        (int)JsonRead.Number(stream, "sample_rate"),
        (long)JsonRead.Number(stream, "bit_rate"));

    /// <summary>
    /// Частота кадров приезжает рациональной строкой вида «30000/1001».
    /// «0/0» означает, что кадров нет вовсе.
    /// </summary>
    private static double ReadFrameRate(JsonElement stream)
    {
        var value = JsonRead.Text(stream, "avg_frame_rate") ?? JsonRead.Text(stream, "r_frame_rate");

        if (value == null)
        {
            // Урезанный ответ ffprobe без частоты кадров: считаем, что она есть
            return stream.TryGetProperty("width", out _) ? 1 : 0;
        }

        var parts = value.Split('/');

        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || denominator == 0)
        {
            return 0;
        }

        return numerator / denominator;
    }

    /// <summary>
    /// Формат результата. Запрошенный явно обязан быть в белом списке — иначе отказ:
    /// имя формата уходит в аргументы. Если формат не просили, оставляем исходный,
    /// а когда и он не из списка (.mov, .mkv, .heic) — берём разумный по умолчанию:
    /// отказывать в обрезке только из-за контейнера было бы глупо.
    /// </summary>
    internal static string? ResolveFormat(string? requested, string inputFileName, bool animated)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var wanted = Normalize(requested);

            return AllowedFormats.Contains(wanted) ? wanted : null;
        }

        var original = Normalize(Path.GetExtension(inputFileName).TrimStart('.'));

        return AllowedFormats.Contains(original) ? original : animated ? "mp4" : "png";
    }

    /// <summary>
    /// Формат звуковой дорожки. Не просили конкретный — берём родной контейнер кодека:
    /// тогда дорожку можно скопировать без перекодирования, а это и быстрее, и без потерь.
    /// </summary>
    internal static string? ResolveAudioFormat(string? requested, AudioStreamInfo? audio)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var wanted = Normalize(requested);

            return AllowedAudioFormats.Contains(wanted) ? wanted : null;
        }

        return NativeContainer(audio?.Codec) ?? "mp3";
    }

    /// <summary>
    /// Контейнер, в который дорожку можно положить как есть.
    /// </summary>
    private static string? NativeContainer(string? codec) => codec?.ToLowerInvariant() switch
    {
        "aac" => "m4a",
        "opus" => "opus",
        "vorbis" => "ogg",
        "mp3" => "mp3",
        _ => null
    };

    private static string Normalize(string format)
    {
        var normalized = format.Trim().ToLowerInvariant();

        return normalized == "jpeg" ? "jpg" : normalized;
    }

    /// <summary>
    /// Собирает аргументы ffmpeg из плана. Всё, что приходит от модели, зажимается
    /// в потолки здесь: план — это пожелание, а не команда.
    /// </summary>
    internal static List<string> BuildArguments(
        MediaPlan plan,
        MediaInfo info,
        string format,
        string inputPath,
        string outputPath,
        MediaLimits? limits = null)
    {
        var caps = limits ?? MediaLimits.Chat(format);
        var arguments = OpenInput(plan, info, inputPath, caps.MaxSeconds);

        if (AllowedAudioFormats.Contains(format))
        {
            AppendAudioOnly(arguments, format, info, bitrateBps: 0, channels: 0);
        }
        else if (format == "gif")
        {
            AppendGif(arguments, BuildFilters(plan, info, format, caps), colors: 0);
        }
        else
        {
            var filters = BuildFilters(plan, info, format, caps);

            if (filters.Count > 0)
            {
                arguments.AddRange(["-vf", string.Join(',', filters)]);
            }

            if (format is "png" or "jpg" or "webp")
            {
                // Один кадр: «сделай скриншот» — это тоже операция над видео
                arguments.AddRange(["-an", "-frames:v", "1"]);
            }
            else
            {
                AppendVideoTail(arguments);
            }
        }

        arguments.Add(outputPath);

        return arguments;
    }

    /// <summary>
    /// Аргументы круга пережатия: параметры кодирования уже посчитаны, план нужен
    /// только ради обрезки — круг всегда начинается с исходника, а не с прошлого результата.
    /// </summary>
    internal static List<string> BuildEncodeArguments(
        EncodeSettings settings,
        MediaPlan plan,
        MediaInfo info,
        string format,
        string inputPath,
        string outputPath)
    {
        var arguments = OpenInput(plan, info, inputPath, double.MaxValue);

        if (settings.CopyStreams)
        {
            // Быстрый путь: резать нечего, достаточно переложить потоки в контейнер
            arguments.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart"]);
        }
        else if (AllowedAudioFormats.Contains(format))
        {
            AppendAudioOnly(arguments, format, info, settings.AudioBitrateBps, settings.Channels);
        }
        else if (format == "gif")
        {
            var filters = new List<string>();

            if (settings.Fps > 0)
            {
                filters.Add($"fps={settings.Fps}");
            }

            if (settings.Width > 0)
            {
                filters.Add($"scale={settings.Width}:-2:flags=lanczos");
            }

            AppendGif(arguments, filters, settings.Colors);
        }
        else
        {
            var filters = new List<string>();

            if (settings.Fps > 0)
            {
                filters.Add($"fps={settings.Fps}");
            }

            if (settings.Width > 0 && settings.Height > 0)
            {
                filters.Add($"scale={settings.Width}:{settings.Height}:flags=lanczos");
            }

            arguments.AddRange(["-map", "0:v:0", "-map", "0:a:0?"]);

            // Однопроходный ABR, а не CRF: попадание в байты через CRF — это ровно тот
            // перебор кругов, который мы и убираем. Второй проход на слабом сервере
            // не окупается против одного корректирующего круга
            arguments.AddRange(
            [
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-b:v", settings.VideoBitrateBps.ToString(CultureInfo.InvariantCulture),
                "-maxrate", ((long)(settings.VideoBitrateBps * 1.45)).ToString(CultureInfo.InvariantCulture),
                "-bufsize", (settings.VideoBitrateBps * 2).ToString(CultureInfo.InvariantCulture)
            ]);

            if (filters.Count > 0)
            {
                arguments.AddRange(["-vf", string.Join(',', filters)]);
            }

            arguments.AddRange(
            [
                "-c:a", "aac",
                "-b:a", settings.AudioBitrateBps.ToString(CultureInfo.InvariantCulture),
                "-ac", Math.Max(settings.Channels, 1).ToString(CultureInfo.InvariantCulture)
            ]);

            AppendVideoTail(arguments);
        }

        arguments.Add(outputPath);

        return arguments;
    }

    /// <summary>
    /// Общее начало: перемотка, вход и длительность. -t однозначен рядом с -ss,
    /// в отличие от -to, у которого смещается точка отсчёта.
    /// </summary>
    private static List<string> OpenInput(MediaPlan plan, MediaInfo info, string inputPath, double maxSeconds)
    {
        var arguments = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        var start = Math.Clamp(plan.Start ?? 0, 0, Math.Max(info.DurationSeconds, 0));

        if (start > 0)
        {
            arguments.AddRange(["-ss", Format(start)]);
        }

        arguments.AddRange(["-i", inputPath]);

        var available = info.DurationSeconds > 0 ? info.DurationSeconds - start : 0;
        var requested = plan.End.HasValue ? plan.End.Value - start : available;
        var duration = Math.Min(requested > 0 ? requested : available, maxSeconds);

        if (duration > 0 && !double.IsInfinity(duration) && duration < double.MaxValue)
        {
            arguments.AddRange(["-t", Format(duration)]);
        }

        return arguments;
    }

    /// <summary>
    /// Хвост видеоконтейнера. yuv420p не опционален: у десятибитного исходника
    /// с YouTube результат иначе открывается плеером и показывает чёрный прямоугольник
    /// в Discord. faststart двигает индекс в начало — иначе превью не проигрывается,
    /// пока файл не скачается целиком.
    /// </summary>
    private static void AppendVideoTail(List<string> arguments) =>
        arguments.AddRange(["-pix_fmt", "yuv420p", "-movflags", "+faststart"]);

    /// <summary>
    /// Гифка собирается через палитру: без palettegen/paletteuse цвета получаются грязными.
    /// split нужен, чтобы один и тот же поток кадров ушёл и на построение палитры,
    /// и на раскраску по ней.
    /// </summary>
    private static void AppendGif(List<string> arguments, IReadOnlyList<string> filters, int colors)
    {
        var chain = filters.Count > 0 ? string.Join(',', filters) + "," : string.Empty;
        var palette = colors > 0 ? $"palettegen=max_colors={colors}:stats_mode=diff" : "palettegen=stats_mode=diff";

        arguments.AddRange(
        [
            "-filter_complex",
            $"[0:v]{chain}split[a][b];[a]{palette}[p];"
                + "[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle",
            "-loop", "0",
            "-an"
        ]);
    }

    /// <summary>
    /// Только звуковая дорожка. Если формат совпал с родным контейнером кодека
    /// и битрейт не задан — копируем как есть: без потерь и мгновенно.
    /// </summary>
    private static void AppendAudioOnly(
        List<string> arguments,
        string format,
        MediaInfo info,
        long bitrateBps,
        int channels)
    {
        arguments.AddRange(["-vn", "-map", "0:a:0"]);

        if (bitrateBps <= 0 && NativeContainer(info.Audio?.Codec) == format)
        {
            arguments.AddRange(["-c:a", "copy"]);
            return;
        }

        var codec = format switch
        {
            "mp3" => "libmp3lame",
            "m4a" => "aac",
            _ => "libopus"
        };

        arguments.AddRange(["-c:a", codec]);

        if (bitrateBps > 0)
        {
            arguments.AddRange(["-b:a", bitrateBps.ToString(CultureInfo.InvariantCulture)]);
        }

        if (channels > 0)
        {
            arguments.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);
        }
    }

    /// <summary>
    /// Цепочка видеофильтров. Частота кадров идёт первой: тогда масштабированию
    /// достаётся пятнадцать кадров в секунду вместо шестидесяти. Кроп — до scale:
    /// его координаты заданы в пикселях исходника.
    /// </summary>
    private static List<string> BuildFilters(MediaPlan plan, MediaInfo info, string format, MediaLimits limits)
    {
        var filters = new List<string>();

        // Гифке частоту задаём всегда: исходные 60 fps раздувают её до неприличия
        var fps = Math.Min(plan.Fps ?? (format == "gif" ? limits.MaxFps : 0), limits.MaxFps);

        if (fps > 0)
        {
            filters.Add($"fps={fps}");
        }

        var sourceWidth = info.Width;

        if (plan.Crop != null)
        {
            var crop = ClampCrop(plan.Crop, info);

            if (crop != null)
            {
                filters.Add($"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}");
                sourceWidth = crop.Width;
            }
        }

        var width = Math.Min(plan.Width ?? sourceWidth, limits.MaxWidth);

        if (width > 0 && width < sourceWidth)
        {
            // -2 вместо -1: высота округляется до чётной, иначе кодеки видео ругаются
            filters.Add($"scale={width}:-2:flags=lanczos");
        }

        return filters;
    }

    /// <summary>
    /// Загоняет кроп в границы кадра: модель считает координаты «на глаз»,
    /// а ffmpeg на выходе за край падает.
    /// </summary>
    internal static CropBox? ClampCrop(CropBox crop, MediaInfo info)
    {
        var x = Math.Clamp(crop.X, 0, Math.Max(info.Width - 1, 0));
        var y = Math.Clamp(crop.Y, 0, Math.Max(info.Height - 1, 0));
        var width = Math.Clamp(crop.Width, 1, info.Width - x);
        var height = Math.Clamp(crop.Height, 1, info.Height - y);

        return width <= 0 || height <= 0 ? null : new CropBox(x, y, width, height);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    #endregion
}
