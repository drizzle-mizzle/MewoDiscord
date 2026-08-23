using System.Globalization;
using System.Text.Json;
using MewoDiscord.Utils;

namespace MewoDiscord.Helpers;

/// <summary>
/// БД сессий ChatGPT. Каждая сессия закреплена за последним сообщением бота в ней —
/// реплай на это сообщение продолжает диалог. Метаданные лежат в state/chatgpt_sessions.txt
/// (строка на сессию), полное состояние (история и последняя картинка) —
/// в state/chatgpt_sessions/{id}.json, чтобы сессии переживали перезапуск бота.
/// Сессий не больше <see cref="MaxSessions"/> — лишние вытесняются по старшинству.
/// </summary>
public static class ChatGptSessionStore
{
    /// <summary>
    /// Максимум живых сессий; создание сверх лимита вытесняет самую старую.
    /// </summary>
    internal const int MaxSessions = 20;

    private static readonly Dictionary<string, SessionEntry> _sessions = new();
    private static readonly Lock _lock = new();

    /// <summary>
    /// Последняя выданная метка времени — гарантирует монотонность UpdatedAtUtc,
    /// иначе сессии, созданные в один тик, неотличимы по свежести.
    /// </summary>
    private static DateTime _lastStamp = DateTime.MinValue;

    private static string IndexPath => Path.Combine(AppConfig.StateDirectory, "chatgpt_sessions.txt");
    private static string StateDirectory => Path.Combine(AppConfig.StateDirectory, "chatgpt_sessions");

    /// <summary>
    /// Живая сессия: метаданные, рантайм-состояние и замок для сериализации хитов.
    /// </summary>
    public sealed class SessionEntry
    {
        public required string Id { get; init; }

        public required ulong GuildId { get; init; }

        public required ulong ChannelId { get; init; }

        public ulong LastMessageId { get; internal set; }

        public DateTime UpdatedAtUtc { get; internal set; }

        public ChatGptSession Runtime { get; init; } = new();

        /// <summary>
        /// Хиты в одну сессию выполняются по одному: ChatGptSession не потокобезопасна.
        /// </summary>
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    /// <summary>
    /// Загружает сессии с диска. Вызывать при старте (до обработки сообщений).
    /// </summary>
    public static void Load()
    {
        lock (_lock)
        {
            _sessions.Clear();

            try
            {
                if (!File.Exists(IndexPath))
                {
                    return;
                }

                foreach (var line in File.ReadAllLines(IndexPath))
                {
                    var entry = ParseIndexLine(line);

                    if (entry == null)
                    {
                        continue;
                    }

                    LoadState(entry);
                    _sessions[entry.Id] = entry;

                    if (entry.UpdatedAtUtc > _lastStamp)
                    {
                        _lastStamp = entry.UpdatedAtUtc;
                    }
                }

                BotLogger.Information("Загружено сессий ChatGPT: {Count}", _sessions.Count);
            }
            catch (Exception ex)
            {
                BotLogger.Error("Ошибка загрузки БД сессий ChatGPT: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Создаёт сессию, закреплённую за сообщением бота. Сверх лимита — вытесняет старейшую.
    /// </summary>
    public static SessionEntry Create(ulong guildId, ulong channelId, ulong messageId)
    {
        lock (_lock)
        {
            var entry = new SessionEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                GuildId = guildId,
                ChannelId = channelId,
                LastMessageId = messageId,
                UpdatedAtUtc = NextStamp()
            };

            _sessions[entry.Id] = entry;

            // Вытеснение старейших сверх лимита
            while (_sessions.Count > MaxSessions)
            {
                var oldest = _sessions.Values.MinBy(e => e.UpdatedAtUtc)!;
                _sessions.Remove(oldest.Id);
                DeleteStateFile(oldest.Id);
            }

            SaveIndex();
            SaveState(entry);

            return entry;
        }
    }

    /// <summary>
    /// Сессия, за которой закреплено это сообщение (валидная цель реплая), иначе null.
    /// </summary>
    public static SessionEntry? FindByMessageId(ulong messageId)
    {
        lock (_lock)
        {
            return _sessions.Values.FirstOrDefault(e => e.LastMessageId == messageId);
        }
    }

    /// <summary>
    /// Последняя активная сессия канала — в неё летят прямые пинги бота.
    /// </summary>
    public static SessionEntry? FindLastActive(ulong channelId)
    {
        lock (_lock)
        {
            return _sessions.Values.Where(e => e.ChannelId == channelId).MaxBy(e => e.UpdatedAtUtc);
        }
    }

    /// <summary>
    /// Есть ли в канале хоть одна сессия.
    /// </summary>
    public static bool HasSessions(ulong channelId)
    {
        lock (_lock)
        {
            return _sessions.Values.Any(e => e.ChannelId == channelId);
        }
    }

    /// <summary>
    /// Перепривязывает сессию к новому последнему сообщению бота и сохраняет всё состояние.
    /// </summary>
    public static void Rebind(SessionEntry entry, ulong newLastMessageId)
    {
        lock (_lock)
        {
            entry.LastMessageId = newLastMessageId;
            entry.UpdatedAtUtc = NextStamp();
            SaveIndex();
            SaveState(entry);
        }
    }

    /// <summary>
    /// Все сессии, свежие сверху.
    /// </summary>
    public static IReadOnlyList<SessionEntry> All()
    {
        lock (_lock)
        {
            return _sessions.Values.OrderByDescending(e => e.UpdatedAtUtc).ToList();
        }
    }

    #region Internals

    /// <summary>
    /// Метка времени, строго больше предыдущей выданной.
    /// </summary>
    private static DateTime NextStamp()
    {
        var now = DateTime.UtcNow;

        if (now <= _lastStamp)
        {
            now = _lastStamp.AddTicks(1);
        }

        _lastStamp = now;
        return now;
    }

    /// <summary>
    /// Разбирает строку индекса: id|guildId|channelId|lastMessageId|updatedAtUtc.
    /// null — строка битая, пропускаем.
    /// </summary>
    internal static SessionEntry? ParseIndexLine(string line)
    {
        var parts = line.Trim().Split('|');

        if (parts.Length != 5 || parts[0].Length == 0)
        {
            return null;
        }

        if (!ulong.TryParse(parts[1], out var guildId)
            || !ulong.TryParse(parts[2], out var channelId)
            || !ulong.TryParse(parts[3], out var messageId))
        {
            return null;
        }

        if (!DateTime.TryParse(parts[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt))
        {
            return null;
        }

        return new SessionEntry
        {
            Id = parts[0],
            GuildId = guildId,
            ChannelId = channelId,
            LastMessageId = messageId,
            UpdatedAtUtc = updatedAt
        };
    }

    private static void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.StateDirectory);

            // Пишем во временный файл и подменяем — краш не оставит обрезанную БД
            var tempPath = IndexPath + ".tmp";
            var lines = _sessions.Values.Select(e =>
                $"{e.Id}|{e.GuildId}|{e.ChannelId}|{e.LastMessageId}|{e.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture)}");
            File.WriteAllLines(tempPath, lines);
            File.Move(tempPath, IndexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка записи индекса сессий ChatGPT: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Сохраняет полное состояние сессии (история, картинка, референсы) в её json.
    /// </summary>
    private static void SaveState(SessionEntry entry)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);

            var dto = new SessionFileDto
            {
                TotalTurns = entry.Runtime.TotalTurns,
                Turns = entry.Runtime.History
                    .Select(t => new TurnDto { Role = t.Role, Text = t.Text, Images = t.ImageDataUrls.ToList() })
                    .ToList(),
                LastImage = entry.Runtime.LastImage == null
                    ? null
                    : new ImageDto
                    {
                        Mime = entry.Runtime.LastImage.MimeType,
                        RevisedPrompt = entry.Runtime.LastImage.RevisedPrompt,
                        Content = entry.Runtime.LastImage.Content
                    }
            };

            var path = Path.Combine(StateDirectory, entry.Id + ".json");
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(dto));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка записи состояния сессии ChatGPT {Id}: {Message}", entry.Id, ex.Message);
        }
    }

    /// <summary>
    /// Восстанавливает рантайм-состояние сессии из json. Битый или отсутствующий файл
    /// не критичен — сессия продолжится с пустой историей.
    /// </summary>
    private static void LoadState(SessionEntry entry)
    {
        var path = Path.Combine(StateDirectory, entry.Id + ".json");

        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<SessionFileDto>(File.ReadAllText(path));

            if (dto == null)
            {
                return;
            }

            foreach (var turn in dto.Turns ?? [])
            {
                if (turn.Role != null && turn.Text != null)
                {
                    entry.Runtime.Append(new ChatGptClient.ChatTurn(turn.Role, turn.Text, turn.Images ?? []));
                }
            }

            // Append посчитал восстановленные ходы, но история обрезана — счётчик из файла
            // помнит и вытесненные. У сессий, сохранённых до появления поля, его нет
            entry.Runtime.TotalTurns = Math.Max(dto.TotalTurns, entry.Runtime.TotalTurns);

            if (dto.LastImage?.Content != null && dto.LastImage.Mime != null)
            {
                entry.Runtime.LastImage = new ChatGptClient.GeneratedImage(dto.LastImage.Content, dto.LastImage.Mime, dto.LastImage.RevisedPrompt);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Состояние сессии ChatGPT {Id} не восстановлено ({Message}) — история будет пустой", entry.Id, ex.Message);
        }
    }

    private static void DeleteStateFile(string id)
    {
        try
        {
            var path = Path.Combine(StateDirectory, id + ".json");

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось удалить состояние сессии ChatGPT {Id}: {Message}", id, ex.Message);
        }
    }

    private class SessionFileDto
    {
        public int TotalTurns { get; init; }

        public List<TurnDto>? Turns { get; init; }

        public ImageDto? LastImage { get; init; }
    }

    private class TurnDto
    {
        public string? Role { get; init; }

        public string? Text { get; init; }

        public List<string>? Images { get; init; }
    }

    private class ImageDto
    {
        public string? Mime { get; init; }

        public string? RevisedPrompt { get; init; }

        public byte[]? Content { get; init; }
    }

    #endregion
}
