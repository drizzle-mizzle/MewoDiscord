using System.Globalization;
using System.Text.Json;

namespace MewoDiscord.Utils;

/// <summary>
/// Мягкое чтение полей из чужого JSON: ответов ffprobe, yt-dlp и веб-ручек соцсетей.
/// Отсутствующее поле, null и неожиданный тип — это не ошибка разбора, а «не сказали»:
/// формат нам не принадлежит и меняется без предупреждения.
/// </summary>
internal static class JsonRead
{
    /// <summary>
    /// Строковое поле. null — поля нет или оно не строка.
    /// </summary>
    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Числовое поле. Число может приехать и строкой — так делают и ffprobe, и yt-dlp,
    /// поэтому строковый вариант разбирается тем же методом. Не сказали — возвращается
    /// <paramref name="fallback"/>: ноль у этих ручек и означает «неизвестно»
    /// (нулевой битрейт или нулевая ширина кадра сами по себе не бывают).
    /// </summary>
    internal static double Number(JsonElement element, string name, double fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
