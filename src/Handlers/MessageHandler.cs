using System.Text;
using System.Text.RegularExpressions;
using BogaNet.BWF;
using BogaNet.BWF.Filter;
using Discord;
using Discord.WebSocket;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

public static class MessageHandler
{
    private const int ContextMessageCount = 10;
    private const int ContextMaxTotalChars = 300;

    private static string DictionaryPath => Path.Combine(AppConfig.FilesDirectory, "swears.txt");

    /// <summary>
    /// Регулярка по корням мата (фоллбэк, если BogaNet и словарь не нашли).
    /// Источник: https://gist.github.com/imDaniX/8449f40655fcc1b92ae8d756cbca1264
    /// </summary>
    private static readonly Regex SwearRegex = new(
        @"\b(?:(?:(?:у|[нз]а|(?:хитро|не)?вз?[ыьъ]|с[ьъ]|(?:и|ра)[зс]ъ?|(?:о[тб]|п[оа]д)[ьъ]?|(?:.\B)+?[оаеи\-])\-?)?(?:[её](?:б(?!о[рй]|рач)|п[уа](?:ц|тс))|и[пб][ае][тцд][ьъ]).*?|(?:(?:н[иеа]|(?:ра|и)[зс]|[зд]?[ао](?:т|дн[оа])?|с(?:м[еи])?|а[пб]ч|в[ъы]?|пр[еи])\-?)?ху(?:[яйиеёю]|л+и(?!ган)).*?|бл(?:[эя]|еа?)(?:[дт][ьъ]?)?|\S*?(?:п(?:[иеё]зд|ид[аое]?р|ед(?:р(?!о)|[аое]р|ик)|охую)|бля(?:[дбц]|тс)|[ое]ху[яйиеё]|хуйн).*?|(?:о[тб]?|про|на|вы)?м(?:анд(?:[ауеыи](?:л(?:и[сзщ])?[ауеиы])?|ой|[ао]в.*?|юк(?:ов|[ауи])?|е[нт]ь|ища)|уд(?:[яаиое].+?|е?н(?:[ьюия]|ей))|[ао]л[ао]ф[ьъ](?:[яиюе]|[еёо]й))|елд[ауые].*?|ля[тд]ь|(?:[нз]а|по)х)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Регулярка для удаления разделителей между кириллическими буквами (п.и.з.д.а, х*у*й, б л я).
    /// </summary>
    private static readonly Regex SeparatorRegex = new(
        @"(?<=\p{IsCyrillic})[.\-*_]+(?=\p{IsCyrillic})",
        RegexOptions.Compiled);

    /// <summary>
    /// Регулярка для схлопывания повторяющихся букв (хуууууй → хуй).
    /// </summary>
    private static readonly Regex RepeatedCharsRegex = new(
        @"(.)\1+",
        RegexOptions.Compiled);

    /// <summary>
    /// Одиночные матерные слова из словаря (для поиска по токенам).
    /// </summary>
    private static HashSet<string> _swearWords = [];

    /// <summary>
    /// Включён ли режим Анти-быдло. По умолчанию включён, сбрасывается при рестарте.
    /// </summary>
    public static bool IsCensorEnabled = true;

    public static void Initialize()
    {
        BadWordFilter.Instance.LoadFiles(true, BWFConstants.BWF_RU);
        LoadDictionary();
        BotLogger.Information("Фильтр мата загружен ({Words} слов)", _swearWords.Count);
    }

    public static async Task HandleMessageReceived(SocketMessage message)
    {
        if (!IsCensorEnabled)
        {
            return;
        }

        if (message.Author.IsBot)
        {
            return;
        }

        if (message is not SocketUserMessage userMessage)
        {
            return;
        }

        var text = NormalizeForSwearCheck(userMessage.Content);

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Поиск мата через BogaNet
        var badWords = new List<string>(BadWordFilter.Instance.GetAll(text));

        // Поиск мата через собственный словарь
        var tokens = text.Split([' ', ',', '!', '?', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (_swearWords.Contains(token) && !badWords.Contains(token))
            {
                badWords.Add(token);
            }
        }

        if (badWords.Count > 0)
        {
            var originalBadWords = FindOriginalWords(userMessage.Content, badWords);
            BotLogger.Information("Обнаружен мат от {User}: {BadWords}", message.Author.Username, string.Join(", ", originalBadWords));
            await HandleProfanityAsync(userMessage, originalBadWords);

            return;
        }

        // Фоллбэк: регулярка + ИИ-верификация
        await CheckWithRegexAsync(text, userMessage);
    }

    /// <summary>
    /// Проверяет текст регуляркой и, при срабатывании, верифицирует через ИИ.
    /// </summary>
    private static async Task CheckWithRegexAsync(string text, SocketUserMessage message)
    {
        var foundWords = FindSwearsByRegex(text);

        if (foundWords.Count == 0)
        {
            return;
        }

        var originalWords = FindOriginalWords(message.Content, foundWords);
        BotLogger.Information("Регулярка нашла подозрительные слова от {User}: {Words}", message.Author.Username, string.Join(", ", originalWords));

        var confirmed = await VerifySwearsWithAiAsync(originalWords);

        if (!confirmed)
        {
            BotLogger.Information("ИИ не подтвердил мат от {User}", message.Author.Username);
            return;
        }

        BotLogger.Information("ИИ подтвердил мат от {User}: {Words}", message.Author.Username, string.Join(", ", originalWords));
        await HandleProfanityAsync(message, originalWords);
    }

    /// <summary>
    /// Ищет мат в тексте по регулярке. Возвращает список уникальных совпадений.
    /// Перед проверкой нормализует текст (латинские двойники → кириллица, убирает разделители).
    /// </summary>
    internal static List<string> FindSwearsByRegex(string text)
    {
        var matches = SwearRegex.Matches(text);

        return matches
            .Select(m => m.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Находит оригинальные слова из исходного текста, соответствующие нормализованным матам.
    /// Если маппинг не нашёлся — возвращает нормализованные слова как есть.
    /// </summary>
    private static List<string> FindOriginalWords(string originalText, IList<string> normalizedBadWords)
    {
        var originalTokens = originalText.ToLowerInvariant()
            .Split([' ', ',', '!', '?', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);

        var result = new List<string>();

        foreach (var token in originalTokens)
        {
            var normalized = NormalizeForSwearCheck(token);

            if (normalizedBadWords.Any(bw => normalized.Contains(bw)) && !result.Contains(token))
            {
                result.Add(token);
            }
        }

        return result.Count > 0 ? result : normalizedBadWords.ToList();
    }

    /// <summary>
    /// Нормализует текст для проверки мата: заменяет латинские двойники на кириллицу,
    /// цифры-двойники на буквы, убирает разделители между буквами.
    /// </summary>
    internal static string NormalizeForSwearCheck(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var ch in text.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'a' => 'а', // Latin a → Cyrillic а
                'b' => 'б', // Latin b → Cyrillic б
                'c' => 'с', // Latin c → Cyrillic с
                'e' => 'е', // Latin e → Cyrillic е
                'h' => 'н', // Latin h → Cyrillic н
                'k' => 'к', // Latin k → Cyrillic к
                'o' => 'о', // Latin o → Cyrillic о
                'p' => 'р', // Latin p → Cyrillic р
                'u' => 'у', // Latin u → Cyrillic у
                'x' => 'х', // Latin x → Cyrillic х
                'y' => 'у', // Latin y → Cyrillic у
                '0' => 'о', // Цифра 0 → Cyrillic о
                '3' => 'з', // Цифра 3 → Cyrillic з
                '6' => 'б', // Цифра 6 → Cyrillic б
                _ => ch,
            });
        }

        var result = sb.ToString();

        // Схлопываем повторяющиеся буквы (хуууууй → хуй)
        result = RepeatedCharsRegex.Replace(result, "$1");

        // Убираем разделители между буквами (п.и.з.д.а, х*у*й, б л я)
        return SeparatorRegex.Replace(result, string.Empty);
    }

    /// <summary>
    /// Отправляет найденные слова в ИИ для верификации. Возвращает true, если ИИ подтвердил мат.
    /// </summary>
    internal static async Task<bool> VerifySwearsWithAiAsync(List<string> foundWords)
    {
        var cfg = AppConfig.SwearsCheckerSettings;
        var swearsStr = string.Join(", ", foundWords);
        var prompt = cfg.MessagePrompt.Replace("{swears}", swearsStr);

        var reply = await AnthropicClient.CompleteAsync(AppConfig.AnthropicApiKey, cfg.Model, userMessage: prompt, systemPrompt: cfg.SystemPrompt, maxTokens: cfg.MaxTokens);

        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        return reply.Trim().StartsWith("да", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadDictionary()
    {
        try
        {
            if (!File.Exists(DictionaryPath))
            {
                BotLogger.Warning("Словарь мата не найден: {Path}", DictionaryPath);
                return;
            }

            var content = File.ReadAllText(DictionaryPath).Trim();
            var entries = content.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().ToLowerInvariant())
                .Where(e => e.Length >= 2)
                .Distinct()
                .ToList();

            _swearWords = entries.Where(e => !e.Contains(' ')).ToHashSet();
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка загрузки словаря мата: {Message}", ex.Message);
        }
    }

    private static async Task HandleProfanityAsync(SocketUserMessage message, IList<string> badWords)
    {
        var cfg = AppConfig.CensorSettings;

        // Качаем предыдущие сообщения для контекста
        var previousMessages = await message.Channel
            .GetMessagesAsync(message, Direction.Before, ContextMessageCount)
            .FlattenAsync();

        // Формируем контекст: предыдущие (не старше 1 часа) + текущее
        var cutoff = message.Timestamp.AddHours(-1);
        var allLines = previousMessages
            .Reverse()
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Timestamp >= cutoff)
            .Select(m => $"{m.Author.Username}: {m.Content}")
            .ToList();

        var currentLine = $"{message.Author.Username}: {message.Content}";

        // Обрезаем старые сообщения, если суммарно больше лимита (минимум одно остаётся)
        var totalChars = currentLine.Length;
        var contextLines = new List<string>();

        for (var i = allLines.Count - 1; i >= 0; i--)
        {
            if (totalChars + allLines[i].Length > ContextMaxTotalChars && contextLines.Count > 0)
            {
                break;
            }

            totalChars += allLines[i].Length;
            contextLines.Insert(0, allLines[i]);
        }

        contextLines.Add(currentLine);

        var context = string.Join('\n', contextLines);
        var user = message.Author.Username;
        var badWordsStr = string.Join(", ", badWords);
        var botName = (message.Channel as SocketGuildChannel)?.Guild.CurrentUser.DisplayName ?? "Bot";

        // Подставляем плейсхолдеры в промпт
        var userMessagePrompt = cfg.MessagePrompt
            .Replace("{context}", context)
            .Replace("{user}", user)
            .Replace("{badWords}", badWordsStr);

        var systemPrompt = cfg.SystemPrompt.Replace("{botName}", botName);

        var reply = await AnthropicClient.CompleteAsync(AppConfig.AnthropicApiKey, cfg.Model, userMessage: userMessagePrompt, systemPrompt: systemPrompt, maxTokens: cfg.MaxTokens);

        if (!string.IsNullOrEmpty(reply))
        {
            await message.Channel.SendMessageAsync(reply);
        }
    }
}
