using System.Text.RegularExpressions;
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
    public static string Humanize(string content, SocketUserMessage message)
    {
        var names = new Dictionary<ulong, string>();

        // Через индексатор, а не ToDictionary: повтор одного и того же упоминания
        // не должен ронять обработку сообщения
        foreach (var user in message.MentionedUsers)
        {
            names[user.Id] = DisplayNameOf(user);
        }

        return Humanize(content, id => names.GetValueOrDefault(id));
    }

    /// <summary>
    /// Имя пользователя так, как его видят на сервере: ник, иначе глобальное имя.
    /// </summary>
    public static string DisplayNameOf(SocketUser user) =>
        user is SocketGuildUser guildUser ? guildUser.DisplayName : user.GlobalName ?? user.Username;

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMentionRegex();
}
