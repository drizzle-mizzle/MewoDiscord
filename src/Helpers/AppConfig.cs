using Serilog;

namespace MewoDiscord.Helpers;

/// <summary>
/// Конфигурация приложения из config.ini.
/// Формат: [СЕКЦИЯ], Key: Value, многострочные значения (строки без ключа продолжают предыдущее).
/// </summary>
public static class AppConfig
{
    public static string BotToken => Get("COMMON", nameof(BotToken));
    public static ulong VoiceStatusChannel => GetUlong("COMMON", nameof(VoiceStatusChannel));
    public static ulong LogsChannel => GetUlong("COMMON", nameof(LogsChannel));

    /// <summary>
    /// Общий чат сервера: туда уходят общие события публичных голосовых каналов —
    /// начало и конец разговора и начало стрима. 0 — не отправлять.
    /// </summary>
    public static ulong GeneralChatChannel => GetUlong("COMMON", nameof(GeneralChatChannel));

    public static string LocalTimeZone => Get("COMMON", nameof(LocalTimeZone), "Europe/Kiev");

    /// <summary>
    /// Путь к ffmpeg и ffprobe для операций над медиа. По умолчанию — из PATH:
    /// в docker-образе они ставятся пакетом, локально нужны на машине разработчика.
    /// </summary>
    public static string FfmpegPath => Get("COMMON", nameof(FfmpegPath), "ffmpeg");

    public static string FfprobePath => Get("COMMON", nameof(FfprobePath), "ffprobe");

    /// <summary>
    /// Включена ли ChatGPT-часть (чат с генерацией изображений через CLIProxyAPI).
    /// В отличие от остальных настроек фиксируется при запуске: от флага зависит
    /// создание лог-треда и набор команд, горячая перезагрузка его не подхватывает.
    /// </summary>
    public static bool UseChatGpt { get; private set; }

    /// <summary>
    /// Адрес CLIProxyAPI — OpenAI-совместимого прокси к подписке ChatGPT Plus.
    /// В Docker — http://cliproxy:8317, локально — http://localhost:8317.
    /// </summary>
    public static string ChatGptProxyUrl => Get("COMMON", nameof(ChatGptProxyUrl));

    /// <summary>
    /// Ключ клиента для CLIProxyAPI (совпадает с одним из api-keys в cliproxy/config.yaml).
    /// </summary>
    public static string ChatGptProxyApiKey => Get("COMMON", nameof(ChatGptProxyApiKey));

    /// <summary>
    /// Пароль management API CLIProxyAPI — для OAuth-логина через /chatgpt-auth login.
    /// Совпадает с MANAGEMENT_PASSWORD в cliproxy/management.env.
    /// </summary>
    public static string ChatGptManagementKey => Get("COMMON", nameof(ChatGptManagementKey));

    public static ChatGptSectionConfig ChatGptSettings { get; } = new("CHATGPT_SETTINGS");

    public static MediaSectionConfig MediaSettings { get; } = new("MEDIA");

    /// <summary>
    /// Типизированная секция работы с медиа: где рабочий каталог, сколько ему позволено
    /// занять и чем качать. Пути к ffmpeg и ffprobe остались в COMMON.
    /// </summary>
    public record MediaSectionConfig(string SectionName)
    {
        public string YtDlpPath => Get(SectionName, nameof(YtDlpPath), "yt-dlp");

        /// <summary>
        /// Рабочий каталог yt-dlp и ffmpeg. Пусто — временный каталог системы; в Docker
        /// сюда монтируется том, иначе видео растёт в writable-слое контейнера.
        /// </summary>
        public string WorkDirectory
        {
            get
            {
                var value = Get(SectionName, nameof(WorkDirectory));

                return value.Length > 0 ? value : Path.Combine(Path.GetTempPath(), "mewo-media");
            }
        }

        /// <summary>
        /// Потолок на рабочий каталог, МБ: исходник плюс артефакты пережатия.
        /// У обычного тома Docker размера нет, поэтому потолок держит сам бот.
        /// </summary>
        public int BudgetMb => GetInt(SectionName, nameof(BudgetMb), 3800);

        /// <summary>
        /// Потолок на скачиваемый исходник, МБ.
        /// </summary>
        public int MaxSourceMb => GetInt(SectionName, nameof(MaxSourceMb), 2048);

        /// <summary>
        /// Максимальная длительность видео, минут: слот держится всю операцию.
        /// </summary>
        public int MaxDurationMinutes => GetInt(SectionName, nameof(MaxDurationMinutes), 60);

        public int DownloadTimeoutMinutes => GetInt(SectionName, nameof(DownloadTimeoutMinutes), 30);

        public int EncodeTimeoutMinutes => GetInt(SectionName, nameof(EncodeTimeoutMinutes), 20);

        /// <summary>
        /// Файл кук YouTube в формате Netscape. Пусто — работаем без него.
        /// Нужен, когда YouTube начинает требовать «подтвердите, что вы не робот».
        /// </summary>
        public string YoutubeCookiesFile => Get(SectionName, nameof(YoutubeCookiesFile));

        /// <summary>
        /// Дополнительные аргументы yt-dlp, разбиваются по пробелам. Исключение из правила
        /// «командную строку собирает код»: их пишет администратор, и чинить обход
        /// антибот-проверки приходится быстрее, чем выходит новая сборка.
        /// </summary>
        public string YtDlpExtraArgs => Get(SectionName, nameof(YtDlpExtraArgs));
    }

    /// <summary>
    /// Типизированная секция настроек ChatGPT. Параметров изображений здесь нет —
    /// картинки рисует сама модель по ходу диалога, как в веб-интерфейсе.
    /// </summary>
    public record ChatGptSectionConfig(string SectionName)
    {
        public string ChatModel => Get(SectionName, "ChatModel", "gpt-5.5");

        /// <summary>
        /// Дешёвая модель для служебных запросов кастомных действий (распознать
        /// попадание, формализовать запрос). Пусто — берём ChatModel.
        /// </summary>
        public string InstantModel
        {
            get
            {
                var model = Get(SectionName, nameof(InstantModel));

                return model.Length > 0 ? model : ChatModel;
            }
        }

        public int MaxTokens => GetInt(SectionName, "MaxTokens", 2048);

        /// <summary>
        /// Глубина рассуждений в обычном чате: minimal, low, medium, high. Значение вне
        /// списка означает «не передавать поле вовсе» — иначе неизвестный уровень бэкенд
        /// отверг бы вместе со всем запросом. Служебные запросы уровень не получают.
        /// </summary>
        public string ReasoningEffort => Get(SectionName, nameof(ReasoningEffort), "high");

        public string SystemPrompt => Get(SectionName, "SystemPrompt");
    }

    #region Internals

    /// <summary>
    /// Базовая директория с файлами (config.ini, swears.txt и т.д.).
    /// По умолчанию — папка Files рядом с исполняемым файлом.
    /// Можно переопределить из тестов для указания на исходные файлы проекта.
    /// </summary>
    internal static string FilesDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");

    /// <summary>
    /// Директория изменяемого состояния бота (БД исходных имён каналов).
    /// Отдельно от Files: в Docker та лежит внутри образа и теряется при пересборке,
    /// а состояние должно переживать перезапуск. Можно переопределить из тестов.
    /// </summary>
    internal static string StateDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state");

    private static string ConfigPath => Path.Combine(FilesDirectory, "config.ini");
    private static volatile Dictionary<string, Dictionary<string, string>> _sections = new();

    /// <summary>
    /// Вотчер перечитки. Хранится полем не для использования, а чтобы жить: работающий
    /// вотчер рантайм держит слабой ссылкой, и без укоренения его соберёт GC.
    /// </summary>
    private static readonly FileSystemWatcher? _watcher;

    static AppConfig()
    {
        Reload();
        UseChatGpt = GetBool("COMMON", nameof(UseChatGpt), false);

        _watcher = HotReload.Watch(Path.GetDirectoryName(ConfigPath) ?? ".", Path.GetFileName(ConfigPath), Reload);
    }

    public static string Get(string section, string key, string defaultValue = "") =>
        _sections.TryGetValue(section, out var dict) && dict.TryGetValue(key, out var value) ? value : defaultValue;

    public static int GetInt(string section, string key, int defaultValue = 0) =>
        int.TryParse(Get(section, key), out var result) ? result : defaultValue;

    public static ulong GetUlong(string section, string key, ulong defaultValue = 0) =>
        ulong.TryParse(Get(section, key), out var result) ? result : defaultValue;

    public static bool GetBool(string section, string key, bool defaultValue = false) =>
        bool.TryParse(Get(section, key), out var result) ? result : defaultValue;

    public static double GetDouble(string section, string key, double defaultValue = 0) =>
        double.TryParse(Get(section, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : defaultValue;

    /// <summary>
    /// Записывает значение в указанную секцию config.ini.
    /// Если ключ уже существует — обновляет, иначе — добавляет в конец секции.
    /// </summary>
    public static void Set(string section, string key, string value)
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        var lines = File.ReadAllLines(ConfigPath).ToList();
        var sectionHeader = $"[{section}]";
        var sectionIndex = lines.FindIndex(l => l.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase));

        if (sectionIndex < 0)
        {
            return;
        }

        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            // Следующая секция — ключ не найден
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                lines.Insert(i, $"{key}: {value}");
                File.WriteAllLines(ConfigPath, lines);
                return;
            }

            var colonIndex = trimmed.IndexOf(':');

            if (IniFormat.IsKey(trimmed, colonIndex) && trimmed[..colonIndex].Trim() == key)
            {
                lines[i] = $"{key}: {value}";
                File.WriteAllLines(ConfigPath, lines);
                return;
            }
        }

        // Ключ не найден, секция последняя — добавляем в конец
        lines.Add($"{key}: {value}");
        File.WriteAllLines(ConfigPath, lines);
    }

    private static void Reload()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            _sections = Parse(File.ReadAllLines(ConfigPath));
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка при загрузке config.ini: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Разбирает config.ini: секции в квадратных скобках, «Ключ: значение», комментарии
    /// через #, а строка без ключа продолжает предыдущее значение — так пишутся промпты.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, string>> Parse(IEnumerable<string> source)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>();
        string? currentSection = null;
        string? currentKey = null;
        var lines = new List<string>();

        foreach (var line in source)
        {
            var trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                FlushValue(sections, currentSection, currentKey, lines);
                currentSection = trimmed[1..^1].Trim();
                currentKey = null;

                if (!sections.ContainsKey(currentSection))
                {
                    sections[currentSection] = new Dictionary<string, string>();
                }

                continue;
            }

            if (currentSection == null)
            {
                continue;
            }

            var colonIndex = trimmed.IndexOf(':');

            if (IniFormat.IsKey(trimmed, colonIndex))
            {
                FlushValue(sections, currentSection, currentKey, lines);
                currentKey = trimmed[..colonIndex].Trim();
                var value = trimmed[(colonIndex + 1)..].Trim();

                if (!string.IsNullOrEmpty(value))
                {
                    lines.Add(value);
                }
            }
            else if (currentKey != null)
            {
                // Продолжение многострочного значения
                lines.Add(trimmed);
            }
        }

        FlushValue(sections, currentSection, currentKey, lines);

        return sections;
    }


    private static void FlushValue(Dictionary<string, Dictionary<string, string>> sections, string? section, string? key, List<string> lines)
    {
        if (section != null && key != null && lines.Count > 0)
        {
            sections[section][key] = string.Join('\n', lines);
        }

        lines.Clear();
    }

    #endregion
}
