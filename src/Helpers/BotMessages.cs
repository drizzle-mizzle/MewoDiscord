namespace MewoDiscord.Helpers;

public static class BotMessages
{
    public static string VoiceSessionDuration(string timer) =>
        Format(nameof(VoiceSessionDuration), ("{timer}", timer));

    public static string VoiceConversationStarted() =>
        Format(nameof(VoiceConversationStarted));

    public static string VoiceConversationEnded(string channel) =>
        Format(nameof(VoiceConversationEnded), ("{channel}", channel));

    public static string VoiceUserJoined(string user, string channel) =>
        Format(nameof(VoiceUserJoined), ("{user}", user), ("{channel}", channel));

    public static string VoiceUserLeft(string user, string channel) =>
        Format(nameof(VoiceUserLeft), ("{user}", user), ("{channel}", channel));

    public static string VoiceUserKicked(string bot, string user) =>
        Format(nameof(VoiceUserKicked), ("{bot}", bot), ("{user}", user));

    public static string VoiceUserMuted(string user) =>
        Format(nameof(VoiceUserMuted), ("{user}", user));

    public static string VoiceUserUnmuted(string user) =>
        Format(nameof(VoiceUserUnmuted), ("{user}", user));

    public static string VoiceUserServerMuted(string user) =>
        Format(nameof(VoiceUserServerMuted), ("{user}", user));

    public static string VoiceUserServerUnmuted(string user) =>
        Format(nameof(VoiceUserServerUnmuted), ("{user}", user));

    public static string VoiceUserDeafened(string user) =>
        Format(nameof(VoiceUserDeafened), ("{user}", user));

    public static string VoiceUserUndeafened(string user) =>
        Format(nameof(VoiceUserUndeafened), ("{user}", user));

    public static string VoiceUserServerDeafened(string user) =>
        Format(nameof(VoiceUserServerDeafened), ("{user}", user));

    public static string VoiceUserServerUndeafened(string user) =>
        Format(nameof(VoiceUserServerUndeafened), ("{user}", user));

    public static string VoiceUserStartedStream(string user) =>
        Format(nameof(VoiceUserStartedStream), ("{user}", user));

    public static string VoiceUserStoppedStream(string user) =>
        Format(nameof(VoiceUserStoppedStream), ("{user}", user));

    // Те же события для общего чата: там нет ни треда, ни таймера сессии — зато нужен канал

    public static string VoiceCommonWithLink(string text, string link) =>
        Format(nameof(VoiceCommonWithLink), ("{text}", text), ("{link}", link));

    public static string VoiceConversationStartedCommon(string channel) =>
        Format(nameof(VoiceConversationStartedCommon), ("{channel}", channel));

    public static string VoiceConversationEndedCommon(string channel) =>
        Format(nameof(VoiceConversationEndedCommon), ("{channel}", channel));

    public static string VoiceUserStartedStreamCommon(string user, string channel) =>
        Format(nameof(VoiceUserStartedStreamCommon), ("{user}", user), ("{channel}", channel));

    public static string VoiceAloneCheck(string user) =>
        Format(nameof(VoiceAloneCheck), ("{user}", user));

    public static string VoiceAloneButton() =>
        Format(nameof(VoiceAloneButton));

    public static string VoiceAloneConfirmed(string user) =>
        Format(nameof(VoiceAloneConfirmed), ("{user}", user));

    public static string VoiceAloneNoAnswer(string user) =>
        Format(nameof(VoiceAloneNoAnswer), ("{user}", user));

    public static string VoiceAloneNotYours() =>
        Format(nameof(VoiceAloneNotYours));

    public static string VoiceAloneStale() =>
        Format(nameof(VoiceAloneStale));

    public static string VoiceChannelRenamed(string oldName, string newName) =>
        Format(nameof(VoiceChannelRenamed), ("{oldName}", oldName), ("{newName}", newName));

    public static string ReinstallDone(string global, string guild, string registered) =>
        Format(nameof(ReinstallDone), ("{global}", global), ("{guild}", guild), ("{registered}", registered));

    public static string SayDone() =>
        Format(nameof(SayDone));

    public static string HelpText() =>
        Format(nameof(HelpText));

    public static string HelpTextChatGpt() =>
        Format(nameof(HelpTextChatGpt));

    public static string PurgeDone(string count) =>
        Format(nameof(PurgeDone), ("{count}", count));

    public static string PurgeTooOld(string count) =>
        Format(nameof(PurgeTooOld), ("{count}", count));

    public static string PurgeNoPermission() =>
        Format(nameof(PurgeNoPermission));

    public static string PurgeScanned(string count) =>
        Format(nameof(PurgeScanned), ("{count}", count));

    public static string PurgeScanStopped(string found, string requested) =>
        Format(nameof(PurgeScanStopped), ("{found}", found), ("{requested}", requested));

    public static string PurgeScanLimit(string found, string requested, string scanned) =>
        Format(nameof(PurgeScanLimit), ("{found}", found), ("{requested}", requested), ("{scanned}", scanned));

    public static string PurgeFailed() =>
        Format(nameof(PurgeFailed));

    public static string PurgePeriodClamped() =>
        Format(nameof(PurgePeriodClamped));

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

    public static string XFooter() =>
        Format(nameof(XFooter));

    public static string XTooBig(string size, string url) =>
        Format(nameof(XTooBig), ("{size}", size), ("{url}", url));

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

    public static string AiActionUserNotFound() =>
        Format(nameof(AiActionUserNotFound));

    public static string MediaTooBig(string size) =>
        Format(nameof(MediaTooBig), ("{size}", size));

    public static string MediaResultTooBig(string size) =>
        Format(nameof(MediaResultTooBig), ("{size}", size));

    public static string MediaNotReadable() =>
        Format(nameof(MediaNotReadable));

    public static string MediaFormatNotSupported(string format) =>
        Format(nameof(MediaFormatNotSupported), ("{format}", format));

    public static string MediaFailed() =>
        Format(nameof(MediaFailed));

    public static string MediaBusy(string what) =>
        Format(nameof(MediaBusy), ("{what}", what));

    public static string MediaPlanFailed() =>
        Format(nameof(MediaPlanFailed));

    public static string MediaTruncated(string seconds) =>
        Format(nameof(MediaTruncated), ("{seconds}", seconds));

    public static string MediaShrinkFailed(string limit) =>
        Format(nameof(MediaShrinkFailed), ("{limit}", limit));

    public static string MediaGifTooHeavy() =>
        Format(nameof(MediaGifTooHeavy));

    public static string MediaSourceGone() =>
        Format(nameof(MediaSourceGone));

    public static string MediaModelNeedsStill() =>
        Format(nameof(MediaModelNeedsStill));

    public static string YoutubeDownloading(string quality) =>
        Format(nameof(YoutubeDownloading), ("{quality}", quality));

    public static string YoutubeCompressing() =>
        Format(nameof(YoutubeCompressing));

    public static string YoutubeMeta(string container, string resolution, string video, string audio) =>
        Format(
            nameof(YoutubeMeta),
            ("{container}", container),
            ("{resolution}", resolution),
            ("{video}", video),
            ("{audio}", audio));

    public static string YoutubeNoAudio() =>
        Format(nameof(YoutubeNoAudio));

    public static string YoutubeQualityReduced(string quality, string best) =>
        Format(nameof(YoutubeQualityReduced), ("{quality}", quality), ("{best}", best));

    public static string YoutubeRecompressed(string limit) =>
        Format(nameof(YoutubeRecompressed), ("{limit}", limit));

    public static string YoutubeTooBig(string size) =>
        Format(nameof(YoutubeTooBig), ("{size}", size));

    public static string YoutubeTooLong(string duration, string limit) =>
        Format(nameof(YoutubeTooLong), ("{duration}", duration), ("{limit}", limit));

    public static string YoutubeLive() =>
        Format(nameof(YoutubeLive));

    public static string YoutubeAgeRestricted() =>
        Format(nameof(YoutubeAgeRestricted));

    public static string YoutubeBotCheck() =>
        Format(nameof(YoutubeBotCheck));

    public static string YoutubePrivate() =>
        Format(nameof(YoutubePrivate));

    public static string YoutubeGeoBlocked() =>
        Format(nameof(YoutubeGeoBlocked));

    public static string YoutubeUnavailable() =>
        Format(nameof(YoutubeUnavailable));

    public static string YoutubeToolOutdated() =>
        Format(nameof(YoutubeToolOutdated));

    public static string YoutubeJsRuntime() =>
        Format(nameof(YoutubeJsRuntime));

    public static string YoutubeNoRoom() =>
        Format(nameof(YoutubeNoRoom));

    public static string YoutubeFailed() =>
        Format(nameof(YoutubeFailed));

    #region Internals

    private static string MessagesPath => Path.Combine(AppConfig.FilesDirectory, "messages.ini");
    private static volatile Dictionary<string, string> _templates = new();

    /// <summary>
    /// Вотчер перечитки: хранится полем, чтобы не быть собранным сборщиком мусора —
    /// см. <see cref="HotReload.Watch"/>.
    /// </summary>
    private static readonly FileSystemWatcher? _watcher;

    static BotMessages()
    {
        Reload();

        _watcher = HotReload.Watch(Path.GetDirectoryName(MessagesPath) ?? ".", Path.GetFileName(MessagesPath), Reload);
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

            if (IniFormat.IsKey(trimmed, colonIndex))
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