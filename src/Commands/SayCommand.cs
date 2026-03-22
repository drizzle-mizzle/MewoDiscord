using Discord;
using Discord.Interactions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

[DefaultMemberPermissions(GuildPermission.Administrator)]
public class SayCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("say", "Написать сообщение от имени бота")]
    public async Task Say(
        [Summary("text", "Текст сообщения")]
        string text)
    {
        await Context.Channel.SendMessageAsync(text);
        BotLogger.LogCommand("{User} использовал /say в #{Channel}", Context.User.Username, Context.Channel.Name);
        await RespondAsync("✅", ephemeral: true);
    }
}
