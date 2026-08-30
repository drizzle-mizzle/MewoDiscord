using System.Collections.Concurrent;
using System.Globalization;

namespace MewoDiscord.Helpers;

/// <summary>
/// Медиа-сессия: результат операции над файлом, на который можно ответить и попросить
/// поправить. Устроена как вкладка разговора, только предмет разговора — файл.
/// SourceMessageId указывает на сообщение с исходником, а не на результат: каждый круг
/// правок выполняется от оригинала, поэтому «ещё чуть-чуть» не накапливает потери
/// перекодирования. Plan — что к исходнику уже применили, чтобы модель считала уточнение
/// от текущей картинки, а не от нуля.
/// </summary>
public record MediaSession(
    ulong AnchorMessageId,
    ulong ChannelId,
    ulong SourceMessageId,
    string Plan,
    DateTimeOffset UpdatedAt);

/// <summary>
/// БД медиа-сессий: по записи на последний отправленный результат.
/// Формат файла — строка на сессию, поля через табуляцию, план последним полем
/// (в нём только числа и формат из белого списка, табуляций и переводов строк там нет).
/// Переживает перезапуск: иначе реплай на вчерашнюю картинку молча уходил бы в никуда —
/// ровно та беда, ради которой сессии и заводятся.
/// </summary>
public static class MediaSessionStore
{
    /// <summary>
    /// Сколько сессий держим. Лишние вытесняются по старшинству: реплай на давно
    /// забытый результат встречается редко, а файл БД расти бесконечно не должен.
    /// </summary>
    internal const int MaxSessions = 200;

    private static readonly ConcurrentDictionary<ulong, MediaSession> _sessions = new();
    private static readonly Lock _fileLock = new();

    private static string FilePath => Path.Combine(AppConfig.StateDirectory, "media_sessions.txt");

    /// <summary>
    /// Загружает БД с диска. Вызывать при запуске, до обработки сообщений.
    /// </summary>
    public static void Load()
    {
        try
        {
            _sessions.Clear();

            if (!File.Exists(FilePath))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(FilePath))
            {
                var session = Parse(line);

                if (session != null)
                {
                    _sessions[session.AnchorMessageId] = session;
                }
            }

            BotLogger.Information("Загружено медиа-сессий: {Count}", _sessions.Count);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка загрузки БД медиа-сессий: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Сессия, закреплённая за сообщением. null — это не результат операции над медиа.
    /// </summary>
    public static MediaSession? FindByAnchor(ulong messageId) => _sessions.GetValueOrDefault(messageId);

    /// <summary>
    /// Переносит сессию на новый результат. Старый якорь снимается: отвечать имеет смысл
    /// только на последнее сообщение, как и у сессий ChatGPT.
    /// </summary>
    public static void Remember(
        ulong anchorMessageId,
        ulong channelId,
        ulong sourceMessageId,
        string plan,
        ulong? previousAnchorId = null)
    {
        if (previousAnchorId != null)
        {
            _sessions.TryRemove(previousAnchorId.Value, out _);
        }

        _sessions[anchorMessageId] = new MediaSession(
            anchorMessageId,
            channelId,
            sourceMessageId,
            plan,
            DateTimeOffset.UtcNow);

        Evict();
        Save();
    }

    #region Internals

    private static void Evict()
    {
        while (_sessions.Count > MaxSessions)
        {
            var oldest = _sessions.Values.MinBy(session => session.UpdatedAt);

            if (oldest == null || !_sessions.TryRemove(oldest.AnchorMessageId, out _))
            {
                return;
            }
        }
    }

    internal static MediaSession? Parse(string line)
    {
        // План идёт последним полем и может содержать что угодно, кроме табуляции,
        // поэтому режем ровно на пять частей
        var parts = line.Split('\t', 5);

        if (parts.Length < 5
            || !ulong.TryParse(parts[0], out var anchorId)
            || !ulong.TryParse(parts[1], out var channelId)
            || !ulong.TryParse(parts[2], out var sourceId)
            || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            return null;
        }

        return new MediaSession(
            anchorId,
            channelId,
            sourceId,
            parts[4],
            new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    internal static string Format(MediaSession session) => string.Join(
        '\t',
        session.AnchorMessageId,
        session.ChannelId,
        session.SourceMessageId,
        session.UpdatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
        session.Plan);

    private static void Save()
    {
        lock (_fileLock)
        {
            StateFiles.WriteAtomic(FilePath, _sessions.Values.Select(Format), "БД медиа-сессий");
        }
    }

    #endregion
}
