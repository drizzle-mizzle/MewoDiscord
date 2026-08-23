using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.AiActionsProcessors;

/// <summary>
/// Процессор действия «скачай это видео»: качает ролик с YouTube и, если просили,
/// обрабатывает его — обрезает, меняет формат, вытаскивает звук, делает гифку.
/// Порядок нарочно такой, чтобы не тратить впустую: сначала разведка форматов,
/// потом расчёт, влезет ли результат в лимит вложения, и только потом байты.
/// Заведомо безнадёжный случай (двухчасовой ролик в полсотни мегабайт — это каша)
/// отсекается до качания: тратить на него двадцать минут незачем.
/// Работа идёт под <see cref="MediaWorkspace"/>: один слот на всё и гарантированная
/// уборка, потому что промежуточные файлы здесь измеряются гигабайтами.
/// </summary>
public static partial class DownloadVideo
{
    /// <summary>
    /// Сколько всего времени отводится на запрос. Слот занят всё это время,
    /// и один патологический ролик не должен запирать бота на час.
    /// </summary>
    private const int MaxTotalMinutes = 45;

    private const int MaxFileNameLength = 64;

    /// <summary>
    /// Список полей плана для видео, которого мы ещё не видели: кроп сюда не входит —
    /// размеры кадра до скачивания неизвестны, и посчитать его модель не может.
    /// </summary>
    private const string PlanFields =
        """
        Поля (все необязательные, лишние не добавляй):
        "format" — желаемый формат результата: mp4, webm, gif
        (для звуковой дорожки: mp3, m4a, opus, ogg);
        "audio" — true, если просят только звук без картинки;
        "start" — с какой секунды начать (число);
        "end" — на какой секунде закончить (число);
        "width" — желаемая ширина результата в пикселях (число);
        "fps" — частота кадров результата (число).

        Время в просьбе бывает записано как «1:04» или «с 1 минуты 4 секунд» —
        переводи его в секунды числом.
        Клади в объект только то, о чём просили. Если просят просто скачать,
        ответь пустым объектом: {}
        """;

    public static async Task RunAsync(CustomAiActionContext context)
    {
        var message = context.Message;
        var videoId = YoutubeLinks.FirstVideoId(context.Text);

        if (videoId == null)
        {
            return;
        }

        // Ждать бессмысленно: впереди у занявшего слот могут быть минуты качания
        using var workspace = await MediaWorkspace.TryAcquireAsync(MediaWorkspace.DownloadGrace);

        if (workspace == null)
        {
            await MediaReply.SendEmbedAsync(message, BotEmbeds.Warning(BotMessages.MediaBusy()));
            return;
        }

        await ProcessAsync(context, videoId, workspace);
    }

    #region Internals

    private static async Task ProcessAsync(CustomAiActionContext context, string videoId, MediaWorkspace workspace)
    {
        var message = context.Message;
        var clock = Stopwatch.StartNew();

        var (meta, failure) = await YtDlpRunner.ProbeAsync(videoId);

        if (meta == null)
        {
            await MediaReply.SendEmbedAsync(message, BotEmbeds.Error(Explain(failure)));
            return;
        }

        // Отказы, которые видно по метаданным: ни одного байта на них не тратим
        if (meta.IsLiveOrUpcoming)
        {
            await MediaReply.SendEmbedAsync(message, BotEmbeds.Warning(BotMessages.YoutubeLive()));
            return;
        }

        if (meta.DurationSeconds <= 0)
        {
            await MediaReply.SendEmbedAsync(message, BotEmbeds.Error(BotMessages.YoutubeFailed()));
            return;
        }

        var maxDuration = AppConfig.MediaSettings.MaxDurationMinutes * 60d;

        if (meta.DurationSeconds > maxDuration)
        {
            await MediaReply.SendEmbedAsync(
                message,
                BotEmbeds.Warning(BotMessages.YoutubeTooLong(Duration(meta.DurationSeconds), Duration(maxDuration))));

            return;
        }

        var plan = await AskPlanAsync(context.Text, meta);
        var section = ResolveSection(plan, meta.DurationSeconds);
        var outputSeconds = section == null ? meta.DurationSeconds : section.Value.End - section.Value.Start;

        var uploadLimit = (long)(((message.Channel as SocketGuildChannel)?.Guild)?.MaxUploadLimit
            ?? FfmpegRunner.MaxInputBytes);

        var hardCap = (long)AppConfig.MediaSettings.MaxSourceMb * 1024 * 1024;
        var choice = YtDlpRunner.Choose(
            YtDlpRunner.BuildLadder(meta),
            uploadLimit,
            hardCap,
            meta.DurationSeconds > 0 ? outputSeconds / meta.DurationSeconds : 1);

        if (choice == null)
        {
            await MediaReply.SendEmbedAsync(message, BotEmbeds.Warning(BotMessages.YoutubeTooBig(Size(hardCap))));
            return;
        }

        // Расчёт уже знает, что смотрибельного результата не выйдет — незачем качать
        // два гигабайта ради этого вывода
        if (choice.OverLimit && !plan.AudioOnly && !MediaShrink.CanFitVideo(outputSeconds, Target(uploadLimit)))
        {
            await MediaReply.SendEmbedAsync(
                message,
                BotEmbeds.Warning(BotMessages.MediaShrinkFailed(Size(uploadLimit))));

            return;
        }

        if (!workspace.HasRoomFor(choice.EstimatedBytes))
        {
            await MediaReply.SendEmbedAsync(message, BotEmbeds.Warning(BotMessages.YoutubeNoRoom()));
            return;
        }

        var progress = await MediaReply.SendEmbedAsync(
            message,
            BotEmbeds.Info(BotMessages.YoutubeDownloading(Quality(choice.Height))));

        try
        {
            await DeliverAsync(context, videoId, workspace, meta, plan, section, choice, uploadLimit, outputSeconds, clock);
        }
        finally
        {
            await MediaReply.DeleteAsync(progress);
        }
    }

    private static async Task DeliverAsync(
        CustomAiActionContext context,
        string videoId,
        MediaWorkspace workspace,
        YtDlpRunner.VideoMeta meta,
        FfmpegRunner.MediaPlan plan,
        (double Start, double End)? section,
        YtDlpRunner.Choice choice,
        long uploadLimit,
        double outputSeconds,
        Stopwatch clock)
    {
        var message = context.Message;
        using var typing = message.Channel.EnterTypingState();

        var hardCap = (long)AppConfig.MediaSettings.MaxSourceMb * 1024 * 1024;
        var (source, failure) = await YtDlpRunner.DownloadAsync(videoId, choice, workspace, section, hardCap);

        if (source == null)
        {
            await MediaReply.SendEmbedAsync(message, BotEmbeds.Error(Explain(failure)));
            return;
        }

        var info = await FfmpegRunner.ProbeAsync(source);

        if (info == null)
        {
            await MediaReply.SendEmbedAsync(message, BotEmbeds.Error(BotMessages.YoutubeFailed()));
            return;
        }

        // Огрызок недокачанного файла ffprobe читает успешно и уверенно врёт про длину,
        // а на этой длине держится вся математика пережатия
        if (!DurationMatches(info.DurationSeconds, outputSeconds))
        {
            BotLogger.Warning(
                "Скачанный файл идёт {Actual} с вместо {Expected} с — считаю качание неудачным",
                info.DurationSeconds,
                outputSeconds);

            await MediaReply.SendEmbedAsync(message, BotEmbeds.Error(BotMessages.YoutubeFailed()));
            return;
        }

        // Обрезку уже сделал yt-dlp, второй раз она не нужна
        var rest = section != null && !choice.Manifest ? plan with { Start = null, End = null } : plan;
        var working = source;
        var displayName = SafeName(meta.Title);

        if (!rest.IsEmpty)
        {
            var processed = await FfmpegRunner.RunAsync(
                workspace,
                source,
                displayName,
                rest,
                info,
                FfmpegRunner.MediaLimits.Download(rest.Format ?? "mp4", info.DurationSeconds));

            if (processed.FilePath == null)
            {
                await MediaReply.SendEmbedAsync(message, BotEmbeds.Error(processed.Error ?? BotMessages.MediaFailed()));
                return;
            }

            working = processed.FilePath;
            displayName = processed.FileName!;
            info = await FfmpegRunner.ProbeAsync(working) ?? info;
        }
        else
        {
            displayName = Path.GetFileNameWithoutExtension(displayName) + Path.GetExtension(source);
        }

        var (result, shrunk) = await ShrinkAsync(workspace, working, info, uploadLimit, outputSeconds, clock, displayName);

        if (result == null)
        {
            await MediaReply.SendEmbedAsync(
                message,
                BotEmbeds.Warning(Path.GetExtension(working) == ".gif"
                    ? BotMessages.MediaGifTooHeavy()
                    : BotMessages.MediaShrinkFailed(Size(uploadLimit))));

            return;
        }

        var finalInfo = await FfmpegRunner.ProbeAsync(result) ?? info;
        var notes = new List<string> { Describe(result, finalInfo) };

        if (choice.Reduced && !choice.OverLimit)
        {
            notes.Add(BotMessages.YoutubeQualityReduced(Quality(choice.Height), Quality(choice.BestHeight)));
        }

        if (shrunk)
        {
            notes.Add(BotMessages.YoutubeRecompressed(Size(uploadLimit)));
        }

        // Отправка обязана закончиться внутри using рабочего каталога: вложение
        // читается с диска потоком, и снос каталога оборвал бы заливку
        await MediaReply.SendFileAsync(
            message,
            result,
            Path.GetFileNameWithoutExtension(displayName) + Path.GetExtension(result),
            string.Join('\n', notes));
    }

    /// <summary>
    /// Круги пережатия под лимит вложения. Каждый круг читает один и тот же исходник
    /// и сносит неудачный артефакт предыдущего: цепочка перекодирований накапливала бы
    /// потери, а мусор — место на диске.
    /// </summary>
    private static async Task<(string? Path, bool Shrunk)> ShrinkAsync(
        MediaWorkspace workspace,
        string workingPath,
        FfmpegRunner.MediaInfo info,
        long uploadLimit,
        double outputSeconds,
        Stopwatch clock,
        string displayName)
    {
        var target = Target(uploadLimit);
        var size = new FileInfo(workingPath).Length;

        if (size <= target)
        {
            return (workingPath, false);
        }

        var sourceFormat = Path.GetExtension(workingPath).TrimStart('.').ToLowerInvariant();
        var kind = ResolveKind(sourceFormat);

        // Видео пережимаем всегда в mp4: круг кодирует H.264, а webm его не принимает —
        // там свои кодеки. Гифка и звук остаются в своём контейнере
        var format = kind == MediaShrink.ShrinkKind.Video ? "mp4" : sourceFormat;
        var settings = MediaShrink.Plan(kind, info, outputSeconds, target, size);
        string? previous = null;

        for (var attempt = 1; attempt <= MediaShrink.MaxAttempts; attempt++)
        {
            if (settings == null)
            {
                break;
            }

            if (clock.Elapsed.TotalMinutes > MaxTotalMinutes)
            {
                BotLogger.Warning("Запрос идёт дольше {Minutes} минут — прекращаю пережатие", MaxTotalMinutes);
                break;
            }

            if (previous != null)
            {
                // Круг начинается с чистого места: неудачный артефакт нам больше не нужен
                workspace.Delete(previous);
            }

            var outputPath = workspace.PathFor($"out-{attempt}.{format}");

            var encoded = await FfmpegRunner.EncodeAsync(
                workspace,
                workingPath,
                outputPath,
                format,
                settings,
                new FfmpegRunner.MediaPlan(),
                info,
                displayName);

            if (encoded.FilePath == null)
            {
                break;
            }

            previous = encoded.FilePath;
            var encodedSize = new FileInfo(encoded.FilePath).Length;

            BotLogger.Information(
                "Круг {Attempt}: получилось {Size}, цель {Target}",
                attempt,
                Size(encodedSize),
                Size(target));

            if (encodedSize <= target)
            {
                return (encoded.FilePath, true);
            }

            settings = MediaShrink.Correct(kind, settings, encodedSize, target, info, outputSeconds);
        }

        if (previous != null)
        {
            workspace.Delete(previous);
        }

        return (null, true);
    }

    private static MediaShrink.ShrinkKind ResolveKind(string format)
    {
        if (format == "gif")
        {
            return MediaShrink.ShrinkKind.Gif;
        }

        return FfmpegRunner.AllowedAudioFormats.Contains(format)
            ? MediaShrink.ShrinkKind.Audio
            : MediaShrink.ShrinkKind.Video;
    }

    /// <summary>
    /// Переводит свободную фразу в план операции. Просто «скачай» даёт пустой план —
    /// это нормально и означает «отдай как есть».
    /// </summary>
    private static async Task<FfmpegRunner.MediaPlan> AskPlanAsync(string text, YtDlpRunner.VideoMeta meta)
    {
        var answer = await ChatGptClient.AskInstantAsync(
            $"{MediaPlanParser.PromptHeader}\n\n{PlanFields}\n\n"
            + $"Видео идёт {meta.DurationSeconds.ToString("0.#", CultureInfo.InvariantCulture)} с.\n\n"
            + $"Просьба пользователя:\n\"\"\"\n{text}\n\"\"\"");

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "📺 План для {Title}: {Plan}", meta.Title, answer);

        return MediaPlanParser.Parse(answer) ?? new FfmpegRunner.MediaPlan();
    }

    /// <summary>
    /// Отрезок, который просили. Качать его отрезком — главный выигрыш всей затеи:
    /// полминуты из трёхчасового ролика это мегабайты, а не гигабайты.
    /// </summary>
    private static (double Start, double End)? ResolveSection(FfmpegRunner.MediaPlan plan, double duration)
    {
        if (plan.Start == null && plan.End == null)
        {
            return null;
        }

        var start = Math.Clamp(plan.Start ?? 0, 0, Math.Max(duration - 1, 0));
        var end = Math.Clamp(plan.End ?? duration, start, duration);

        return end > start ? (start, end) : null;
    }

    /// <summary>
    /// Сошлась ли длительность скачанного с ожидаемой. Допуск нужен: точный рез
    /// двигает границы к ближайшим кадрам, а контейнер округляет длительность.
    /// </summary>
    private static bool DurationMatches(double actual, double expected) =>
        expected <= 0 || Math.Abs(actual - expected) <= Math.Max(2, expected * 0.1);

    /// <summary>
    /// Мета-подпись к результату. Строится по факту — из того, что показал ffprobe
    /// на готовом файле, а не из того, что мы собирались получить.
    /// Едет мелким текстом в самом сообщении, а не embed'ом: та же роль, что
    /// у подписи под медиа из Telegram, и с вложением embed уживается хуже.
    /// </summary>
    private static string Describe(string path, FfmpegRunner.MediaInfo info)
    {
        var container = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

        var resolution = info.Video == null
            ? Duration(info.DurationSeconds)
            : $"{info.Video.Width}×{info.Video.Height}, {info.Video.Fps:0.#} к/с, {Duration(info.DurationSeconds)}";

        var video = info.Video == null ? "—" : $"{info.Video.Codec} {Bitrate(info.Video.BitrateBps)}";

        var audio = info.Audio == null
            ? BotMessages.YoutubeNoAudio()
            : $"{info.Audio.Codec}, {Channels(info.Audio.Channels)}, {Bitrate(info.Audio.BitrateBps)}";

        return BotMessages.YoutubeMeta(container, resolution, video, audio);
    }

    private static string Explain(YtDlpRunner.YtDlpFailure failure) => failure switch
    {
        YtDlpRunner.YtDlpFailure.Live => BotMessages.YoutubeLive(),
        YtDlpRunner.YtDlpFailure.AgeRestricted => BotMessages.YoutubeAgeRestricted(),
        YtDlpRunner.YtDlpFailure.BotCheck => BotMessages.YoutubeBotCheck(),
        YtDlpRunner.YtDlpFailure.Private => BotMessages.YoutubePrivate(),
        YtDlpRunner.YtDlpFailure.GeoBlocked => BotMessages.YoutubeGeoBlocked(),
        YtDlpRunner.YtDlpFailure.Unavailable => BotMessages.YoutubeUnavailable(),
        YtDlpRunner.YtDlpFailure.Outdated => BotMessages.YoutubeToolOutdated(),
        YtDlpRunner.YtDlpFailure.JsRuntime => BotMessages.YoutubeJsRuntime(),
        _ => BotMessages.YoutubeFailed()
    };

    /// <summary>
    /// Целимся ниже лимита: в теле запроса едет не только файл, но и разметка
    /// многочастного тела, а она тоже считается.
    /// </summary>
    private static long Target(long uploadLimit) => (long)(uploadLimit * YtDlpRunner.FitSafetyFactor);

    /// <summary>
    /// Имя файла для чата из заголовка видео. В заголовках бывают слэши, юникод
    /// и двести символов, поэтому от него остаётся только безопасная часть.
    /// </summary>
    private static string SafeName(string title)
    {
        var name = UnsafeFileCharsRegex().Replace(title, string.Empty).Trim();

        if (name.Length > MaxFileNameLength)
        {
            name = name[..MaxFileNameLength].Trim();
        }

        return (name.Length == 0 ? "video" : name) + ".mp4";
    }

    private static string Quality(int height) => height > 0 ? $"{height}p" : "минимальном";

    private static string Size(long bytes) => $"{bytes / 1024d / 1024d:F0} МБ";

    private static string Bitrate(long bitsPerSecond) =>
        bitsPerSecond <= 0 ? "?" : $"{bitsPerSecond / 1000d / 1000d:0.##} Мбит/с";

    private static string Channels(int channels) => channels switch
    {
        <= 0 => "?",
        1 => "моно",
        2 => "стерео",
        _ => $"{channels} канала"
    };

    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);

        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    [GeneratedRegex("""[\\/:*?"<>|\r\n]""")]
    private static partial Regex UnsafeFileCharsRegex();

    #endregion
}
