using System.Globalization;

using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

/// <summary>
/// Довесок к родному превью YouTube: тонкий красный embed реплаем на сообщение со ссылкой —
/// голоса, просмотры, дата и, если превью его обрезало, полное название. Само превью
/// не гасим: в нём плеер, а свой плеер боту вставить некуда — поле video у embed'а бывает
/// только у входящих, а компонента со встроенным плеером в Components V2 нет.
/// </summary>
public static class YoutubePreviewHandler
{
    /// <summary>
    /// Сколько символов названия родное превью показывает целиком: длиннее — обрывает
    /// многоточием. YouTube отдаёт Discord название полностью, режет уже сам Discord,
    /// и порог взят из наблюдения, а не из документации.
    /// </summary>
    internal const int NativeTitleLimit = 37;

    private static readonly Color _youtubeRed = new(0xFF0000);

    /// <summary>
    /// Разряды разделяются неразрывным пробелом, как принято по-русски. Формат свой,
    /// а не культура ru-RU: так результат не зависит от того, есть ли в образе данные ICU.
    /// </summary>
    private static readonly NumberFormatInfo _counts = new()
    {
        NumberGroupSeparator = "\u00A0",
        NumberDecimalDigits = 0
    };

    /// <summary>
    /// Разбирает сообщение и, если в нём есть ссылки на видео, отвечает сведениями о них.
    /// Сообщение, обращённое к боту, пропускаем: «@бот скачай ‹ссылка›» ждёт файл, а не справку.
    /// Упоминания слушает только ChatGPT-часть — без неё обращаться не к кому, и превью нужно.
    /// </summary>
    public static void HandleInBackground(SocketUserMessage message)
    {
        var videoIds = YoutubeLinks.VideoIds(message.Content, PostMediaHandler.MaxLinksPerMessage);

        if (videoIds.Count == 0 || (AppConfig.UseChatGpt && MentionsBot(message)))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ReplyAsync(message, videoIds);
            }
            catch (Exception ex)
            {
                BotLogger.Error("Ошибка превью YouTube в сообщении {MessageId}: {Message}", message.Id, ex.Message);
            }
        });
    }

    /// <summary>
    /// Полное название нужно, только когда родное превью его обрезало.
    /// </summary>
    internal static bool NeedsFullTitle(string title) => title.Length > NativeTitleLimit;

    internal static string FormatCount(long count) => count.ToString("N0", _counts);

    private static bool MentionsBot(SocketUserMessage message)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        return guild != null && DiscordMentions.ExplicitUserIds(message.Content).Contains(guild.CurrentUser.Id);
    }

    private static async Task ReplyAsync(SocketUserMessage message, IReadOnlyList<string> videoIds)
    {
        var infos = await Task.WhenAll(videoIds.Select(YoutubeVideoClient.TryGetAsync));

        var embeds = infos
            .OfType<YoutubeVideoClient.VideoInfo>()
            .Select(BuildEmbed)
            .OfType<Embed>()
            .ToArray();

        if (embeds.Length == 0)
        {
            return;
        }

        await message.Channel.SendMessageAsync(
            embeds: embeds,
            allowedMentions: AllowedMentions.None,
            messageReference: new MessageReference(message.Id, failIfNotExists: false));
    }

    /// <summary>
    /// Собирает embed. null — показать нечего: название превью показало целиком, а голоса
    /// не пришли. Одной даты в подписи на отдельное сообщение мало.
    /// </summary>
    private static Embed? BuildEmbed(YoutubeVideoClient.VideoInfo info)
    {
        var lines = new List<string>();

        if (NeedsFullTitle(info.Title))
        {
            lines.Add(BotMessages.YoutubePreviewTitle(Format.Sanitize(info.Title)));
        }

        if (info.Votes != null)
        {
            lines.Add(BotMessages.YoutubePreviewVotes(FormatCount(info.Votes.Likes), FormatCount(info.Votes.Dislikes)));
            lines.Add(BotMessages.YoutubePreviewViews(info.Votes.Views, FormatCount(info.Votes.Views)));
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var embed = new EmbedBuilder()
            .WithColor(_youtubeRed)
            .WithDescription(string.Join("\n", lines))
            .WithFooter(BotMessages.YoutubeFooter());

        if (info.PublishedAt != null)
        {
            // У embed'а под дату свой слот: каждый видит её в своей зоне и на своём языке
            embed.WithTimestamp(info.PublishedAt.Value);
        }

        return embed.Build();
    }
}
