using System.Text.RegularExpressions;

using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

/// <summary>
/// Подтягивает медиа из публичных постов Telegram, ссылки на которые появились в чате.
/// Скачивание, оформление и отправка — общие, в <see cref="PostMediaHandler"/>;
/// здесь только поиск ссылок и фирменный стиль источника.
/// </summary>
public static partial class TelegramMediaHandler
{
    /// <summary>
    /// Фирменный синий Telegram.
    /// </summary>
    private static readonly Color TelegramBlue = new(0x2AABEE);

    private static readonly PostMediaHandler.PostStyle Style = new(
        TelegramBlue,
        "telegram",
        BotMessages.TelegramFooter,
        () => BotEmotes.Telegram,
        BotMessages.TelegramTooBig);

    /// <summary>
    /// Разбирает сообщение и, если в нём есть ссылки на посты, отвечает медиа.
    /// </summary>
    public static void HandleInBackground(SocketUserMessage message)
    {
        var requests = FindLinks(message.Content)
            .Select(link => new PostMediaHandler.PostRequest(
                TelegramPostClient.BuildPostUrl(link.Channel, link.PostId),
                _ => TelegramPostClient.TryGetPostAsync(link.Channel, link.PostId)))
            .ToList();

        PostMediaHandler.HandleInBackground(message, requests, Style);
    }

    /// <summary>
    /// Находит ссылки на посты публичных каналов. Приватные (t.me/c/...) не подходят:
    /// их виджет требует авторизации.
    /// </summary>
    internal static IReadOnlyList<(string Channel, string PostId)> FindLinks(string text)
    {
        var result = new List<(string Channel, string PostId)>();

        foreach (Match match in PostLinkRegex().Matches(text))
        {
            var link = (match.Groups[1].Value, match.Groups[2].Value);

            if (!result.Contains(link))
            {
                result.Add(link);
            }

            if (result.Count == PostMediaHandler.MaxLinksPerMessage)
            {
                break;
            }
        }

        return result;
    }

    [GeneratedRegex(@"https?://(?:t\.me|telegram\.me)/(?:s/)?([A-Za-z][A-Za-z0-9_]{3,31})/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PostLinkRegex();
}
