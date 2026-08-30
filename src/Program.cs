using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using MewoDiscord.Commands;
using MewoDiscord.Handlers;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

using Serilog;
using Serilog.Events;

using System.Runtime.InteropServices;

namespace MewoDiscord;

internal class Program
{
    private static DiscordSocketClient? _client;
    private static InteractionService? _interactions;
    private static bool _channelNamesRestored;
    private static bool _commandsRegistered;
    private static bool _emotesReady;
    private static bool _loggerSessionReady;

    /// <summary>
    /// Модули команд отключённой ИИ-части: код оставлен, но в Discord не регистрируется.
    /// </summary>
    private static readonly Type[] _aiCommandModules = [typeof(SetCommands), typeof(ToggleCommands)];

    /// <summary>
    /// Модули команд ChatGPT-части. Регистрируются только при UseChatGpt = true.
    /// </summary>
    private static readonly Type[] _chatGptCommandModules = [typeof(ChatGptAuthCommands), typeof(ChatGptSessionCommands)];

    private static async Task Main()
    {
        // Настройка логирования
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/bot-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("Запуск бота...");

        // Проверка токена
        if (string.IsNullOrEmpty(AppConfig.BotToken))
        {
            Log.Error("Токен бота не найден в config.ini! Установите BotToken в файле конфигурации.");
            return;
        }

        // Создание клиента Discord
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
            MessageCacheSize = 100
        };

        _client = new DiscordSocketClient(config);
        _interactions = new InteractionService(_client.Rest);
        BotLogger.SetClient(_client);

        // БД исходных имён голосовых каналов, переименованных ботом
        ChannelNameStore.Load();

        // БД сессий ChatGPT и кастомные действия — нужны до первого сообщения
        if (AppConfig.UseChatGpt)
        {
            ChatGptSessionStore.Load();
            CustomAiActionStore.Load();
            MediaSessionStore.Load();

            // YouTube ломает yt-dlp примерно раз в месяц, и на вопрос «почему перестало
            // работать» в журнале должен быть ответ. Не отвечает — не беда: остальной
            // бот работает, а действие download_video скажет об этом само
            var ytDlpVersion = await YtDlpRunner.VersionAsync();

            if (ytDlpVersion != null)
            {
                Log.Information("yt-dlp версии {Version}", ytDlpVersion);
            }
            else
            {
                Log.Warning("yt-dlp не найден — скачивание видео с YouTube работать не будет");
            }
        }

        // Регистрация модулей команд
        await _interactions.AddModulesAsync(typeof(Program).Assembly, services: null);

        // ИИ-часть на OpenRouter отключена целиком — её команды в Discord не регистрируются
        foreach (var module in _aiCommandModules)
        {
            await _interactions.RemoveModuleAsync(module);
        }

        // Аналогично для ChatGPT-части
        if (!AppConfig.UseChatGpt)
        {
            BotLogger.Information("ChatGPT отключён (UseChatGpt: false): команды /chatgpt не активны");

            foreach (var module in _chatGptCommandModules)
            {
                await _interactions.RemoveModuleAsync(module);
            }
        }

        // Ретранслятор общих событий в общий чат — подписка до первого события
        GeneralChatRelay.Subscribe();

        // Обработчики событий
        _client.Log += OnLog;
        _interactions.Log += OnLog;
        _client.Ready += () => RunInBackground(OnReady());
        _client.InteractionCreated += interaction => RunInBackground(OnInteractionCreated(interaction));
        _client.MessageReceived += message => RunInBackground(MessageHandler.HandleMessageReceived(message));
        _client.UserVoiceStateUpdated += (user, before, after) => RunInBackground(VoiceStatusHandler.HandleVoiceStateUpdated(user, before, after));

        // Подключение к Discord
        await _client.LoginAsync(TokenType.Bot, AppConfig.BotToken);
        await _client.StartAsync();

        // Graceful shutdown по Ctrl+C (запуск с консоли) и по SIGTERM (docker stop —
        // основной способ остановки: без него контейнер гасят убийством процесса,
        // и каждая штатная остановка выглядит в логах падением)
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Ожидаемо при Ctrl+C
        }

        BotLogger.Information("Завершение работы бота...");
        await _client.StopAsync();
        await Log.CloseAndFlushAsync();
    }

    /// <summary>
    /// Принудительно переустанавливает слеш-команды: сносит все глобальные и серверные,
    /// включая устаревшие, которых уже нет в коде, и регистрирует текущий набор модулей
    /// на сервере, откуда вызвана команда. Набор учитывает снятые при запуске модули
    /// (ИИ-часть, ChatGPT при UseChatGpt: false) — обратно они не вернутся.
    /// </summary>
    internal static async Task<(int RemovedGlobal, int RemovedGuild, int Registered)> ReinstallCommandsAsync(SocketGuild? guild)
    {
        var removedGlobal = await DeleteStaleGlobalCommandsAsync();

        if (guild == null)
        {
            return (removedGlobal, 0, 0);
        }

        var removedGuild = (await guild.GetApplicationCommandsAsync()).Count;

        // Регистрация серверных команд — bulk-перезапись: устаревшие исчезают сами
        var registered = await _interactions!.RegisterCommandsToGuildAsync(guild.Id);

        return (removedGlobal, removedGuild, registered.Count);
    }

    /// <summary>
    /// Сносит глобальные слеш-команды: бот их больше не заводит, но могли остаться
    /// от прошлых версий, и рядом с серверными они дублировались бы в списке у клиентов.
    /// Возвращает количество снесённых.
    /// </summary>
    private static async Task<int> DeleteStaleGlobalCommandsAsync()
    {
        var existing = await _client!.Rest.GetGlobalApplicationCommands();

        if (existing.Count > 0)
        {
            await _client.Rest.DeleteAllGlobalCommandsAsync();
        }

        return existing.Count;
    }

    private static async Task OnReady()
    {
        // Ready повторяется на каждом переподключении gateway, а регистрация команд —
        // разовая операция с жёстким лимитом Discord: делаем только при первом запуске
        if (!_commandsRegistered)
        {
            _commandsRegistered = true;
            await RegisterGuildCommandsAsync();
        }

        // Тоже разово: иначе каждое переподключение gateway слало бы в канал логов
        // новое «Бот запущен» и заводило вторую пару тредов
        if (!_loggerSessionReady)
        {
            _loggerSessionReady = true;
            await BotLogger.InitializeSessionAsync();
        }

        // Эмодзи приложения живут у самого приложения, а не у сервера: заводятся один раз
        if (!_emotesReady)
        {
            _emotesReady = true;
            await BotEmotes.EnsureAsync(_client!);
        }

        // Только при первом Ready: событие повторяется на каждом переподключении gateway.
        // Сверка лишь будит вотчеры — те сами решают по актуальному составу каналов
        if (!_channelNamesRestored)
        {
            _channelNamesRestored = true;
            ChannelRenameWatcher.RestoreOnStartup(_client!);
        }
    }

    /// <summary>
    /// Регистрирует слеш-команды на каждом сервере, где есть бот. Серверные команды выбраны
    /// вместо глобальных: они появляются у клиентов сразу, а не расползаются по кэшу Discord
    /// до часа. Бот живёт на одном небольшом сервере, так что запросов немного.
    /// </summary>
    private static async Task RegisterGuildCommandsAsync()
    {
        var staleGlobal = await DeleteStaleGlobalCommandsAsync();

        if (staleGlobal > 0)
        {
            BotLogger.Information("Удалено глобальных слеш-команд: {Count}", staleGlobal);
        }

        foreach (var guild in _client!.Guilds)
        {
            try
            {
                // Bulk-перезапись: команды, которых больше нет в коде, исчезают сами
                await _interactions!.RegisterCommandsToGuildAsync(guild.Id);
                BotLogger.Information("Слеш-команды зарегистрированы на сервере {Guild}", guild.Name);
            }
            catch (Exception ex)
            {
                BotLogger.Error(ex, "Не удалось зарегистрировать команды на сервере {Guild}", guild.Name);
            }
        }
    }

    private static async Task OnInteractionCreated(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(_client!, interaction);

        // Interaction Framework ловит исключения команд сам и отдаёт их результатом:
        // выброшенный результат означает, что упавшая команда не оставит ни строчки
        // в логе, а пользователь увидит только «приложение не отвечает»
        var result = await _interactions!.ExecuteCommandAsync(context, services: null);

        if (!result.IsSuccess)
        {
            BotLogger.Error("Команда не выполнена ({Error}): {Reason}", result.Error?.ToString() ?? "неизвестно", result.ErrorReason ?? string.Empty);
        }
    }

    /// <summary>
    /// Наблюдает за уже запущенной задачей обработчика, не задерживая поток gateway,
    /// и логирует необработанные ошибки: это последний рубеж для всех событий.
    /// </summary>
    private static Task RunInBackground(Task task)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                BotLogger.Error(ex, "Необработанная ошибка в обработчике: {Message}", ex.Message);
            }
        });

        return Task.CompletedTask;
    }

    private static Task OnLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogEventLevel.Fatal,
            LogSeverity.Error => LogEventLevel.Error,
            LogSeverity.Warning => LogEventLevel.Warning,
            LogSeverity.Info => LogEventLevel.Information,
            LogSeverity.Verbose => LogEventLevel.Verbose,
            LogSeverity.Debug => LogEventLevel.Debug,
            _ => LogEventLevel.Information
        };

        BotLogger.Write(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
