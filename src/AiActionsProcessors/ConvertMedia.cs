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
    /// Потолок картинки, уходящей модели. Она смотрит на неподвижный кадр, и десяток
    /// мегабайт в запросе не даёт ничего, кроме времени ожидания.
    /// </summary>
    private const long MaxModelImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Сторона, до которой ужимается картинка перед отправкой модели. Фото с телефона
    /// бывает и на четыре тысячи пикселей, а рисует модель около тысячи по стороне —
    /// разница уходит в ожидание и ни во что больше.
    /// </summary>
    private const int MaxModelImageSide = 1536;

    /// <summary>
    /// До какой длительности файл считается неподвижной картинкой. Ноль тут не годится:
    /// ffprobe отдаёт для одиночного кадра длительность в одну сороковую секунды,
    /// и проверка «строго ноль» отбраковывала обычный jpg с телефона.
    /// </summary>
    private const double MaxStillSeconds = 1;

    /// <summary>
    /// Список полей плана для файла, который видно: кроп здесь осмыслен, потому что
    /// размеры исходника известны и сообщаются модели.
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

        // Пустой план — надёжный признак, что просьба не механическая: его вернула та же
        // модель, которой перечислен весь список операций. «Вырежи персонажа с фона»
        // сюда доезжает именно так, и тупик «не понял, что сделать» был бы враньём —
        // сделать можно, просто не ffmpeg'ом
        if (plan == null || plan.IsEmpty)
        {
            var image = await PrepareForModelAsync(workspace, inputPath, source.FileName, info);

            // Слот больше не нужен, а поход в модель занимает минуту: отпускаем заранее,
            // чтобы чужая обрезка не ждала чужой перерисовки. Dispose идемпотентен,
            // внешний using повторит его без последствий
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
        info.Video != null && info.DurationSeconds < MaxStillSeconds;

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
            new FfmpegRunner.MediaLimits(MaxStillSeconds, MaxModelImageSide, 0));

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
