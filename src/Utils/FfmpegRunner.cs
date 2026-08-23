using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Запуск ffmpeg над файлом из чата. Модель сюда не пишет ни строчки командной строки:
/// она отдаёт типизированный план (<see cref="MediaPlan"/>), а аргументы собирает код
/// из белого списка операций — иначе это было бы выполнение произвольных команд из вывода ИИ.
/// Операции нарочно простые: формат, обрезка по времени, кроп, размер, частота кадров.
/// Задачи выполняются по одной и с потолками: сервер маленький, и неограниченная
/// конвертация длинного видео способна его занять целиком.
/// </summary>
public static class FfmpegRunner
{
    /// <summary>
    /// Максимальная длительность результата. Гифке в чат больше не нужно, а каждая
    /// лишняя секунда — это кадры, которые кто-то должен пережать.
    /// </summary>
    internal const double MaxOutputSeconds = 15;

    /// <summary>
    /// Максимальная ширина результата в пикселях.
    /// </summary>
    internal const int MaxWidth = 640;

    /// <summary>
    /// Максимальная частота кадров результата.
    /// </summary>
    internal const int MaxFps = 20;

    /// <summary>
    /// Потолок входного файла. Совпадает с обычным лимитом вложений Discord —
    /// больше к нам всё равно не приедет.
    /// </summary>
    internal const int MaxInputBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Сколько ждать процесс, прежде чем счесть его зависшим и убить.
    /// </summary>
    private const int ProcessTimeoutSeconds = 60;

    /// <summary>
    /// Форматы, в которые разрешено конвертировать. Белый список, а не проверка на «плохое»:
    /// имя формата уходит в аргументы, и гадать о безопасности неизвестного не хочется.
    /// </summary>
    internal static readonly string[] AllowedFormats = ["gif", "mp4", "webm", "png", "jpg", "webp"];

    /// <summary>
    /// Конвертации идут по одной: параллельный ffmpeg — самый быстрый способ
    /// положить маленький сервер.
    /// </summary>
    private static readonly SemaphoreSlim Slot = new(1, 1);

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
        int? Fps = null)
    {
        /// <summary>
        /// Есть ли в плане хоть одна операция: пустой план выполнять бессмысленно.
        /// </summary>
        public bool IsEmpty => Format == null && Start == null && End == null && Crop == null && Width == null && Fps == null;
    }

    public record CropBox(int X, int Y, int Width, int Height);

    /// <summary>
    /// Размеры и длительность исходника — нужны и для кропа, и для проверки лимитов.
    /// </summary>
    public record MediaInfo(int Width, int Height, double DurationSeconds);

    /// <summary>
    /// Результат: готовый файл либо причина отказа для пользователя.
    /// </summary>
    public record MediaResult(byte[]? Content, string? FileName, string? Error);

    /// <summary>
    /// Выполняет план над файлом. Ошибки не бросаются: наружу едет причина отказа.
    /// </summary>
    public static async Task<MediaResult> RunAsync(byte[] input, string inputFileName, MediaPlan plan, MediaInfo info)
    {
        var format = ResolveFormat(plan.Format, inputFileName);

        if (format == null)
        {
            return new MediaResult(null, null, BotMessages.MediaFormatNotSupported(plan.Format ?? "?"));
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "mewo-media", Guid.NewGuid().ToString("N"));
        var inputPath = Path.Combine(workDirectory, "in" + Path.GetExtension(inputFileName));
        var outputPath = Path.Combine(workDirectory, "out." + format);

        await Slot.WaitAsync();

        try
        {
            Directory.CreateDirectory(workDirectory);
            await File.WriteAllBytesAsync(inputPath, input);

            var arguments = BuildArguments(plan, info, format, inputPath, outputPath);
            var error = await ExecuteAsync(AppConfig.FfmpegPath, arguments);

            if (error != null)
            {
                BotLogger.Warning("ffmpeg не справился: {Error}", error);
                return new MediaResult(null, null, BotMessages.MediaFailed());
            }

            if (!File.Exists(outputPath))
            {
                return new MediaResult(null, null, BotMessages.MediaFailed());
            }

            var content = await File.ReadAllBytesAsync(outputPath);

            return new MediaResult(content, Path.GetFileNameWithoutExtension(inputFileName) + "." + format, null);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка конвертации медиа: {Message}", ex.Message);
            return new MediaResult(null, null, BotMessages.MediaFailed());
        }
        finally
        {
            Slot.Release();
            Cleanup(workDirectory);
        }
    }

    /// <summary>
    /// Спрашивает у ffprobe размеры и длительность. null — файл не читается как медиа.
    /// </summary>
    public static async Task<MediaInfo?> ProbeAsync(byte[] input, string inputFileName)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), "mewo-media", Guid.NewGuid().ToString("N"));
        var inputPath = Path.Combine(workDirectory, "in" + Path.GetExtension(inputFileName));

        try
        {
            Directory.CreateDirectory(workDirectory);
            await File.WriteAllBytesAsync(inputPath, input);

            var output = await ReadOutputAsync(AppConfig.FfprobePath,
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width,height",
                "-show_entries", "format=duration",
                "-of", "json",
                inputPath
            ]);

            return output == null ? null : ParseProbe(output);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("ffprobe не смог прочитать файл: {Message}", ex.Message);
            return null;
        }
        finally
        {
            Cleanup(workDirectory);
        }
    }

    #region Internals

    /// <summary>
    /// Разбирает json от ffprobe. null — размеров нет, значит это не видео и не картинка.
    /// </summary>
    internal static MediaInfo? ParseProbe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            {
                return null;
            }

            var stream = streams[0];

            if (!stream.TryGetProperty("width", out var width) || !stream.TryGetProperty("height", out var height))
            {
                return null;
            }

            var duration = 0d;

            if (document.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durationValue)
                && double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }

            return new MediaInfo(width.GetInt32(), height.GetInt32(), duration);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Формат результата: из плана, иначе исходное расширение. null — формат не в белом списке.
    /// </summary>
    internal static string? ResolveFormat(string? requested, string inputFileName)
    {
        var format = (requested ?? Path.GetExtension(inputFileName).TrimStart('.')).Trim().ToLowerInvariant();

        if (format == "jpeg")
        {
            format = "jpg";
        }

        return AllowedFormats.Contains(format) ? format : null;
    }

    /// <summary>
    /// Собирает аргументы ffmpeg из плана. Всё, что приходит от модели, зажимается
    /// в лимиты здесь: план — это пожелание, а не команда.
    /// Для gif строится палитра (palettegen/paletteuse) — без неё цвета получаются грязными.
    /// </summary>
    internal static List<string> BuildArguments(MediaPlan plan, MediaInfo info, string format, string inputPath, string outputPath)
    {
        var arguments = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };

        var start = Math.Clamp(plan.Start ?? 0, 0, Math.Max(info.DurationSeconds, 0));

        if (start > 0)
        {
            arguments.AddRange(["-ss", Format(start)]);
        }

        arguments.AddRange(["-i", inputPath]);

        // Длительность считаем сами и режем по потолку: -t однозначен, в отличие от -to
        // рядом с -ss, который смещает точку отсчёта
        var available = info.DurationSeconds > 0 ? info.DurationSeconds - start : 0;
        var requested = plan.End.HasValue ? plan.End.Value - start : available;
        var duration = Math.Min(requested > 0 ? requested : available, MaxOutputSeconds);

        if (duration > 0)
        {
            arguments.AddRange(["-t", Format(duration)]);
        }

        var filters = BuildFilters(plan, info, format);

        if (format == "gif")
        {
            // split нужен, чтобы один и тот же поток кадров ушёл и на построение палитры,
            // и на раскраску по ней
            var chain = filters.Count > 0 ? string.Join(',', filters) + "," : string.Empty;
            arguments.AddRange(["-filter_complex", $"[0:v]{chain}split[a][b];[a]palettegen[p];[b][p]paletteuse"]);
            arguments.AddRange(["-loop", "0"]);
        }
        else if (filters.Count > 0)
        {
            arguments.AddRange(["-vf", string.Join(',', filters)]);
        }

        // У картинки и гифки звука быть не может, у видео — оставляем как есть
        if (format is "gif" or "png" or "jpg" or "webp")
        {
            arguments.Add("-an");
        }

        if (format is "png" or "jpg" or "webp")
        {
            // Один кадр: «сделай скриншот» — это тоже операция над видео
            arguments.AddRange(["-frames:v", "1"]);
        }

        arguments.Add(outputPath);

        return arguments;
    }

    /// <summary>
    /// Цепочка видеофильтров. Порядок важен: сначала вырезаем область, потом уменьшаем.
    /// </summary>
    private static List<string> BuildFilters(MediaPlan plan, MediaInfo info, string format)
    {
        var filters = new List<string>();

        if (plan.Crop != null)
        {
            var crop = ClampCrop(plan.Crop, info);

            if (crop != null)
            {
                filters.Add($"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}");
            }
        }

        var sourceWidth = plan.Crop != null ? ClampCrop(plan.Crop, info)?.Width ?? info.Width : info.Width;
        var width = Math.Min(plan.Width ?? sourceWidth, MaxWidth);

        if (width > 0 && width < sourceWidth)
        {
            // -2 вместо -1: высота округляется до чётной, иначе кодеки видео ругаются
            filters.Add($"scale={width}:-2:flags=lanczos");
        }

        // Гифке частоту задаём всегда: исходные 60 fps раздувают её до неприличия
        var fps = Math.Min(plan.Fps ?? (format == "gif" ? MaxFps : 0), MaxFps);

        if (fps > 0)
        {
            filters.Add($"fps={fps}");
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

    /// <summary>
    /// Запускает процесс. null — успех, иначе текст ошибки из stderr.
    /// Аргументы уходят списком, а не строкой: оболочки в цепочке нет, склеивать нечего.
    /// </summary>
    private static async Task<string?> ExecuteAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = StartProcess(fileName, arguments);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ProcessTimeoutSeconds));

        var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return "процесс не уложился в отведённое время";
        }

        return process.ExitCode == 0 ? null : stderr.Trim();
    }

    /// <summary>
    /// Запускает процесс и возвращает stdout. null — процесс завершился с ошибкой.
    /// </summary>
    private static async Task<string?> ReadOutputAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = StartProcess(fileName, arguments);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ProcessTimeoutSeconds));

        var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return null;
        }

        return process.ExitCode == 0 ? stdout : null;
    }

    private static Process StartProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"не удалось запустить {fileName}");
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось убить зависший процесс: {Message}", ex.Message);
        }
    }

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось убрать временный каталог {Directory}: {Message}", directory, ex.Message);
        }
    }

    #endregion
}
