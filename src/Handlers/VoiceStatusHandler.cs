using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

public static class VoiceStatusHandler
{
    /// <summary>
    /// Имя, на которое переименовывается публичный канал, если в нём остался один человек.
    /// </summary>
    private const string AloneChannelName = "Одинокий пидоуебан";

    /// <summary>
    /// Сколько человек должен просидеть один, прежде чем канал переименуется.
    /// </summary>
    private const int AloneRenameDelayMs = 5000;

    private static readonly AllowedMentions NoMentions = AllowedMentions.None;
    private static readonly ConcurrentDictionary<ulong, DateTime> ChannelTimers = new();
    private static readonly ConcurrentDictionary<ulong, IMessageChannel> ChannelTargets = new();
    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> ChannelLocks = new();
    private static readonly ConcurrentDictionary<ulong, CancellationTokenSource> AloneChecks = new();

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

    /// <summary>
    /// Сверяет имена каналов из БД с фактическими: после падения бота канал мог остаться
    /// с «одиноким» именем. Вызывать один раз при запуске.
    /// </summary>
    public static async Task RestoreRenamedChannelsAsync(DiscordSocketClient client)
    {
        foreach (var (channelId, originalName) in ChannelNameStore.All())
        {
            if (client.GetChannel(channelId) is not SocketVoiceChannel channel)
            {
                ChannelNameStore.Forget(channelId);
                BotLogger.Warning("Канал {ChannelId} из БД имён не найден — запись удалена", channelId);
                continue;
            }

            if (channel.Name == originalName)
            {
                ChannelNameStore.Forget(channelId);
                continue;
            }

            if (channel.Name != AloneChannelName)
            {
                // Канал переименовали вручную, пока бот лежал — не спорим с админом
                ChannelNameStore.Forget(channelId);
                BotLogger.Warning("Канал {Channel} переименован вручную — запись удалена", channel.Name);
                continue;
            }

            // Имя «одинокое», и человек всё ещё сидит один — состояние верное
            if (channel.ConnectedUsers.Count == 1)
            {
                continue;
            }

            await TryRestoreNameAsync(channel);
        }
    }

    private static async Task HandleJoin(SocketUser user, SocketVoiceChannel channel)
    {
        var name = Mention(user);

        // Раньше журнала: переименование не зависит от того, настроен ли статус-канал,
        // а ниже по коду есть ранние выходы
        await UpdateAloneStateAsync(channel);

        // Первый пользователь — создаём сессию
        if (channel.ConnectedUsers.Count == 1)
        {
            ChannelTimers[channel.Id] = DateTime.UtcNow;

            if (IsPrivateChannel(channel))
            {
                await channel.SendMessageAsync(
                    BotMessages.VoiceConversationStarted(channel.Mention),
                    allowedMentions: NoMentions);

                ChannelTargets[channel.Id] = channel;
            }
            else
            {
                var statusChannelId = AppConfig.VoiceStatusChannel;

                if (statusChannelId == 0)
                {
                    return;
                }

                var statusChannel = channel.Guild.GetTextChannel(statusChannelId);

                if (statusChannel == null)
                {
                    return;
                }

                var message = await statusChannel.SendMessageAsync(
                    BotMessages.VoiceConversationStarted(channel.Mention),
                    allowedMentions: NoMentions);

                var thread = await statusChannel.CreateThreadAsync(
                    channel.Name,
                    message: message);

                ChannelTargets[channel.Id] = thread;
            }
        }

        var target = GetTarget(channel.Id);

        if (target != null)
        {
            await target.SendMessageAsync(
                BotMessages.VoiceUserJoined(name, channel.Mention, GetTimer(channel.Id)),
                allowedMentions: NoMentions);
        }
    }

    private static async Task HandleLeave(SocketUser user, SocketVoiceChannel channel)
    {
        // Раньше проверки цели: переименование не зависит от того, настроен ли журнал
        await UpdateAloneStateAsync(channel);

        var target = GetTarget(channel.Id);

        if (target == null)
        {
            return;
        }

        var name = Mention(user);
        var timer = GetTimer(channel.Id);

        await target.SendMessageAsync(BotMessages.VoiceUserLeft(name, channel.Mention, timer),
            allowedMentions: NoMentions);

        // Последний ушёл — завершаем сессию
        if (channel.ConnectedUsers.Count == 0)
        {
            await target.SendMessageAsync(BotMessages.VoiceConversationEnded(channel.Mention, timer),
                allowedMentions: NoMentions);

            ChannelTargets.TryRemove(channel.Id, out _);
            ChannelTimers.TryRemove(channel.Id, out _);
        }
    }

    private static async Task HandleStateChange(
        SocketUser user, SocketVoiceState before, SocketVoiceState after,
        SocketVoiceChannel channel)
    {
        var target = GetTarget(channel.Id);

        if (target == null)
        {
            return;
        }

        var name = Mention(user);
        var timer = GetTimer(channel.Id);
        var ch = channel.Mention;

        // Стрим — независимо от мута/дефена
        if (before.IsStreaming != after.IsStreaming)
        {
            var msg = after.IsStreaming
                ? BotMessages.VoiceUserStartedStream(name, ch, timer)
                : BotMessages.VoiceUserStoppedStream(name, ch, timer);
            await target.SendMessageAsync(msg, allowedMentions: NoMentions);
        }

        // Деафен приоритетнее мута (деафен автоматически включает мут)
        if (before.IsSelfDeafened != after.IsSelfDeafened)
        {
            var msg = after.IsSelfDeafened
                ? BotMessages.VoiceUserDeafened(name, ch, timer)
                : BotMessages.VoiceUserUndeafened(name, ch, timer);
            await target.SendMessageAsync(msg, allowedMentions: NoMentions);
        }
        else if (before.IsDeafened != after.IsDeafened)
        {
            var msg = after.IsDeafened
                ? BotMessages.VoiceUserServerDeafened(name, ch, timer)
                : BotMessages.VoiceUserServerUndeafened(name, ch, timer);
            await target.SendMessageAsync(msg, allowedMentions: NoMentions);
        }
        else if (before.IsSelfMuted != after.IsSelfMuted)
        {
            var msg = after.IsSelfMuted
                ? BotMessages.VoiceUserMuted(name, ch, timer)
                : BotMessages.VoiceUserUnmuted(name, ch, timer);
            await target.SendMessageAsync(msg, allowedMentions: NoMentions);
        }
        else if (before.IsMuted != after.IsMuted)
        {
            var msg = after.IsMuted
                ? BotMessages.VoiceUserServerMuted(name, ch, timer)
                : BotMessages.VoiceUserServerUnmuted(name, ch, timer);
            await target.SendMessageAsync(msg, allowedMentions: NoMentions);
        }
    }

    /// <summary>
    /// Пересчитывает «одинокое» состояние канала: остался один — планируем переименование,
    /// иначе отменяем проверку и возвращаем родное имя.
    /// </summary>
    private static async Task UpdateAloneStateAsync(SocketVoiceChannel channel)
    {
        if (channel.ConnectedUsers.Count == 1)
        {
            ScheduleAloneCheck(channel);
            return;
        }

        CancelAloneCheck(channel.Id);
        await TryRestoreNameAsync(channel);
    }

    /// <summary>
    /// Планирует проверку одиночества через AloneRenameDelayMs.
    /// Предыдущая запланированная проверка этого канала отменяется.
    /// </summary>
    private static void ScheduleAloneCheck(SocketVoiceChannel channel)
    {
        if (IsPrivateChannel(channel))
        {
            return;
        }

        CancelAloneCheck(channel.Id);

        var cts = new CancellationTokenSource();
        var channelId = channel.Id;
        var guild = channel.Guild;
        AloneChecks[channelId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AloneRenameDelayMs, cts.Token);

                var semaphore = ChannelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(cts.Token);

                try
                {
                    // Канал перечитываем: за время задержки состав мог смениться
                    var current = guild.GetVoiceChannel(channelId);

                    if (current != null && current.ConnectedUsers.Count == 1)
                    {
                        await TryRenameToAloneAsync(current);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Кто-то зашёл или канал опустел — штатная отмена
            }
            catch (Exception ex)
            {
                BotLogger.Error("Ошибка проверки одиночества в канале {ChannelId}: {Message}", channelId, ex.Message);
            }
            finally
            {
                AloneChecks.TryRemove(new KeyValuePair<ulong, CancellationTokenSource>(channelId, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// Пишет о смене имени в журнал сессии канала, если он открыт.
    /// При сверке после перезапуска журнала ещё нет — тогда сообщение просто не отправляется.
    /// </summary>
    private static async Task NotifyRenameAsync(ulong channelId, string oldName, string newName)
    {
        var target = GetTarget(channelId);

        if (target == null)
        {
            return;
        }

        await target.SendMessageAsync(
            BotMessages.VoiceChannelRenamed(oldName, newName),
            allowedMentions: NoMentions);
    }

    /// <summary>
    /// Отменяет запланированную проверку одиночества, если она есть.
    /// </summary>
    private static void CancelAloneCheck(ulong channelId)
    {
        if (!AloneChecks.TryRemove(channelId, out var cts))
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Проверка успела завершиться сама
        }
    }

    /// <summary>
    /// Переименовывает канал в AloneChannelName, запомнив исходное имя.
    /// Имя пишется в БД ДО обращения к Discord: краш между записью и переименованием оставит
    /// лишь безобидную запись, а при обратном порядке родное имя потерялось бы навсегда.
    /// </summary>
    private static async Task TryRenameToAloneAsync(SocketVoiceChannel channel)
    {
        if (channel.Name == AloneChannelName || ChannelNameStore.GetOriginalName(channel.Id) != null)
        {
            return;
        }

        if (!channel.Guild.CurrentUser.GetPermissions(channel).ManageChannel)
        {
            BotLogger.Warning("Нет права управлять каналом {Channel} — переименование пропущено", channel.Name);
            return;
        }

        var originalName = channel.Name;
        ChannelNameStore.Remember(channel.Id, originalName);

        try
        {
            await channel.ModifyAsync(properties => properties.Name = AloneChannelName);
            BotLogger.Information("Канал {Channel} переименован в «{NewName}»", originalName, AloneChannelName);
            await NotifyRenameAsync(channel.Id, originalName, AloneChannelName);
        }
        catch (Exception ex)
        {
            // Обычно это лимит Discord: 2 переименования канала за 10 минут
            ChannelNameStore.Forget(channel.Id);
            BotLogger.Error("Не удалось переименовать канал {Channel}: {Message}", originalName, ex.Message);
        }
    }

    /// <summary>
    /// Возвращает каналу исходное имя, если бот его переименовывал.
    /// При неудаче запись в БД остаётся — её подхватит сверка при следующем запуске.
    /// </summary>
    private static async Task TryRestoreNameAsync(SocketVoiceChannel channel)
    {
        var originalName = ChannelNameStore.GetOriginalName(channel.Id);

        if (originalName == null)
        {
            return;
        }

        if (channel.Name == originalName)
        {
            ChannelNameStore.Forget(channel.Id);
            return;
        }

        try
        {
            var previousName = channel.Name;
            await channel.ModifyAsync(properties => properties.Name = originalName);
            ChannelNameStore.Forget(channel.Id);
            BotLogger.Information("Каналу возвращено имя {Channel}", originalName);
            await NotifyRenameAsync(channel.Id, previousName, originalName);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось вернуть имя каналу {Channel}: {Message}", originalName, ex.Message);
        }
    }

    private static bool IsPrivateChannel(SocketVoiceChannel channel)
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
}
