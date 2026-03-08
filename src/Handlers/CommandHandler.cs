using Discord;
using Discord.WebSocket;
using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

/// <summary>
/// Обработчик слеш-команд бота.
/// </summary>
public static class CommandHandler
{
    /// <summary>
    /// Регистрирует слеш-команды. Вызывается при Ready.
    /// </summary>
    public static async Task RegisterCommandsAsync(DiscordSocketClient client)
    {
        var toggleCommand = new SlashCommandBuilder()
            .WithName("toggle")
            .WithDescription("Управление режимами бота")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("anti-bydlo")
                .WithDescription("Включить/выключить режим Анти-быдло")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();

        await client.CreateGlobalApplicationCommandAsync(toggleCommand);
        BotLogger.Information("Слеш-команды зарегистрированы");
    }

    /// <summary>
    /// Обработчик InteractionCreated.
    /// </summary>
    public static async Task HandleInteractionCreated(SocketInteraction interaction)
    {
        if (interaction is not SocketSlashCommand command)
        {
            return;
        }

        if (command.CommandName != "toggle")
        {
            return;
        }

        var subCommand = command.Data.Options.FirstOrDefault()?.Name;

        if (subCommand == "anti-bydlo")
        {
            await HandleToggleAntiBydlo(command);
        }
    }

    private static async Task HandleToggleAntiBydlo(SocketSlashCommand command)
    {
        MessageHandler.IsCensorEnabled = !MessageHandler.IsCensorEnabled;

        var status = MessageHandler.IsCensorEnabled ? "включён ✅" : "выключен ❌";
        BotLogger.Information("Режим Анти-быдло {Status} пользователем {User}", status, command.User.Username);

        await command.RespondAsync(
            $"Режим **Анти-быдло** {status}",
            ephemeral: true);
    }
}
