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
        // Текст в журнал обязателен: сообщение уходит от имени бота, и без него нельзя
        // сопоставить реплику в чате с тем, кто её продиктовал. Тред команд видят
        // только администраторы
        BotLogger.LogCommand(
            "{User} использовал /say в #{Channel}: {Text}",
            Context.User.Username, Context.Channel.Name, text);
        await RespondAsync(embed: BotEmbeds.Success(BotMessages.SayDone()), ephemeral: true);
    }
}
