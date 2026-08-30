using System.Globalization;

using Discord;
using Discord.WebSocket;

using MewoDiscord.Handlers;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.AiActionsProcessors;

/// <summary>
/// Процессор действия «сделай что-нибудь с этим файлом»: обрезать, кадрировать,
/// уменьшить, сменить формат, вытащить звук. Модель работает переводчиком — превращает
/// фразу в типизированный план (<see cref="FfmpegRunner.MediaPlan"/>), а работу делает
/// ffmpeg по аргументам, собранным кодом; командную строку модель не пишет никогда.
/// За результатом закрепляется медиа-сессия: ответом на него можно попросить поправку,
/// и тогда план уточняется, но выполняется всё равно от исходника — круги правок
/// не накапливают потери перекодирования.
/// </summary>
public static class ConvertMedia
{
    private const int DownloadTimeoutSeconds = 120;

    private const long MaxModelImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Сторона, до которой ужимается картинка перед отправкой модели: рисует она около
    /// тысячи пикселей по стороне, а фото с телефона бывает вчетверо больше.
    /// </summary>
    private const int MaxModelImageSide = 1536;

    /// <summary>
    /// Чем занят слот медиа, пока идёт операция: это видит тот, кому слота не хватило.
    /// </summary>
    private const string BusyDescription = "обрабатываю файл из чата";

    /// <summary>
    /// Поля плана для файла, который видно: кроп здесь осмыслен — размеры исходника
    /// известны и сообщаются модели.
    /// </summary>
    private const string PlanFields =
        """
        Это план для ffmpeg. Он умеет ровно семь вещей и ничего сверх них:
        1. сменить формат — gif, mp4, webm, png, jpg, webp;
        2. вытащить звуковую дорожку — mp3, m4a, opus, ogg;
        3. обрезать по времени — только видео, гифку и звук;
        4. вырезать прямоугольную область кадра по координатам;
        5. изменить размер;
        6. изменить частоту кадров — только видео и гифку;
        7. взять один кадр из видео или гифки.

        Он НЕ умеет ничего, что меняет содержимое кадра: дорисовать, убрать или
        заменить предмет, отделить объект от фона, перекрасить, улучшить качество,
        дорисовать края, сгенерировать новое изображение. Это работа не для него.

        Поля (все необязательные, лишние не добавляй):
        "format" — желаемый формат результата: gif, mp4, webm, png, jpg, webp
        (для звуковой дорожки: mp3, m4a, opus, ogg);
        "audio" — true, если просят оставить только звуковую дорожку;
        "start" — с какой секунды начать (число);
        "end" — на какой секунде закончить (число);
        "crop" — объект {"x":число,"y":число,"w":число,"h":число} в пикселях исходника;
        "width" — желаемая ширина результата в пикселях (число);
        "fps" — частота кадров результата (число).

        Клади в объект только то, о чём просили.
        Если просьба не сводится к семи операциям выше — ответь пустым объектом: {}
        Пустой объект здесь нормальный ответ, а не признание ошибки: такую просьбу
        выполнит другой механизм. Не пытайся подобрать похожую операцию из списка.
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

    private static readonly HttpClient _http = new()
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
        // Скачивание файла, ffprobe и поход в модель за планом занимают десятки секунд
        using var typing = message.Channel.EnterTypingState();

        // Потолок входа — лимит вложений сервера, а не константа: свой же результат
        // на буст-тире бывает крупнее её, и круг правок упирался бы в отказ на ровном месте
        var limit = UploadLimit(message);

        if (source.Size > limit)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaTooBig(DiscordLimits.FormatSize(source.Size))));
            return;
        }

        // Слот занят другой операцией — ждать за чужим качанием видео бессмысленно
        using var workspace = await MediaWorkspace.TryAcquireAsync(MediaWorkspace.ConvertGrace, BusyDescription);

        if (workspace == null)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaBusy(MediaWorkspace.BusyWith)));
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

        var (plan, modelAnswered) = await AskPlanAsync(text, source.FileName, info, previous);

        // Модель промолчала вовсе — это сбой сети или прокси, а не «просьба не механическая»:
        // молча увозить такую просьбу в чат к той же неотвечающей модели незачем
        if (!modelAnswered)
        {
            await ReplyAsync(message, BotEmbeds.Error(BotMessages.MediaPlanFailed()));
            return;
        }

        // Пустой план — надёжный признак, что просьба не механическая: его вернула та же
        // модель, которой перечислен весь список операций. «Вырежи персонажа с фона»
        // доезжает сюда и уходит модели
        if (plan == null || plan.IsEmpty)
        {
            var image = await PrepareForModelAsync(workspace, inputPath, source.FileName, info);

            // Слот больше не нужен, а поход в модель занимает минуту: отпускаем заранее.
            // Dispose идемпотентен, внешний using повторит его без последствий
            workspace.Dispose();

            if (image == null)
            {
                // Механической операции не вышло, творческой тоже: значит это видео
                // или гифка, а покадрово их не перерисовать
                await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaModelNeedsStill()));
                return;
            }

            await HandOffToModelAsync(message, text, source.FileName, image, previousAnchorId);
            return;
        }

        var result = await FfmpegRunner.RunAsync(workspace, inputPath, source.FileName, plan, info);

        if (result.FilePath == null)
        {
            await ReplyAsync(message, BotEmbeds.Error(result.Error ?? BotMessages.MediaFailed()));
            return;
        }

        var size = new FileInfo(result.FilePath).Length;

        if (size > limit)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaResultTooBig(DiscordLimits.FormatSize(size))));
            return;
        }

        // Потолок обработки мог отрезать хвост, о котором не просили: молча отдать
        // первые пять минут восьмиминутного видео — значит выглядеть сломанным
        var note = result.TruncatedSeconds > 0
            ? BotMessages.MediaTruncated(DiscordLimits.FormatDuration(result.TruncatedSeconds))
            : null;

        // Отправка обязана закончиться внутри using: вложение читается с диска потоком,
        // а Dispose рабочего каталога сносит файл
        var sent = await MediaReply.SendFileAsync(message, result.FilePath, result.FileName!, note);

        if (sent != null)
        {
            MediaSessionStore.Remember(
                sent.Id,
                message.Channel.Id,
                sourceMessageId,
                MediaPlanParser.Serialize(plan),
                previousAnchorId);

            // Если на прошлом якоре висела и сессия ChatGPT, переносим её следом:
            // тогда «а теперь добавь усики» после механической правки продолжит
            // тот же разговор, а не начнёт с чистого листа
            if (previousAnchorId != null
                && ChatGptSessionStore.FindByMessageId(previousAnchorId.Value) is { } chat)
            {
                ChatGptSessionStore.Rebind(chat, sent.Id);
            }
        }
    }

    /// <summary>
    /// Неподвижная ли это картинка — то есть можно ли отдать её модели на перерисовку.
    /// Сравнение с нулём тут не работает: ffprobe отдаёт для одиночного кадра
    /// длительность в одну сороковую секунды, и строгая проверка отбраковывала
    /// обычное фото с телефона.
    /// </summary>
    internal static bool IsStillImage(FfmpegRunner.MediaInfo info) =>
        info.Video != null && info.DurationSeconds < FfmpegRunner.MaxStillSeconds;

    /// <summary>
    /// Готовит кадр для модели. Тяжёлое или крупное ужимается тем же ffmpeg, а не
    /// отбраковывается: отказ из-за размера пользователь читает как «бот не умеет»,
    /// хотя уметь тут нечего — достаточно уменьшить. Формат сохраняется исходный,
    /// чтобы не терять прозрачность у png.
    /// null — только для того, что моделью действительно не правится.
    /// </summary>
    private static async Task<byte[]?> PrepareForModelAsync(
        MediaWorkspace workspace,
        string inputPath,
        string fileName,
        FfmpegRunner.MediaInfo info)
    {
        if (!IsStillImage(info))
        {
            BotLogger.Information(
                "Творческая правка не для этого файла: длительность {Duration} с, видеодорожка {HasVideo}",
                info.DurationSeconds,
                info.Video != null);

            return null;
        }

        var size = new FileInfo(inputPath).Length;

        if (size <= MaxModelImageBytes && info.Width <= MaxModelImageSide && info.Height <= MaxModelImageSide)
        {
            return await File.ReadAllBytesAsync(inputPath);
        }

        var prepared = await FfmpegRunner.RunAsync(
            workspace,
            inputPath,
            fileName,
            new FfmpegRunner.MediaPlan(Width: MaxModelImageSide),
            info,
            new FfmpegRunner.MediaLimits(FfmpegRunner.MaxStillSeconds, MaxModelImageSide, 0));

        if (prepared.FilePath == null)
        {
            BotLogger.Warning("Не удалось ужать картинку для модели: {Error}", prepared.Error ?? "?");
            return null;
        }

        var reduced = new FileInfo(prepared.FilePath).Length;

        if (reduced > MaxModelImageBytes)
        {
            BotLogger.Warning("Картинка осталась на {Size} байт даже после уменьшения", reduced);
            return null;
        }

        BotLogger.Information("Картинка для модели ужата: {Before} → {After} байт", size, reduced);

        return await File.ReadAllBytesAsync(prepared.FilePath);
    }

    /// <summary>
    /// Отдаёт просьбу штатной сессии ChatGPT с текущей картинкой во вложении.
    /// Картинка прикладывается всегда, а не полагается на память сессии: после
    /// механической правки модель помнит прошлый кадр, и «добавь усики» ушли бы
    /// на необрезанную версию.
    /// </summary>
    private static async Task HandOffToModelAsync(
        SocketUserMessage message,
        string text,
        string fileName,
        byte[] image,
        ulong? previousAnchorId)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        if (guild == null)
        {
            return;
        }

        var anchorId = previousAnchorId ?? message.Id;

        var entry = ChatGptSessionStore.FindByMessageId(anchorId)
            ?? ChatGptSessionStore.Create(guild.Id, message.Channel.Id, anchorId);

        var author = DiscordMentions.DisplayNameOf(message.Author);

        BotLogger.LogAi(
            BotLogger.ChatGptThreadKey,
            "🎨 Просьба не механическая — отдаю модели в сессию {Id}: {Text}",
            entry.Id,
            text);

        await entry.Lock.WaitAsync();

        IUserMessage? sent;

        try
        {
            using var typing = message.Channel.EnterTypingState();

            sent = await ChatGptSessionHandler.RunTurnAsync(
                message.Channel,
                message.Id,
                entry,
                new ChatGptSessionHandler.TurnRequest(
                    text,
                    [new ChatGptClient.InputFile(fileName, image, MimeOf(fileName))],
                    new ChatGptClient.ChatContext(guild.CurrentUser.DisplayName, author),
                    new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { [author] = message.Author.Id }));
        }
        finally
        {
            entry.Lock.Release();
        }

        // Медиа-сессия переезжает на ответ модели, только если та прислала картинку:
        // над текстовым ответом обрезать нечего, и тогда дальше разговор ведёт ChatGPT
        if (sent is { Attachments.Count: > 0 })
        {
            MediaSessionStore.Remember(
                sent.Id,
                message.Channel.Id,
                sent.Id,
                MediaPlanParser.Serialize(new FfmpegRunner.MediaPlan()),
                previousAnchorId);
        }
    }

    private static string MimeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg"
    };

    /// <summary>
    /// Переводит фразу в план. Размеры и длительность уходят в промпт: без них модель
    /// не посчитает ни кроп, ни проценты.
    /// </summary>
    /// <returns>
    /// План (null — модель ответила, но плана в ответе нет: просьба не механическая)
    /// и признак того, что ответ вообще был. Молчание — это сбой сети или прокси,
    /// и путать его с осознанным «тут нужна не ffmpeg-операция» нельзя.
    /// </returns>
    private static async Task<(FfmpegRunner.MediaPlan? Plan, bool Answered)> AskPlanAsync(
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

        return string.IsNullOrWhiteSpace(answer)
            ? (null, false)
            : (MediaPlanParser.Parse(answer), true);
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
                return new MediaSource(url, DiscordLimits.FileNameFromUrl(url, "media.mp4", requireExtension: true), 0);
            }
        }

        return null;
    }

    private static long UploadLimit(SocketUserMessage message) =>
        (long)DiscordLimits.UploadLimit(message, FfmpegRunner.MaxInputBytes);

    /// <summary>
    /// Качает файл на диск, а не в память: дальше с ним работает ffmpeg, которому
    /// всё равно нужен путь. Потолок проверяется по ходу — Content-Length может соврать.
    /// </summary>
    private static async Task<bool> DownloadAsync(string url, string path, long maxBytes)
    {
        try
        {
            // Таймаут клиента кончается на заголовках: тело мы читаем сами, и замолчавший
            // сервер держал бы это чтение бесконечно, а с ним и слот медиа
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(DownloadTimeoutSeconds));

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return false;
            }

            await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var target = File.Create(path);

            var buffer = new byte[81920];
            var total = 0L;
            int read;

            while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
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

    #endregion
}
