using System.Text;
using System.Text.RegularExpressions;

using Discord;
using Discord.WebSocket;

namespace MewoDiscord.Helpers;

/// <summary>
/// Приведение упоминаний Discord к читаемому виду. В промпты ИИ уходит не «голое»
/// <c>&lt;@1234567890&gt;</c>, а <c>@ИмяПользователя</c>: модель не умеет разворачивать
/// идентификаторы, а по имени понимает, о ком речь.
/// </summary>
public static partial class DiscordMentions
{
    /// <summary>
    /// Заменяет упоминания пользователей на @отображаемое-имя. Упоминание, для которого
    /// имя не нашлось, остаётся как есть — лучше сырой id, чем потерянный смысл.
    /// </summary>
    internal static string Humanize(string content, Func<ulong, string?> resolve)
    {
        return UserMentionRegex().Replace(content, match =>
        {
            if (!ulong.TryParse(match.Groups[1].Value, out var id))
            {
                return match.Value;
            }

            var name = resolve(id);

            return string.IsNullOrWhiteSpace(name) ? match.Value : "@" + name;
        });
    }

    /// <summary>
    /// То же для текста сообщения Discord: имена берутся из упомянутых в нём пользователей,
    /// у участников сервера — ник на сервере. Текст передаётся отдельно, потому что
    /// к этому моменту он мог быть уже подчищен (например, без упоминания бота).
    /// </summary>
    public static string Humanize(string content, SocketUserMessage message, SocketGuild? guild = null)
    {
        var names = new Dictionary<ulong, string>();

        // Через индексатор, а не ToDictionary: повтор одного и того же упоминания
        // не должен ронять обработку сообщения
        foreach (var user in message.MentionedUsers)
        {
            names[user.Id] = DisplayNameOf(user);
        }

        // Запасной путь — кэш участников сервера: упоминания цитируемого сообщения
        // в MentionedUsers текущего не попадают
        return Humanize(content, id => names.GetValueOrDefault(id) ?? NameFromGuild(guild, id));
    }

    /// <summary>
    /// То же для произвольного текста, известного только сервером (например, цитаты).
    /// </summary>
    public static string Humanize(string content, SocketGuild? guild) =>
        Humanize(content, id => NameFromGuild(guild, id));

    private static string? NameFromGuild(SocketGuild? guild, ulong id)
    {
        var user = guild?.GetUser(id);

        return user == null ? null : DisplayNameOf(user);
    }

    /// <summary>
    /// Обратная операция: @имя из ответа модели превращается в настоящее упоминание.
    /// Имена берутся только из участников текущего обмена — так модель не может позвать
    /// произвольного человека, а совпадения не ловятся наугад. Возвращает текст и id тех,
    /// кого действительно упомянули: по ним строится белый список пингов.
    /// </summary>
    internal static (string Text, IReadOnlyList<ulong> Mentioned) Restore(string text, IReadOnlyDictionary<string, ulong> names)
    {
        if (names.Count == 0 || !text.Contains('@'))
        {
            return (text, []);
        }

        // Длинные имена первыми: иначе «@Иван» съел бы начало «@Иван Петрович»
        var ordered = names.OrderByDescending(pair => pair.Key.Length).ToList();
        var result = new StringBuilder(text.Length);
        var mentioned = new List<ulong>();
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] != '@')
            {
                result.Append(text[i]);
                i++;
                continue;
            }

            var matched = false;

            foreach (var (name, id) in ordered)
            {
                var end = i + 1 + name.Length;

                // Имя должно кончаться там же, где кончается слово: «@Иван» в «@Иванов» — не он
                if (end > text.Length
                    || string.Compare(text, i + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0
                    || (end < text.Length && IsNameChar(text[end])))
                {
                    continue;
                }

                result.Append("<@").Append(id).Append('>');

                if (!mentioned.Contains(id))
                {
                    mentioned.Add(id);
                }

                i = end;
                matched = true;
                break;
            }

            if (!matched)
            {
                result.Append(text[i]);
                i++;
            }
        }

        return (result.ToString(), mentioned);
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Id пользователей, упомянутых **в тексте** сообщения. Не то же самое, что
    /// MentionedUsers: реплай с включённым «@» добавляет туда автора сообщения, на которое
    /// отвечают, хотя тот в тексте не упомянут. Для решений вида «пользователь кого-то
    /// назвал» годится только явное упоминание.
    /// </summary>
    public static IReadOnlyList<ulong> ExplicitUserIds(string content)
    {
        var ids = new List<ulong>();

        foreach (Match match in UserMentionRegex().Matches(content))
        {
            if (ulong.TryParse(match.Groups[1].Value, out var id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Имя пользователя так, как его видят на сервере: ник, иначе глобальное имя.
    /// Берёт IUser, а не SocketUser: автор цитаты может приехать из REST, а не из кэша.
    /// </summary>
    public static string DisplayNameOf(IUser user) =>
        user is IGuildUser guildUser ? guildUser.DisplayName : user.GlobalName ?? user.Username;

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMentionRegex();
}
