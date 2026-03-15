using Discord;
using Discord.Interactions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

[DefaultMemberPermissions(GuildPermission.Administrator)]
public class PurgeCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("purge", "Удалить последние сообщения в канале")]
    public async Task Purge(
        [Summary("count", "Количество сообщений для удаления (1–100)")]
        [MinValue(1), MaxValue(100)]
        int count,
        [Summary("user", "Удалить только сообщения этого пользователя")]
        IUser? user = null)
    {
        if (Context.Channel is not ITextChannel textChannel)
        {
            await RespondAsync(BotMessages.PurgeNotTextChannel(), ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var messages = await textChannel.GetMessagesAsync(count).FlattenAsync();

        if (user is not null)
        {
            messages = messages.Where(m => m.Author.Id == user.Id);
        }

        // Discord API не позволяет массово удалять сообщения старше 14 дней
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
        var deletable = messages.Where(m => m.CreatedAt > cutoff).ToList();
        var tooOld = messages.Count() - deletable.Count;

        if (deletable.Count > 0)
        {
            await textChannel.DeleteMessagesAsync(deletable);
        }

        var reply = BotMessages.PurgeDone(deletable.Count.ToString());

        if (tooOld > 0)
        {
            reply += "\n" + BotMessages.PurgeTooOld(tooOld.ToString());
        }

        BotLogger.Information("{User} удалил {Count} сообщений в #{Channel}", Context.User.Username, deletable.Count, textChannel.Name);
        await FollowupAsync(reply, ephemeral: true);
    }
}
