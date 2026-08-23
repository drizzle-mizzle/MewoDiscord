using System.Text.RegularExpressions;

using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

/// <summary>
/// Подтягивает медиа из постов X, ссылки на которые появились в чате. Своё превью X в Discord
/// показывает, но видео и гифку в нём заменяет неподвижный кадр, а картинок влезает не больше
/// четырёх — поэтому отвечаем своим контейнером с файлами, а родное превью гасим.
/// Заодно это работает загрузчиком: на сайте скачать видео штатно нельзя.
/// Скачивание, оформление и отправка — общие, в <see cref="PostMediaHandler"/>.
/// </summary>
public static partial class XMediaHandler
{
    /// <summary>
    /// Фирменный чёрный X.
    /// </summary>
    private static readonly Color XBlack = new(0x000000);

    private static readonly PostMediaHandler.PostStyle Style = new(
        XBlack,
        "x",
        BotMessages.XFooter,
        () => BotEmotes.X,
        BotMessages.XTooBig);

    /// <summary>
    /// Разбирает сообщение и, если в нём есть ссылки на посты, отвечает медиа.
    /// </summary>
    public static void HandleInBackground(SocketUserMessage message)
    {
        var requests = FindLinks(message.Content)
            .Select(link => new PostMediaHandler.PostRequest(
                XPostClient.BuildPostUrl(link.Author, link.StatusId),
                limit => XPostClient.TryGetPostAsync(link.StatusId, limit)))
            .ToList();

        PostMediaHandler.HandleInBackground(message, requests, Style);
    }

    /// <summary>
    /// Находит ссылки на посты. Кроме x.com принимаем старый twitter.com — ссылки оттуда
    /// живы до сих пор, и в чат их приносят наравне с новыми. Повторы схлопываются по
    /// идентификатору поста: та же ссылка с чужим логином в пути ведёт туда же.
    /// </summary>
    internal static IReadOnlyList<(string Author, string StatusId)> FindLinks(string text)
    {
        var result = new List<(string Author, string StatusId)>();

        foreach (Match match in PostLinkRegex().Matches(text))
        {
            var link = (Author: match.Groups[1].Value, StatusId: match.Groups[2].Value);

            if (!result.Any(known => known.StatusId == link.StatusId))
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

    /// <summary>
    /// Логин в пути бывает и служебным: у ссылок «поделиться» это i/web, у старых — i.
    /// Отсюда необязательный хвост /web, а не просто логин.
    /// </summary>
    [GeneratedRegex(
        @"https?://(?:www\.|mobile\.)?(?:x|twitter)\.com/([A-Za-z0-9_]{1,15}(?:/web)?)/status(?:es)?/(\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PostLinkRegex();
}
