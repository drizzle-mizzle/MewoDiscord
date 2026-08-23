using System.Globalization;

using Discord;
using Discord.WebSocket;

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

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds)
    };

    public static async Task RunAsync(CustomAiActionContext context)
    {
        var message = context.Message;
        var source = FindSource(message) ?? (context.Quoted == null ? null : FindSource(context.Quoted));

        if (source == null)
        {
            return;
        }

        if (source.Size > FfmpegRunner.MaxInputBytes)
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

        if (!await DownloadAsync(source.Url, inputPath))
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

        // Размеры и длительность уходят в промпт: без них модель не сможет посчитать кроп
        var answer = await ChatGptClient.AskInstantAsync(
            $"{MediaPlanParser.PromptHeader}\n\n{PlanFields}\n\n"
            + $"Исходный файл: {source.FileName}, {info.Width}x{info.Height}, "
            + $"длительность {info.DurationSeconds.ToString("0.#", CultureInfo.InvariantCulture)} с.\n\n"
            + $"Просьба пользователя:\n\"\"\"\n{context.Text}\n\"\"\"");

        var plan = MediaPlanParser.Parse(answer);

        if (plan == null || plan.IsEmpty)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaPlanUnclear()));
            return;
        }

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "🎬 План операции над {File}: {Plan}", source.FileName, answer);

        using var typing = message.Channel.EnterTypingState();

        var result = await FfmpegRunner.RunAsync(workspace, inputPath, source.FileName, plan, info);

        if (result.FilePath == null)
        {
            await ReplyAsync(message, BotEmbeds.Error(result.Error ?? BotMessages.MediaFailed()));
            return;
        }

        var uploadLimit = ((message.Channel as SocketGuildChannel)?.Guild)?.MaxUploadLimit ?? FfmpegRunner.MaxInputBytes;
        var size = new FileInfo(result.FilePath).Length;

        if ((ulong)size > uploadLimit)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaResultTooBig(FormatSize(size))));
            return;
        }

        // Отправка обязана закончиться внутри using: вложение читается с диска потоком,
        // а Dispose рабочего каталога сносит файл
        await MediaReply.SendFileAsync(message, result.FilePath, result.FileName!);
    }

    #region Internals

    /// <summary>
    /// Что именно обрабатываем: вложение или картинка из embed'а (гифка по ссылке).
    /// </summary>
    private record MediaSource(string Url, string FileName, long Size);

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

            if (isVideo || isImage)
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

    /// <summary>
    /// Качает файл на диск, а не в память: дальше с ним работает ffmpeg, которому
    /// всё равно нужен путь. Потолок проверяется по ходу — Content-Length может соврать.
    /// </summary>
    private static async Task<bool> DownloadAsync(string url, string path)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > FfmpegRunner.MaxInputBytes)
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

                if (total > FfmpegRunner.MaxInputBytes)
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
