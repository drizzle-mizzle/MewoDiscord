namespace MewoDiscord.Utils;

/// <summary>
/// Расчёт параметров пережатия под заданный размер. Слепой шаг «минус двадцать процентов»
/// расточителен: каждый промах — полное перекодирование, а на слабом сервере это минуты.
/// Поэтому цель считается напрямую из размера и длительности, а следующий круг
/// корректируется по факту предыдущего.
/// Все методы чистые: сюда не приезжают ни файлы, ни процессы — только числа.
/// </summary>
public static class MediaShrink
{
    /// <summary>
    /// Сколько кругов пережатия допустимо. Каждый круг — полное перекодирование,
    /// и слот всё это время занят.
    /// </summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// Целевая плотность битрейта на пиксель в секунду. Стандартная мера вменяемости
    /// x264: около 0.10 комфортно, 0.05 приемлемо, ниже 0.03 картинка сыпется.
    /// </summary>
    private const double TargetBitsPerPixel = 0.06;

    /// <summary>
    /// Нижний порог плотности: под ним отдавать результат стыдно — лучше отказать.
    /// </summary>
    private const double MinBitsPerPixel = 0.03;

    /// <summary>
    /// Ниже этого битрейта видео превращается в кашу независимо от разрешения.
    /// </summary>
    private const long MinVideoBitrateBps = 120_000;

    /// <summary>
    /// Кадры выше тридцати вдвое режут плотность битрейта задаром: на встроенном
    /// плеере Discord разницы не видно, а битов уходит вдвое больше.
    /// </summary>
    private const int MaxShrinkFps = 30;

    /// <summary>
    /// Целимся чуть ниже лимита, чтобы не крутиться на самой границе.
    /// </summary>
    private const double Undershoot = 0.97;

    /// <summary>
    /// Ступени разрешения, по которым спускаемся вслед за битрейтом.
    /// </summary>
    private static readonly int[] HeightLadder = [1080, 720, 576, 480, 360, 240];

    /// <summary>
    /// Ступени битрейта звука, бит/с. Ниже последней речь уже неразборчива.
    /// </summary>
    private static readonly long[] AudioLadder = [128_000, 96_000, 64_000, 48_000, 32_000];

    /// <summary>
    /// Ниже этой ширины гифка перестаёт быть результатом, которого кто-то хотел.
    /// </summary>
    internal const int MinGifWidth = 240;

    /// <summary>
    /// Ниже этой частоты гифка превращается в слайд-шоу.
    /// </summary>
    internal const int MinGifFps = 8;

    /// <summary>
    /// Ступени палитры гифки. Ноль означает «палитру ещё не ограничивали»:
    /// palettegen по умолчанию берёт 256 цветов.
    /// </summary>
    private static readonly int[] ColorLadder = [64, 32];

    /// <summary>
    /// Что именно ужимаем: у видео, гифки и звука разные ручки.
    /// </summary>
    public enum ShrinkKind
    {
        Video,
        Gif,
        Audio
    }

    /// <summary>
    /// Первый круг: параметры, с которых начинаем ужимать. null — цель недостижима
    /// без превращения результата в кашу, и это повод отказать, а не пытаться.
    /// </summary>
    public static FfmpegRunner.EncodeSettings? Plan(
        ShrinkKind kind,
        FfmpegRunner.MediaInfo info,
        double outputSeconds,
        long targetBytes,
        long currentBytes) => kind switch
        {
            ShrinkKind.Video => PlanVideo(info, outputSeconds, targetBytes),
            ShrinkKind.Audio => PlanAudio(outputSeconds, targetBytes),
            ShrinkKind.Gif => StepGif(CurrentGif(info), currentBytes, targetBytes),
            _ => null
        };

    /// <summary>
    /// Следующий круг: поправка по факту предыдущего, а не фиксированный шаг.
    /// </summary>
    public static FfmpegRunner.EncodeSettings? Correct(
        ShrinkKind kind,
        FfmpegRunner.EncodeSettings previous,
        long actualBytes,
        long targetBytes,
        FfmpegRunner.MediaInfo info,
        double outputSeconds) => kind switch
        {
            ShrinkKind.Video => CorrectVideo(previous, actualBytes, targetBytes, info),
            ShrinkKind.Audio => CorrectAudio(previous, actualBytes, targetBytes),
            ShrinkKind.Gif => StepGif(previous, actualBytes, targetBytes),
            _ => null
        };

    /// <summary>
    /// Влезет ли вообще: тот же расчёт, что и у первого круга, но без самих параметров.
    /// Нужен до скачивания — длительность и лимит известны из метаданных, и тратить
    /// двадцать минут, чтобы узнать заведомое «нет», незачем.
    /// </summary>
    public static bool CanFitVideo(double outputSeconds, long targetBytes) =>
        PlanVideo(new FfmpegRunner.MediaInfo(0, 0, outputSeconds), outputSeconds, targetBytes) != null;

    #region Internals

    /// <summary>
    /// Решаем задачу с конца: из размера и длительности получаем битрейт, а под битрейт
    /// подбираем самое большое разрешение, которое он способен вытянуть.
    /// </summary>
    internal static FfmpegRunner.EncodeSettings? PlanVideo(
        FfmpegRunner.MediaInfo info,
        double outputSeconds,
        long targetBytes)
    {
        if (outputSeconds <= 0 || targetBytes <= 0)
        {
            return null;
        }

        var totalBps = (long)(targetBytes * 8 / outputSeconds);
        var audioBps = ChooseVideoAudio(totalBps);

        // Пара процентов уходит на накладные расходы контейнера
        var videoBps = (long)((totalBps - audioBps) * Undershoot);

        return Compose(videoBps, audioBps, info);
    }

    internal static FfmpegRunner.EncodeSettings? CorrectVideo(
        FfmpegRunner.EncodeSettings previous,
        long actualBytes,
        long targetBytes,
        FfmpegRunner.MediaInfo info)
    {
        if (actualBytes <= 0)
        {
            return null;
        }

        // Зажим снизу не даёт одному кривому замеру (кусок с частыми ключевыми кадрами)
        // утащить следующий круг в нечитаемость
        var ratio = Math.Clamp((double)targetBytes / actualBytes, 0.5, 1.0);
        var videoBps = (long)(previous.VideoBitrateBps * ratio * Undershoot);
        var audioBps = ChooseVideoAudio(videoBps + previous.AudioBitrateBps);

        return Compose(videoBps, audioBps, info);
    }

    /// <summary>
    /// Подбирает разрешение и кадры под уже посчитанный битрейт. Спускаемся по ступеням,
    /// пока плотность битов на пиксель не станет приличной.
    /// </summary>
    private static FfmpegRunner.EncodeSettings? Compose(long videoBps, long audioBps, FfmpegRunner.MediaInfo info)
    {
        if (videoBps < MinVideoBitrateBps)
        {
            return null;
        }

        var sourceFps = info.Video?.Fps ?? 0;
        var fps = sourceFps > 0 ? (int)Math.Round(Math.Min(sourceFps, MaxShrinkFps)) : MaxShrinkFps;
        fps = Math.Max(fps, 1);

        var applicable = HeightLadder.Where(height => info.Height <= 0 || height <= info.Height).ToList();

        if (applicable.Count == 0)
        {
            // Исходник ниже самой мелкой ступени — уменьшать некуда, берём как есть
            applicable = [Math.Max(info.Height, 1)];
        }

        var channels = audioBps >= 64_000 ? 2 : 1;

        foreach (var height in applicable)
        {
            var (width, fitted) = FitSize(info, height);

            if (Density(videoBps, width, fitted, fps) >= TargetBitsPerPixel)
            {
                return new FfmpegRunner.EncodeSettings(videoBps, audioBps, width, fitted, fps, Channels: channels);
            }
        }

        // Целевую плотность не даёт ни одна ступень — пробуем нижнюю по минимальному порогу
        var lowest = applicable[^1];
        var (lowestWidth, lowestHeight) = FitSize(info, lowest);

        return Density(videoBps, lowestWidth, lowestHeight, fps) >= MinBitsPerPixel
            ? new FfmpegRunner.EncodeSettings(videoBps, audioBps, lowestWidth, lowestHeight, fps, Channels: channels)
            : null;
    }

    private static double Density(long videoBps, int width, int height, int fps) =>
        width <= 0 || height <= 0 || fps <= 0 ? 0 : (double)videoBps / ((long)width * height * fps);

    /// <summary>
    /// Размеры под заданную высоту с сохранением пропорций. Обе стороны чётные:
    /// иначе кодеки видео ругаются. Размеров исходника нет — считаем 16:9.
    /// </summary>
    private static (int Width, int Height) FitSize(FfmpegRunner.MediaInfo info, int targetHeight)
    {
        var height = Even(info.Height > 0 ? Math.Min(targetHeight, info.Height) : targetHeight);

        if (info.Width <= 0 || info.Height <= 0)
        {
            return (Even(height * 16 / 9), height);
        }

        var width = Even((int)Math.Round((double)info.Width * height / info.Height));

        return (width, height);
    }

    private static int Even(int value) => Math.Max(value - (value % 2), 2);

    /// <summary>
    /// Сколько отдать звуку внутри общего битрейта. У тихого ролика забирать
    /// у картинки последние биты ради стерео 128 кбит/с смысла нет.
    /// </summary>
    private static long ChooseVideoAudio(long totalBps) => totalBps switch
    {
        > 1_000_000 => 128_000,
        > 500_000 => 96_000,
        > 250_000 => 64_000,
        _ => 48_000
    };

    /// <summary>
    /// Звук — единственный случай, где математика точная, а не оценочная:
    /// размер потока прямо равен битрейту на длительность.
    /// </summary>
    internal static FfmpegRunner.EncodeSettings? PlanAudio(double outputSeconds, long targetBytes)
    {
        if (outputSeconds <= 0 || targetBytes <= 0)
        {
            return null;
        }

        var available = (long)(targetBytes * 8 / outputSeconds * Undershoot);

        return Snap(available);
    }

    private static FfmpegRunner.EncodeSettings? CorrectAudio(
        FfmpegRunner.EncodeSettings previous,
        long actualBytes,
        long targetBytes)
    {
        if (actualBytes <= 0)
        {
            return null;
        }

        var ratio = Math.Clamp((double)targetBytes / actualBytes, 0.5, 1.0);
        var wanted = (long)(previous.AudioBitrateBps * ratio * Undershoot);

        // Круг обязан быть шагом вниз, иначе мы будем крутиться на месте
        return Snap(Math.Min(wanted, previous.AudioBitrateBps - 1));
    }

    /// <summary>
    /// Ближайшая ступень не выше запрошенного битрейта. Ниже последней — отказ:
    /// трёхчасовой подкаст в десять мегабайт это семь килобит, то есть ничего.
    /// </summary>
    private static FfmpegRunner.EncodeSettings? Snap(long bitrateBps)
    {
        foreach (var rung in AudioLadder)
        {
            if (rung <= bitrateBps)
            {
                return new FfmpegRunner.EncodeSettings(
                    AudioBitrateBps: rung,
                    Channels: rung >= 64_000 ? 2 : 1);
            }
        }

        return null;
    }

    /// <summary>
    /// Параметры гифки, которая уже лежит на диске: с них начинается спуск.
    /// </summary>
    private static FfmpegRunner.EncodeSettings CurrentGif(FfmpegRunner.MediaInfo info)
    {
        var width = info.Width > 0 ? Math.Min(info.Width, FfmpegRunner.MaxGifWidth) : FfmpegRunner.MaxGifWidth;
        var sourceFps = info.Video?.Fps ?? 0;
        var fps = sourceFps > 0 ? (int)Math.Round(Math.Min(sourceFps, FfmpegRunner.MaxGifFps)) : FfmpegRunner.MaxGifFps;

        return new FfmpegRunner.EncodeSettings(Width: width, Fps: Math.Max(fps, 1));
    }

    /// <summary>
    /// Шаг спуска для гифки. Битрейта у неё нет, размер определяется энтропией картинки,
    /// поэтому посчитать вперёд нельзя — только «сожми, померь, шагни». Зато шагать
    /// можно по правильным осям: размер примерно линеен по частоте кадров и примерно
    /// квадратичен по ширине, поэтому кадры дешевле отдавать первыми.
    /// </summary>
    private static FfmpegRunner.EncodeSettings? StepGif(
        FfmpegRunner.EncodeSettings previous,
        long actualBytes,
        long targetBytes)
    {
        if (actualBytes <= 0 || previous.Fps <= 0 || previous.Width <= 0)
        {
            return null;
        }

        var ratio = Math.Clamp((double)targetBytes / actualBytes, 0.1, 0.95);

        // 1. Кадры
        var fps = Math.Max(MinGifFps, (int)Math.Round(previous.Fps * ratio));
        var remaining = ratio / ((double)fps / previous.Fps);

        // 2. Палитра: следующая ступень вниз, если ужать ещё надо
        var colors = previous.Colors;

        if (remaining < 1)
        {
            var next = ColorLadder.FirstOrDefault(rung => colors == 0 || rung < colors);

            if (next != 0)
            {
                colors = next;

                // Ограничение палитры даёт примерно четверть объёма
                remaining /= 0.75;
            }
        }

        // 3. Ширина — в последнюю очередь: она заметнее всего
        var width = previous.Width;

        if (remaining < 1)
        {
            width = (int)Math.Round(width * Math.Sqrt(remaining));
        }

        width = Even(width);

        if (width < MinGifWidth || fps < MinGifFps)
        {
            return null;
        }

        if (width == previous.Width && fps == previous.Fps && colors == previous.Colors)
        {
            // Шагать больше некуда, а результат всё ещё велик
            return null;
        }

        return new FfmpegRunner.EncodeSettings(Width: width, Fps: fps, Colors: colors);
    }

    #endregion
}
