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
