using Discord;
using Discord.Interactions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

[Group("purge", "Удаление сообщений в канале")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
public class PurgeCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("by-count", "Удалить последние N сообщений")]
    public async Task ByCount(
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

        BotLogger.LogCommand("/purge by-count — {User} удалил {Count} сообщений в #{Channel}", Context.User.Username, deletable.Count, textChannel.Name);
        await FollowupAsync(reply, ephemeral: true);
    }

    [SlashCommand("by-time", "Удалить сообщения за указанный период")]
    public async Task ByTime(
        [Summary("from", "Начало периода (формат: yyyy-MM-dd HH:mm)")]
        string from,
        [Summary("user", "Удалить только сообщения этого пользователя")]
        IUser? user = null,
        [Summary("to", "Конец периода (формат: yyyy-MM-dd HH:mm, по умолчанию — сейчас)")]
        string? to = null)
    {
        if (Context.Channel is not ITextChannel textChannel)
        {
            await RespondAsync(BotMessages.PurgeNotTextChannel(), ephemeral: true);
            return;
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(AppConfig.LocalTimeZone);

        if (!DateTime.TryParseExact(from, "yyyy-MM-dd HH:mm", null, System.Globalization.DateTimeStyles.None, out var fromLocal))
        {
            await RespondAsync(BotMessages.PurgeBadDateFormat(), ephemeral: true);
            return;
        }

        DateTimeOffset fromUtc = new DateTimeOffset(fromLocal, tz.GetUtcOffset(fromLocal)).ToUniversalTime();
        DateTimeOffset toUtc;

        if (to is not null)
        {
            if (!DateTime.TryParseExact(to, "yyyy-MM-dd HH:mm", null, System.Globalization.DateTimeStyles.None, out var toLocal))
            {
                await RespondAsync(BotMessages.PurgeBadDateFormat(), ephemeral: true);
                return;
            }

            toUtc = new DateTimeOffset(toLocal, tz.GetUtcOffset(toLocal)).ToUniversalTime();
        }
        else
        {
            toUtc = DateTimeOffset.UtcNow;
        }

        await DeferAsync(ephemeral: true);

        // Discord API не позволяет массово удалять сообщения старше 14 дней
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14);

        if (fromUtc < cutoff)
        {
            fromUtc = cutoff;
        }

        // Собираем сообщения в указанном диапазоне батчами
        var allMessages = new List<IMessage>();
        var fromSnowflake = SnowflakeUtils.ToSnowflake(fromUtc);
        const int batchSize = 100;

        while (true)
        {
            var batch = await textChannel.GetMessagesAsync(fromSnowflake, Direction.After, batchSize).FlattenAsync();
            var list = batch.Where(m => m.CreatedAt <= toUtc).ToList();

            if (user is not null)
            {
                list = list.Where(m => m.Author.Id == user.Id).ToList();
            }

            allMessages.AddRange(list);

            // Если пришло меньше batchSize или последнее сообщение уже за пределами toUtc — выходим
            var batchList = batch.ToList();

            if (batchList.Count < batchSize || batchList.Last().CreatedAt > toUtc)
            {
                break;
            }

            fromSnowflake = batchList.Max(m => m.Id);
        }

        var deletable = allMessages.Where(m => m.CreatedAt > cutoff).ToList();
        var tooOld = allMessages.Count - deletable.Count;

        // Удаляем батчами по 100 (ограничение Discord API)
        foreach (var chunk in deletable.Chunk(100))
        {
            await textChannel.DeleteMessagesAsync(chunk);
        }

        var reply = BotMessages.PurgeDone(deletable.Count.ToString());

        if (tooOld > 0)
        {
            reply += "\n" + BotMessages.PurgeTooOld(tooOld.ToString());
        }

        BotLogger.LogCommand("/purge by-time — {User} удалил {Count} сообщений в #{Channel}", Context.User.Username, deletable.Count, textChannel.Name);
        await FollowupAsync(reply, ephemeral: true);
    }
}
