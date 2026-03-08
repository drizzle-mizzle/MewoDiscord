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
    private const int ContextMessageCount = 5;

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
    /// Одиночные матерные слова из словаря (для поиска по токенам).
    /// </summary>
    private static HashSet<string> _swearWords = [];

    /// <summary>
    /// Матерные фразы из словаря (для поиска через Contains).
    /// </summary>
    private static List<string> _swearPhrases = [];

    /// <summary>
    /// Включён ли режим Анти-быдло. По умолчанию включён, сбрасывается при рестарте.
    /// </summary>
    public static bool IsCensorEnabled = true;

    public static void Initialize()
    {
        BadWordFilter.Instance.LoadFiles(true, BWFConstants.BWF_RU);
        LoadDictionary();
        BotLogger.Information("Фильтр мата загружен ({Words} слов, {Phrases} фраз из словаря)", _swearWords.Count, _swearPhrases.Count);
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

        var text = userMessage.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Поиск мата через BogaNet
        var badWords = new List<string>(BadWordFilter.Instance.GetAll(text));

        // Поиск мата через собственный словарь
        var lower = text.ToLowerInvariant();
        var tokens = lower.Split([' ', ',', '.', '!', '?', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (_swearWords.Contains(token) && !badWords.Contains(token))
            {
                badWords.Add(token);
            }
        }

        foreach (var phrase in _swearPhrases)
        {
            if (lower.Contains(phrase) && !badWords.Contains(phrase))
            {
                badWords.Add(phrase);
            }
        }

        if (badWords.Count > 0)
        {
            BotLogger.Information("Обнаружен мат от {User}: {BadWords}", message.Author.Username, string.Join(", ", badWords));
            await HandleProfanityAsync(userMessage, badWords);
            return;
        }

        // Фоллбэк: регулярка + ИИ-верификация
        await CheckWithRegexAsync(lower, userMessage);
    }

    /// <summary>
    /// Проверяет текст регуляркой и, при срабатывании, верифицирует через ИИ.
    /// </summary>
    private static async Task CheckWithRegexAsync(string lowerText, SocketUserMessage message)
    {
        var foundWords = FindSwearsByRegex(lowerText);

        if (foundWords.Count == 0)
        {
            return;
        }

        BotLogger.Information("Регулярка нашла подозрительные слова от {User}: {Words}", message.Author.Username, string.Join(", ", foundWords));

        var confirmed = await VerifySwearsWithAiAsync(foundWords);

        if (!confirmed)
        {
            BotLogger.Information("ИИ не подтвердил мат от {User}", message.Author.Username);
            return;
        }

        BotLogger.Information("ИИ подтвердил мат от {User}: {Words}", message.Author.Username, string.Join(", ", foundWords));
        await HandleProfanityAsync(message, foundWords);
    }

    /// <summary>
    /// Ищет мат в тексте по регулярке. Возвращает список уникальных совпадений.
    /// Перед проверкой нормализует текст (латинские двойники → кириллица, убирает разделители).
    /// </summary>
    internal static List<string> FindSwearsByRegex(string lowerText)
    {
        var normalized = NormalizeForSwearCheck(lowerText);
        var matches = SwearRegex.Matches(normalized);

        return matches
            .Select(m => m.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Нормализует текст для проверки мата: заменяет латинские двойники на кириллицу,
    /// цифры-двойники на буквы, убирает разделители между буквами.
    /// </summary>
    internal static string NormalizeForSwearCheck(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var ch in text)
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

        // Убираем разделители между буквами (п.и.з.д.а, х*у*й, б л я)
        return SeparatorRegex.Replace(sb.ToString(), string.Empty);
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
            _swearPhrases = entries.Where(e => e.Contains(' ')).ToList();
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
        var contextLines = previousMessages
            .Reverse()
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Timestamp >= cutoff)
            .Select(m => $"{m.Author.Username}: {m.Content}")
            .ToList();

        contextLines.Add($"{message.Author.Username}: {message.Content}");

        var context = string.Join('\n', contextLines);
        var user = message.Author.Username;
        var badWordsStr = string.Join(", ", badWords);

        // Подставляем плейсхолдеры в промпт
        var prompt = cfg.MessagePrompt
            .Replace("{context}", context)
            .Replace("{user}", user)
            .Replace("{badWords}", badWordsStr);

        var reply = await AnthropicClient.CompleteAsync(AppConfig.AnthropicApiKey, cfg.Model, userMessage: prompt, systemPrompt: cfg.SystemPrompt, maxTokens: cfg.MaxTokens);

        if (!string.IsNullOrEmpty(reply))
        {
            await message.Channel.SendMessageAsync(reply);
        }
    }
}
