using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Serilog;
using Serilog.Events;

namespace MewoDiscord.Helpers;

/// <summary>
/// Логгер-обёртка: пишет в Serilog и отправляет сообщения в текстовый канал Discord.
/// </summary>
public static partial class BotLogger
{
    private static DiscordSocketClient? _client;
    private static ulong _channelId;

    /// <summary>
    /// Устанавливает клиент Discord и читает ID канала логов из конфига.
    /// Вызывать после создания клиента.
    /// </summary>
    public static void SetClient(DiscordSocketClient client)
    {
        _client = client;
        _channelId = AppConfig.LogsChannel;
    }

    public static void Information(string template, params object[] args)
    {
        Log.Information(template, args);
        SendToDiscord(LogEventLevel.Information, template, args);
    }

    public static void Warning(string template, params object[] args)
    {
        Log.Warning(template, args);
        SendToDiscord(LogEventLevel.Warning, template, args);
    }

    public static void Error(string template, params object[] args)
    {
        Log.Error(template, args);
        SendToDiscord(LogEventLevel.Error, template, args);
    }

    public static void Error(Exception? exception, string template, params object[] args)
    {
        Log.Error(exception, template, args);
        SendToDiscord(LogEventLevel.Error, template, args);
    }

    public static void Write(LogEventLevel level, Exception? exception, string template, params object[] args)
    {
        Log.Write(level, exception, template, args);
        SendToDiscord(level, template, args);
    }

    private static void SendToDiscord(LogEventLevel level, string template, object[] args)
    {
        // Не спамим Debug/Verbose в Discord
        if (level < LogEventLevel.Information)
        {
            return;
        }

        // Клиент не установлен или канал не настроен
        if (_client is null || _channelId == 0)
        {
            return;
        }

        // Клиент не подключён
        if (_client.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        // Fire-and-forget: не блокируем вызывающий код
        _ = Task.Run(async () =>
        {
            try
            {
                if (_client.GetChannel(_channelId) is not ITextChannel channel)
                {
                    return;
                }

                var message = RenderTemplate(template, args);

                // Обрезаем, если сообщение слишком длинное для embed
                if (message.Length > 4000)
                {
                    message = message[..4000] + "...";
                }

                var embed = new EmbedBuilder()
                    .WithTitle(GetLevelName(level))
                    .WithDescription(message)
                    .WithColor(GetColor(level))
                    .WithCurrentTimestamp()
                    .Build();

                await channel.SendMessageAsync(embed: embed);
            }
            catch
            {
                // Ошибка отправки лога в Discord не должна ломать бота
            }
        });
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

    private static Color GetColor(LogEventLevel level) => level switch
    {
        LogEventLevel.Information => Color.Green,
        LogEventLevel.Warning => Color.Gold,
        LogEventLevel.Error => Color.Red,
        LogEventLevel.Fatal => Color.DarkRed,
        _ => Color.Default
    };

    private static string GetLevelName(LogEventLevel level) => level switch
    {
        LogEventLevel.Information => "INFO",
        LogEventLevel.Warning => "WARNING",
        LogEventLevel.Error => "ERROR",
        LogEventLevel.Fatal => "FATAL",
        _ => "LOG"
    };

    [GeneratedRegex(@"\{[A-Za-z_]\w*\}")]
    private static partial Regex TemplatePlaceholder();
}
