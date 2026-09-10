using System.Text.Json;

namespace MewoDiscord.DevBot;

/// <summary>
/// Мягкое чтение полей ответа Discord: нет поля или тип не тот — значит, не сказали.
/// </summary>
public static class Json
{
    public static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    public static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number ? field.GetInt32() : 0;

    public static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number ? field.GetInt64() : 0;

    public static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;

    public static JsonElement? Object(JsonElement element, string name) =>
        element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object ? field : null;

    public static IEnumerable<JsonElement> Array(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Array
            ? field.EnumerateArray()
            : [];
}
