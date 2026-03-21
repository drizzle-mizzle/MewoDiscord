using System.Globalization;
using Discord;
using Discord.Interactions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

[Group("set", "Настройка параметров бота")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
public class SetCommands : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("temperature", "Установить температуру ИИ (0.0 — 1.0)")]
    public async Task Temperature(
        [Summary("значение", "Температура от 0.0 до 1.0")]
        [MinValue(0.0)]
        [MaxValue(1.0)]
        double значение)
    {
        var valueStr = значение.ToString("F1", CultureInfo.InvariantCulture);

        AppConfig.Set("ANTHROPIC_CENSOR_SETTINGS", "Temperature", valueStr);
        AppConfig.Set("ANTHROPIC_SWEARS_CHECKER_SETTINGS", "Temperature", valueStr);

        BotLogger.Information("Температура ИИ изменена на {Temperature} пользователем {User}", valueStr, Context.User.Username);

        await RespondAsync(
            BotMessages.SetTemperature(valueStr),
            ephemeral: true);
    }
}
