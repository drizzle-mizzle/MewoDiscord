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

    public static string ChatGptSessionNewChat() =>
        Format(nameof(ChatGptSessionNewChat));

    public static string ChatGptSessionNewImage() =>
        Format(nameof(ChatGptSessionNewImage));

    public static string ChatGptSessionsEmpty() =>
        Format(nameof(ChatGptSessionsEmpty));

    public static string ChatGptSessionsTitle() =>
        Format(nameof(ChatGptSessionsTitle));

    public static string ChatGptRequestFailed() =>
        Format(nameof(ChatGptRequestFailed));

    public static string ChatGptImageFailed() =>
        Format(nameof(ChatGptImageFailed));

    public static string ChatGptImageTooBig(string size) =>
        Format(nameof(ChatGptImageTooBig), ("{size}", size));

    public static string ChatGptEmptyPrompt() =>
        Format(nameof(ChatGptEmptyPrompt));

    public static string ChatGptGuildOnly() =>
        Format(nameof(ChatGptGuildOnly));

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

            var dict = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(MessagesPath))
            {
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var colonIndex = trimmed.IndexOf(':');

                if (colonIndex <= 0)
                {
                    continue;
                }

                dict[trimmed[..colonIndex].Trim()] = trimmed[(colonIndex + 1)..].Trim();
            }

            _templates = dict;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при загрузке messages.ini: {ex.Message}");
        }
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