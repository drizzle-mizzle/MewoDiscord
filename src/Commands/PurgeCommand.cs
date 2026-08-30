using Discord;
using Discord.Interactions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

[Group("purge", "Удаление сообщений в канале")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
public class PurgeCommand : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>
    /// Сколько сообщений канала просматриваем, набирая нужное число сообщений автора.
    /// Страховка от бездонного канала, где спрошенный человек давно не писал: десять
    /// запросов истории — это недели переписки на нашем сервере.
    /// </summary>
    private const int MaxScannedMessages = 1000;

    [SlashCommand("by-count", "Удалить последние N сообщений")]
    public async Task ByCount(
        [Summary("count", "Сколько сообщений удалить (1–100)")]
        [MinValue(1), MaxValue(100)]
        int count,
        [Summary("user", "Удалить только сообщения этого пользователя")]
        IUser? user = null)
    {
        if (Context.Channel is not ITextChannel textChannel)
        {
            await RespondAsync(embed: BotEmbeds.Error(BotMessages.PurgeNotTextChannel()), ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        // Discord API не позволяет массово удалять сообщения старше 14 дней
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14);

        var (deletable, scanned, exhausted) = user is null
            ? await TakeLatestAsync(textChannel, count, cutoff)
            : await CollectFromUserAsync(textChannel, user, count, cutoff);

        if (deletable.Count > 0 && !await TryDeleteAsync(textChannel, deletable))
        {
            return;
        }

        var reply = BotMessages.PurgeDone(deletable.Count.ToString());
        var incomplete = false;

        if (user is not null)
        {
            reply += "\n" + BotMessages.PurgeScanned(scanned.ToString());

            // Набрали меньше, чем просили: без этой строки пользователь решит, что бот
            // удалил не всё по своей прихоти
            if (deletable.Count < count && exhausted)
            {
                reply += "\n" + BotMessages.PurgeScanStopped(deletable.Count.ToString(), count.ToString());
                incomplete = true;
            }
        }
        else if (scanned > deletable.Count)
        {
            // Часть сообщений оказалась старше 14 дней — исход неполный, цвет жёлтый
            reply += "\n" + BotMessages.PurgeTooOld((scanned - deletable.Count).ToString());
            incomplete = true;
        }

        var embed = incomplete ? BotEmbeds.Warning(reply) : BotEmbeds.Success(reply);

        BotLogger.LogCommand(
            "/purge by-count — {User} удалил {Count} сообщений в #{Channel} (просмотрено {Scanned})",
            Context.User.Username, deletable.Count, textChannel.Name, scanned);

        await FollowupAsync(embed: embed, ephemeral: true);
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
            await RespondAsync(embed: BotEmbeds.Error(BotMessages.PurgeNotTextChannel()), ephemeral: true);
            return;
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(AppConfig.LocalTimeZone);

        if (!DateTime.TryParseExact(from, "yyyy-MM-dd HH:mm", null, System.Globalization.DateTimeStyles.None, out var fromLocal))
        {
            await RespondAsync(embed: BotEmbeds.Error(BotMessages.PurgeBadDateFormat()), ephemeral: true);
            return;
        }

        DateTimeOffset fromUtc = new DateTimeOffset(fromLocal, tz.GetUtcOffset(fromLocal)).ToUniversalTime();
        DateTimeOffset toUtc;

        if (to is not null)
        {
            if (!DateTime.TryParseExact(to, "yyyy-MM-dd HH:mm", null, System.Globalization.DateTimeStyles.None, out var toLocal))
            {
                await RespondAsync(embed: BotEmbeds.Error(BotMessages.PurgeBadDateFormat()), ephemeral: true);
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

        // Запрошенное начало старше границы двух недель — двигаем его, но молчать
        // об этом нельзя: «удали с июля» ответило бы зелёным «удалено 37», умолчав,
        // что месяц истории остался на месте
        var clamped = fromUtc < cutoff;

        if (clamped)
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

        // Старше границы тут быть уже не может: с неё и начали выборку
        var deletable = allMessages;

        // Удаляем батчами по 100 (ограничение Discord API)
        foreach (var chunk in deletable.Chunk(100))
        {
            if (!await TryDeleteAsync(textChannel, chunk))
            {
                return;
            }
        }

        var reply = BotMessages.PurgeDone(deletable.Count.ToString());

        if (clamped)
        {
            reply += "\n" + BotMessages.PurgePeriodClamped();
        }

        var embed = clamped ? BotEmbeds.Warning(reply) : BotEmbeds.Success(reply);

        BotLogger.LogCommand("/purge by-time — {User} удалил {Count} сообщений в #{Channel}", Context.User.Username, deletable.Count, textChannel.Name);
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    #region Internals

    /// <summary>
    /// Последние сообщения канала. Просмотрено здесь — это столько же, сколько взято
    /// до отсева по возрасту: спрашивали именно последние N сообщений канала.
    /// </summary>
    private static async Task<(List<IMessage> Deletable, int Scanned, bool Exhausted)> TakeLatestAsync(
        ITextChannel channel, int count, DateTimeOffset cutoff)
    {
        var messages = (await channel.GetMessagesAsync(count).FlattenAsync()).ToList();
        var deletable = messages.Where(m => m.CreatedAt > cutoff).ToList();

        return (deletable, messages.Count, messages.Count < count);
    }

    /// <summary>
    /// Сообщения одного автора: листаем историю назад, пока не наберём нужное число.
    /// «Последние N» при фильтре по человеку — это N его сообщений, а не N сообщений
    /// канала, из которых ему принадлежит пара штук.
    /// </summary>
    private static async Task<(List<IMessage> Deletable, int Scanned, bool Exhausted)> CollectFromUserAsync(
        ITextChannel channel, IUser user, int count, DateTimeOffset cutoff)
    {
        var collected = new List<IMessage>(count);
        var scanned = 0;
        ulong? oldestSeen = null;
        var exhausted = false;

        while (collected.Count < count && scanned < MaxScannedMessages)
        {
            var page = (oldestSeen == null
                ? await channel.GetMessagesAsync(DiscordConfig.MaxMessagesPerBatch).FlattenAsync()
                : await channel.GetMessagesAsync(oldestSeen.Value, Direction.Before, DiscordConfig.MaxMessagesPerBatch).FlattenAsync())
                .ToList();

            if (page.Count == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var message in page)
            {
                scanned++;

                // Страница идёт от новых к старым: первое же сообщение за границей
                // означает, что дальше только старше — удалять их Discord не даст
                if (message.CreatedAt <= cutoff)
                {
                    exhausted = true;
                    break;
                }

                if (message.Author.Id == user.Id)
                {
                    collected.Add(message);

                    if (collected.Count == count)
                    {
                        break;
                    }
                }
            }

            if (exhausted || page.Count < DiscordConfig.MaxMessagesPerBatch)
            {
                exhausted = true;
                break;
            }

            oldestSeen = page[^1].Id;
        }

        return (collected, scanned, exhausted || collected.Count < count);
    }

    /// <summary>
    /// Удаляет пачку. false — не хватило прав: без обработки команда падала бы молча,
    /// а пользователь видел бы «приложение не отвечает».
    /// </summary>
    private async Task<bool> TryDeleteAsync(ITextChannel channel, IEnumerable<IMessage> messages)
    {
        try
        {
            await channel.DeleteMessagesAsync(messages);
            return true;
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            BotLogger.Warning("Удаление сообщений в #{Channel} запрещено: {Message}", channel.Name, ex.Message);
            await FollowupAsync(embed: BotEmbeds.Error(BotMessages.PurgeNoPermission()), ephemeral: true);

            return false;
        }
    }

    #endregion
}
