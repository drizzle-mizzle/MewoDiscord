using System.Collections.Concurrent;
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
    private const int ContextMessageCount = 20;
    private const int ContextMaxTotalChars = 500;
    private const int ChatContextMessageCount = 15;
    private const int ChatContextMaxTotalChars = 2000;
    private const int MaxHeatLevel = 3;
    private const int ConversationTrackMessages = 5;
    private static readonly TimeSpan HeatCooldown = TimeSpan.FromMinutes(5);
    private static readonly double[] HeatTemperatureBonus = [0, 0, 0.5, 0.5];

    private static readonly ConcurrentDictionary<ulong, UserHeatState> _heatMap = new();
    private static readonly ConcurrentDictionary<ulong, int> _activeConversations = new();
    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _channelLocks = new();

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
        if (message.Author.IsBot)
        {
            return;
        }

        if (message is not SocketUserMessage userMessage)
        {
            return;
        }

        // Последовательная обработка сообщений в канале — чтобы ответ бота
        // успел появиться в контексте до обработки следующего сообщения.
        var channelLock = _channelLocks.GetOrAdd(message.Channel.Id, _ => new SemaphoreSlim(1, 1));
        await channelLock.WaitAsync();

        try
        {
            await ProcessMessageAsync(userMessage);
        }
        finally
        {
            channelLock.Release();
        }
    }

    private static async Task ProcessMessageAsync(SocketUserMessage userMessage)
    {
        // Верификация — проверяем до всего
        if (await VerificationHandler.TryHandleAsync(userMessage))
        {
            return;
        }

        // 1. Пинг / реплай бота → AI_CHAT (без ИИ-проверок)
        if (IsBotAddressed(userMessage))
        {
            await HandleChatAsync(userMessage);
            return;
        }

        // 2. Быстрая проверка на мат (BogaNet + словарь, без ИИ) → AI_CENSOR
        if (IsCensorEnabled && await TryHandleProfanityFastAsync(userMessage))
        {
            return;
        }

        // 3. Continuation (ИИ-проверка, но пропускается если 5+ сообщений) → AI_CHAT
        if (await TryContinueConversationAsync(userMessage))
        {
            return;
        }

        // 4. Проверка на мат с ИИ (регулярка + верификация) → AI_CENSOR
        if (IsCensorEnabled && await TryHandleProfanityWithAiAsync(userMessage))
        {
            return;
        }
    }

    /// <summary>
    /// Быстрая проверка на мат (BogaNet + словарь). Без обращений к ИИ.
    /// </summary>
    private static async Task<bool> TryHandleProfanityFastAsync(SocketUserMessage userMessage)
    {
        var text = NormalizeForSwearCheck(userMessage.Content);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
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

        if (badWords.Count == 0)
        {
            return false;
        }

        var originalBadWords = FindOriginalWords(userMessage.Content, badWords);
        BotLogger.LogAi("AI_CENSOR_SETTINGS", "Обнаружен мат от {User}: {BadWords}", userMessage.Author.Username, string.Join(", ", originalBadWords));
        await HandleProfanityAsync(userMessage, originalBadWords);
        return true;
    }

    /// <summary>
    /// Проверка на мат через регулярку + ИИ-верификацию. Вызывается после быстрой проверки и continuation.
    /// </summary>
    private static async Task<bool> TryHandleProfanityWithAiAsync(SocketUserMessage userMessage)
    {
        var text = NormalizeForSwearCheck(userMessage.Content);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return await CheckWithRegexAsync(text, userMessage);
    }

    /// <summary>
    /// Проверяет текст регуляркой и, при срабатывании, верифицирует через ИИ.
    /// </summary>
    private static async Task<bool> CheckWithRegexAsync(string text, SocketUserMessage message)
    {
        var foundWords = FindSwearsByRegex(text);

        if (foundWords.Count == 0)
        {
            return false;
        }

        var originalWords = FindOriginalWords(message.Content, foundWords);
        BotLogger.LogAi("AI_SWEARS_CHECKER_SETTINGS", "Регулярка нашла подозрительные слова от {User}: {Words}", message.Author.Username, string.Join(", ", originalWords));

        var confirmed = await VerifySwearsWithAiAsync(originalWords);

        if (!confirmed)
        {
            BotLogger.LogAi("AI_SWEARS_CHECKER_SETTINGS", "ИИ не подтвердил мат от {User}", message.Author.Username);
            return false;
        }

        BotLogger.LogAi("AI_SWEARS_CHECKER_SETTINGS", "ИИ подтвердил мат от {User}: {Words}", message.Author.Username, string.Join(", ", originalWords));
        AddToDictionary(foundWords);
        await HandleProfanityAsync(message, originalWords);
        return true;
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

        var reply = await AiClient.CompleteAsync(cfg, userMessage: prompt, systemPrompt: cfg.SystemPrompt, maxTokens: cfg.MaxTokens, temperature: cfg.Temperature);

        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        return reply.Trim().StartsWith("да", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Добавляет новые нормализованные слова в словарь (в память и в файл).
    /// </summary>
    private static void AddToDictionary(IList<string> normalizedWords)
    {
        var newWords = normalizedWords
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length >= 2 && !w.Contains(' ') && _swearWords.Add(w))
            .ToList();

        if (newWords.Count == 0)
        {
            return;
        }

        try
        {
            var existing = File.Exists(DictionaryPath) ? File.ReadAllText(DictionaryPath).Trim() : string.Empty;
            var separator = existing.Length > 0 ? ", " : string.Empty;
            File.WriteAllText(DictionaryPath, existing + separator + string.Join(", ", newWords));
            BotLogger.Information("Словарь пополнен: {Words}", string.Join(", ", newWords));
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка записи в словарь: {Message}", ex.Message);
        }
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
        var userId = message.Author.Id;
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        // Определяем уровень накала для пользователя
        var heatLevel = GetAndUpdateHeatLevel(userId);

        // Качаем предыдущие сообщения для контекста
        var previousMessages = await message.Channel
            .GetMessagesAsync(message, Direction.Before, ContextMessageCount)
            .FlattenAsync();

        // Формируем контекст: предыдущие (не старше 1 часа) + текущее
        var cutoff = message.Timestamp.AddHours(-1);
        var allLines = previousMessages
            .Reverse()
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Timestamp >= cutoff)
            .Select(m => $"{GetDisplayName(m.Author)}: {ResolveMentions(m.Content, guild)}")
            .ToList();

        var currentLine = $"{GetDisplayName(message.Author)}: {ResolveMentions(message.Content, guild)}";

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
        var user = GetDisplayName(message.Author);
        var badWordsStr = string.Join(", ", badWords);
        var botName = (message.Channel as SocketGuildChannel)?.Guild.CurrentUser.DisplayName ?? "Bot";

        // Подставляем плейсхолдеры в промпт
        var userMessagePrompt = cfg.MessagePrompt
            .Replace("{context}", context)
            .Replace("{user}", user)
            .Replace("{badWords}", badWordsStr)
            .Replace("{botName}", botName);

        // Выбираем системный промпт и температуру по уровню накала
        var systemPrompt = cfg.SystemPrompt.Replace("{botName}", botName);
        var temperature = cfg.Temperature + HeatTemperatureBonus[heatLevel];

        BotLogger.LogAi("AI_CENSOR_SETTINGS", "Накал для {User}: уровень {Level}, температура {Temperature:F2}", user, heatLevel, temperature);

        var reply = await AiClient.CompleteAsync(cfg, userMessage: userMessagePrompt, systemPrompt: systemPrompt, maxTokens: cfg.MaxTokens, temperature: temperature);

        if (!string.IsNullOrEmpty(reply))
        {
            await message.Channel.SendMessageAsync(reply);
            StartTrackingConversation(message.Channel.Id);
        }
    }

    /// <summary>
    /// Возвращает текущий уровень накала для пользователя и обновляет состояние.
    /// Если с последнего нарушения прошло больше 5 минут — сброс на 1.
    /// Иначе — уровень повышается (макс 3). Бонус температуры: 1→+0, 2→+0.5, 3→+1.0.
    /// </summary>
    private static int GetAndUpdateHeatLevel(ulong userId)
    {
        var now = DateTime.UtcNow;

        var state = _heatMap.AddOrUpdate(
            userId,
            _ => new UserHeatState { Level = 1, LastViolationTime = now },
            (_, existing) =>
            {
                if (now - existing.LastViolationTime > HeatCooldown)
                {
                    existing.Level = 1;
                }
                else if (existing.Level < MaxHeatLevel)
                {
                    existing.Level++;
                }

                existing.LastViolationTime = now;
                return existing;
            });

        return state.Level;
    }

    /// <summary>
    /// Возвращает серверное отображаемое имя (ник) пользователя, либо Username как фоллбэк.
    /// </summary>
    private static string GetDisplayName(IUser user)
    {
        return user is IGuildUser guildUser ? guildUser.DisplayName : user.Username;
    }

    /// <summary>
    /// Регулярка для Discord-упоминаний: &lt;@123&gt;, &lt;@!123&gt;, &lt;#123&gt;, &lt;@&amp;123&gt;.
    /// </summary>
    private static readonly Regex MentionRegex = new(
        @"<(?:@!?|#|@&)(\d+)>",
        RegexOptions.Compiled);

    /// <summary>
    /// Заменяет Discord-упоминания в тексте на отображаемые имена.
    /// </summary>
    private static string ResolveMentions(string text, IGuild? guild)
    {
        if (guild is null)
        {
            return text;
        }

        return MentionRegex.Replace(text, match =>
        {
            if (!ulong.TryParse(match.Groups[1].Value, out var id))
            {
                return match.Value;
            }

            var prefix = match.Value[1]; // '@' или '#'

            if (prefix == '#')
            {
                var channel = guild.GetChannelAsync(id).GetAwaiter().GetResult();
                return channel is not null ? $"#{channel.Name}" : match.Value;
            }

            var user = guild.GetUserAsync(id).GetAwaiter().GetResult();
            return user is not null ? $"@{user.DisplayName}" : match.Value;
        });
    }

    /// <summary>
    /// Проверяет, обращено ли сообщение к боту (пинг или ответ на сообщение бота).
    /// </summary>
    private static bool IsBotAddressed(SocketUserMessage message)
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;

        if (guild is null)
        {
            return false;
        }

        var botId = guild.CurrentUser.Id;

        // Прямой пинг бота
        if (message.MentionedUsers.Any(u => u.Id == botId))
        {
            return true;
        }

        // Ответ на сообщение бота
        if (message.Reference?.MessageId.IsSpecified == true)
        {
            var referenced = message.Channel.GetCachedMessage(message.Reference.MessageId.Value);

            if (referenced?.Author.Id == botId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Обрабатывает обращение к боту: собирает контекст и генерирует ответ через ИИ.
    /// startTracking: true для новых триггеров (пинг/реплай), false для продолжений.
    /// </summary>
    private static async Task HandleChatAsync(SocketUserMessage message, bool startTracking = true)
    {
        var cfg = AppConfig.ChatSettings;

        if (string.IsNullOrEmpty(cfg.SystemPrompt))
        {
            return;
        }

        var guild = (message.Channel as SocketGuildChannel)?.Guild;
        var botName = guild?.CurrentUser.DisplayName ?? "Bot";

        // Собираем контекст из предыдущих сообщений
        var previousMessages = await message.Channel
            .GetMessagesAsync(message, Direction.Before, ChatContextMessageCount)
            .FlattenAsync();

        var cutoff = message.Timestamp.AddHours(-1);
        var contextLines = new List<string>();
        var totalChars = 0;

        foreach (var msg in previousMessages.Reverse().Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Timestamp >= cutoff))
        {
            if (totalChars + msg.Content.Length > ChatContextMaxTotalChars && contextLines.Count > 0)
            {
                break;
            }

            var line = $"{GetDisplayName(msg.Author)}: {ResolveMentions(msg.Content, guild)}";
            contextLines.Add(line);
            totalChars += line.Length;
        }

        contextLines.Add($"{GetDisplayName(message.Author)}: {ResolveMentions(message.Content, guild)}");

        var context = string.Join('\n', contextLines);
        var user = GetDisplayName(message.Author);

        var userMessagePrompt = cfg.MessagePrompt
            .Replace("{context}", context)
            .Replace("{user}", user)
            .Replace("{botName}", botName);

        var systemPrompt = cfg.SystemPrompt.Replace("{botName}", botName);

        BotLogger.LogAi("AI_CHAT_SETTINGS", "Чат-запрос от {User}", user);

        using var typing = message.Channel.EnterTypingState();
        var reply = await AiClient.CompleteAsync(cfg, userMessage: userMessagePrompt, systemPrompt: systemPrompt, maxTokens: cfg.MaxTokens, temperature: cfg.Temperature);

        if (!string.IsNullOrEmpty(reply))
        {
            await message.Channel.SendMessageAsync(reply, messageReference: new MessageReference(message.Id));

            if (startTracking)
            {
                StartTrackingConversation(message.Channel.Id);
            }
        }
    }

    /// <summary>
    /// Начинает (или сбрасывает) отслеживание диалога в канале.
    /// </summary>
    private static void StartTrackingConversation(ulong channelId)
    {
        _activeConversations[channelId] = ConversationTrackMessages;
    }

    /// <summary>
    /// Проверяет, является ли сообщение продолжением активного диалога с ботом в канале.
    /// Если да — отвечает через чат и возвращает true. Декрементирует счётчик, при 0 — снимает трекинг.
    /// </summary>
    private static async Task<bool> TryContinueConversationAsync(SocketUserMessage message)
    {
        var channelId = message.Channel.Id;

        if (!_activeConversations.TryGetValue(channelId, out var remaining))
        {
            return false;
        }

        // Декрементируем счётчик
        if (remaining <= 1)
        {
            _activeConversations.TryRemove(channelId, out _);
            BotLogger.LogAi("AI_CONTINUATION_CHECKER_SETTINGS", "Диалог в канале {Channel} остыл (лимит сообщений)", message.Channel.Name);
            return false;
        }

        _activeConversations[channelId] = remaining - 1;

        // Спрашиваем ИИ, является ли это продолжением диалога
        var isContinuation = await CheckContinuationWithAiAsync(message);

        if (!isContinuation)
        {
            return false;
        }

        BotLogger.LogAi("AI_CONTINUATION_CHECKER_SETTINGS", "Продолжение диалога в канале {Channel} от {User} (осталось {Remaining})", message.Channel.Name, GetDisplayName(message.Author), remaining - 1);
        await HandleChatAsync(message, startTracking: false);
        return true;
    }

    /// <summary>
    /// Спрашивает ИИ, является ли сообщение продолжением диалога с ботом.
    /// </summary>
    private static async Task<bool> CheckContinuationWithAiAsync(SocketUserMessage message)
    {
        var cfg = AppConfig.ContinuationCheckerSettings;

        if (string.IsNullOrEmpty(cfg.SystemPrompt))
        {
            return false;
        }

        var guild = (message.Channel as SocketGuildChannel)?.Guild;
        var botName = guild?.CurrentUser.DisplayName ?? "Bot";

        // Собираем короткий контекст
        var previousMessages = await message.Channel
            .GetMessagesAsync(message, Direction.Before, ContextMessageCount)
            .FlattenAsync();

        var cutoff = message.Timestamp.AddHours(-1);
        var contextLines = new List<string>();
        var totalChars = 0;

        foreach (var msg in previousMessages.Reverse().Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Timestamp >= cutoff))
        {
            if (totalChars + msg.Content.Length > ContextMaxTotalChars && contextLines.Count > 0)
            {
                break;
            }

            var line = $"{GetDisplayName(msg.Author)}: {ResolveMentions(msg.Content, guild)}";
            contextLines.Add(line);
            totalChars += line.Length;
        }

        contextLines.Add($"{GetDisplayName(message.Author)}: {ResolveMentions(message.Content, guild)}");
        var context = string.Join('\n', contextLines);

        var user = GetDisplayName(message.Author);

        var prompt = cfg.MessagePrompt
            .Replace("{context}", context)
            .Replace("{user}", user)
            .Replace("{botName}", botName);

        var systemPrompt = cfg.SystemPrompt
            .Replace("{user}", user)
            .Replace("{botName}", botName);

        var reply = await AiClient.CompleteAsync(cfg, userMessage: prompt, systemPrompt: systemPrompt, maxTokens: cfg.MaxTokens, temperature: cfg.Temperature);

        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        return reply.Trim().StartsWith("да", StringComparison.OrdinalIgnoreCase);
    }

    private class UserHeatState
    {
        public int Level { get; set; }

        public DateTime LastViolationTime { get; set; }
    }
}
