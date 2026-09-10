using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MewoDiscord.DevBot;

/// <summary>
/// Вывод сообщений в консоль. Embed'ы и компоненты печатаются JSON'ом как есть: ради них
/// dev-бот и нужен — увидеть, что на самом деле лежит в сообщении, а не как это нарисовал клиент.
/// </summary>
public static class MessagePrinter
{
    private const int PreviewLength = 100;

    private static readonly JsonSerializerOptions _pretty = new()
    {
        WriteIndented = true,

        // Кириллица и эмодзи — как есть, а не \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Флаги сообщения, которые нам встречаются; остальное видно по числу.
    /// </summary>
    private static readonly (long Bit, string Name)[] _flags =
    [
        (1 << 2, "SuppressEmbeds"),
        (1 << 5, "HasThread"),
        (1 << 12, "SuppressNotifications"),
        (1 << 15, "ComponentsV2")
    ];

    public static void PrintMessage(JsonElement message, string? channelName, bool raw)
    {
        if (raw)
        {
            Console.WriteLine(Pretty(message));
            return;
        }

        var channelId = Json.Text(message, "channel_id");
        Console.WriteLine($"Канал:     {(channelName == null ? channelId : $"#{channelName} ({channelId})")}");
        Console.WriteLine($"Сообщение: {Json.Text(message, "id")}, {Time(Json.Text(message, "timestamp"))}");
        Console.WriteLine($"Автор:     {Author(message)}");

        var edited = Json.Text(message, "edited_timestamp");

        if (edited != null)
        {
            Console.WriteLine($"Изменено:  {Time(edited)}");
        }

        var flags = Json.Long(message, "flags");

        if (flags != 0)
        {
            Console.WriteLine($"Флаги:     {flags} ({string.Join(", ", _flags.Where(f => (flags & f.Bit) != 0).Select(f => f.Name))})");
        }

        var reference = Json.Text(Json.Object(message, "message_reference"), "message_id");

        if (reference != null)
        {
            Console.WriteLine($"Ответ на:  {reference}");
        }

        var content = Json.Text(message, "content");
        Console.WriteLine();
        Console.WriteLine(string.IsNullOrEmpty(content) ? "Текст: (пусто)" : $"Текст:\n{content}");

        PrintJsonArray(message, "embeds", "Embed'ы");
        PrintJsonArray(message, "components", "Компоненты");

        foreach (var attachment in Json.Array(message, "attachments"))
        {
            Console.WriteLine(
                $"Вложение: {Json.Text(attachment, "filename")} ({Json.Long(attachment, "size")} байт, "
                + $"{Json.Text(attachment, "content_type")}) {Json.Text(attachment, "url")}");
        }
    }

    /// <summary>
    /// Список сообщений строкой на каждое: время, id, автор, начало текста и заголовки embed'ов.
    /// </summary>
    public static void PrintList(IEnumerable<JsonElement> messages)
    {
        foreach (var message in messages)
        {
            var line = $"{Time(Json.Text(message, "timestamp"))}  {Json.Text(message, "id")}  {Author(message)}: "
                + Preview(Json.Text(message, "content"));

            var titles = Json.Array(message, "embeds")
                .Select(embed => Json.Text(embed, "title"))
                .OfType<string>()
                .ToList();

            if (titles.Count > 0)
            {
                line += $"  [embed: {string.Join(" | ", titles)}]";
            }

            var attachments = Json.Array(message, "attachments").Count();

            if (attachments > 0)
            {
                line += $"  [вложений: {attachments}]";
            }

            Console.WriteLine(line);
        }
    }

    private static void PrintJsonArray(JsonElement message, string name, string label)
    {
        var items = Json.Array(message, name).ToList();

        if (items.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{label} ({items.Count}):");
        Console.WriteLine(JsonSerializer.Serialize(items, _pretty));
    }

    private static string Author(JsonElement message)
    {
        if (Json.Object(message, "author") is not { } author)
        {
            return "?";
        }

        var name = Json.Text(author, "global_name") ?? Json.Text(author, "username") ?? "?";

        return Json.Bool(author, "bot") ? $"{name} [бот]" : name;
    }

    private static string Preview(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return "(пусто)";
        }

        var line = content.ReplaceLineEndings(" ⏎ ");

        return line.Length > PreviewLength ? string.Concat(line.AsSpan(0, PreviewLength), "…") : line;
    }

    private static string Time(string? timestamp) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "?";

    private static string Pretty(JsonElement element) => JsonSerializer.Serialize(element, _pretty);
}
