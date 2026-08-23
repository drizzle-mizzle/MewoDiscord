using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Serilog;
using Serilog.Events;

namespace MewoDiscord.Helpers;

/// <summary>
/// Логгер-обёртка: пишет в Serilog и отправляет сообщения в Discord-треды.
/// При старте создаёт тред для слеш-команд и, если включена ChatGPT-часть, тред «ChatGPT».
/// </summary>
public static partial class BotLogger
{
    private const string CommandsThreadKey = "commands";

    /// <summary>
    /// Ключ треда ChatGPT-части (чат и генерация изображений через CLIProxyAPI).
    /// </summary>
    internal const string ChatGptThreadKey = "CHATGPT";

    private static DiscordSocketClient? _client;
    private static ulong _channelId;

    /// <summary>
    /// Треды текущей сессии: ключ → тред.
    /// </summary>
    private static readonly Dictionary<string, IThreadChannel> _threads = new();

    /// <summary>
    /// Устанавливает клиент Discord и читает ID канала логов из конфига.
    /// Вызывать после создания клиента.
    /// </summary>
    public static void SetClient(DiscordSocketClient client)
    {
        _client = client;
        _channelId = AppConfig.LogsChannel;
    }

    /// <summary>
    /// Инициализирует сессию логирования: отправляет стартовое сообщение и создаёт треды.
    /// Вызывать из OnReady() после регистрации команд.
    /// </summary>
    public static async Task InitializeSessionAsync()
    {
        if (_client is null || _channelId == 0)
        {
            return;
        }

        if (_client.GetChannel(_channelId) is not ITextChannel channel)
        {
            Log.Warning("Канал логов {ChannelId} не найден", _channelId);
            return;
        }

        try
        {
            // Стартовое сообщение
            var chatGptStatus = AppConfig.UseChatGpt ? "включён ✅" : "выключен ❌";

            var startEmbed = new EmbedBuilder()
                .WithTitle("Бот запущен")
                .WithDescription($"Сессия: {GetLocalNow():yyyy-MM-dd HH:mm:ss}\nChatGPT: {chatGptStatus}")
                .WithColor(Color.Green)
                .WithCurrentTimestamp()
                .Build();

            await channel.SendMessageAsync(embed: startEmbed);

            // Тред для слеш-команд (без привязки к сообщению — Discord позволяет только 1 тред на сообщение)
            var cmdThread = await channel.CreateThreadAsync(
                "Слеш-команды",
                ThreadType.PublicThread);

            _threads[CommandsThreadKey] = cmdThread;

            // Тред ChatGPT-части
            if (AppConfig.UseChatGpt)
            {
                var thread = await channel.CreateThreadAsync(
                    "ChatGPT",
                    ThreadType.PublicThread);

                _threads[ChatGptThreadKey] = thread;

                var cfg = AppConfig.ChatGptSettings;
                var configText = $"⚙️ **Конфигурация {cfg.SectionName}**\n" +
                                 $"Модель: `{cfg.ChatModel}`\n" +
                                 $"MaxTokens: `{cfg.MaxTokens}`";

                await thread.SendMessageAsync(configText);
            }

            Log.Information("Сессия логирования инициализирована: {ThreadCount} тредов", _threads.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка инициализации сессии логирования");
        }
    }

    #region Serilog-обёртки (только файл + консоль, без Discord)

    public static void Information(string template, params object[] args)
    {
        Log.Information(template, args);
    }

    public static void Warning(string template, params object[] args)
    {
        Log.Warning(template, args);
    }

    public static void Error(string template, params object[] args)
    {
        Log.Error(template, args);
    }

    public static void Error(Exception? exception, string template, params object[] args)
    {
        Log.Error(exception, template, args);
    }

    public static void Write(LogEventLevel level, Exception? exception, string template, params object[] args)
    {
        Log.Write(level, exception, template, args);
    }

    #endregion

    #region Discord-треды

    /// <summary>
    /// Логирует сообщение в тред слеш-команд.
    /// </summary>
    public static void LogCommand(string template, params object[] args)
    {
        Log.Information(template, args);
        SendToThread(CommandsThreadKey, template, args);
    }

    /// <summary>
    /// Логирует сообщение в тред указанной AI-конфигурации.
    /// </summary>
    public static void LogAi(string configName, string template, params object[] args)
    {
        Log.Information(template, args);
        SendToThread(configName, template, args);
    }

    private static void SendToThread(string threadKey, string template, object[] args)
    {
        if (!_threads.TryGetValue(threadKey, out var thread))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var message = RenderTemplate(template, args);
                var timestamp = GetLocalNow().ToString("HH:mm:ss");
                var text = $"`[{timestamp}]` {message}";

                // Discord ограничение — 2000 символов; разбиваем на части
                foreach (var chunk in SplitMessage(text))
                {
                    await thread.SendMessageAsync(chunk, allowedMentions: AllowedMentions.None);
                }
            }
            catch
            {
                // Ошибка отправки лога в Discord не должна ломать бота
            }
        });
    }

    #endregion

    /// <summary>
    /// Разбивает сообщение на части по 2000 символов, стараясь резать по переносу строки.
    /// Используется и для логов, и для длинных ответов ChatGPT.
    /// </summary>
    internal static List<string> SplitMessage(string text, int maxLength = 2000)
    {
        if (text.Length <= maxLength)
        {
            return [text];
        }

        var parts = new List<string>();

        while (text.Length > 0)
        {
            if (text.Length <= maxLength)
            {
                parts.Add(text);
                break;
            }

            // Ищем последний перенос строки в пределах лимита
            var cutAt = text.LastIndexOf('\n', maxLength - 1);

            if (cutAt <= 0)
            {
                // Нет переноса — режем по лимиту
                cutAt = maxLength;
            }

            parts.Add(text[..cutAt]);
            text = text[cutAt..].TrimStart('\n');
        }

        return parts;
    }

    /// <summary>
    /// Заменяет {Named} плейсхолдеры Serilog аргументами по порядку.
    /// </summary>
    private static string RenderTemplate(string template, object[] args)
    {
        if (args.Length == 0)
        {
            return template;
        }

        var index = 0;

        return TemplatePlaceholder().Replace(template, match =>
        {
            if (index < args.Length)
            {
                return args[index++]?.ToString() ?? string.Empty;
            }

            return match.Value;
        });
    }

    /// <summary>
    /// Возвращает текущее время в локальной таймзоне из конфига.
    /// </summary>
    private static DateTime GetLocalNow()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(AppConfig.LocalTimeZone);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    [GeneratedRegex(@"\{[A-Za-z_]\w*\}")]
    private static partial Regex TemplatePlaceholder();
}
