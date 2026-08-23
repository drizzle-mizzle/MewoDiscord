using System.Globalization;
using System.Text.Json;

namespace MewoDiscord.Utils;

/// <summary>
/// Разбор ответа модели в типизированный план операции (<see cref="FfmpegRunner.MediaPlan"/>).
/// Общий для всех процессоров, работающих с медиа: модель здесь переводчик, а не автор
/// команд — из её ответа в аргументы ffmpeg попадают только числа и формат из белого списка.
/// Формат ответа описан в <see cref="PromptHeader"/> и списках полей у процессоров,
/// поэтому менять их надо вместе с <see cref="Parse"/>.
/// </summary>
public static class MediaPlanParser
{
    /// <summary>
    /// Общая шапка промпта-переводчика. Списки полей у процессоров разные: кроп осмыслен
    /// для файла, который видно, и бессмыслен для ещё не скачанного видео.
    /// </summary>
    internal const string PromptHeader =
        """
        Ты переводишь просьбу пользователя в план операции над медиафайлом.
        Ответь строго одним объектом JSON, без пояснений и без markdown.
        """;

    /// <summary>
    /// Разбирает ответ модели в план. null — это не JSON: считаем, что просьбу не поняли.
    /// Числа берутся мягко (модель может прислать строку) — но только числа,
    /// никаких строк в аргументы ffmpeg отсюда не попадает, кроме формата,
    /// который дальше проверяется по белому списку.
    /// </summary>
    public static FfmpegRunner.MediaPlan? Parse(string answer)
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
                ReadInt(root, "fps"),
                ReadBool(root, "audio"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Обратная операция: план в тот же JSON, что понимает <see cref="Parse"/>.
    /// Нужен медиа-сессии — накопленный план уезжает в БД и обратно в промпт, чтобы
    /// уточнение вроде «ещё пять процентов снизу» считалось от текущей картинки.
    /// Сериализуется штатным писателем, а не склейкой строк: он же и экранирует,
    /// поэтому ни табуляции, ни перевода строки в результате не будет — а на этом
    /// держится формат файла сессий.
    /// </summary>
    public static string Serialize(FfmpegRunner.MediaPlan plan)
    {
        var map = new Dictionary<string, object>();

        if (plan.Format != null)
        {
            map["format"] = plan.Format;
        }

        if (plan.Start != null)
        {
            map["start"] = plan.Start.Value;
        }

        if (plan.End != null)
        {
            map["end"] = plan.End.Value;
        }

        if (plan.Crop != null)
        {
            map["crop"] = new Dictionary<string, int>
            {
                ["x"] = plan.Crop.X,
                ["y"] = plan.Crop.Y,
                ["w"] = plan.Crop.Width,
                ["h"] = plan.Crop.Height
            };
        }

        if (plan.Width != null)
        {
            map["width"] = plan.Width.Value;
        }

        if (plan.Fps != null)
        {
            map["fps"] = plan.Fps.Value;
        }

        if (plan.AudioOnly)
        {
            map["audio"] = true;
        }

        return JsonSerializer.Serialize(map);
    }

    #region Internals

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

    /// <summary>
    /// Булево поле. Модель отвечает и настоящим true, и строкой «true» — берём оба.
    /// </summary>
    private static bool ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

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

    #endregion
}
