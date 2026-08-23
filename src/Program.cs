using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using MewoDiscord.Commands;
using MewoDiscord.Handlers;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

using Serilog;
using Serilog.Events;

namespace MewoDiscord;


internal class Program
{
    private static DiscordSocketClient? _client;
    private static InteractionService? _interactions;
    private static bool _channelNamesRestored;
    private static bool _commandsRegistered;

    /// <summary>
    /// Модули команд отключённой ИИ-части: код оставлен, но в Discord не регистрируется.
    /// </summary>
    private static readonly Type[] AiCommandModules = [typeof(SetCommands), typeof(ToggleCommands)];

    /// <summary>
    /// Модули команд ChatGPT-части. Регистрируются только при UseChatGpt = true.
    /// </summary>
    private static readonly Type[] ChatGptCommandModules = [typeof(ChatGptCommands), typeof(ChatGptSessionCommands)];

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
        foreach (var module in AiCommandModules)
        {
            await _interactions.RemoveModuleAsync(module);
        }

        // Аналогично для ChatGPT-части
        if (!AppConfig.UseChatGpt)
        {
            BotLogger.Information("ChatGPT отключён (UseChatGpt: false): команды /chatgpt не активны");

            foreach (var module in ChatGptCommandModules)
            {
                await _interactions.RemoveModuleAsync(module);
            }
        }

        // Обработчики событий
        _client.Log += OnLog;
        _client.Ready += () => RunInBackground(OnReady());
        _client.InteractionCreated += interaction => RunInBackground(OnInteractionCreated(interaction));
        _client.MessageReceived += message => RunInBackground(MessageHandler.HandleMessageReceived(message));
        _client.UserVoiceStateUpdated += (user, before, after) => RunInBackground(VoiceStatusHandler.HandleVoiceStateUpdated(user, before, after));

        // Подключение к Discord
        await _client.LoginAsync(TokenType.Bot, AppConfig.BotToken);
        await _client.StartAsync();

        // Graceful shutdown по Ctrl+C
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

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
        // Глобальных команд бот больше не заводит, но могли остаться от прошлых версий:
        // рядом с серверными они дублировались бы в списке у клиентов
        var existingGlobal = await _client!.Rest.GetGlobalApplicationCommands();

        if (existingGlobal.Count > 0)
        {
            await _client.Rest.DeleteAllGlobalCommandsAsync();
        }

        if (guild == null)
        {
            return (existingGlobal.Count, 0, 0);
        }

        var removedGuild = (await guild.GetApplicationCommandsAsync()).Count;

        // Регистрация серверных команд — bulk-перезапись: устаревшие исчезают сами
        var registered = await _interactions!.RegisterCommandsToGuildAsync(guild.Id);

        return (existingGlobal.Count, removedGuild, registered.Count);
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

        await BotLogger.InitializeSessionAsync();

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
        // Глобальные команды могли остаться от прошлых версий бота: рядом с серверными
        // они дублировались бы в списке у клиентов
        var staleGlobal = await _client!.Rest.GetGlobalApplicationCommands();

        if (staleGlobal.Count > 0)
        {
            await _client.Rest.DeleteAllGlobalCommandsAsync();
            BotLogger.Information("Удалено глобальных слеш-команд: {Count}", staleGlobal.Count);
        }

        foreach (var guild in _client.Guilds)
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
        await _interactions!.ExecuteCommandAsync(context, services: null);
    }

    /// <summary>
    /// Запускает задачу в фоне, не блокируя gateway. Ошибки логируются.
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
                BotLogger.Error("Необработанная ошибка в обработчике: {Message}", ex.Message);
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
