using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Клиент ChatGPT через CLIProxyAPI — локальный OpenAI-совместимый прокси, который расходует
/// квоту подписки ChatGPT Plus (Codex OAuth) вместо API-биллинга.
/// Единственный путь — POST /v1/chat/completions: прокси подмешивает в запрос инструмент
/// image_generation, поэтому модель сама решает, ответить текстом или нарисовать картинку,
/// как в веб-интерфейсе. Прокси stateless, история диалога живёт
/// в <see cref="ChatGptSession"/> на нашей стороне.
/// </summary>
public static class ChatGptClient
{
    private const string ChatCompletionsPath = "/v1/chat/completions";

    /// <summary>
    /// Генерация картинки занимает минуты, обычный таймаут HttpClient не подходит.
    /// Читается один раз при создании клиента, горячая перезагрузка не подхватит.
    /// </summary>
    private const int RequestTimeoutSeconds = 300;

    /// <summary>
    /// Потолок служебного запроса к инстант-модели: он делается под замком канала,
    /// и пятиминутный таймаут генерации картинок остановил бы весь канал.
    /// </summary>
    private const int InstantTimeoutSeconds = 30;

    /// <summary>
    /// Максимум ходов в истории сессии (обмен «вопрос-ответ» — это два хода).
    /// </summary>
    internal const int MaxHistoryTurns = 40;

    /// <summary>
    /// Сколько последних ходов истории хранят приложенные к ним картинки; дальше в прошлое
    /// остаётся только служебная строка с именами файлов. Картинки уходят в запрос при
    /// каждом обмене, и без потолка пара фотографий раздувает и запрос, и состояние на диске.
    /// </summary>
    internal const int MaxImageTurns = 4;

    /// <summary>
    /// Максимальный размер входного файла: base64 раздувает его ещё на треть,
    /// а всё вместе должно помещаться в один HTTP-запрос к прокси.
    /// </summary>
    internal const int MaxInputFileBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Максимум картинок в одном запросе (референсы генерации или вложения чата).
    /// </summary>
    internal const int MaxImagesPerRequest = 8;

    /// <summary>
    /// Потолок ответа инстант-модели. Ей отвечать «ДА» или одной формализованной фразой,
    /// но у рассуждающих моделей часть бюджета уходит на скрытые токены — с запасом.
    /// </summary>
    internal const int InstantMaxTokens = 512;

    /// <summary>
    /// Потолок цитаты в шапке сообщения: она напоминает, о чём речь, а не пересказывает.
    /// </summary>
    internal const int MaxQuotedLength = 300;

    private const string DefaultBotName = "bot";

    /// <summary>
    /// Базовый системный промпт: модель в общем чате с многими собеседниками, как её
    /// зовут и в каком формате приходят сообщения. Формат описан здесь, а собирается
    /// в BuildHeader — править их надо вместе. Про Discord намеренно ни слова.
    /// Промпт из config.ini (характер, язык) дописывается следом.
    /// </summary>
    private const string BaseSystemPrompt =
        """
        Ты — собеседник в общем чате, где много участников.
        Тебя зовут {botName}, к тебе обращаются по имени через @.

        Сообщения участников приходят в устойчивом формате. Сначала служебные строки
        в квадратных скобках, каждая со своей строки:
        [имя] — кто написал сообщение, эта строка есть всегда;
        [quotes имя: "текст"] — сообщение, на которое отвечает автор, если он кому-то отвечает;
        [приложил изображения: имена файлов] — если к сообщению приложены картинки.
        После служебных строк идёт сам текст сообщения.

        Отвечай только текстом ответа: без служебных строк, без своего имени в начале
        и без кавычек вокруг ответа. Чтобы обратиться к участнику, упомяни его как @имя —
        ровно тем именем, которое стоит в квадратных скобках.
        """;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
    };

    public record InputFile(string FileName, byte[] Content, string? MimeType = null);

    public record GeneratedImage(byte[] Content, string MimeType, string? RevisedPrompt);

    /// <summary>
    /// Ответ чата: текст и картинки, которые модель решила нарисовать (может быть и то, и другое).
    /// <paramref name="Unauthorized"/> — ответа не будет, пока администратор не перелогинится:
    /// повтор запроса тут не поможет, и предлагать его пользователю нельзя.
    /// </summary>
    public record ChatReply(string Text, IReadOnlyList<GeneratedImage> Images, bool Unauthorized = false);

    /// <summary>
    /// Пустой ответ — вернуть при любой ошибке.
    /// </summary>
    private static readonly ChatReply _emptyReply = new(string.Empty, []);

    /// <summary>
    /// Ход диалога: роль, текст и data-URL приложенных картинок.
    /// </summary>
    internal record ChatTurn(string Role, string Text, IReadOnlyList<string> ImageDataUrls);

    /// <summary>
    /// Обстановка вокруг сообщения: как зовут бота в этом чате, кто написал и на что
    /// отвечает. Из неё собирается шапка сообщения.
    /// </summary>
    public record ChatContext(
        string? BotName = null,
        string? AuthorName = null,
        string? QuotedAuthor = null,
        string? QuotedText = null);

    /// <summary>
    /// Отправляет сообщение в чат с учётом истории сессии. Картинки из files уходят
    /// мультимодальными частями, текстовые файлы вклеиваются в текст, остальные форматы
    /// пропускаются с пометкой. В ответе может быть и текст, и изображения.
    /// </summary>
    public static async Task<ChatReply> ChatAsync(ChatGptSession session, string text, IReadOnlyList<InputFile>? files = null, ChatContext? context = null)
    {
        if (!IsReady())
        {
            return _emptyReply;
        }

        var cfg = AppConfig.ChatGptSettings;
        var turn = PrepareUserTurn(text, files, context);

        var carry = ResolveCarryImage(session, turn);
        var effort = NormalizeEffort(cfg.ReasoningEffort);

        var json = BuildChatRequestJson(
            cfg.ChatModel,
            cfg.MaxTokens,
            session.History,
            turn,
            BuildSystemPrompt(context?.BotName),
            carry,
            effort);

        BotLogger.LogAi(
            BotLogger.ChatGptThreadKey,
            "📤 Чат ({Model}, рассуждения: {Effort}, картинок: {Images}):\n{Text}",
            cfg.ChatModel,
            effort ?? "по умолчанию",
            turn.ImageDataUrls.Count,
            turn.Text);

        var response = await PostJsonAsync(ChatCompletionsPath, json);

        if (response.Body == null)
        {
            // Отказ авторизации доносим до вызывающего: при отозванном токене
            // нужен перелогин администратора, а не повтор запроса
            return response.Unauthorized ? _emptyReply with { Unauthorized = true } : _emptyReply;
        }

        var reply = ParseChatResponse(response.Body);

        if (reply.Text.Length == 0 && reply.Images.Count == 0)
        {
            BotLogger.LogAi(BotLogger.ChatGptThreadKey, "⚠️ Пустой ответ от ChatGPT");
            return _emptyReply;
        }

        session.Append(turn);
        session.Append(new ChatTurn("assistant", BuildAssistantTurnText(reply), []));

        if (reply.Images.Count > 0)
        {
            session.LastImage = reply.Images[^1];
        }

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "📥 Ответ (картинок: {Images}):\n{Reply}", reply.Images.Count, reply.Text.Length > 0 ? reply.Text : "(без текста)");

        return reply;
    }

    /// <summary>
    /// Одноразовый запрос к дешёвой «инстант»-модели: ни истории, ни сессии, ни картинок.
    /// Нужен кастомным действиям — распознать попадание («ДА»/«НЕТ») и формализовать
    /// запрос перед полноценной сессией. Пустая строка — ошибка или пустой ответ.
    /// </summary>
    public static async Task<string> AskInstantAsync(string prompt, int maxTokens = InstantMaxTokens)
    {
        if (!IsReady() || string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        var model = AppConfig.ChatGptSettings.InstantModel;
        var turn = new ChatTurn("user", prompt, []);
        var json = BuildChatRequestJson(model, maxTokens, [], turn);

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "⚡ Инстант-запрос ({Model}):\n{Text}", model, prompt);

        var response = await PostJsonAsync(ChatCompletionsPath, json, TimeSpan.FromSeconds(InstantTimeoutSeconds));

        if (response.Body == null)
        {
            return string.Empty;
        }

        var reply = ParseChatResponse(response.Body).Text.Trim();

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "⚡ Инстант-ответ:\n{Reply}", reply.Length > 0 ? reply : "(пусто)");

        return reply;
    }

    #region Внутренности

    /// <summary>
    /// Проверяет флаг и настройки подключения. Пишет предупреждение, если чего-то не хватает.
    /// </summary>
    private static bool IsReady()
    {
        if (!AppConfig.UseChatGpt)
        {
            BotLogger.Warning("ChatGPT-часть выключена (UseChatGpt: false)");
            return false;
        }

        if (string.IsNullOrEmpty(AppConfig.ChatGptProxyUrl) || string.IsNullOrEmpty(AppConfig.ChatGptProxyApiKey))
        {
            BotLogger.Warning("ChatGptProxyUrl или ChatGptProxyApiKey не заданы");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ответ прокси: тело или null при ошибке. Отдельным признаком — отказ авторизации:
    /// он единственный, который пользователю нельзя лечить повтором запроса.
    /// </summary>
    private record ProxyResponse(string? Body, bool Unauthorized = false);

    /// <summary>
    /// POST к прокси, исключения наружу не бросает.
    /// <paramref name="timeout"/> ограничивает конкретный запрос: общий потолок клиента
    /// рассчитан на генерацию картинок и служебным запросам не годится.
    /// </summary>
    private static async Task<ProxyResponse> PostJsonAsync(string path, string json, TimeSpan? timeout = null)
    {
        var url = AppConfig.ChatGptProxyUrl.TrimEnd('/') + path;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Add("Authorization", $"Bearer {AppConfig.ChatGptProxyApiKey}");

            using var cts = timeout == null ? null : new CancellationTokenSource(timeout.Value);
            using var response = await _http.SendAsync(request, cts?.Token ?? CancellationToken.None);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                BotLogger.Error("ChatGPT-прокси ошибка {StatusCode}: {Body}", status, responseBody);

                // Умершая авторизация видна как 401/403 — подсказываем в Discord-треде
                if (status is 401 or 403)
                {
                    BotLogger.LogAi(BotLogger.ChatGptThreadKey, "⚠️ Прокси вернул {Status} — проверь ключи, а если умерла авторизация Codex, выполни /chatgpt-auth login", status);
                    return new ProxyResponse(null, Unauthorized: true);
                }

                return new ProxyResponse(null);
            }

            return new ProxyResponse(responseBody);
        }
        catch (Exception ex)
        {
            BotLogger.Error("ChatGPT-прокси недоступен ({Url}): {Message}", url, ex.Message);
            return new ProxyResponse(null);
        }
    }

    /// <summary>
    /// Собирает пользовательский ход: картинки — в data-URL (не больше
    /// <see cref="MaxImagesPerRequest"/>), текстовые файлы — вклейкой в текст,
    /// негодные файлы — пометкой о пропуске.
    /// </summary>
    internal static ChatTurn PrepareUserTurn(string text, IReadOnlyList<InputFile>? files, ChatContext? context = null)
    {
        var sb = new StringBuilder(text ?? string.Empty);
        var images = new List<string>();
        var imageNames = new List<string>();

        foreach (var file in files ?? [])
        {
            if (file.Content.Length > MaxInputFileBytes)
            {
                sb.Append($"\n[файл {file.FileName} пропущен: превышен лимит размера]");
                continue;
            }

            var imageMime = ResolveImageMime(file);

            if (imageMime != null)
            {
                if (images.Count >= MaxImagesPerRequest)
                {
                    sb.Append($"\n[файл {file.FileName} пропущен: слишком много картинок в одном запросе]");
                    continue;
                }

                images.Add(BuildDataUrl(imageMime, file.Content));
                imageNames.Add(file.FileName);
                continue;
            }

            if (IsTextLikeFile(file))
            {
                sb.Append($"\n\n--- файл {file.FileName} ---\n{Encoding.UTF8.GetString(file.Content)}\n--- конец файла ---");
                continue;
            }

            sb.Append($"\n[файл {file.FileName} пропущен: формат не поддерживается]");
        }

        return new ChatTurn("user", BuildHeader(context, imageNames) + sb.ToString().Trim(), images);
    }

    /// <summary>
    /// Служебная шапка сообщения: кто написал, на что отвечает, что приложил.
    /// Формат описан модели в <see cref="BaseSystemPrompt"/> — менять их надо вместе.
    /// </summary>
    private static string BuildHeader(ChatContext? context, IReadOnlyList<string> imageNames)
    {
        var header = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(context?.AuthorName))
        {
            header.Append($"[{context.AuthorName}]").Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(context?.QuotedAuthor))
        {
            var quoted = Shorten(context.QuotedText);

            header
                .Append(quoted.Length > 0
                    ? $"[quotes {context.QuotedAuthor}: \"{quoted}\"]"
                    : $"[quotes {context.QuotedAuthor}]")
                .Append('\n');
        }

        if (imageNames.Count > 0)
        {
            header.Append($"[приложил изображения: {string.Join(", ", imageNames)}]").Append('\n');
        }

        return header.ToString();
    }

    /// <summary>
    /// Ужимает цитируемый текст в одну строку.
    /// </summary>
    private static string Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var single = text.ReplaceLineEndings(" ").Trim();

        return single.Length <= MaxQuotedLength ? single : single[..MaxQuotedLength] + "…";
    }

    /// <summary>
    /// Решает, подмешивать ли в запрос последнюю нарисованную картинку. Она нужна, чтобы
    /// модель могла править нарисованное («сделай его рыжим»): в истории её нет — картинки
    /// от роли assistant прокси отбрасывает. Свои картинки пользователя отменяют подмешивание:
    /// предмет разговора теперь они.
    /// </summary>
    internal static string? ResolveCarryImage(ChatGptSession session, ChatTurn turn) =>
        session.LastImage == null || turn.ImageDataUrls.Count > 0
            ? null
            : BuildDataUrl(session.LastImage.MimeType, session.LastImage.Content);

    /// <summary>
    /// Склеивает базовый промпт (формат чата и имя бота) с настроенным в config.ini.
    /// </summary>
    internal static string BuildSystemPrompt(string? botName)
    {
        var name = string.IsNullOrWhiteSpace(botName) ? DefaultBotName : botName.Trim();
        var basePrompt = BaseSystemPrompt.Replace("{botName}", name);
        var custom = AppConfig.ChatGptSettings.SystemPrompt;

        return custom.Length > 0 ? basePrompt + "\n\n" + custom : basePrompt;
    }

    /// <summary>
    /// MIME картинки: из явно заданного типа, по сигнатуре содержимого или по расширению.
    /// null — файл не является картинкой поддерживаемого формата.
    /// </summary>
    internal static string? ResolveImageMime(InputFile file)
    {
        if (file.MimeType != null && file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return file.MimeType.ToLowerInvariant();
        }

        return DetectImageMime(file.Content) ?? GetImageMimeByFileName(file.FileName);
    }

    /// <summary>
    /// Определяет MIME картинки по сигнатуре содержимого (PNG/JPEG/GIF/WEBP).
    /// </summary>
    internal static string? DetectImageMime(byte[] content)
    {
        if (content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47)
        {
            return "image/png";
        }

        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (content.Length >= 4 && content[0] == 0x47 && content[1] == 0x49 && content[2] == 0x46 && content[3] == 0x38)
        {
            return "image/gif";
        }

        if (content.Length >= 12 && content[0] == 0x52 && content[1] == 0x49 && content[2] == 0x46 && content[3] == 0x46
            && content[8] == 0x57 && content[9] == 0x45 && content[10] == 0x42 && content[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// MIME картинки по расширению файла. null — расширение не картиночное.
    /// </summary>
    internal static string? GetImageMimeByFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null
        };
    }

    /// <summary>
    /// Текстоподобный ли файл — такой вклеивается в текст сообщения целиком.
    /// </summary>
    internal static bool IsTextLikeFile(InputFile file)
    {
        if (file.MimeType != null && file.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        return extension is ".txt" or ".md" or ".log" or ".csv" or ".json" or ".xml" or ".yaml" or ".yml"
            or ".ini" or ".html" or ".css" or ".js" or ".ts" or ".py" or ".cs" or ".sh" or ".sql";
    }

    /// <summary>
    /// Собирает data-URL для мультимодальных частей запроса.
    /// </summary>
    internal static string BuildDataUrl(string mime, byte[] content) =>
        $"data:{mime};base64,{Convert.ToBase64String(content)}";

    /// <summary>
    /// JSON запроса чата: content строкой без картинок и массивом частей с ними.
    /// Непустой systemPrompt уходит первым сообщением с ролью system,
    /// carryImageDataUrl — последняя сгенерированная картинка, приложенная к текущему ходу.
    /// </summary>
    internal static string BuildChatRequestJson(
        string model,
        int maxTokens,
        IReadOnlyList<ChatTurn> history,
        ChatTurn userTurn,
        string? systemPrompt = null,
        string? carryImageDataUrl = null,
        string? reasoningEffort = null)
    {
        var messages = new List<ChatApiMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });
        }

        foreach (var turn in history)
        {
            messages.Add(ToApiMessage(turn));
        }

        var current = carryImageDataUrl != null && userTurn.ImageDataUrls.Count < MaxImagesPerRequest
            ? userTurn with { ImageDataUrls = [.. userTurn.ImageDataUrls, carryImageDataUrl] }
            : userTurn;

        messages.Add(ToApiMessage(current));

        var request = new ChatApiRequest
        {
            Model = model,
            MaxTokens = maxTokens,
            ReasoningEffort = NormalizeEffort(reasoningEffort),
            Messages = messages
        };

        return JsonSerializer.Serialize(request, _jsonOptions);
    }

    /// <summary>
    /// Приводит уровень рассуждений к известному бэкенду. Незнакомое отбрасывается:
    /// неизвестный уровень бэкенд отвергает вместе со всем запросом.
    /// </summary>
    internal static string? NormalizeEffort(string? effort)
    {
        var normalized = effort?.Trim().ToLowerInvariant();

        return normalized is "minimal" or "low" or "medium" or "high" ? normalized : null;
    }

    /// <summary>
    /// Извлекает ответ чата: текст и картинки. Картинки прокси кладёт не в content,
    /// а отдельным полем choices[].message.images[] в виде data-URL.
    /// Пустой ответ — ни текста, ни картинок.
    /// </summary>
    internal static ChatReply ParseChatResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ChatApiResponse>(json, _jsonOptions);
            var message = response?.Choices?.FirstOrDefault()?.Message;
            var images = new List<GeneratedImage>();

            foreach (var item in message?.Images ?? [])
            {
                var image = ParseImageDataUrl(item.ImageUrl?.Url);

                if (image != null)
                {
                    images.Add(image);
                }
            }

            return new ChatReply(message?.Content ?? string.Empty, images);
        }
        catch (JsonException)
        {
            return _emptyReply;
        }
    }

    /// <summary>
    /// Разбирает data-URL картинки из ответа. null — строка не data-URL или битый base64.
    /// </summary>
    internal static GeneratedImage? ParseImageDataUrl(string? dataUrl)
    {
        const string marker = ";base64,";

        if (dataUrl == null || !dataUrl.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var markerIndex = dataUrl.IndexOf(marker, StringComparison.Ordinal);

        if (markerIndex < 0)
        {
            return null;
        }

        try
        {
            var content = Convert.FromBase64String(dataUrl[(markerIndex + marker.Length)..]);

            if (content.Length == 0)
            {
                return null;
            }

            var declaredMime = dataUrl[5..markerIndex];

            return new GeneratedImage(content, DetectImageMime(content) ?? (declaredMime.Length > 0 ? declaredMime : "image/png"), null);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Текст ассистентского хода для истории: картинки в неё не кладём (прокси всё равно
    /// отбросит их у роли assistant), вместо них — пометка, чтобы диалог оставался связным.
    /// </summary>
    internal static string BuildAssistantTurnText(ChatReply reply)
    {
        if (reply.Images.Count == 0)
        {
            return reply.Text;
        }

        var note = reply.Images.Count == 1 ? "[сгенерировано изображение]" : $"[сгенерировано изображений: {reply.Images.Count}]";

        return reply.Text.Length > 0 ? $"{reply.Text}\n{note}" : note;
    }

    /// <summary>
    /// Ход диалога в формат OpenAI: строка или массив мультимодальных частей.
    /// </summary>
    private static ChatApiMessage ToApiMessage(ChatTurn turn)
    {
        if (turn.ImageDataUrls.Count == 0)
        {
            return new ChatApiMessage { Role = turn.Role, Content = turn.Text };
        }

        var parts = new List<ContentPart>();

        if (!string.IsNullOrEmpty(turn.Text))
        {
            parts.Add(new ContentPart { Type = "text", Text = turn.Text });
        }

        foreach (var url in turn.ImageDataUrls)
        {
            parts.Add(new ContentPart { Type = "image_url", ImageUrl = new ImageUrlPart { Url = url } });
        }

        return new ChatApiMessage { Role = turn.Role, Content = parts };
    }

    #endregion

    #region Модели данных

    private class ChatApiRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("max_tokens")]
        public required int MaxTokens { get; init; }

        /// <summary>
        /// Глубина рассуждений. null — поля в запросе не будет вовсе, и бэкенд
        /// возьмёт свой уровень по умолчанию.
        /// </summary>
        [JsonPropertyName("reasoning_effort")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReasoningEffort { get; init; }

        [JsonPropertyName("messages")]
        public required List<ChatApiMessage> Messages { get; init; }
    }

    private class ChatApiMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        /// <summary>
        /// Строка или список <see cref="ContentPart"/> — формат OpenAI допускает оба варианта.
        /// </summary>
        [JsonPropertyName("content")]
        public required object Content { get; init; }
    }

    private class ContentPart
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("image_url")]
        public ImageUrlPart? ImageUrl { get; init; }
    }

    private class ImageUrlPart
    {
        [JsonPropertyName("url")]
        public required string Url { get; init; }
    }

    private class ChatApiResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; init; }
    }

    private class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatChoiceMessage? Message { get; init; }
    }

    private class ChatChoiceMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }

        /// <summary>
        /// Нестандартное поле прокси: картинки, нарисованные инструментом image_generation.
        /// </summary>
        [JsonPropertyName("images")]
        public List<ChatImageItem>? Images { get; init; }
    }

    private class ChatImageItem
    {
        [JsonPropertyName("image_url")]
        public ChatImageUrl? ImageUrl { get; init; }
    }

    private class ChatImageUrl
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion
}
