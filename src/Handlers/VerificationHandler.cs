using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

/// <summary>
/// Обработчик верификации: выдаёт роль «котёночек» за ключевые слова в канале верификации.
/// </summary>
public static class VerificationHandler
{
    private static readonly Regex TriggerRegex = new(
        @"\b(мяу|nya|meow)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Проверяет сообщение и выдаёт роль, если условия выполнены.
    /// Возвращает true, если сообщение было обработано (дальнейшая обработка не нужна).
    /// </summary>
    public static async Task<bool> TryHandleAsync(SocketUserMessage message)
    {
        var channelId = AppConfig.VerificationChannel;
        var roleId = AppConfig.VerificationRole;

        if (channelId == 0 || roleId == 0)
        {
            return false;
        }

        if (message.Channel.Id != channelId)
        {
            return false;
        }

        if (!TriggerRegex.IsMatch(message.Content))
        {
            return false;
        }

        if (message.Author is not SocketGuildUser guildUser)
        {
            return false;
        }

        // Уже есть роль
        if (guildUser.Roles.Any(r => r.Id == roleId))
        {
            return true;
        }

        var role = guildUser.Guild.GetRole(roleId);

        if (role is null)
        {
            BotLogger.Error("Роль верификации {RoleId} не найдена на сервере", roleId);
            return true;
        }

        await guildUser.AddRoleAsync(role);

        BotLogger.Information("{User} прошёл верификацию в #{Channel}", guildUser.Username, message.Channel.Name);

        return true;
    }
}
