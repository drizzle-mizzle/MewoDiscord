using Discord;
using Discord.Interactions;
using MewoDiscord.Handlers;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

[Group("toggle", "Управление режимами бота")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
public class ToggleCommands : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("anti-bydlo", "Включить/выключить режим Анти-быдло")]
    public async Task AntiBydlo()
    {
        MessageHandler.IsCensorEnabled = !MessageHandler.IsCensorEnabled;

        var status = MessageHandler.IsCensorEnabled ? "включён ✅" : "выключен ❌";
        BotLogger.Information("Режим Анти-быдло {Status} пользователем {User}", status, Context.User.Username);

        await RespondAsync(
            $"Режим **Анти-быдло** {status}",
            ephemeral: true);
    }
}
