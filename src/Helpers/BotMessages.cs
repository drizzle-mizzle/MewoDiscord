namespace MewoDiscord.Helpers;

public static class BotMessages
{
    public static string VoiceConversationStarted(string channel) =>
        Format(nameof(VoiceConversationStarted), ("{channel}", channel));

    public static string VoiceConversationEnded(string channel, string timer) =>
        Format(nameof(VoiceConversationEnded), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserJoined(string user, string channel, string timer) =>
        Format(nameof(VoiceUserJoined), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserLeft(string user, string channel, string timer) =>
        Format(nameof(VoiceUserLeft), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserMuted(string user, string channel, string timer) =>
        Format(nameof(VoiceUserMuted), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserUnmuted(string user, string channel, string timer) =>
        Format(nameof(VoiceUserUnmuted), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserServerMuted(string user, string channel, string timer) =>
        Format(nameof(VoiceUserServerMuted), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserServerUnmuted(string user, string channel, string timer) =>
        Format(nameof(VoiceUserServerUnmuted), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserDeafened(string user, string channel, string timer) =>
        Format(nameof(VoiceUserDeafened), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserUndeafened(string user, string channel, string timer) =>
        Format(nameof(VoiceUserUndeafened), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserServerDeafened(string user, string channel, string timer) =>
        Format(nameof(VoiceUserServerDeafened), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserServerUndeafened(string user, string channel, string timer) =>
        Format(nameof(VoiceUserServerUndeafened), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserStartedStream(string user, string channel, string timer) =>
        Format(nameof(VoiceUserStartedStream), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceUserStoppedStream(string user, string channel, string timer) =>
        Format(nameof(VoiceUserStoppedStream), ("{user}", user), ("{channel}", channel), ("{timer}", timer));

    public static string VoiceChannelRenamed(string oldName, string newName) =>
        Format(nameof(VoiceChannelRenamed), ("{oldName}", oldName), ("{newName}", newName));

    public static string ReinstallDone(string global, string guild, string registered) =>
        Format(nameof(ReinstallDone), ("{global}", global), ("{guild}", guild), ("{registered}", registered));

    public static string SayDone() =>
        Format(nameof(SayDone));

    public static string PurgeDone(string count) =>
        Format(nameof(PurgeDone), ("{count}", count));

    public static string PurgeTooOld(string count) =>
        Format(nameof(PurgeTooOld), ("{count}", count));

    public static string PurgeNoPermission() =>
        Format(nameof(PurgeNoPermission));

    public static string PurgeNotTextChannel() =>
        Format(nameof(PurgeNotTextChannel));

    public static string PurgeBadDateFormat() =>
        Format(nameof(PurgeBadDateFormat));

    public static string SetTemperature(string value) =>
        Format(nameof(SetTemperature), ("{value}", value));

    public static string TelegramFooter() =>
        Format(nameof(TelegramFooter));

    public static string TelegramTooBig(string size, string url) =>
        Format(nameof(TelegramTooBig), ("{size}", size), ("{url}", url));

    public static string ChatGptLoginInstructions(string url) =>
        Format(nameof(ChatGptLoginInstructions), ("{url}", url));

    public static string ChatGptLoginStartFailed() =>
        Format(nameof(ChatGptLoginStartFailed));

    public static string ChatGptLoginDone() =>
        Format(nameof(ChatGptLoginDone));

    public static string ChatGptLoginFailed(string error) =>
        Format(nameof(ChatGptLoginFailed), ("{error}", error));

    public static string ChatGptStatusEmpty() =>
        Format(nameof(ChatGptStatusEmpty));

    public static string ChatGptStatusHeader(string count) =>
        Format(nameof(ChatGptStatusHeader), ("{count}", count));

    public static string ChatGptStatusUnavailable() =>
        Format(nameof(ChatGptStatusUnavailable));

    public static string ChatGptSessionNew() =>
        Format(nameof(ChatGptSessionNew));

    public static string ChatGptSessionsEmpty() =>
        Format(nameof(ChatGptSessionsEmpty));

    public static string ChatGptSessionsTitle() =>
        Format(nameof(ChatGptSessionsTitle));

    public static string ChatGptSessionsLine(string index, string channel, string count, string updated, string link) =>
        Format(
            nameof(ChatGptSessionsLine),
            ("{index}", index),
            ("{channel}", channel),
            ("{count}", count),
            ("{updated}", updated),
            ("{link}", link));

    public static string ChatGptRequestFailed() =>
        Format(nameof(ChatGptRequestFailed));

    public static string ChatGptImageTooBig(string size) =>
        Format(nameof(ChatGptImageTooBig), ("{size}", size));

    public static string ChatGptEmptyPrompt() =>
        Format(nameof(ChatGptEmptyPrompt));

    public static string ChatGptGuildOnly() =>
        Format(nameof(ChatGptGuildOnly));

    public static string ChatGptNotAuthorized() =>
        Format(nameof(ChatGptNotAuthorized));

    public static string AiActionAvatarCard(string user) =>
        Format(nameof(AiActionAvatarCard), ("{user}", user));

    public static string AiActionAvatarFailed(string user) =>
        Format(nameof(AiActionAvatarFailed), ("{user}", user));

    #region Internals

    private static readonly string MessagesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files", "messages.ini");
    private static volatile Dictionary<string, string> _templates = new();

    static BotMessages()
    {
        Reload();

        try
        {
            var dir = Path.GetDirectoryName(MessagesPath) ?? ".";
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(MessagesPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, _) =>
            {
                Thread.Sleep(100);
                Reload();
            };
        }
        catch
        {
            // Watcher не критичен
        }
    }

    private static void Reload()
    {
        try
        {
            if (!File.Exists(MessagesPath))
            {
                return;
            }

            _templates = Parse(File.ReadAllLines(MessagesPath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при загрузке messages.ini: {ex.Message}");
        }
    }

    /// <summary>
    /// Разбирает messages.ini: «Ключ: текст», комментарии через #, а строка без ключа
    /// продолжает предыдущее сообщение (так пишутся многострочные инструкции).
    /// </summary>
    internal static Dictionary<string, string> Parse(IEnumerable<string> source)
    {
        var dict = new Dictionary<string, string>();
        string? currentKey = null;
        var lines = new List<string>();

        foreach (var line in source)
        {
            var trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var colonIndex = trimmed.IndexOf(':');

            if (colonIndex > 0 && IsKey(trimmed[..colonIndex]))
            {
                FlushValue(dict, currentKey, lines);
                currentKey = trimmed[..colonIndex];
                var value = trimmed[(colonIndex + 1)..].Trim();

                if (value.Length > 0)
                {
                    lines.Add(value);
                }
            }
            else if (currentKey != null)
            {
                // Строка без ключа продолжает предыдущее значение — так пишутся
                // многострочные тексты (инструкции в несколько шагов)
                lines.Add(trimmed);
            }
        }

        FlushValue(dict, currentKey, lines);

        return dict;
    }

    /// <summary>
    /// Ключом считается только латинский идентификатор: иначе продолжение вроде
    /// «Используй: yyyy-MM-dd» приняли бы за начало нового сообщения.
    /// </summary>
    private static bool IsKey(string candidate)
    {
        if (candidate.Length == 0 || !char.IsAsciiLetter(candidate[0]))
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static void FlushValue(Dictionary<string, string> dict, string? key, List<string> lines)
    {
        if (key != null && lines.Count > 0)
        {
            dict[key] = string.Join('\n', lines);
        }

        lines.Clear();
    }

    private static string Format(string key, params (string placeholder, string value)[] replacements)
    {
        var template = _templates.GetValueOrDefault(key, key);
        foreach (var (placeholder, val) in replacements)
        {
            template = template.Replace(placeholder, val);
        }

        return template;
    }

    #endregion
}