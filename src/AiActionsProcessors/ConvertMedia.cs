using System.Globalization;
using System.Text.Json;

using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.AiActionsProcessors;

/// <summary>
/// Процессор действия «сделай что-нибудь с этим файлом»: обрезать, кадрировать,
/// уменьшить, сменить формат. Модель здесь работает переводчиком — превращает фразу
/// в типизированный план (<see cref="FfmpegRunner.MediaPlan"/>), а саму работу делает
/// ffmpeg по аргументам, собранным кодом. Командную строку модель не пишет никогда.
/// Генерации тут нет вовсе: операция механическая и детерминированная.
/// </summary>
public static class ConvertMedia
{
    private const int DownloadTimeoutSeconds = 120;

    /// <summary>
    /// Промпт-переводчик: свободная фраза → план операции. Формат ответа описан здесь же,
    /// поэтому менять его надо вместе с <see cref="ParsePlan"/>.
    /// </summary>
    private const string PlanPrompt =
        """
        Ты переводишь просьбу пользователя в план операции над медиафайлом.
        Ответь строго одним объектом JSON, без пояснений и без markdown.

        Поля (все необязательные, лишние не добавляй):
        "format" — желаемый формат результата: gif, mp4, webm, png, jpg, webp;
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

        var input = await DownloadAsync(source.Url);

        if (input == null)
        {
            await ReplyAsync(message, BotEmbeds.Error(BotMessages.MediaFailed()));
            return;
        }

        var info = await FfmpegRunner.ProbeAsync(input, source.FileName);

        if (info == null)
        {
            await ReplyAsync(message, BotEmbeds.Error(BotMessages.MediaNotReadable()));
            return;
        }

        // Размеры и длительность уходят в промпт: без них модель не сможет посчитать кроп
        var answer = await ChatGptClient.AskInstantAsync(
            $"{PlanPrompt}\n\nИсходный файл: {source.FileName}, {info.Width}x{info.Height}, "
            + $"длительность {info.DurationSeconds.ToString("0.#", CultureInfo.InvariantCulture)} с.\n\n"
            + $"Просьба пользователя:\n\"\"\"\n{context.Text}\n\"\"\"");

        var plan = ParsePlan(answer);

        if (plan == null || plan.IsEmpty)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaPlanUnclear()));
            return;
        }

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "🎬 План операции над {File}: {Plan}", source.FileName, answer);

        using var typing = message.Channel.EnterTypingState();

        var result = await FfmpegRunner.RunAsync(input, source.FileName, plan, info);

        if (result.Content == null)
        {
            await ReplyAsync(message, BotEmbeds.Error(result.Error ?? BotMessages.MediaFailed()));
            return;
        }

        var uploadLimit = ((message.Channel as SocketGuildChannel)?.Guild)?.MaxUploadLimit ?? FfmpegRunner.MaxInputBytes;

        if ((ulong)result.Content.Length > uploadLimit)
        {
            await ReplyAsync(message, BotEmbeds.Warning(BotMessages.MediaResultTooBig(FormatSize(result.Content.Length))));
            return;
        }

        await SendResultAsync(message, result);
    }

    #region Internals

    /// <summary>
    /// Что именно обрабатываем: вложение или картинка из embed'а (гифка по ссылке).
    /// </summary>
    private record MediaSource(string Url, string FileName, long Size);

    /// <summary>
    /// Разбирает ответ модели в план. null — это не JSON: считаем, что просьбу не поняли.
    /// Числа берутся мягко (модель может прислать строку) — но только числа,
    /// никаких строк в аргументы ffmpeg отсюда не попадает, кроме формата из белого списка.
    /// </summary>
    internal static FfmpegRunner.MediaPlan? ParsePlan(string answer)
    {
        var json = ExtractJson(answer);

        if (json == null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            FfmpegRunner.CropBox? crop = null;

            if (root.TryGetProperty("crop", out var cropValue) && cropValue.ValueKind == JsonValueKind.Object)
            {
                var x = ReadInt(cropValue, "x");
                var y = ReadInt(cropValue, "y");
                var width = ReadInt(cropValue, "w");
                var height = ReadInt(cropValue, "h");

                if (width is > 0 && height is > 0)
                {
                    crop = new FfmpegRunner.CropBox(x ?? 0, y ?? 0, width.Value, height.Value);
                }
            }

            return new FfmpegRunner.MediaPlan(
                ReadString(root, "format"),
                ReadDouble(root, "start"),
                ReadDouble(root, "end"),
                crop,
                ReadInt(root, "width"),
                ReadInt(root, "fps"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Вытаскивает объект JSON из ответа: модель любит обрамить его ```json.
    /// </summary>
    internal static string? ExtractJson(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');

        return start < 0 || end <= start ? null : answer[start..(end + 1)];
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        var value = ReadDouble(element, name);

        return value == null ? null : (int)Math.Round(value.Value);
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

    private static async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            var content = await Http.GetByteArrayAsync(url);

            return content.Length > FfmpegRunner.MaxInputBytes ? null : content;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось скачать файл {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    private static async Task SendResultAsync(SocketUserMessage message, FfmpegRunner.MediaResult result)
    {
        var attachment = new FileAttachment(new MemoryStream(result.Content!), result.FileName!);

        try
        {
            await message.Channel.SendFilesAsync(
                [attachment],
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(message.Id, failIfNotExists: false));
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось отправить результат конвертации: {Message}", ex.Message);
        }
        finally
        {
            attachment.Dispose();
        }
    }

    private static async Task ReplyAsync(SocketUserMessage message, Embed embed)
    {
        try
        {
            await message.Channel.SendMessageAsync(
                embed: embed,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(message.Id, failIfNotExists: false));
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось отправить сообщение действия: {Message}", ex.Message);
        }
    }

    private static string FormatSize(long bytes) => $"{bytes / 1024d / 1024d:F1} МБ";

    #endregion
}
