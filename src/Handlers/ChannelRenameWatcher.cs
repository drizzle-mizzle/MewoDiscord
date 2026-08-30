using System.Collections.Concurrent;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

/// <summary>
/// Вотчер имён голосовых каналов: по актору на канал, каждый приводит имя к ожидаемому
/// состоянию — «одинокое» (AloneChannelName), если в публичном канале сидит один человек,
/// и родное из ChannelNameStore иначе.
/// Устройство продиктовано лимитом Discord «2 переименования канала за 10 минут»:
/// запросы шлются в режиме AlwaysFail (без ожидания в очереди Discord.NET), отказ превращается
/// в кулдаун, а каждая попытка заново смотрит актуальный состав канала. Журнал сессий
/// (VoiceStatusHandler) только сигналит сюда и никогда не ждёт переименований.
/// </summary>
public static class ChannelRenameWatcher
{
    /// <summary>
    /// Имя, на которое переименовывается публичный канал, если в нём остался один человек.
    /// </summary>
    internal const string AloneChannelName = "Одинокий пидоуебан";

    /// <summary>
    /// Окно тишины после последнего события, прежде чем трогать имя канала.
    /// Новое событие перезапускает отсчёт — транзитные входы-выходы не дёргают Discord.
    /// </summary>
    private const int QuietWindowMs = 5000;

    /// <summary>
    /// Кулдаун после отказа Discord перед следующей попыткой.
    /// </summary>
    private const int RetryDelayMs = 60_000;

    /// <summary>
    /// Предел попыток на один заход — с запасом больше окна лимита (10 минут),
    /// чтобы пустой канал не остался с «одиноким» именем: событий в нём больше не будет.
    /// </summary>
    private const int MaxAttempts = 15;

    /// <summary>
    /// Таймаут запроса переименования. С AlwaysFail исчерпанный лимит виден мгновенно,
    /// таймаут страхует только сетевые зависания.
    /// </summary>
    private const int RenameRequestTimeoutMs = 10_000;

    private static readonly ConcurrentDictionary<ulong, Watcher> _watchers = new();

    private enum ReconcileResult
    {
        /// <summary>Имя соответствует составу канала, делать нечего.</summary>
        Settled,

        /// <summary>Discord отказал (лимит/таймаут) — повторить после кулдауна.</summary>
        Retry,

        /// <summary>Канал больше не существует, актор не нужен.</summary>
        ChannelGone,
    }

    /// <summary>
    /// Решение, что делать с именем канала. Чистая функция — вынесена для тестов.
    /// </summary>
    internal enum RenameDecision
    {
        /// <summary>Ничего не делать.</summary>
        None,

        /// <summary>Переименовать в AloneChannelName.</summary>
        ToAlone,

        /// <summary>Вернуть родное имя из БД.</summary>
        ToOriginal,

        /// <summary>Имя сменили извне — забыть запись и не спорить с админом.</summary>
        Forget,
    }

    /// <summary>
    /// Сигнал «состав канала изменился». Единственная точка входа, мгновенная:
    /// вся работа происходит в фоновом акторе канала.
    /// </summary>
    public static void NotifyChannelChanged(SocketVoiceChannel channel)
    {
        while (true)
        {
            if (_watchers.TryGetValue(channel.Id, out var existing))
            {
                existing.Pulse();
                return;
            }

            // Новый вотчер рождается уже «сигналенным» — Pulse не нужен
            var created = new Watcher();

            if (_watchers.TryAdd(channel.Id, created))
            {
                _ = Task.Run(() => RunAsync(channel.Guild, channel.Id, created));
                return;
            }
        }
    }

    /// <summary>
    /// Сверка после запуска: будит вотчеры всех каналов из БД — вдруг бот упал
    /// с «одиноким» именем. Вызывать один раз при первом Ready.
    /// </summary>
    public static void RestoreOnStartup(DiscordSocketClient client)
    {
        foreach (var (channelId, _) in ChannelNameStore.All())
        {
            if (client.GetChannel(channelId) is SocketVoiceChannel channel)
            {
                NotifyChannelChanged(channel);
            }
            else
            {
                ChannelNameStore.Forget(channelId);
                BotLogger.Warning("Канал {ChannelId} из БД имён не найден — запись удалена", channelId);
            }
        }
    }

    /// <summary>
    /// Выбирает действие по текущему имени, записи в БД и составу канала.
    /// Принцип: бот меняет имя, только пока оно одно из двух ожидаемых им самим;
    /// любое стороннее имя означает вмешательство админа — уступаем и забываем запись.
    /// </summary>
    internal static RenameDecision Decide(string currentName, string? originalName, bool wantAlone)
    {
        // Записи нет — бот этот канал не переименовывал
        if (originalName == null)
        {
            if (!wantAlone || currentName == AloneChannelName)
            {
                return RenameDecision.None;
            }

            return RenameDecision.ToAlone;
        }

        if (wantAlone)
        {
            if (currentName == AloneChannelName)
            {
                return RenameDecision.None;
            }

            // Успели вернуть родное имя, но человек снова остался один
            if (currentName == originalName)
            {
                return RenameDecision.ToAlone;
            }

            return RenameDecision.Forget;
        }

        if (currentName == AloneChannelName)
        {
            return RenameDecision.ToOriginal;
        }

        // Родное имя уже на месте либо канал переименовали извне
        return RenameDecision.Forget;
    }

    /// <summary>
    /// Вечный цикл актора: спит до сигнала, выжидает окно тишины, затем сводит имя
    /// к ожидаемому, пережидая кулдауны Discord. Умирает только с каналом.
    /// </summary>
    private static async Task RunAsync(SocketGuild guild, ulong channelId, Watcher watcher)
    {
        try
        {
            while (true)
            {
                await watcher.Signal.WaitAsync();

                // Окно тишины: каждый новый сигнал перезапускает отсчёт
                while (await watcher.Signal.WaitAsync(QuietWindowMs))
                {
                }

                var settled = false;

                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    var result = await ReconcileAsync(guild, channelId);

                    if (result == ReconcileResult.ChannelGone)
                    {
                        return;
                    }

                    if (result == ReconcileResult.Settled)
                    {
                        settled = true;
                        break;
                    }

                    BotLogger.Warning(
                        "Канал {ChannelId}: попытка переименования {Attempt}/{Max} не прошла, повтор через {Delay} сек",
                        channelId, attempt, MaxAttempts, RetryDelayMs / 1000);
                    await Task.Delay(RetryDelayMs);
                }

                if (!settled)
                {
                    // Запись в БД цела: доведём при следующем событии или сверке после рестарта
                    BotLogger.Warning("Канал {ChannelId}: имя не выправлено за {Max} попыток, жду нового события", channelId, MaxAttempts);
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Error(ex, "Вотчер имени канала {ChannelId} остановлен: {Message}", channelId, ex.Message);
        }
        finally
        {
            _watchers.TryRemove(channelId, out _);
        }
    }

    /// <summary>
    /// Один шаг сведения: перечитывает канал, решает и применяет. Состав канала берётся
    /// на момент вызова, поэтому решение всегда актуально, сколько бы длился кулдаун.
    /// </summary>
    private static async Task<ReconcileResult> ReconcileAsync(SocketGuild guild, ulong channelId)
    {
        var channel = guild.GetVoiceChannel(channelId);

        if (channel == null)
        {
            ChannelNameStore.Forget(channelId);
            return ReconcileResult.ChannelGone;
        }

        var originalName = ChannelNameStore.GetOriginalName(channelId);
        var wantAlone = channel.ConnectedUsers.Count == 1 && !VoiceStatusHandler.IsPrivateChannel(channel);

        switch (Decide(channel.Name, originalName, wantAlone))
        {
            case RenameDecision.ToAlone:
                if (!channel.Guild.CurrentUser.GetPermissions(channel).ManageChannel)
                {
                    // Кулдаун не поможет: права сами не появятся
                    BotLogger.Warning("Нет права управлять каналом {Channel} — переименование пропущено", channel.Name);
                    return ReconcileResult.Settled;
                }

                // Имя в БД ДО обращения к Discord: краш между записью и переименованием
                // оставит лишь безобидную запись, обратный порядок терял бы имя навсегда
                if (originalName == null)
                {
                    ChannelNameStore.Remember(channelId, channel.Name);
                }

                return await RenameAsync(channel, AloneChannelName, forgetOnSuccess: false);

            case RenameDecision.ToOriginal:
                return await RenameAsync(channel, originalName!, forgetOnSuccess: true);

            case RenameDecision.Forget:
                ChannelNameStore.Forget(channelId);
                return ReconcileResult.Settled;

            default:
                return ReconcileResult.Settled;
        }
    }

    /// <summary>
    /// Само переименование в режиме AlwaysFail: исчерпанный лимит Discord даёт мгновенное
    /// исключение вместо многоминутного ожидания во внутренней очереди Discord.NET.
    /// </summary>
    private static async Task<ReconcileResult> RenameAsync(SocketVoiceChannel channel, string newName, bool forgetOnSuccess)
    {
        var oldName = channel.Name;

        try
        {
            var options = new RequestOptions
            {
                RetryMode = RetryMode.AlwaysFail,
                Timeout = RenameRequestTimeoutMs,
            };

            await channel.ModifyAsync(properties => properties.Name = newName, options);
        }
        catch (RateLimitedException)
        {
            BotLogger.Warning("Канал {Channel}: лимит переименований Discord (2 за 10 минут)", oldName);
            return ReconcileResult.Retry;
        }
        catch (TimeoutException)
        {
            BotLogger.Warning("Канал {Channel}: таймаут запроса переименования", oldName);
            return ReconcileResult.Retry;
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось переименовать канал {Channel}: {Message}", oldName, ex.Message);

            if (!forgetOnSuccess)
            {
                // Намерение «в одинокое» не сбылось — откатываем запись
                ChannelNameStore.Forget(channel.Id);
            }

            return ReconcileResult.Settled;
        }

        if (forgetOnSuccess)
        {
            ChannelNameStore.Forget(channel.Id);
        }

        BotLogger.Information("Канал «{OldName}» переименован в «{NewName}»", oldName, newName);
        await VoiceStatusHandler.NotifyChannelRenamedAsync(channel.Id, oldName, newName);
        return ReconcileResult.Settled;
    }

    /// <summary>
    /// Актор канала: сигнальный семафор ёмкостью 1 — повторные сигналы схлопываются,
    /// свежесозданный вотчер уже «сигнален».
    /// </summary>
    private sealed class Watcher
    {
        public SemaphoreSlim Signal { get; } = new(1, 1);

        public void Pulse()
        {
            try
            {
                Signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Сигнал уже стоит — этого достаточно
            }
        }
    }
}
