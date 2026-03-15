using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using MewoDiscord.Handlers;
using MewoDiscord.Helpers;

using Serilog;
using Serilog.Events;

namespace MewoDiscord;


internal class Program
{
    private static DiscordSocketClient? _client;
    private static InteractionService? _interactions;

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
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(config);
        _interactions = new InteractionService(_client.Rest);
        BotLogger.SetClient(_client);

        // Инициализация обработчиков
        MessageHandler.Initialize();

        // Регистрация модулей команд
        await _interactions.AddModulesAsync(typeof(Program).Assembly, services: null);

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

    private static async Task OnReady()
    {
        await _interactions!.RegisterCommandsGloballyAsync();
        BotLogger.Information("Слеш-команды зарегистрированы");
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
