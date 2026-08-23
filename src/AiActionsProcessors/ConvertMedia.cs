using System.Globalization;

using Discord;
using Discord.WebSocket;

using MewoDiscord.Handlers;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.AiActionsProcessors;

/// <summary>
/// Процессор действия «сделай что-нибудь с этим файлом»: обрезать, кадрировать,
/// уменьшить, сменить формат, вытащить звук. Модель здесь работает переводчиком —
/// превращает фразу в типизированный план (<see cref="FfmpegRunner.MediaPlan"/>),
/// а саму работу делает ffmpeg по аргументам, собранным кодом. Командную строку
/// модель не пишет никогда. Генерации тут нет вовсе: операция механическая
/// и детерминированная.
/// За результатом закрепляется медиа-сессия: ответом на него можно попросить поправку
/// («ещё пять процентов снизу»), и тогда план уточняется, а выполняется всё равно
/// от исходника — круги правок не накапливают потери перекодирования.
/// </summary>
public static class ConvertMedia
{
    private const int DownloadTimeoutSeconds = 120;

    /// <summary>
    /// Список полей плана для файла, который видно: кроп здесь осмыслен, потому что
    /// размеры исходника известны и сообщаются модели.
    /// </summary>
    private const string PlanFields =
        """
        Поля (все необязательные, лишние не добавляй):
        "format" — желаемый формат результата: gif, mp4, webm, png, jpg, webp
        (для звуковой дорожки: mp3, m4a, opus, ogg);
        "audio" — true, если просят оставить только звуковую дорожку;
        "start" — с какой секунды начать (число);
        "end" — на какой секунде закончить (число);
        "crop" — объект {"x":число,"y":число,"w":число,"h":число} в пикселях исходника;
        "width" — желаемая ширина результата в пикселях (число);
        "fps" — частота кадров результата (число).

        Клади в объект только то, о чём просили. Если просьба непонятна
        или к файлу не относится, ответь пустым объектом: {}
        """;

    /// <summary>
    /// Добавка к промпту, когда пользователь уточняет уже сделанное. Ответ должен быть
    /// полным планом от исходника, а не разницей: выполняем мы всегда от оригинала,
    /// иначе каждый круг правок терял бы качество на перекодировании.
    /// </summary>
    private const string RefinePrompt =
        """
        К этому файлу уже применён план, и пользователь его уточняет.
        Применённый план: {{plan}}
        Верни ПОЛНЫЙ новый план относительно ИСХОДНОГО файла, а не разницу.
        Слова вроде «ещё», «сильнее», «чуть больше» считай от того, что получилось
        по применённому плану: проценты бери от его размеров, а не от исходных.
        """;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds)
    };

    /// <summary>
    /// Первый заход: файл ищется в самом сообщении или в том, на которое отвечают.
    /// </summary>
    public static async Task RunAsync(CustomAiActionContext context)
    {
        var message = context.Message;
        var holder = FindSource(message) != null ? (IMessage)message : context.Quoted;

        if (holder == null || FindSource(holder) is not { } source)
        {
            return;
        }

        await ExecuteAsync(message, context.Text, source, holder.Id, previous: null, previousAnchorId: null);
    }

    /// <summary>
    /// Продолжение медиа-сессии: ответ на прошлый результат. Гейт и HIT_PROMPT здесь
    /// не нужны — реплай в закреплённое сообщение сам по себе однозначен.
    /// </summary>
    public static async Task ContinueAsync(SocketUserMessage message, MediaSession session)
    {
        var text = DiscordMentions.Humanize(message.Content, message).Trim();

        if (text.Length == 0)
        {
            return;
        }

        // Исходник берём из того же сообщения, что и в первый раз: сессия помнит его
        // идентификатор, а не ссылку — ссылки Discord протухают
        var holder = await ChatGptSessionHandler.FetchMessageAsync(message.Channel, session.SourceMessageId);

        if (holder == null || FindSource(holder) is not { } source)
        {
            await ReplyAsync(message, BotEmbeds.Error(BotMessages.MediaSourceGone()));
            return;
        }

        await ExecuteAsync(
            message,
            text,
            source,
            session.SourceMessageId,
            MediaPlanParser.Parse(session.Plan),
            session.AnchorMessageId);
    }

    #region Internals

    /// <summary>
    /// Что именно обрабатываем: вложение или картинка из embed'а (гифка по ссылке).
    /// </summary>
    private record MediaSource(string Url, string FileName, long Size);

    private static async Task ExecuteAsync(
        SocketUserMessage message,
        string text,
        MediaSource source,
        ulong sourceMessageId,
        FfmpegRunner.MediaPlan? previous,
        ulong? previousAnchorId)
    {
        // Потолок входа — лимит вложений сервера, а не константа: свой же результат
        // на буст-тире бывает крупнее её, и круг правок упирался бы в отказ на ровном месте
        var limit = UploadLimit(message);

        if (source.Size > limit)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaTooBig(FormatSize(source.Size))));
            return;
        }

        // Слот занят другой операцией — ждать за чужим качанием видео бессмысленно
        using var workspace = await MediaWorkspace.TryAcquireAsync(MediaWorkspace.ConvertGrace);

        if (workspace == null)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaBusy()));
            return;
        }

        var inputPath = workspace.PathFor("source" + Path.GetExtension(source.FileName));

        if (!await DownloadAsync(source.Url, inputPath, limit))
        {
            await ReplyAsync(message, BotEmbeds.Error(BotMessages.MediaFailed()));
            return;
        }

        var info = await FfmpegRunner.ProbeAsync(inputPath);

        if (info == null)
        {
            await ReplyAsync(message, BotEmbeds.Error(BotMessages.MediaNotReadable()));
            return;
        }

        var plan = await AskPlanAsync(text, source.FileName, info, previous);

        if (plan == null || plan.IsEmpty)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaPlanUnclear()));
            return;
        }

        using var typing = message.Channel.EnterTypingState();

        var result = await FfmpegRunner.RunAsync(workspace, inputPath, source.FileName, plan, info);

        if (result.FilePath == null)
        {
            await ReplyAsync(message, BotEmbeds.Error(result.Error ?? BotMessages.MediaFailed()));
            return;
        }

        var size = new FileInfo(result.FilePath).Length;

        if (size > limit)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaResultTooBig(FormatSize(size))));
            return;
        }

        // Отправка обязана закончиться внутри using: вложение читается с диска потоком,
        // а Dispose рабочего каталога сносит файл
        var sent = await MediaReply.SendFileAsync(message, result.FilePath, result.FileName!);

        if (sent != null)
        {
            MediaSessionStore.Remember(
                sent.Id,
                message.Channel.Id,
                sourceMessageId,
                MediaPlanParser.Serialize(plan),
                previousAnchorId);
        }
    }

    /// <summary>
    /// Переводит фразу в план. Размеры и длительность уходят в промпт: без них модель
    /// не посчитает ни кроп, ни проценты.
    /// </summary>
    private static async Task<FfmpegRunner.MediaPlan?> AskPlanAsync(
        string text,
        string fileName,
        FfmpegRunner.MediaInfo info,
        FfmpegRunner.MediaPlan? previous)
    {
        var refine = previous == null || previous.IsEmpty
            ? string.Empty
            : "\n\n" + RefinePrompt.Replace("{{plan}}", MediaPlanParser.Serialize(previous));

        var answer = await ChatGptClient.AskInstantAsync(
            $"{MediaPlanParser.PromptHeader}\n\n{PlanFields}{refine}\n\n"
            + $"Исходный файл: {fileName}, {info.Width}x{info.Height}, "
            + $"длительность {info.DurationSeconds.ToString("0.#", CultureInfo.InvariantCulture)} с.\n\n"
            + $"Просьба пользователя:\n\"\"\"\n{text}\n\"\"\"");

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "🎬 План операции над {File}: {Plan}", fileName, answer);

        return MediaPlanParser.Parse(answer);
    }

    /// <summary>
    /// Ищет в сообщении файл, пригодный для ffmpeg. Вложение приоритетнее embed'а:
    /// у него есть настоящее имя и размер.
    /// </summary>
    private static MediaSource? FindSource(IMessage message)
    {
        foreach (var attachment in message.Attachments)
        {
            var isVideo = attachment.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
            var isImage = attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

            // Звук здесь есть, а в гейте нет намеренно: гейт ловит первое обращение,
            // а сюда приходит и продолжение сессии — правят в том числе вырезанную дорожку
            var isAudio = attachment.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

            if (isVideo || isImage || isAudio)
            {
                return new MediaSource(attachment.Url, attachment.Filename, attachment.Size);
            }
        }

        foreach (var embed in message.Embeds)
        {
            // У gifv настоящий файл — это mp4 в поле video: гифка на tenor только
            // выглядит гифкой, а хранится видео
            var url = embed.Video?.Url ?? embed.Image?.Url ?? embed.Thumbnail?.Url;

            if (url != null)
            {
                return new MediaSource(url, FileNameFromUrl(url), 0);
            }
        }

        return null;
    }

    private static string FileNameFromUrl(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var name = Path.GetFileName(path);

        return string.IsNullOrWhiteSpace(name) || !name.Contains('.') ? "media.mp4" : name;
    }

    private static long UploadLimit(SocketUserMessage message) =>
        (long)(((message.Channel as SocketGuildChannel)?.Guild)?.MaxUploadLimit ?? FfmpegRunner.MaxInputBytes);

    /// <summary>
    /// Качает файл на диск, а не в память: дальше с ним работает ffmpeg, которому
    /// всё равно нужен путь. Потолок проверяется по ходу — Content-Length может соврать.
    /// </summary>
    private static async Task<bool> DownloadAsync(string url, string path, long maxBytes)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return false;
            }

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(path);

            var buffer = new byte[81920];
            var total = 0L;
            int read;

            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                total += read;

                if (total > maxBytes)
                {
                    return false;
                }

                await target.WriteAsync(buffer.AsMemory(0, read));
            }

            return total > 0;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось скачать файл {Url}: {Message}", url, ex.Message);
            return false;
        }
    }

    private static async Task ReplyAsync(SocketUserMessage message, Embed embed) =>
        await MediaReply.SendEmbedAsync(message, embed);

    private static string FormatSize(long bytes) => $"{bytes / 1024d / 1024d:F1} МБ";

    #endregion
}
