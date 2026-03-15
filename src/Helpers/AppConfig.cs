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
    public static string AnthropicApiKey => Get("COMMON", nameof(AnthropicApiKey));
    public static ulong LogsChannel => GetUlong("COMMON", nameof(LogsChannel));
    public static ulong VerificationChannel => GetUlong("COMMON", nameof(VerificationChannel));
    public static ulong VerificationRole => GetUlong("COMMON", nameof(VerificationRole));

    public static AnthropicSectionConfig CensorSettings { get; } = new("ANTHROPIC_CENSOR_SETTINGS");
    public static AnthropicSectionConfig SwearsCheckerSettings { get; } = new("ANTHROPIC_SWEARS_CHECKER_SETTINGS");

    /// <summary>
    /// Типизированная секция настроек Anthropic API (модель, токены, промпты).
    /// </summary>
    public record AnthropicSectionConfig(string SectionName)
    {
        public string Model => Get(SectionName, "AnthropicModel", "claude-haiku-4-5");

        public int MaxTokens => GetInt(SectionName, "MaxTokens", 50);

        public string SystemPrompt => Get(SectionName, "SystemPrompt");

        public string MessagePrompt => Get(SectionName, "MessagePrompt");
    }

    #region Internals

    /// <summary>
    /// Базовая директория с файлами (config.ini, swears.txt и т.д.).
    /// По умолчанию — папка Files рядом с исполняемым файлом.
    /// Можно переопределить из тестов для указания на исходные файлы проекта.
    /// </summary>
    internal static string FilesDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");

    private static string ConfigPath => Path.Combine(FilesDirectory, "config.ini");
    private static volatile Dictionary<string, Dictionary<string, string>> _sections = new();

    static AppConfig()
    {
        Reload();

        try
        {
            var dir = Path.GetDirectoryName(ConfigPath) ?? ".";
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(ConfigPath))
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

    public static string Get(string section, string key, string defaultValue = "") =>
        _sections.TryGetValue(section, out var dict) && dict.TryGetValue(key, out var value) ? value : defaultValue;

    public static int GetInt(string section, string key, int defaultValue = 0) =>
        int.TryParse(Get(section, key), out var result) ? result : defaultValue;

    public static ulong GetUlong(string section, string key, ulong defaultValue = 0) =>
        ulong.TryParse(Get(section, key), out var result) ? result : defaultValue;

    private static void Reload()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            var sections = new Dictionary<string, Dictionary<string, string>>();
            string? currentSection = null;
            string? currentKey = null;
            var lines = new List<string>();

            foreach (var line in File.ReadAllLines(ConfigPath))
            {
                var trimmed = line.Trim();

                // Пустые строки и комментарии
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                // Заголовок секции: [NAME]
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

                // Новый ключ: слово без пробелов, двоеточие, значение
                var colonIndex = trimmed.IndexOf(':');

                if (colonIndex > 0 && !trimmed[..colonIndex].Contains(' '))
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
            _sections = sections;
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка при загрузке config.ini: {Message}", ex.Message);
        }
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
