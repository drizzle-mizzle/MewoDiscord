using Discord;
using Discord.Interactions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

[DefaultMemberPermissions(GuildPermission.Administrator)]
public class ReinstallCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("reinstall", "Переустановить команды: удалить лишние и зарегистрировать актуальные")]
    public async Task Reinstall()
    {
        // Удаление и регистрация — несколько запросов подряд, в три секунды можем не уложиться
        await DeferAsync(ephemeral: true);

        var (removedGlobal, removedGuild, registered) = await Program.ReinstallCommandsAsync(Context.Guild);

        BotLogger.LogCommand(
            "/reinstall — {User}: удалено глобальных {Global}, серверных {Guild}, зарегистрировано {Registered}",
            Context.User.Username, removedGlobal, removedGuild, registered);

        await FollowupAsync(
            embed: BotEmbeds.Success(
                BotMessages.ReinstallDone(
                    removedGlobal.ToString(),
                    removedGuild.ToString(),
                    registered.ToString())),
            ephemeral: true);
    }
}
