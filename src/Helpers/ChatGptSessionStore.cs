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

    /// <summary>
    /// Сколько предыдущих сообщений сессии помним сверх последнего — см.
    /// <see cref="SessionEntry.RecentMessageIds"/>.
    /// </summary>
    internal const int RecentMessagesKept = 8;

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

        /// <summary>
        /// Недавние сообщения сессии, кроме последнего: по ним видно, что реплай пришёл
        /// именно в эту ветку, пусть и не в её конец. Кольцо на
        /// <see cref="RecentMessagesKept"/> записей — на «это моё сообщение?» его хватает,
        /// а индекс не растёт.
        /// </summary>
        internal List<ulong> RecentMessageIds { get; } = [];

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
                DeleteOrphanStates();
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
            // Сессию могло вытеснить, пока её хит выполнялся: у обрабатываемой сессии
            // метка старая, и она — законная кандидатка на вылет. Сохранять вытесненную
            // нельзя: индекс её уже не содержит, а файл состояния воскрес бы навсегда
            if (!_sessions.ContainsKey(entry.Id))
            {
                BotLogger.Information("Сессия ChatGPT {Id} вытеснена во время хита — состояние не сохраняем", entry.Id);
                return;
            }

            if (entry.LastMessageId != 0)
            {
                entry.RecentMessageIds.Add(entry.LastMessageId);

                while (entry.RecentMessageIds.Count > RecentMessagesKept)
                {
                    entry.RecentMessageIds.RemoveAt(0);
                }
            }

            entry.LastMessageId = newLastMessageId;
            entry.UpdatedAtUtc = NextStamp();
            SaveIndex();
            SaveState(entry);
        }
    }

    /// <summary>
    /// Принадлежит ли сообщение канала какой-нибудь сессии — её последнему сообщению
    /// или одному из недавних. Нужно, чтобы отличить реплай в старую ветку сессии
    /// (правки истории не поддерживаются, такой реплай игнорируется) от реплая
    /// на любое другое сообщение бота, который трогать не наше дело.
    /// </summary>
    public static bool IsKnownMessage(ulong channelId, ulong messageId)
    {
        lock (_lock)
        {
            return _sessions.Values.Any(e => e.ChannelId == channelId
                && (e.LastMessageId == messageId || e.RecentMessageIds.Contains(messageId)));
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
    /// Сносит файлы состояния, которых нет в индексе. Такие остаются от вытеснений,
    /// заставших сессию за работой, и сами по себе не исчезают: индекс о них не помнит,
    /// а вытеснение бьёт только по живым записям. Каждый — история и картинка, то есть
    /// мегабайты в томе состояния.
    /// </summary>
    private static void DeleteOrphanStates()
    {
        try
        {
            if (!Directory.Exists(StateDirectory))
            {
                return;
            }

            var orphans = Directory.GetFiles(StateDirectory, "*.json")
                .Where(path => !_sessions.ContainsKey(Path.GetFileNameWithoutExtension(path)))
                .ToList();

            foreach (var path in orphans)
            {
                File.Delete(path);
            }

            if (orphans.Count > 0)
            {
                BotLogger.Information("Удалено осиротевших состояний сессий ChatGPT: {Count}", orphans.Count);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось подчистить состояния сессий ChatGPT: {Message}", ex.Message);
        }
    }

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
    /// Разбирает строку индекса: id|guildId|channelId|lastMessageId|updatedAtUtc
    /// и необязательное шестое поле — недавние сообщения сессии через запятую.
    /// Строки без него остались от прежних версий и читаются как есть.
    /// null — строка битая, пропускаем.
    /// </summary>
    internal static SessionEntry? ParseIndexLine(string line)
    {
        var parts = line.Trim().Split('|');

        if (parts.Length is not (5 or 6) || parts[0].Length == 0)
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

        var entry = new SessionEntry
        {
            Id = parts[0],
            GuildId = guildId,
            ChannelId = channelId,
            LastMessageId = messageId,
            UpdatedAtUtc = updatedAt
        };

        if (parts.Length == 6)
        {
            foreach (var recent in parts[5].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ulong.TryParse(recent, out var recentId))
                {
                    entry.RecentMessageIds.Add(recentId);
                }
            }
        }

        return entry;
    }

    private static void SaveIndex()
    {
        var lines = _sessions.Values.Select(e =>
            $"{e.Id}|{e.GuildId}|{e.ChannelId}|{e.LastMessageId}|{e.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture)}|{string.Join(',', e.RecentMessageIds)}");

        StateFiles.WriteAtomic(IndexPath, lines, "индекса сессий ChatGPT");
    }

    /// <summary>
    /// Сохраняет полное состояние сессии (история, картинка, референсы) в её json.
    /// </summary>
    private static void SaveState(SessionEntry entry)
    {
        try
        {
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
            StateFiles.WriteAtomic(path, JsonSerializer.Serialize(dto), $"состояния сессии ChatGPT {entry.Id}");
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка сборки состояния сессии ChatGPT {Id}: {Message}", entry.Id, ex.Message);
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
