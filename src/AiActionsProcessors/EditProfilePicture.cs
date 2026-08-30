using Discord;
using Discord.WebSocket;

using MewoDiscord.Handlers;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.AiActionsProcessors;

/// <summary>
/// Процессор действия «отредактировать аватарку пользователя». Автоматизирует ручной
/// сценарий: скачать аватарку, создать сессию ChatGPT и отправить в неё картинку
/// с просьбой её изменить. Дальше пользователи продолжают правки обычными реплаями
/// в сессию («добавь усики»), потому что сессия создаётся штатным механизмом.
/// </summary>
public static class EditProfilePicture
{
    private const ushort AvatarSize = 512;

    private const string AvatarFileName = "avatar.png";

    private const int DownloadTimeoutSeconds = 60;

    /// <summary>
    /// Промпт суб-запроса: свободная фраза пользователя превращается в одну инструкцию
    /// для сессии. Живёт здесь, а не в файле действия: это работа процессора,
    /// а не условие попадания.
    /// </summary>
    private const string FormalizePrompt =
        """
        Ниже — просьба пользователя изменить изображение (аватарку).
        Перепиши её как одну короткую инструкцию для художника, работающего с этим
        изображением. Без обращений, без имён и упоминаний, без пояснений — только
        сама инструкция одной фразой. Ответь строго этой фразой и ничем больше.
        """;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds)
    };

    public static async Task RunAsync(CustomAiActionContext context)
    {
        var message = context.Message;
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        if (guild is null)
        {
            return;
        }

        // Цель берём из упоминаний в тексте, а не из MentionedUsers: туда реплай
        // подставляет автора цитаты. Сам бот целью быть не может
        var targetId = DiscordMentions.ExplicitUserIds(message.Content).FirstOrDefault(id => id != guild.CurrentUser.Id);
        var target = targetId == 0 ? null : message.MentionedUsers.FirstOrDefault(u => u.Id == targetId) ?? guild.GetUser(targetId);

        if (target is null)
        {
            // Сообщение уже потреблено сработавшим действием, поэтому молчать нельзя.
            // Такое бывает, когда упомянутый покинул сервер
            BotLogger.Warning("Правка аватарки: упомянутый пользователь {UserId} не найден на сервере", targetId);
            _ = await MediaReply.SendEmbedAsync(message, BotEmbeds.Error(BotMessages.AiActionUserNotFound()));
            return;
        }

        var avatarUrl = target.GetDisplayAvatarUrl(ImageFormat.Png, AvatarSize) ?? target.GetDefaultAvatarUrl();
        var avatar = await DownloadAsync(avatarUrl);

        if (avatar == null)
        {
            _ = await MediaReply.SendEmbedAsync(message, BotEmbeds.Error(BotMessages.AiActionAvatarFailed(DiscordMentions.DisplayNameOf(target))));
            return;
        }

        // Карточка с исходной аватаркой: за ней и закрепится сессия
        var card = await SendCardAsync(message, target, avatar);

        if (card == null)
        {
            return;
        }

        // Свободная фраза пользователя — в чёткую инструкцию для полноценной сессии
        var formalized = await ChatGptClient.AskInstantAsync($"{FormalizePrompt}\n\n\"\"\"\n{context.Text}\n\"\"\"");

        if (formalized.Length == 0)
        {
            // Инстант-модель не ответила — отправляем запрос как есть
            formalized = context.Text;
        }

        var entry = ChatGptSessionStore.Create(guild.Id, message.Channel.Id, card.Id);

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "🖼️ Правка аватарки {User} в новой сессии {Id}: {Request}", target.Username, entry.Id, formalized);

        await entry.Lock.WaitAsync();

        try
        {
            using var typing = message.Channel.EnterTypingState();

            var request = new ChatGptSessionHandler.TurnRequest(
                formalized,
                [new ChatGptClient.InputFile(AvatarFileName, avatar, "image/png")],
                new ChatGptClient.ChatContext(
                    guild.CurrentUser.DisplayName,
                    DiscordMentions.DisplayNameOf(message.Author)),
                new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
                {
                    [DiscordMentions.DisplayNameOf(message.Author)] = message.Author.Id,
                    [DiscordMentions.DisplayNameOf(target)] = target.Id
                });

            await ChatGptSessionHandler.RunTurnAsync(message.Channel, card.Id, entry, request);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>
    /// Отправляет карточку с исходной аватаркой. Картинка прикладывается файлом,
    /// а не ссылкой на CDN: аватарку пользователь может сменить, и ссылка протухнет.
    /// </summary>
    private static async Task<IUserMessage?> SendCardAsync(SocketUserMessage message, SocketUser target, byte[] avatar)
    {
        var embed = new EmbedBuilder()
            .WithColor(BotEmbeds.InfoColor)
            .WithDescription(BotMessages.AiActionAvatarCard(target.Mention))
            .WithImageUrl($"attachment://{AvatarFileName}")
            .Build();

        var attachment = new FileAttachment(new MemoryStream(avatar), AvatarFileName);

        try
        {
            return await message.Channel.SendFilesAsync(
                [attachment],
                embed: embed,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(message.Id, failIfNotExists: false));
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось отправить карточку аватарки: {Message}", ex.Message);
            return null;
        }
        finally
        {
            attachment.Dispose();
        }
    }

    private static async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            return await _http.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось скачать аватарку {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

}
