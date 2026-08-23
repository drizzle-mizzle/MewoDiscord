using System.Diagnostics;
using System.Text;

using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Единственное место, где запускается внешняя программа (ffmpeg, ffprobe, yt-dlp).
/// Аргументы уходят списком, а не строкой: оболочки в цепочке нет, склеивать
/// и экранировать нечего.
/// Оба потока вывода читаются одновременно. Если читать только один, второй упирается
/// в переполненный буфер трубы и процесс встаёт намертво — у ffmpeg это незаметно
/// (при -loglevel error он молчит в stdout), а yt-dlp пишет в оба сразу.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Потолок на объём собираемого вывода. Трубы продолжают читаться и после него —
    /// иначе процесс встанет, — но в память лишнее уже не копится: --dump-single-json
    /// у yt-dlp бывает многомегабайтным.
    /// </summary>
    private const int MaxCapturedChars = 8 * 1024 * 1024;

    /// <summary>
    /// Сколько ждать закрытия труб после выхода процесса. Обычно они закрываются
    /// вместе с ним, но осиротевший потомок мог унаследовать их концы.
    /// </summary>
    private const int PipeDrainSeconds = 5;

    /// <summary>
    /// Результат запуска. TimedOut — процесс не уложился в отведённое время
    /// либо его прервал сторож: в обоих случаях он убит и код возврата бессмыслен.
    /// </summary>
    internal record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
    {
        internal bool Ok => !TimedOut && ExitCode == 0;
    }

    /// <summary>
    /// Запускает процесс и ждёт его завершения. Исключения наружу не летят из-за таймаута —
    /// он приезжает флагом в результате; не запустившаяся программа (нет ffmpeg в PATH)
    /// бросает, и это правильно: конфигурация сломана.
    /// Необязательный сторож работает параллельно процессу и, завершившись, убивает его —
    /// так устроен присмотр за качанием, которому плоского таймаута мало.
    /// </summary>
    internal static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? workingDirectory = null,
        Func<CancellationToken, Task>? watchdog = null)
    {
        using var process = Start(fileName, arguments, workingDirectory);

        // Чтение обоих труб начинается до ожидания выхода — см. комментарий к классу
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);

        using var stop = new CancellationTokenSource(timeout);
        var guard = watchdog == null ? Task.CompletedTask : GuardAsync(watchdog, stop);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            Kill(process);
        }

        await stop.CancelAsync();
        await guard;

        var output = await DrainAsync(stdout);
        var error = await DrainAsync(stderr);

        return new ProcessResult(timedOut ? -1 : process.ExitCode, output, error, timedOut);
    }

    #region Internals

    private static Process Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Без явной кодировки заголовки видео и текст ошибок YouTube приезжают
            // кашей на машине с не-UTF8 консолью, и классификатор ошибок промахивается
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDirectory != null)
        {
            // yt-dlp и ffmpeg роняют рядом с собой .part, .ytdl и логи проходов:
            // пусть это будет рабочий каталог операции, который мы всё равно снесём
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"не удалось запустить {fileName}");
    }

    /// <summary>
    /// Вычитывает трубу до конца, копя в памяти не больше потолка. Читать надо всё
    /// независимо от потолка: недочитанная труба останавливает процесс.
    /// </summary>
    private static async Task<string> ReadAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int read;

        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (builder.Length < MaxCapturedChars)
            {
                builder.Append(buffer, 0, Math.Min(read, MaxCapturedChars - builder.Length));
            }
        }

        return builder.ToString();
    }

    private static async Task<string> DrainAsync(Task<string> reading)
    {
        try
        {
            return await reading.WaitAsync(TimeSpan.FromSeconds(PipeDrainSeconds));
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось дочитать вывод процесса: {Message}", ex.Message);
            return string.Empty;
        }
    }

    /// <summary>
    /// Крутит сторожа. Он завершается либо потому, что операцию отменили извне,
    /// либо потому, что сам решил её прервать — тогда отменяем ожидание, и процесс убивают.
    /// </summary>
    private static async Task GuardAsync(Func<CancellationToken, Task> watchdog, CancellationTokenSource stop)
    {
        try
        {
            await watchdog(stop.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Сторож операции упал: {Message}", ex.Message);
            return;
        }

        await stop.CancelAsync();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(PipeDrainSeconds));
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось убить зависший процесс: {Message}", ex.Message);
        }
    }

    #endregion
}
