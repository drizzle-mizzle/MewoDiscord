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
    private static readonly Color _youtubeRed = new(0xFF0000);

    /// <summary>
    /// Сколько ещё ждать родное превью, если к концу наших запросов его нет: Discord
    /// дорисовывает его уже после доставки сообщения. Не дождались — превью, видимо,
    /// нет вовсе, и полное название показываем: иначе его не видно нигде.
    /// </summary>
    private static readonly TimeSpan _nativePreviewWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan _nativePreviewPoll = TimeSpan.FromMilliseconds(500);

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
    /// Полное название нужно, только когда родное превью показало не его. Discord кладёт
    /// в embed длинное название уже оборванным, с многоточием, а порог у него
    /// не документирован и считается не в символах — поэтому сравниваем с тем, что он
    /// показал. Превью нет вовсе — название больше нигде не видно.
    /// </summary>
    internal static bool NeedsFullTitle(string title, string? nativeTitle) =>
        nativeTitle == null || !string.Equals(nativeTitle.Trim(), title.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Заголовки родных превью по идентификатору видео. Превью узнаём по адресу:
    /// у YouTube в нём ссылка на само видео.
    /// </summary>
    internal static Dictionary<string, string> NativeTitles(IEnumerable<Embed> embeds, IReadOnlyCollection<string> videoIds)
    {
        var titles = new Dictionary<string, string>();

        foreach (var embed in embeds)
        {
            var id = embed.Url == null ? null : YoutubeLinks.FirstVideoId(embed.Url);

            if (id != null && embed.Title != null && videoIds.Contains(id))
            {
                titles.TryAdd(id, embed.Title);
            }
        }

        return titles;
    }

    internal static string FormatCount(long count) => count.ToString("N0", _counts);

    private static bool MentionsBot(SocketUserMessage message)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        return guild != null && DiscordMentions.ExplicitUserIds(message.Content).Contains(guild.CurrentUser.Id);
    }

    private static async Task ReplyAsync(SocketUserMessage message, IReadOnlyList<string> videoIds)
    {
        var infos = (await Task.WhenAll(videoIds.Select(YoutubeVideoClient.TryGetAsync)))
            .OfType<YoutubeVideoClient.VideoInfo>()
            .ToList();

        if (infos.Count == 0)
        {
            return;
        }

        var nativeTitles = await WaitNativeTitlesAsync(message, infos.Select(info => info.Id).ToList());

        var embeds = infos
            .Select(info => BuildEmbed(info, nativeTitles.GetValueOrDefault(info.Id)))
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
    /// Ждёт родные превью. Discord дописывает их в то же сообщение уже после доставки,
    /// а Discord.NET обновляет закэшированный объект на месте — поэтому достаточно
    /// перечитывать его embed'ы, пока не найдутся все или не выйдет срок.
    /// </summary>
    private static async Task<Dictionary<string, string>> WaitNativeTitlesAsync(
        SocketUserMessage message, IReadOnlyList<string> videoIds)
    {
        var deadline = DateTime.UtcNow + _nativePreviewWait;

        while (true)
        {
            var titles = NativeTitles(message.Embeds, videoIds);

            if (titles.Count == videoIds.Count || DateTime.UtcNow >= deadline)
            {
                return titles;
            }

            await Task.Delay(_nativePreviewPoll);
        }
    }

    /// <summary>
    /// Собирает embed. null — показать нечего: название превью показало целиком, а голоса
    /// не пришли. Одной даты в подписи на отдельное сообщение мало.
    /// </summary>
    private static Embed? BuildEmbed(YoutubeVideoClient.VideoInfo info, string? nativeTitle)
    {
        // Название и цифры — отдельными абзацами. Пустую строку внутри значения
        // messages.ini не записать, поэтому её ставит код
        var blocks = new List<string>();

        if (NeedsFullTitle(info.Title, nativeTitle))
        {
            blocks.Add(BotMessages.YoutubePreviewTitle(Format.Sanitize(info.Title)));
        }

        var stats = new List<string>();

        if (info.Likes != null && info.Dislikes != null)
        {
            stats.Add(BotMessages.YoutubePreviewVotes(FormatCount(info.Likes.Value), FormatCount(info.Dislikes.Value)));
        }

        if (info.Views != null)
        {
            stats.Add(BotMessages.YoutubePreviewViews(info.Views.Value, FormatCount(info.Views.Value)));
        }

        if (stats.Count > 0)
        {
            blocks.Add(string.Join("\n", stats));
        }

        if (blocks.Count == 0)
        {
            return null;
        }

        var embed = new EmbedBuilder()
            .WithColor(_youtubeRed)
            .WithDescription(string.Join("\n\n", blocks))
            .WithFooter(BotMessages.YoutubeFooter(), BotEmotes.IconUrl(BotEmotes.Youtube));

        if (info.PublishedAt != null)
        {
            // У embed'а под дату свой слот: каждый видит её в своей зоне и на своём языке
            embed.WithTimestamp(info.PublishedAt.Value);
        }

        return embed.Build();
    }
}
