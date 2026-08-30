using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

public static class VoiceStatusHandler
{
    private static readonly AllowedMentions NoMentions = AllowedMentions.None;
    private static readonly ConcurrentDictionary<ulong, DateTime> ChannelTimers = new();
    private static readonly ConcurrentDictionary<ulong, IMessageChannel> ChannelTargets = new();
    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> ChannelLocks = new();
    private static readonly ConcurrentDictionary<ulong, Timer> IdleTimers = new();

    private static readonly ConcurrentDictionary<ulong, AloneWatch> AloneWatches = new();

    /// <summary>
    /// Сколько журнал молчит, прежде чем длительность разговора напечатается сама.
    /// </summary>
    private static readonly TimeSpan IdleDurationDelay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Сколько человек сидит в канале один, прежде чем бот спросит «приём-приём?».
    /// </summary>
    private static readonly TimeSpan AloneCheckDelay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Сколько ждём ответа на «приём-приём?», прежде чем отключить от канала.
    /// </summary>
    private static readonly TimeSpan AloneAnswerDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Префикс custom id кнопки «я ещё тут». Полный вид — prefix:канал:пользователь:
    /// обработчик кнопки живёт в отдельном модуле и состояния сторожа не видит.
    /// </summary>
    internal const string AloneButtonPrefix = "voice_alive";

    public static async Task HandleVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        var leftChannel = before.VoiceChannel;
        var joinedChannel = after.VoiceChannel;

        var voiceChannelId = (joinedChannel ?? leftChannel)?.Id;

        if (voiceChannelId == null)
        {
            return;
        }

        var semaphore = ChannelLocks.GetOrAdd(voiceChannelId.Value, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            if (leftChannel?.Id != joinedChannel?.Id)
            {
                if (leftChannel != null)
                {
                    await HandleLeave(user, leftChannel);
                }

                if (joinedChannel != null)
                {
                    await HandleJoin(user, joinedChannel);
                }
            }
            else if (joinedChannel != null)
            {
                await HandleStateChange(user, before, after, joinedChannel);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task HandleJoin(SocketUser user, SocketVoiceChannel channel)
    {
        var name = Mention(user);

        // Только сигнал: вотчер работает в своём цикле, журнал его не ждёт
        ChannelRenameWatcher.NotifyChannelChanged(channel);

        // Первый пользователь — создаём сессию
        if (channel.ConnectedUsers.Count == 1)
        {
            ChannelTimers[channel.Id] = DateTime.UtcNow;

            var started = BotMessages.VoiceConversationStarted();

            if (IsPrivateChannel(channel))
            {
                await channel.SendMessageAsync(started, allowedMentions: NoMentions);

                ChannelTargets[channel.Id] = channel;
            }
            else
            {
                var statusChannelId = AppConfig.VoiceStatusChannel;
                var statusChannel = statusChannelId == 0 ? null : channel.Guild.GetTextChannel(statusChannelId);
                IUserMessage? root = null;

                // В журнал это сообщение ложится особым путём — корнем треда сессии
                if (statusChannel != null)
                {
                    root = await statusChannel.SendMessageAsync(started, allowedMentions: NoMentions);

                    var thread = await statusChannel.CreateThreadAsync(
                        channel.Name,
                        message: root);

                    ChannelTargets[channel.Id] = thread;
                }

                // Общим объявляем в любом случае, даже без журнала: общий чат ждёт пары
                // к «закончился разговор». Ссылка при этом будет только если журнал есть
                await PublishCommonAsync(channel, BotMessages.VoiceConversationStartedCommon(channel.Mention), root);
            }
        }

        await JournalAsync(channel.Id, BotMessages.VoiceUserJoined(name, channel.Mention));

        await SyncAloneWatchAsync(channel);
    }

    private static async Task HandleLeave(SocketUser user, SocketVoiceChannel channel)
    {
        // Только сигнал: вотчер работает в своём цикле, журнал его не ждёт
        ChannelRenameWatcher.NotifyChannelChanged(channel);

        await JournalAsync(channel.Id, BotMessages.VoiceUserLeft(Mention(user), channel.Mention));

        await SyncAloneWatchAsync(channel);

        // Последний ушёл — завершаем сессию. Ранний выход при пустом журнале был бы неверен:
        // журнала может не быть вовсе (статусный канал не настроен), а общий чат всё равно
        // ждёт парного сообщения к «начался разговор»
        if (channel.ConnectedUsers.Count != 0)
        {
            return;
        }

        await AnnounceAsync(channel,
            BotMessages.VoiceConversationEnded(channel.Mention),
            BotMessages.VoiceConversationEndedCommon(channel.Mention));

        StopIdleDuration(channel.Id);
        ChannelTargets.TryRemove(channel.Id, out _);
        ChannelTimers.TryRemove(channel.Id, out _);
    }

    private static async Task HandleStateChange(
        SocketUser user, SocketVoiceState before, SocketVoiceState after,
        SocketVoiceChannel channel)
    {
        var name = Mention(user);
        var ch = channel.Mention;

        // Стрим — независимо от мута/дефена. Общим событием идёт только начало:
        // позвать посмотреть можно на то, что ещё идёт. Конец, как мут и деафен,
        // остаётся журналу
        if (before.IsStreaming != after.IsStreaming)
        {
            if (after.IsStreaming)
            {
                await AnnounceAsync(channel,
                    BotMessages.VoiceUserStartedStream(name),
                    BotMessages.VoiceUserStartedStreamCommon(name, ch));
            }
            else
            {
                await JournalAsync(channel.Id, BotMessages.VoiceUserStoppedStream(name));
            }
        }

        // Деафен приоритетнее мута (деафен автоматически включает мут)
        if (before.IsSelfDeafened != after.IsSelfDeafened)
        {
            await JournalAsync(channel.Id, after.IsSelfDeafened
                ? BotMessages.VoiceUserDeafened(name)
                : BotMessages.VoiceUserUndeafened(name));
        }
        else if (before.IsDeafened != after.IsDeafened)
        {
            await JournalAsync(channel.Id, after.IsDeafened
                ? BotMessages.VoiceUserServerDeafened(name)
                : BotMessages.VoiceUserServerUndeafened(name));
        }
        else if (before.IsSelfMuted != after.IsSelfMuted)
        {
            await JournalAsync(channel.Id, after.IsSelfMuted
                ? BotMessages.VoiceUserMuted(name)
                : BotMessages.VoiceUserUnmuted(name));
        }
        else if (before.IsMuted != after.IsMuted)
        {
            await JournalAsync(channel.Id, after.IsMuted
                ? BotMessages.VoiceUserServerMuted(name)
                : BotMessages.VoiceUserServerUnmuted(name));
        }
    }

    /// <summary>
    /// Пишет о смене имени в журнал сессии канала, если он открыт. Вызывается вотчером имён;
    /// если сессия уже закрыта (или ещё не открыта), сообщение просто не отправляется.
    /// </summary>
    internal static async Task NotifyChannelRenamedAsync(ulong channelId, string oldName, string newName)
    {
        await JournalAsync(channelId, BotMessages.VoiceChannelRenamed(oldName, newName));
    }

    /// <summary>
    /// Пишет сообщение в журнал сессии канала и следом — длительность разговора.
    /// Журнала может не быть — статусный канал не настроен или сессия ещё не открыта, —
    /// и тогда это тихий no-op.
    /// </summary>
    private static async Task<IUserMessage?> JournalAsync(ulong channelId, string text,
        MessageComponent? components = null, AllowedMentions? mentions = null)
    {
        var target = GetTarget(channelId);

        if (target == null)
        {
            return null;
        }

        var message = await target.SendMessageAsync(text,
            allowedMentions: mentions ?? NoMentions,
            components: components);

        await SendDurationAsync(channelId, target);

        return message;
    }

    /// <summary>
    /// Длительность разговора отдельным сообщением. Ходит парой за каждой строкой журнала,
    /// а в молчащей сессии печатается сама — сторож тишины заводится здесь же, так что
    /// одноразового таймера хватает: каждая напечатанная длительность заводит следующий.
    /// В общий чат не уходит: там нет ни треда, ни сессии, к которой её отнести.
    /// </summary>
    private static async Task SendDurationAsync(ulong channelId, IMessageChannel target)
    {
        await target.SendMessageAsync(
            BotMessages.VoiceSessionDuration(GetTimer(channelId)),
            allowedMentions: NoMentions);

        ScheduleIdleDuration(channelId);
    }

    /// <summary>
    /// Заводит сторож тишины на IdleDurationDelay или переводит уже заведённый.
    /// </summary>
    private static void ScheduleIdleDuration(ulong channelId)
    {
        if (IdleTimers.TryGetValue(channelId, out var existing))
        {
            existing.Change(IdleDurationDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        var timer = new Timer(_ => _ = ReportIdleDurationAsync(channelId), null,
            IdleDurationDelay, Timeout.InfiniteTimeSpan);

        // Кто-то успел завести свой, пока мы создавали этот, — лишний выбрасываем
        if (!IdleTimers.TryAdd(channelId, timer))
        {
            timer.Dispose();
            ScheduleIdleDuration(channelId);
        }
    }

    /// <summary>
    /// Гасит сторож тишины: сессия закрыта, отсчитывать больше нечего.
    /// </summary>
    private static void StopIdleDuration(ulong channelId)
    {
        if (IdleTimers.TryRemove(channelId, out var timer))
        {
            timer.Dispose();
        }
    }

    /// <summary>
    /// Печать длительности в молчащей сессии. Исключение отсюда некому поймать —
    /// колбэк таймера зовут без ожидания, — поэтому гасим на месте.
    /// </summary>
    private static async Task ReportIdleDurationAsync(ulong channelId)
    {
        try
        {
            if (GetTarget(channelId) == null)
            {
                StopIdleDuration(channelId);
                return;
            }

            var semaphore = ChannelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();

            try
            {
                // Под замком проверяем ещё раз: сессия могла закрыться, пока его ждали
                var target = GetTarget(channelId);

                if (target != null)
                {
                    await SendDurationAsync(channelId, target);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            BotLogger.Error(ex, "Не удалось напечатать длительность разговора: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Пишет сообщение в журнал и объявляет событие общим. Тексты разные: журнал сидит
    /// в треде, названном по каналу, и таймер сессии там к месту, а в общем чате нет
    /// ни того, ни другого контекста — зато нужен канал, чтобы было понятно, куда идти.
    /// </summary>
    private static async Task AnnounceAsync(SocketVoiceChannel channel, string journalText, string commonText)
    {
        var journalMessage = await JournalAsync(channel.Id, journalText);

        await PublishCommonAsync(channel, commonText, journalMessage);
    }

    /// <summary>
    /// Объявляет текст общим событием, не трогая журнал: нужно там, где в журнал он уже
    /// лёг особым путём. К тексту дописывается ссылка на ту же строку в журнале — по ней
    /// из общего чата видно, где смотреть подробности; журнала может не быть вовсе, тогда
    /// и ссылки нет. Приватные каналы общими событиями не делятся: их журнал намеренно
    /// не выходит за пределы самого канала, а анонс раскрыл бы скрытый канал всему серверу.
    /// </summary>
    private static async Task PublishCommonAsync(SocketVoiceChannel channel, string text, IUserMessage? journalMessage)
    {
        if (IsPrivateChannel(channel))
        {
            return;
        }

        if (journalMessage != null)
        {
            text = BotMessages.VoiceCommonWithLink(text, journalMessage.GetJumpUrl());
        }

        await CommonEvents.PublishAsync(new CommonEvent(channel.Guild, text));
    }

    /// <summary>
    /// Сводит сторож одиночества к составу канала. Заводится он **только** переходом
    /// в одиночество, а идущий отсчёт приход соседа не трогает: полчаса меряют не то,
    /// сидел ли человек один всё это время, а то, один ли он был в начале и остался ли
    /// один к концу. Поэтому заглянувший на минуту сосед получаса не обнуляет, а если
    /// к концу отсчёта человек не один — вопроса просто не будет, и следующий отсчёт
    /// начнётся с нового одиночества.
    /// </summary>
    private static async Task SyncAloneWatchAsync(SocketVoiceChannel channel)
    {
        var users = channel.ConnectedUsers;

        // Канал опустел — сторожить некого
        if (users.Count == 0)
        {
            await DropAloneWatchAsync(channel.Id);
            return;
        }

        // Народу прибавилось — отсчёт продолжает идти, решение примет срабатывание
        if (users.Count != 1)
        {
            return;
        }

        var userId = users.First().Id;

        // Отсчёт про этого же одиночку уже идёт — сбрасывать его нечем
        if (AloneWatches.TryGetValue(channel.Id, out var existing) && existing.UserId == userId)
        {
            return;
        }

        // Сторож остался про другого — тот ушёл, и его вопрос уже ни о чём;
        // одиночество этого началось только что, ему свои полчаса
        await DropAloneWatchAsync(channel.Id);

        var watch = new AloneWatch(channel, userId);
        watch.Timer = new Timer(_ => _ = OnAloneTimerAsync(channel.Id), null,
            AloneCheckDelay, Timeout.InfiniteTimeSpan);

        // Кто-то успел завести свой, пока мы создавали этот, — лишний выбрасываем
        if (!AloneWatches.TryAdd(channel.Id, watch))
        {
            watch.Dispose();
        }
    }

    /// <summary>
    /// Гасит сторож одиночества вместе с заданным вопросом, если тот висит: отвечать
    /// на него больше незачем.
    /// </summary>
    private static async Task DropAloneWatchAsync(ulong channelId)
    {
        if (!AloneWatches.TryRemove(channelId, out var watch))
        {
            return;
        }

        watch.Dispose();

        await ClearAlonePromptAsync(watch);
    }

    /// <summary>
    /// Ответ на кнопку «я ещё тут»: отсчёт одиночества заводится заново с нуля.
    /// Возвращает false, если сторожа уже нет (сессия закрылась, бот перезапускался) —
    /// тогда кнопка просто устарела. Зовётся из обработчика кнопки.
    /// </summary>
    internal static bool ConfirmAlone(ulong channelId, ulong userId)
    {
        if (!AloneWatches.TryGetValue(channelId, out var watch) || watch.UserId != userId || !watch.Asked)
        {
            return false;
        }

        watch.Asked = false;
        watch.Prompt = null;
        watch.Timer?.Change(AloneCheckDelay, Timeout.InfiniteTimeSpan);

        return true;
    }

    /// <summary>
    /// Единственный колбэк сторожа: первое срабатывание спрашивает, второе — отключает.
    /// Исключение отсюда некому поймать, поэтому гасим на месте.
    /// </summary>
    private static async Task OnAloneTimerAsync(ulong channelId)
    {
        try
        {
            if (!AloneWatches.TryGetValue(channelId, out var watch))
            {
                return;
            }

            var semaphore = ChannelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();

            try
            {
                // Под замком состав канала мог измениться, пока его ждали
                var users = watch.Channel.ConnectedUsers;

                // Полчаса вышли, но человек уже не один (или один остался другой) —
                // вопроса не будет, а следующий отсчёт заведёт новое одиночество
                if (users.Count != 1 || users.First().Id != watch.UserId)
                {
                    await DropAloneWatchAsync(channelId);
                    return;
                }

                if (watch.Asked)
                {
                    await DisconnectAloneAsync(channelId, watch, users.First());
                }
                else
                {
                    await AskAloneAsync(channelId, watch, users.First());
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            BotLogger.Error(ex, "Сторож одиночества упал: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// «Приём-приём?» с кнопкой. Единственное сообщение журнала, которое пингует:
    /// без уведомления вопрос не имеет смысла — отошедший его не увидит.
    /// </summary>
    private static async Task AskAloneAsync(ulong channelId, AloneWatch watch, SocketGuildUser user)
    {
        var button = new ComponentBuilder()
            .WithButton(BotMessages.VoiceAloneButton(), $"{AloneButtonPrefix}:{channelId}:{watch.UserId}")
            .Build();

        watch.Prompt = await JournalAsync(channelId,
            BotMessages.VoiceAloneCheck(user.Mention),
            button,
            new AllowedMentions { UserIds = [watch.UserId] });

        // Журнала нет — спросить негде, а отключать за неответ на незаданный вопрос нельзя
        if (watch.Prompt == null)
        {
            await DropAloneWatchAsync(channelId);
            return;
        }

        watch.Asked = true;
        watch.Timer?.Change(AloneAnswerDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Отключение от голосового канала за неответ. Об исключении журнал пишет **после**
    /// удавшегося отключения: не вышло (нет права «Перемещать участников») — исключения
    /// не было, и врать о нём нельзя. Порядок сообщений держит замок канала: событие
    /// голосового состояния встанет на нём и допишет «покинул» и «закончился разговор»
    /// только когда мы его отпустим.
    /// </summary>
    private static async Task DisconnectAloneAsync(ulong channelId, AloneWatch watch, SocketGuildUser user)
    {
        await DropAloneWatchAsync(channelId);

        await JournalAsync(channelId, BotMessages.VoiceAloneNoAnswer(user.Mention));

        await user.ModifyAsync(properties => properties.Channel = null);

        await JournalAsync(channelId,
            BotMessages.VoiceUserKicked(user.Guild.CurrentUser.Mention, user.Mention));
    }

    /// <summary>
    /// Снимает кнопку с заданного вопроса: отвечать уже не на что.
    /// </summary>
    private static async Task ClearAlonePromptAsync(AloneWatch watch)
    {
        var prompt = watch.Prompt;
        watch.Prompt = null;

        if (prompt == null)
        {
            return;
        }

        await prompt.ModifyAsync(properties => properties.Components = new ComponentBuilder().Build());
    }

    internal static bool IsPrivateChannel(SocketVoiceChannel channel)
    {
        var overwrite = channel.GetPermissionOverwrite(channel.Guild.EveryoneRole);
        return overwrite?.ViewChannel == PermValue.Deny;
    }

    private static IMessageChannel? GetTarget(ulong channelId) =>
        ChannelTargets.TryGetValue(channelId, out var target) ? target : null;

    private static string Mention(SocketUser user) =>
        user.Mention;

    private static string GetTimer(ulong channelId)
    {
        if (!ChannelTimers.TryGetValue(channelId, out var startTime))
        {
            return "0сек";
        }

        var elapsed = DateTime.UtcNow - startTime;
        var parts = new List<string>();

        if (elapsed.Hours > 0)
        {
            parts.Add($"{elapsed.Hours}ч");
        }

        if (elapsed.Minutes > 0)
        {
            parts.Add($"{elapsed.Minutes}мин");
        }

        parts.Add($"{elapsed.Seconds}сек");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Сторож одиночества одного канала: кто сидит один, один таймер на обе фазы
    /// (полчаса до вопроса, минута до отключения) и заданный вопрос, с которого
    /// потом снимается кнопка.
    /// </summary>
    private sealed class AloneWatch(SocketVoiceChannel channel, ulong userId) : IDisposable
    {
        public SocketVoiceChannel Channel { get; } = channel;

        public ulong UserId { get; } = userId;

        public Timer? Timer { get; set; }

        /// <summary>Вопрос задан — значит следующее срабатывание таймера отключает.</summary>
        public bool Asked { get; set; }

        public IUserMessage? Prompt { get; set; }

        public void Dispose()
        {
            Timer?.Dispose();
            Timer = null;
        }
    }
}
