using Discord;
using Discord.Interactions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

/// <summary>
/// Сессии ChatGPT — доступны всем участникам сервера (без DefaultMemberPermissions).
/// /chatgpt new закрепляет сессию за ответным сообщением: реплай на него — хит в сессию.
/// /chatgpt sessions — список сессий со ссылками на их последние сообщения.
/// </summary>
[Group("chatgpt", "Сессии ChatGPT")]
public class ChatGptSessionCommands : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("new", "Создать новую сессию ChatGPT")]
    public async Task New(
        [Summary("тип", "Тип сессии")]
        [Choice("chat", "chat")]
        [Choice("image-gen", "image-gen")]
        string тип = "chat")
    {
        if (Context.Guild is null)
        {
            await RespondAsync(BotMessages.ChatGptGuildOnly(), ephemeral: true);
            return;
        }

        var type = тип == "image-gen" ? ChatGptSessionType.ImageGen : ChatGptSessionType.Chat;

        // Ответ публичный: на него нужно реплаить, ephemeral-сообщения для этого не годятся
        await RespondAsync(type == ChatGptSessionType.Chat
            ? BotMessages.ChatGptSessionNewChat()
            : BotMessages.ChatGptSessionNewImage());

        var original = await GetOriginalResponseAsync();
        var entry = ChatGptSessionStore.Create(Context.Guild.Id, Context.Channel.Id, original.Id, type);

        BotLogger.LogCommand("/chatgpt new {Type} — {User}: сессия {Id}", тип, Context.User.Username, entry.Id);
    }

    [SlashCommand("sessions", "Список сессий ChatGPT")]
    public async Task Sessions()
    {
        var all = ChatGptSessionStore.All();

        BotLogger.LogCommand("/chatgpt sessions — {User}: сессий {Count}", Context.User.Username, all.Count);

        if (all.Count == 0)
        {
            await RespondAsync(BotMessages.ChatGptSessionsEmpty(), ephemeral: true);
            return;
        }

        var lines = new List<string>();

        for (var i = 0; i < all.Count; i++)
        {
            var entry = all[i];
            var link = $"https://discord.com/channels/{entry.GuildId}/{entry.ChannelId}/{entry.LastMessageId}";
            var icon = entry.Type == ChatGptSessionType.ImageGen ? "🎨" : "💬";
            var unix = new DateTimeOffset(DateTime.SpecifyKind(entry.UpdatedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            lines.Add($"{i + 1}. [перейти]({link}) — {icon} {ChatGptSessionStore.TypeToString(entry.Type)} — <t:{unix}:R>");
        }

        var embed = new EmbedBuilder()
            .WithTitle(BotMessages.ChatGptSessionsTitle())
            .WithColor(Color.Teal)
            .WithDescription(string.Join('\n', lines))
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }
}
