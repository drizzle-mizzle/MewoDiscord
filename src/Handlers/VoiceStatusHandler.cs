using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

public static class VoiceStatusHandler
{
    private static readonly AllowedMentions _noMentions = AllowedMentions.None;
    private static readonly ConcurrentDictionary<ulong, DateTime> _channelTimers = new();
    private static readonly ConcurrentDictionary<ulong, IMessageChannel> _channelTargets = new();

    /// <summary>
    /// Голосовой канал открытой сессии: сторожам, которых будит таймер, доступен только
    /// её идентификатор, а состав канала им перепроверять надо.
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, SocketVoiceChannel> _channelVoices = new();

    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelLocks = new();
    private static readonly ConcurrentDictionary<ulong, Timer> _idleTimers = new();

    private static readonly ConcurrentDictionary<ulong, AloneWatch> _aloneWatches = new();

    /// <summary>
    /// Сколько журнал молчит, прежде чем длительность разговора напечатается сама.
    /// </summary>
    private static readonly TimeSpan _idleDurationDelay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Сколько человек сидит в канале один, прежде чем бот спросит «приём-приём?».
    /// </summary>
    private static readonly TimeSpan _aloneCheckDelay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Сколько ждём ответа на «приём-приём?», прежде чем отключить от канала.
    /// </summary>
    private static readonly TimeSpan _aloneAnswerDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Префикс custom id кнопки «я ещё тут». Полный вид — prefix:канал:пользователь.
    /// </summary>
    internal const string AloneButtonPrefix = "voice_alive";

    /// <summary>
    /// Что делать со сторожем одиночества, когда состав канала изменился.
    /// </summary>
    internal enum AloneWatchState
    {
        /// <summary>Оставить как есть: идущий отсчёт трогать нечем.</summary>
        Keep,

        /// <summary>Снять: сторожить больше некого.</summary>
        Drop,

        /// <summary>Завести заново: одиночка теперь другой, и его полчаса только начались.</summary>
        Restart
    }

    /// <summary>
    /// Что делать по срабатыванию сторожа одиночества.
    /// </summary>
    internal enum AloneAlarm
    {
        /// <summary>Снять сторож: человек уже не один или остался другой.</summary>
        Drop,

        /// <summary>Спросить «приём-приём?» — это первая фаза.</summary>
        Ask,

        /// <summary>Отключить от канала: на заданный вопрос не ответили.</summary>
        Disconnect
    }

    public static async Task HandleVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        var leftChannel = before.VoiceChannel;
        var joinedChannel = after.VoiceChannel;

        // Микрофон, звук или стрим: канал не менялся
        if (leftChannel?.Id == joinedChannel?.Id)
        {
            if (joinedChannel != null)
            {
                await UnderChannelLockAsync(joinedChannel.Id, () => HandleStateChange(user, before, after, joinedChannel));
            }

            return;
        }

        // Переход из канала в канал — события двух разных каналов: уход идёт под замком
        // покинутого, приход — под замком нового
        if (leftChannel != null)
        {
            await UnderChannelLockAsync(leftChannel.Id, () => HandleLeave(user, leftChannel));
        }

        if (joinedChannel != null)
        {
            await UnderChannelLockAsync(joinedChannel.Id, () => HandleJoin(user, joinedChannel));
        }
    }

    /// <summary>
    /// Выполняет работу под замком канала. Замок сериализует всё, что пишет в журнал
    /// канала и трогает его сторожей: события gateway, оба сторожа и кнопку «я ещё тут».
    /// Два замка одновременно не берутся нигде.
    /// </summary>
    private static async Task UnderChannelLockAsync(ulong channelId, Func<Task> action)
    {
        var semaphore = _channelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task HandleJoin(SocketUser user, SocketVoiceChannel channel)
    {
        var name = user.Mention;

        // Только сигнал: вотчер работает в своём цикле, журнал его не ждёт
        ChannelRenameWatcher.NotifyChannelChanged(channel);

        // Годится любое из двух условий. «Зашёл первый» одного не хватает: кэш Discord
        // к этому моменту знает обо всех вошедших, и два одновременных захода в пустой
        // канал увидели бы двоих. «Записи нет» тоже мало: потерянное при обрыве gateway
        // событие ухода оставляет её навсегда
        if (!_channelTimers.ContainsKey(channel.Id) || channel.ConnectedUsers.Count == 1)
        {
            _channelTimers[channel.Id] = DateTime.UtcNow;
            _channelVoices[channel.Id] = channel;

            var started = BotMessages.VoiceConversationStarted();

            if (IsPrivateChannel(channel))
            {
                await channel.SendMessageAsync(started, allowedMentions: _noMentions);

                _channelTargets[channel.Id] = channel;
            }
            else
            {
                var statusChannelId = AppConfig.VoiceStatusChannel;
                var statusChannel = statusChannelId == 0 ? null : channel.Guild.GetTextChannel(statusChannelId);
                IUserMessage? root = null;

                // В журнал это сообщение ложится особым путём — корнем треда сессии
                if (statusChannel != null)
                {
                    root = await statusChannel.SendMessageAsync(started, allowedMentions: _noMentions);

                    var thread = await statusChannel.CreateThreadAsync(
                        channel.Name,
                        message: root);

                    _channelTargets[channel.Id] = thread;
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

        await JournalAsync(channel.Id, BotMessages.VoiceUserLeft(user.Mention, channel.Mention));

        await SyncAloneWatchAsync(channel);

        // Последний ушёл — завершаем сессию. Журнала может не быть вовсе, но общий чат
        // всё равно ждёт парного сообщения к «начался разговор»
        if (channel.ConnectedUsers.Count != 0)
        {
            return;
        }

        await AnnounceAsync(channel,
            BotMessages.VoiceConversationEnded(channel.Mention),
            BotMessages.VoiceConversationEndedCommon(channel.Mention));

        CloseSession(channel.Id);
    }

    /// <summary>
    /// Закрывает сессию канала: гасит сторож тишины и забывает журнал, таймер и сам канал.
    /// </summary>
    private static void CloseSession(ulong channelId)
    {
        StopIdleDuration(channelId);
        _channelTargets.TryRemove(channelId, out _);
        _channelTimers.TryRemove(channelId, out _);
        _channelVoices.TryRemove(channelId, out _);
    }

    private static async Task HandleStateChange(
        SocketUser user, SocketVoiceState before, SocketVoiceState after,
        SocketVoiceChannel channel)
    {
        var name = user.Mention;
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
    internal static Task NotifyChannelRenamedAsync(ulong channelId, string oldName, string newName) =>
        // Под замком канала, как и все прочие записи в журнал: вотчер имён работает
        // в своём фоне, задержка ему не страшна, а порядок сообщений в треде важен
        UnderChannelLockAsync(channelId, () => WriteChannelRenamedAsync(channelId, oldName, newName));

    private static async Task WriteChannelRenamedAsync(ulong channelId, string oldName, string newName)
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
            allowedMentions: mentions ?? _noMentions,
            components: components);

        await SendDurationAsync(channelId, target);

        return message;
    }

    /// <summary>
    /// Длительность разговора отдельным сообщением, парой к каждой строке журнала.
    /// Здесь же заводится сторож тишины, поэтому одноразового таймера хватает: каждая
    /// напечатанная длительность заводит следующий. В общий чат не уходит.
    /// </summary>
    private static async Task SendDurationAsync(ulong channelId, IMessageChannel target)
    {
        await target.SendMessageAsync(
            BotMessages.VoiceSessionDuration(SessionDuration(channelId)),
            allowedMentions: _noMentions);

        ScheduleIdleDuration(channelId);
    }

    /// <summary>
    /// Заводит сторож тишины на _idleDurationDelay или переводит уже заведённый.
    /// </summary>
    private static void ScheduleIdleDuration(ulong channelId)
    {
        if (_idleTimers.TryGetValue(channelId, out var existing))
        {
            existing.Change(_idleDurationDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        var timer = new Timer(_ => _ = ReportIdleDurationAsync(channelId), null,
            _idleDurationDelay, Timeout.InfiniteTimeSpan);

        // Кто-то успел завести свой, пока мы создавали этот, — лишний выбрасываем
        if (!_idleTimers.TryAdd(channelId, timer))
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
        if (_idleTimers.TryRemove(channelId, out var timer))
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

            await UnderChannelLockAsync(channelId, async () =>
            {
                // Под замком проверяем ещё раз: сессия могла закрыться, пока его ждали
                var target = GetTarget(channelId);

                if (target == null)
                {
                    return;
                }

                // Канал пуст, а сессия жива — уход последнего до нас не доехал (обрыв
                // gateway событий не переигрывает). Закрываем сами, иначе длительность
                // капала бы в мёртвый тред каждые полчаса вечно
                if (_channelVoices.TryGetValue(channelId, out var voice) && voice.ConnectedUsers.Count == 0)
                {
                    BotLogger.Information("Канал {ChannelId}: сессия закрыта сторожем — канал пуст", channelId);
                    CloseSession(channelId);
                    return;
                }

                await SendDurationAsync(channelId, target);
            });
        }
        catch (Exception ex)
        {
            BotLogger.Error(ex, "Не удалось напечатать длительность разговора: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Пишет сообщение в журнал и объявляет событие общим. Тексты разные: в треде,
    /// названном по каналу, его имя лишнее, а в общем чате — обязательно.
    /// </summary>
    private static async Task AnnounceAsync(SocketVoiceChannel channel, string journalText, string commonText)
    {
        var journalMessage = await JournalAsync(channel.Id, journalText);

        await PublishCommonAsync(channel, commonText, journalMessage);
    }

    /// <summary>
    /// Объявляет текст общим событием, не трогая журнал: нужно там, где в журнал он уже
    /// лёг особым путём. К тексту дописывается ссылка на ту же строку журнала, если журнал
    /// есть. Приватные каналы не публикуют ничего — анонс раскрыл бы скрытый канал.
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
    /// Сводит сторож одиночества к составу канала. Заводится он только переходом
    /// в одиночество, а идущий отсчёт приход соседа не трогает: полчаса меряют не «сидел
    /// один всё это время», а «был один в начале и остался один к концу».
    /// </summary>
    private static async Task SyncAloneWatchAsync(SocketVoiceChannel channel)
    {
        var users = channel.ConnectedUsers;
        var userId = users.Count == 1 ? users.First().Id : 0;
        var watched = _aloneWatches.TryGetValue(channel.Id, out var existing) ? existing.UserId : (ulong?)null;

        switch (DecideWatch(users.Count, userId, watched))
        {
            case AloneWatchState.Keep:
                return;

            case AloneWatchState.Drop:
                await DropAloneWatchAsync(channel.Id);
                return;
        }

        // Сторож остался про другого: одиночество этого началось только что
        await DropAloneWatchAsync(channel.Id);

        var watch = new AloneWatch(channel, userId);
        watch.Timer = new Timer(_ => _ = OnAloneTimerAsync(channel.Id, watch), null,
            _aloneCheckDelay, Timeout.InfiniteTimeSpan);

        // Кто-то успел завести свой, пока мы создавали этот, — лишний выбрасываем
        if (!_aloneWatches.TryAdd(channel.Id, watch))
        {
            watch.Dispose();
        }
    }

    /// <summary>
    /// Что делать со сторожем одиночества при изменении состава канала. Чистая функция —
    /// вынесена для тестов. <paramref name="lonerId"/> осмыслен только при одном человеке
    /// в канале, <paramref name="watchedUserId"/> — null, если сторожа нет.
    /// </summary>
    internal static AloneWatchState DecideWatch(int usersCount, ulong lonerId, ulong? watchedUserId)
    {
        // Канал опустел — сторожить некого
        if (usersCount == 0)
        {
            return AloneWatchState.Drop;
        }

        // Народу прибавилось — отсчёт продолжает идти, решение примет срабатывание
        if (usersCount != 1)
        {
            return AloneWatchState.Keep;
        }

        // Отсчёт про этого же одиночку уже идёт — сбрасывать его нечем
        return watchedUserId == lonerId ? AloneWatchState.Keep : AloneWatchState.Restart;
    }

    /// <summary>
    /// Что делать по срабатыванию сторожа одиночества. Чистая функция — вынесена
    /// для тестов. <paramref name="asked"/> различает фазы: вопрос уже задан — значит
    /// это второе срабатывание, и оно отключает.
    /// </summary>
    internal static AloneAlarm DecideAlarm(int usersCount, ulong lonerId, ulong watchedUserId, bool asked)
    {
        // Полчаса вышли, но человек уже не один (или один остался другой) —
        // вопроса не будет, а следующий отсчёт заведёт новое одиночество
        if (usersCount != 1 || lonerId != watchedUserId)
        {
            return AloneAlarm.Drop;
        }

        return asked ? AloneAlarm.Disconnect : AloneAlarm.Ask;
    }

    /// <summary>
    /// Гасит сторож одиночества вместе с заданным вопросом, если тот висит: отвечать
    /// на него больше незачем.
    /// </summary>
    private static async Task DropAloneWatchAsync(ulong channelId)
    {
        if (!_aloneWatches.TryRemove(channelId, out var watch))
        {
            return;
        }

        watch.Dispose();

        await ClearAlonePromptAsync(watch);
    }

    /// <summary>
    /// Ответ на кнопку «я ещё тут»: отсчёт одиночества заводится заново с нуля.
    /// false — сторожа уже нет (сессия закрылась, бот перезапускался), кнопка устарела.
    /// </summary>
    internal static async Task<bool> ConfirmAloneAsync(ulong channelId, ulong userId)
    {
        var confirmed = false;

        // Под замком канала: иначе уже стартовавший колбэк сторожа прочитал бы
        // сброшенный Asked и задал вопрос заново — или отключил только что ответившего
        await UnderChannelLockAsync(channelId, () =>
        {
            if (!_aloneWatches.TryGetValue(channelId, out var watch) || watch.UserId != userId || !watch.Asked)
            {
                return Task.CompletedTask;
            }

            watch.Asked = false;
            watch.Prompt = null;
            watch.Timer?.Change(_aloneCheckDelay, Timeout.InfiniteTimeSpan);
            confirmed = true;

            return Task.CompletedTask;
        });

        return confirmed;
    }

    /// <summary>
    /// Единственный колбэк сторожа: первое срабатывание спрашивает, второе — отключает.
    /// Сторож приходит параметром, а не берётся из словаря: колбэк принадлежит своему
    /// отсчёту. Исключение отсюда некому поймать, поэтому гасим на месте.
    /// </summary>
    private static async Task OnAloneTimerAsync(ulong channelId, AloneWatch watch)
    {
        try
        {
            await UnderChannelLockAsync(channelId, async () =>
            {
                // Пока ждали замок, одиночка мог смениться вместе со сторожем. Решать
                // за чужой нельзя: снимет чужой отсчёт или отключит того, кого в этот
                // раз ни о чём не спрашивали
                if (!_aloneWatches.TryGetValue(channelId, out var current) || !ReferenceEquals(current, watch))
                {
                    return;
                }

                var users = watch.Channel.ConnectedUsers;
                var loner = users.Count == 1 ? users.First() : null;

                switch (DecideAlarm(users.Count, loner?.Id ?? 0, watch.UserId, watch.Asked))
                {
                    case AloneAlarm.Drop:
                        await DropAloneWatchAsync(channelId);
                        break;

                    case AloneAlarm.Disconnect:
                        await DisconnectAloneAsync(channelId, watch, loner!);
                        break;

                    case AloneAlarm.Ask:
                        await AskAloneAsync(channelId, watch, loner!);
                        break;
                }
            });
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
        watch.Timer?.Change(_aloneAnswerDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Отключение от голосового канала за неответ. Об исключении журнал пишет после
    /// удавшегося отключения: не вышло (нет права «Перемещать участников») — не было
    /// и исключения.
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
        _channelTargets.TryGetValue(channelId, out var target) ? target : null;

    /// <summary>
    /// Сколько идёт разговор в канале. Сессии нет — «0сек».
    /// </summary>
    private static string SessionDuration(ulong channelId) =>
        _channelTimers.TryGetValue(channelId, out var startTime)
            ? FormatDuration(DateTime.UtcNow - startTime)
            : "0сек";

    /// <summary>
    /// Длительность человеку: «1ч 2мин 3сек». Часы целиком, а не остатком от суток.
    /// </summary>
    internal static string FormatDuration(TimeSpan elapsed)
    {
        var hours = (int)elapsed.TotalHours;
        var parts = new List<string>();

        if (hours > 0)
        {
            parts.Add($"{hours}ч");
        }

        if (elapsed.Minutes > 0)
        {
            parts.Add($"{elapsed.Minutes}мин");
        }

        parts.Add($"{elapsed.Seconds}сек");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Сторож одиночества канала: кто сидит один, один таймер на обе фазы (полчаса
    /// до вопроса, минута до отключения) и сам вопрос — с него потом снимается кнопка.
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
