namespace MewoDiscord.Helpers;

/// <summary>
/// Системное условие, без которого действие даже не пытается попасть в ИИ.
/// Гейт проверяется кодом, а не моделью: дешёвый отсев до похода в сеть.
/// </summary>
public enum CustomAiActionGate
{
    /// <summary>
    /// В сообщении есть прямое упоминание живого пользователя (кроме самого бота).
    /// </summary>
    HasUserMention,

    /// <summary>
    /// К сообщению или к цитируемому им сообщению приложено медиа — видео, гифка
    /// или картинка, в том числе вставленная ссылкой, то есть живущая в embed'е.
    /// </summary>
    HasMediaAttached
}

/// <summary>
/// Описание кастомного действия из файла custom_ai_actions/{id}.ini.
/// Id — имя файла без расширения, оно же ключ процессора.
/// </summary>
public record CustomAiAction(
    string Id,
    string Name,
    CustomAiActionGate Gate,
    string HitPrompt);

/// <summary>
/// Реестр кастомных действий (модуль CustomAiActions): плоские ini-файлы в
/// Files/custom_ai_actions, по файлу на действие. Формат — секции в квадратных скобках:
/// [ACTION] — человеческое название, [GATE] — системное условие из
/// <see cref="CustomAiActionGate"/>, [HIT_PROMPT] — промпт проверки попадания (ждём «ДА»),
/// в нём доступен плейсхолдер <see cref="MessagePlaceholder"/>. Что делать после
/// попадания — целиком забота процессора действия.
/// Файлы перечитываются на лету, как config.ini и messages.ini.
/// </summary>
public static class CustomAiActionStore
{
    /// <summary>
    /// Плейсхолдер текста сообщения пользователя в промптах действия.
    /// </summary>
    internal const string MessagePlaceholder = "{{message}}";

    private const string ActionsFolderName = "custom_ai_actions";

    private static volatile IReadOnlyList<CustomAiAction> _actions = [];

    private static string ActionsDirectory => Path.Combine(AppConfig.FilesDirectory, ActionsFolderName);

    /// <summary>
    /// Действия с указанным гейтом, в стабильном порядке (по имени файла).
    /// Порядок важен: HIT_PROMPT'ы пробуются по очереди до первого попадания.
    /// </summary>
    public static IReadOnlyList<CustomAiAction> ByGate(CustomAiActionGate gate) =>
        _actions.Where(a => a.Gate == gate).ToList();

    /// <summary>
    /// Загружает действия с диска и подписывается на изменения каталога.
    /// Вызывать при старте, до обработки сообщений.
    /// </summary>
    public static void Load()
    {
        Reload();

        try
        {
            Directory.CreateDirectory(ActionsDirectory);

            var watcher = new FileSystemWatcher(ActionsDirectory, "*.ini")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, _) => DelayedReload();
            watcher.Created += (_, _) => DelayedReload();
            watcher.Deleted += (_, _) => DelayedReload();
            watcher.Renamed += (_, _) => DelayedReload();
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Слежение за каталогом действий не включено: {Message}", ex.Message);
        }
    }

    #region Internals

    private static void DelayedReload()
    {
        Thread.Sleep(100);
        Reload();
    }

    private static void Reload()
    {
        try
        {
            if (!Directory.Exists(ActionsDirectory))
            {
                _actions = [];
                return;
            }

            var actions = new List<CustomAiAction>();

            // Порядок перебора действий должен быть предсказуем — сортируем по имени файла
            foreach (var path in Directory.GetFiles(ActionsDirectory, "*.ini").OrderBy(p => p, StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                var action = Parse(id, File.ReadAllLines(path));

                if (action == null)
                {
                    BotLogger.Warning("Действие {Id} пропущено: файл неполный или гейт неизвестен", id);
                    continue;
                }

                actions.Add(action);
            }

            _actions = actions;
            BotLogger.Information("Загружено кастомных действий: {Count}", actions.Count);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка загрузки кастомных действий: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Разбирает файл действия. null — нет обязательной секции или гейт неизвестен.
    /// Строки, начинающиеся с #, — комментарии (в том числе внутри секций).
    /// </summary>
    internal static CustomAiAction? Parse(string id, IEnumerable<string> source)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;

        foreach (var line in source)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = [];
                sections[trimmed[1..^1].Trim()] = current;
                continue;
            }

            // Текст до первой секции — мусор, игнорируем
            current?.Add(line.TrimEnd());
        }

        var name = ReadSection(sections, "ACTION");
        var gateName = ReadSection(sections, "GATE");
        var hitPrompt = ReadSection(sections, "HIT_PROMPT");

        if (name.Length == 0 || hitPrompt.Length == 0)
        {
            return null;
        }

        if (!TryParseGate(gateName, out var gate))
        {
            return null;
        }

        return new CustomAiAction(id, name, gate, hitPrompt);
    }

    /// <summary>
    /// Разбирает имя гейта из файла: SCREAMING_SNAKE_CASE соответствует значению enum.
    /// </summary>
    internal static bool TryParseGate(string value, out CustomAiActionGate gate)
    {
        gate = default;

        if (value.Length == 0)
        {
            return false;
        }

        var normalized = value.Trim().Replace("_", string.Empty);

        return Enum.TryParse(normalized, ignoreCase: true, out gate) && Enum.IsDefined(gate);
    }

    /// <summary>
    /// Тело секции целиком: многострочное, с обрезанными пустыми строками по краям.
    /// </summary>
    private static string ReadSection(Dictionary<string, List<string>> sections, string name)
    {
        if (!sections.TryGetValue(name, out var lines))
        {
            return string.Empty;
        }

        return string.Join('\n', lines).Trim();
    }

    #endregion
}
