using System.Text.Json;

namespace MewoDiscord.DevBot;

/// <summary>
/// Поиск сообщения по серверу, когда известен только его id. Discord без канала сообщение
/// не отдаёт, поэтому обходим все места, где сообщения бывают: сначала каналы, потом треды.
/// </summary>
public static class MessageSearch
{
    /// <summary>
    /// Каналы, в которых сообщения лежат прямо в канале: текстовый, голосовой (у него
    /// свой чат), новостной и трибуна.
    /// </summary>
    private static readonly int[] _messageChannelTypes = [0, 2, 5, 13];

    /// <summary>
    /// Каналы, у которых бывают треды: текстовый, новостной, форум и медиа.
    /// </summary>
    private static readonly int[] _threadParentTypes = [0, 5, 15, 16];

    public static async Task<(JsonElement Message, string? ChannelName)?> FindAsync(
        DiscordApi api, string guildId, string messageId)
    {
        var channels = await api.GetAsync($"guilds/{guildId}/channels")
            ?? throw new DiscordApiException($"Сервер {guildId} не найден или бота на нём нет");

        var all = channels.EnumerateArray().ToList();

        foreach (var channel in all.Where(c => _messageChannelTypes.Contains(Json.Int(c, "type"))))
        {
            var found = await TryGetAsync(api, channel, messageId);

            if (found != null)
            {
                return found;
            }
        }

        // Активные треды отдаются одним запросом на весь сервер, архивные — по родителю.
        // Приватные архивные видны только с правом «Управлять тредами»: без него — 403,
        // то есть null, и это не ошибка. Первой сотни архивных на канал нашему серверу
        // хватает с запасом, поэтому дальше не листаем
        var threads = new List<JsonElement>(Json.Array(await api.GetAsync($"guilds/{guildId}/threads/active"), "threads"));

        foreach (var parent in all.Where(c => _threadParentTypes.Contains(Json.Int(c, "type"))))
        {
            var id = Json.Text(parent, "id");
            threads.AddRange(Json.Array(await api.GetAsync($"channels/{id}/threads/archived/public?limit=100"), "threads"));
            threads.AddRange(Json.Array(await api.GetAsync($"channels/{id}/threads/archived/private?limit=100"), "threads"));
        }

        foreach (var thread in threads)
        {
            var found = await TryGetAsync(api, thread, messageId);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static async Task<(JsonElement Message, string? ChannelName)?> TryGetAsync(
        DiscordApi api, JsonElement channel, string messageId)
    {
        var message = await api.GetAsync($"channels/{Json.Text(channel, "id")}/messages/{messageId}");

        return message == null ? null : (message.Value, Json.Text(channel, "name"));
    }
}
