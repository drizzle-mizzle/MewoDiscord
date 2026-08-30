using System.Collections.Concurrent;

namespace MewoDiscord.Helpers;

/// <summary>
/// Текстовая БД исходных имён голосовых каналов, переименованных ботом.
/// Имя канала хранится на стороне Discord, поэтому после падения бота само не восстановится —
/// записи из этой БД сверяются при запуске.
/// Формат файла: строка на канал, «id: исходное имя».
/// </summary>
public static class ChannelNameStore
{
    private static readonly ConcurrentDictionary<ulong, string> _names = new();
    private static readonly Lock _fileLock = new();

    private static string FilePath => Path.Combine(AppConfig.StateDirectory, "voice_channels.txt");

    /// <summary>
    /// Загружает БД с диска, заменяя содержимое в памяти. Вызывать при запуске.
    /// </summary>
    public static void Load()
    {
        try
        {
            _names.Clear();

            if (!File.Exists(FilePath))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(FilePath))
            {
                var trimmed = line.Trim();
                var colonIndex = trimmed.IndexOf(':');

                if (colonIndex <= 0)
                {
                    continue;
                }

                // Двоеточие в имени безопасно: разбираем только по первому
                if (!ulong.TryParse(trimmed[..colonIndex].Trim(), out var channelId))
                {
                    continue;
                }

                var name = trimmed[(colonIndex + 1)..].Trim();

                if (name.Length > 0)
                {
                    _names[channelId] = name;
                }
            }

            BotLogger.Information("БД имён каналов загружена ({Count} записей)", _names.Count);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка загрузки БД имён каналов: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Возвращает исходное имя канала или null, если бот его не переименовывал.
    /// </summary>
    public static string? GetOriginalName(ulong channelId) =>
        _names.TryGetValue(channelId, out var name) ? name : null;

    /// <summary>
    /// Запоминает исходное имя канала перед переименованием.
    /// </summary>
    public static void Remember(ulong channelId, string originalName)
    {
        _names[channelId] = originalName;
        Save();
    }

    /// <summary>
    /// Убирает запись о канале — после возврата родного имени.
    /// </summary>
    public static void Forget(ulong channelId)
    {
        if (_names.TryRemove(channelId, out _))
        {
            Save();
        }
    }

    /// <summary>
    /// Все известные записи — для сверки при запуске.
    /// </summary>
    public static IReadOnlyCollection<(ulong ChannelId, string OriginalName)> All() =>
        _names.Select(pair => (pair.Key, pair.Value)).ToList();

    private static void Save()
    {
        lock (_fileLock)
        {
            StateFiles.WriteAtomic(FilePath, _names.Select(pair => $"{pair.Key}: {pair.Value}"), "БД имён каналов");
        }
    }
}
