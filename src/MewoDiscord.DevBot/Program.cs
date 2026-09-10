using System.Text;
using System.Text.RegularExpressions;

namespace MewoDiscord.DevBot;

/// <summary>
/// Dev-бот: читает Discord тем же токеном, что и боевой бот, но только через REST.
/// Шлюза он не касается — не получает событий, не может на них ответить и не трогает
/// статус бота, поэтому его безопасно запускать с dev-машины рядом с боевым.
/// Команды только читают: отправки, правки и удаления здесь нет намеренно.
/// </summary>
public static partial class Program
{
    private const string ConfigFileName = "devbot.ini";

    private const int DefaultRecentCount = 20;

    /// <summary>
    /// Больше Discord за один запрос не отдаёт.
    /// </summary>
    private const int MaxRecentCount = 100;

    private const string Usage =
        """
        Использование: dotnet run --project src/MewoDiscord.DevBot -- <команда>

          message <ссылка на сообщение> [--raw]
              Сообщение: автор, текст, embed'ы, компоненты и вложения.
              --raw — JSON целиком, как его отдаёт Discord
          message <id сервера> <id сообщения> [--raw]
              То же без ссылки: ищет сообщение по всем каналам и тредам сервера
          recent <ссылка на канал | id канала> [N]
              Последние N сообщений канала, по умолчанию 20, не больше 100
        """;

    private record Link(string ChannelId, string? MessageId);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var raw = args.Contains("--raw");
        var rest = args.Where(arg => arg != "--raw").ToArray();

        if (rest.Length == 0)
        {
            return PrintUsage();
        }

        var token = ReadToken();

        if (token == null)
        {
            Console.Error.WriteLine($"Нет токена: создай {ConfigFileName} в папке проекта по образцу devbot.example.ini");
            return 1;
        }

        using var api = new DiscordApi(token);

        try
        {
            return rest[0] switch
            {
                "message" => await MessageAsync(api, rest[1..], raw),
                "recent" => await RecentAsync(api, rest[1..]),
                _ => PrintUsage()
            };
        }
        catch (DiscordApiException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> MessageAsync(DiscordApi api, string[] args, bool raw)
    {
        if (args.Length == 1 && ParseLink(args[0]) is { MessageId: not null } link)
        {
            var message = await api.GetAsync($"channels/{link.ChannelId}/messages/{link.MessageId}");

            if (message == null)
            {
                Console.Error.WriteLine("Сообщение не найдено: удалено или бот не видит канал");
                return 2;
            }

            var channel = await api.GetAsync($"channels/{link.ChannelId}");
            MessagePrinter.PrintMessage(message.Value, Json.Text(channel, "name"), raw);
            return 0;
        }

        // Discord не ищет сообщение без канала — придётся обойти весь сервер
        if (args.Length == 2 && IsId(args[0]) && IsId(args[1]))
        {
            Console.Error.WriteLine("Канала нет — ищу сообщение по всему серверу…");
            var found = await MessageSearch.FindAsync(api, args[0], args[1]);

            if (found == null)
            {
                Console.Error.WriteLine("Сообщение не найдено ни в одном канале и треде, которые видит бот");
                return 2;
            }

            MessagePrinter.PrintMessage(found.Value.Message, found.Value.ChannelName, raw);
            return 0;
        }

        return PrintUsage();
    }

    private static async Task<int> RecentAsync(DiscordApi api, string[] args)
    {
        var channelId = args.Length == 0
            ? null
            : ParseLink(args[0])?.ChannelId ?? (IsId(args[0]) ? args[0] : null);

        if (channelId == null)
        {
            return PrintUsage();
        }

        var count = args.Length > 1 && int.TryParse(args[1], out var requested)
            ? Math.Clamp(requested, 1, MaxRecentCount)
            : DefaultRecentCount;

        var messages = await api.GetAsync($"channels/{channelId}/messages?limit={count}");

        if (messages == null)
        {
            Console.Error.WriteLine("Канал не найден или бот его не видит");
            return 2;
        }

        // Discord отдаёт от новых к старым, а читать удобнее как в чате — сверху вниз
        MessagePrinter.PrintList(messages.Value.EnumerateArray().Reverse());
        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine(Usage);
        return 1;
    }

    /// <summary>
    /// Токен лежит в devbot.ini, который при сборке копируется к исполняемому файлу.
    /// Это токен боевого бота: dev-бот — то же приложение Discord.
    /// </summary>
    private static string? ReadToken()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            var match = TokenLineRegex().Match(line);

            if (match.Success)
            {
                return match.Groups["token"].Value;
            }
        }

        return null;
    }

    private static Link? ParseLink(string text)
    {
        var match = MessageLinkRegex().Match(text);

        if (!match.Success)
        {
            return null;
        }

        var message = match.Groups["message"];

        return new Link(match.Groups["channel"].Value, message.Success ? message.Value : null);
    }

    private static bool IsId(string text) => ulong.TryParse(text, out _);

    [GeneratedRegex(@"^\s*BotToken\s*:\s*(?<token>\S+)\s*$")]
    private static partial Regex TokenLineRegex();

    /// <summary>
    /// Ссылка на канал или сообщение из «Копировать ссылку»: у ptb и canary свои поддомены,
    /// а старые ссылки ещё ведут на discordapp.com.
    /// </summary>
    [GeneratedRegex(@"discord(?:app)?\.com/channels/(?:\d+|@me)/(?<channel>\d+)(?:/(?<message>\d+))?")]
    private static partial Regex MessageLinkRegex();
}
