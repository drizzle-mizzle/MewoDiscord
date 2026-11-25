using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

/// <summary>
/// LRU с фиксированным TTL и файловым хранилищем. Новые записи вытесняют давно не использованные;
/// просроченные удаляются при обращении и фоновой очистке, включая их каталоги на диске.
/// </summary>
internal sealed class XTranslationMessageCache
{
    public const int MaxEntries = 20;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly Dictionary<ulong, XTranslationMessage> _entries = [];

    public XTranslationMessageCache(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public XTranslationMessage AddOrGet(
        PostMediaHandler.PostSentMessage context,
        DateTimeOffset now) => AddOrGet(context, now, out _);

    public XTranslationMessage AddOrGet(
        PostMediaHandler.PostSentMessage context,
        DateTimeOffset now,
        out bool added)
    {
        var removed = new List<XTranslationMessage>();
        XTranslationMessage result;

        lock (_sync)
        {
            if (_entries.TryGetValue(context.Message.Id, out var existing))
            {
                if (now < existing.ExpiresAt)
                {
                    existing.Touch(now);
                    added = false;
                    return existing;
                }

                _entries.Remove(context.Message.Id);
                removed.Add(existing);
            }

            result = new XTranslationMessage(
                context,
                Path.Combine(RootDirectory, context.Message.Id.ToString()),
                now);
            _entries.Add(context.Message.Id, result);
            added = true;

            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries.Values
                    .OrderBy(entry => entry.LastAccessAt)
                    .ThenBy(entry => entry.CreatedAt)
                    .First();
                _entries.Remove(oldest.Context.Message.Id);
                removed.Add(oldest);
            }
        }

        DeleteFiles(removed);
        return result;
    }

    public bool TryGet(ulong messageId, DateTimeOffset now, out XTranslationMessage? entry)
    {
        XTranslationMessage? expired = null;

        lock (_sync)
        {
            if (!_entries.TryGetValue(messageId, out entry))
            {
                return false;
            }

            if (now >= entry.ExpiresAt)
            {
                _entries.Remove(messageId);
                expired = entry;
                entry = null;
            }
            else
            {
                entry.Touch(now);
                return true;
            }
        }

        if (expired != null)
        {
            DeleteFiles([expired]);
        }

        return false;
    }

    public bool IsCurrent(XTranslationMessage entry)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(entry.Context.Message.Id, out var current) &&
                ReferenceEquals(entry, current) &&
                DateTimeOffset.UtcNow < entry.ExpiresAt;
        }
    }

    public void Remove(XTranslationMessage entry)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(entry.Context.Message.Id, out var current) ||
                !ReferenceEquals(entry, current))
            {
                return;
            }

            _entries.Remove(entry.Context.Message.Id);
        }

        DeleteFiles([entry]);
    }

    public void CleanupExpired(DateTimeOffset now)
    {
        var removed = new List<XTranslationMessage>();

        lock (_sync)
        {
            foreach (var pair in _entries.Where(pair => now >= pair.Value.ExpiresAt).ToList())
            {
                _entries.Remove(pair.Key);
                removed.Add(pair.Value);
            }
        }

        DeleteFiles(removed);
    }

    /// <summary>
    /// На запуске метаданных прошлого процесса уже нет, поэтому все оставшиеся файлы — мусор.
    /// </summary>
    public void CleanupStorage()
    {
        lock (_sync)
        {
            _entries.Clear();
        }

        if (Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }

        Directory.CreateDirectory(RootDirectory);
    }

    private static void DeleteFiles(IEnumerable<XTranslationMessage> entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                entry.DeleteFiles();
            }
            catch (Exception ex)
            {
                BotLogger.Warning(
                    "Не удалось удалить кэш X-карточки {MessageId}: {Message}",
                    entry.Context.Message.Id,
                    ex.Message);
            }
        }
    }
}
