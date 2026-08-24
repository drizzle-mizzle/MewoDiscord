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

    // Management API CLIProxyAPI — OAuth-логин и список аккаунтов
    private const string ManagementAuthUrlPath = "/v0/management/codex-auth-url";
    private const string ManagementCallbackPath = "/v0/management/oauth-callback";
    private const string ManagementAuthStatusPath = "/v0/management/get-auth-status";
    private const string ManagementAuthFilesPath = "/v0/management/auth-files";

    /// <summary>
    /// Сколько раз и с каким шагом опрашивать статус логина после вставки ссылки
    /// (обмен кода на токены занимает считанные секунды).
    /// </summary>
    private const int LoginPollAttempts = 30;

    private const int LoginPollDelayMs = 1000;

    /// <summary>
    /// Генерация картинки занимает минуты, обычный таймаут HttpClient не подходит.
    /// Читается один раз при создании клиента, горячая перезагрузка не подхватит.
    /// </summary>
    private const int RequestTimeoutSeconds = 300;

    /// <summary>
    /// Максимум ходов в истории сессии (обмен «вопрос-ответ» — это два хода).
    /// </summary>
    internal const int MaxHistoryTurns = 40;

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
    /// Потолок цитаты в шапке сообщения: она напоминает, о чём речь, а не пересказывает
    /// всю переписку — на это есть история сессии.
    /// </summary>
    internal const int MaxQuotedLength = 300;

    /// <summary>
    /// Имя бота, если настоящее определить не удалось.
    /// </summary>
    private const string DefaultBotName = "bot";

    /// <summary>
    /// Базовый системный промпт: объясняет модели, что она в общем чате с многими
    /// собеседниками, как её зовут и в каком формате приходят сообщения. Формат описан
    /// здесь и собирается в BuildHeader — править их надо вместе. Про Discord намеренно
    /// ни слова: лишняя информация, которую модель начнёт трактовать.
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

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
    };

    /// <summary>
    /// Входной файл — вложение пользователя (картинка, текстовый файл и т.п.).
    /// </summary>
    public record InputFile(string FileName, byte[] Content, string? MimeType = null);

    /// <summary>
    /// Сгенерированная картинка.
    /// </summary>
    public record GeneratedImage(byte[] Content, string MimeType, string? RevisedPrompt);

    /// <summary>
    /// Ответ чата: текст и картинки, которые модель решила нарисовать (может быть и то, и другое).
    /// </summary>
    public record ChatReply(string Text, IReadOnlyList<GeneratedImage> Images);

    /// <summary>
    /// Пустой ответ — вернуть при любой ошибке.
    /// </summary>
    private static readonly ChatReply EmptyReply = new(string.Empty, []);

    /// <summary>
    /// Ход диалога: роль, текст и data-URL приложенных картинок.
    /// </summary>
    internal record ChatTurn(string Role, string Text, IReadOnlyList<string> ImageDataUrls);

    /// <summary>
    /// Обстановка вокруг сообщения: как зовут бота в этом чате, кто написал и на что
    /// отвечает. Из неё собирается шапка сообщения и подставляется имя в системный промпт.
    /// </summary>
    public record ChatContext(
        string? BotName = null,
        string? AuthorName = null,
        string? QuotedAuthor = null,
        string? QuotedText = null);

    /// <summary>
    /// Начатый OAuth-логин: ссылка для браузера и state сессии (живёт 5 минут).
    /// </summary>
    public record LoginStart(string Url, string State);

    /// <summary>
    /// Результат завершения логина. Error — техническая причина отказа.
    /// </summary>
    public record LoginResult(bool Ok, string? Error);

    /// <summary>
    /// Подключённый к прокси аккаунт ChatGPT.
    /// </summary>
    public record ChatGptAccount(string Name, string? Email, bool Disabled, bool Unavailable, string? StatusMessage);

    /// <summary>
    /// Разобранный ответ get-auth-status: ok, wait или error с причиной.
    /// </summary>
    internal record AuthStatus(string Status, string? Error);

    /// <summary>
    /// Отправляет сообщение в чат с учётом истории сессии. Картинки из files уходят
    /// мультимодальными частями, текстовые файлы вклеиваются в текст, остальные форматы
    /// пропускаются с пометкой. Модель сама решает, ответить текстом или нарисовать
    /// картинку (инструмент image_generation прокси подмешивает в каждый запрос),
    /// поэтому ответ может нести и текст, и изображения.
    /// </summary>
    public static async Task<ChatReply> ChatAsync(ChatGptSession session, string text, IReadOnlyList<InputFile>? files = null, ChatContext? context = null)
    {
        if (!IsReady())
        {
            return EmptyReply;
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

        var responseBody = await PostJsonAsync(ChatCompletionsPath, json);

        if (responseBody == null)
        {
            return EmptyReply;
        }

        var reply = ParseChatResponse(responseBody);

        if (reply.Text.Length == 0 && reply.Images.Count == 0)
        {
            BotLogger.LogAi(BotLogger.ChatGptThreadKey, "⚠️ Пустой ответ от ChatGPT");
            return EmptyReply;
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

        var responseBody = await PostJsonAsync(ChatCompletionsPath, json);

        if (responseBody == null)
        {
            return string.Empty;
        }

        var reply = ParseChatResponse(responseBody).Text.Trim();

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "⚡ Инстант-ответ:\n{Reply}", reply.Length > 0 ? reply : "(пусто)");

        return reply;
    }

    /// <summary>
    /// Начинает OAuth-логин Codex: возвращает ссылку для браузера и state сессии.
    /// null — прокси недоступен или management API выключен.
    /// </summary>
    public static async Task<LoginStart?> BeginLoginAsync()
    {
        if (!IsManagementReady())
        {
            return null;
        }

        var response = await SendManagementAsync(HttpMethod.Get, ManagementAuthUrlPath);

        if (response == null || response.Value.Status < 200 || response.Value.Status >= 300)
        {
            return null;
        }

        var start = ParseLoginStartResponse(response.Value.Body);

        if (start != null)
        {
            BotLogger.LogAi(BotLogger.ChatGptThreadKey, "🔐 Начат OAuth-логин Codex, state: {State}", start.State);
        }

        return start;
    }

    /// <summary>
    /// Завершает логин: передаёт прокси ссылку, на которую средиректил браузер после входа
    /// (http://localhost:1455/auth/callback?code=...&amp;state=...), и ждёт обмена кода на токены.
    /// </summary>
    public static async Task<LoginResult> CompleteLoginAsync(string redirectUrl)
    {
        if (!IsManagementReady())
        {
            return new LoginResult(false, "прокси недоступен или не задан ChatGptManagementKey");
        }

        var state = ExtractStateFromRedirectUrl(redirectUrl);

        if (state == null)
        {
            return new LoginResult(false, "в ссылке нет параметров code и state — нужен полный URL из адресной строки");
        }

        var response = await SendManagementAsync(HttpMethod.Post, ManagementCallbackPath, BuildOAuthCallbackJson(redirectUrl));

        if (response == null)
        {
            return new LoginResult(false, "прокси недоступен");
        }

        if (response.Value.Status < 200 || response.Value.Status >= 300)
        {
            return new LoginResult(false, $"прокси отклонил ссылку ({response.Value.Status}) — возможно, сессия логина истекла, начни заново");
        }

        // Прокси меняет код на токены в фоне — опрашиваем статус
        for (var attempt = 0; attempt < LoginPollAttempts; attempt++)
        {
            await Task.Delay(LoginPollDelayMs);

            var statusResponse = await SendManagementAsync(HttpMethod.Get, $"{ManagementAuthStatusPath}?state={Uri.EscapeDataString(state)}");

            if (statusResponse == null)
            {
                continue;
            }

            var status = ParseAuthStatusResponse(statusResponse.Value.Body);

            if (status.Status == "ok")
            {
                BotLogger.LogAi(BotLogger.ChatGptThreadKey, "✅ OAuth-логин Codex завершён");
                return new LoginResult(true, null);
            }

            if (status.Status == "error")
            {
                return new LoginResult(false, status.Error ?? "неизвестная ошибка");
            }
        }

        return new LoginResult(false, "прокси не подтвердил логин за отведённое время");
    }

    /// <summary>
    /// Возвращает подключённые к прокси аккаунты Codex. null — прокси недоступен.
    /// </summary>
    public static async Task<IReadOnlyList<ChatGptAccount>?> GetAccountsAsync()
    {
        if (!IsManagementReady())
        {
            return null;
        }

        var response = await SendManagementAsync(HttpMethod.Get, ManagementAuthFilesPath);

        if (response == null || response.Value.Status < 200 || response.Value.Status >= 300)
        {
            return null;
        }

        return ParseAuthFilesResponse(response.Value.Body);
    }

    /// <summary>
    /// Есть ли у прокси хотя бы один рабочий аккаунт ChatGPT.
    /// null — проверить не удалось (management API не настроен или прокси не ответил):
    /// вызывающий решает сам, мешать ему или нет.
    /// </summary>
    public static async Task<bool?> HasWorkingAccountAsync()
    {
        var accounts = await GetAccountsAsync();

        return accounts?.Any(account => !account.Disabled && !account.Unavailable);
    }

    #region Внутренности

    /// <summary>
    /// Проверяет доступность management API: флаг, адрес и пароль management.
    /// </summary>
    private static bool IsManagementReady()
    {
        if (!AppConfig.UseChatGpt)
        {
            BotLogger.Warning("ChatGPT-часть выключена (UseChatGpt: false)");
            return false;
        }

        if (string.IsNullOrEmpty(AppConfig.ChatGptProxyUrl) || string.IsNullOrEmpty(AppConfig.ChatGptManagementKey))
        {
            BotLogger.Warning("ChatGptProxyUrl или ChatGptManagementKey не заданы");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Запрос к management API прокси с ключом X-Management-Key.
    /// null — сетевая ошибка (уже залогирована); иначе статус и тело как есть.
    /// </summary>
    private static async Task<(int Status, string Body)?> SendManagementAsync(HttpMethod method, string path, string? json = null)
    {
        var url = AppConfig.ChatGptProxyUrl.TrimEnd('/') + path;

        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-Management-Key", AppConfig.ChatGptManagementKey);

            if (json != null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await Http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                BotLogger.Error("Management API прокси ошибка {StatusCode}: {Body} (404 — не задан MANAGEMENT_PASSWORD в cliproxy/management.env)", (int)response.StatusCode, responseBody);
            }

            return ((int)response.StatusCode, responseBody);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Management API прокси недоступен ({Url}): {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Разбирает ответ codex-auth-url: ссылка для браузера и state сессии.
    /// </summary>
    internal static LoginStart? ParseLoginStartResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<LoginStartResponse>(json, JsonOptions);

            if (string.IsNullOrEmpty(response?.Url) || string.IsNullOrEmpty(response.State))
            {
                return null;
            }

            return new LoginStart(response.Url, response.State);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Достаёт state из ссылки, на которую средиректил браузер после логина.
    /// null — в ссылке нет параметров code и state (вставлено что-то не то).
    /// </summary>
    internal static string? ExtractStateFromRedirectUrl(string url)
    {
        var queryIndex = url.IndexOf('?');

        if (queryIndex < 0 || queryIndex == url.Length - 1)
        {
            return null;
        }

        string? code = null;
        string? state = null;

        foreach (var pair in url[(queryIndex + 1)..].Split('&'))
        {
            var eqIndex = pair.IndexOf('=');

            if (eqIndex <= 0)
            {
                continue;
            }

            var key = pair[..eqIndex];
            var value = Uri.UnescapeDataString(pair[(eqIndex + 1)..]);

            if (key == "code")
            {
                code = value;
            }
            else if (key == "state")
            {
                state = value;
            }
        }

        return string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) ? null : state;
    }

    /// <summary>
    /// JSON для oauth-callback: провайдер и вставленная ссылка целиком
    /// (code и state прокси достанет из неё сам).
    /// </summary>
    internal static string BuildOAuthCallbackJson(string redirectUrl)
    {
        var request = new OAuthCallbackRequest
        {
            Provider = "codex",
            RedirectUrl = redirectUrl
        };

        return JsonSerializer.Serialize(request, JsonOptions);
    }

    /// <summary>
    /// Разбирает ответ get-auth-status. Нечитаемый ответ считается ошибкой.
    /// </summary>
    internal static AuthStatus ParseAuthStatusResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<AuthStatusResponse>(json, JsonOptions);

            if (string.IsNullOrEmpty(response?.Status))
            {
                return new AuthStatus("error", "пустой ответ прокси");
            }

            return new AuthStatus(response.Status, response.Error);
        }
        catch (JsonException)
        {
            return new AuthStatus("error", "нечитаемый ответ прокси");
        }
    }

    /// <summary>
    /// Разбирает список auth-files, оставляя только аккаунты Codex.
    /// </summary>
    internal static IReadOnlyList<ChatGptAccount> ParseAuthFilesResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<AuthFilesResponse>(json, JsonOptions);
            var result = new List<ChatGptAccount>();

            foreach (var file in response?.Files ?? [])
            {
                if (!string.Equals(file.Provider, "codex", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new ChatGptAccount(
                    file.Name ?? "(без имени)",
                    file.Email,
                    file.Disabled,
                    file.Unavailable,
                    file.StatusMessage));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

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
    /// POST к прокси. Возвращает тело ответа или null при ошибке (сеть, статус-код).
    /// Наружу исключения не бросает — стиль OpenRouterClient.
    /// </summary>
    private static async Task<string?> PostJsonAsync(string path, string json)
    {
        var url = AppConfig.ChatGptProxyUrl.TrimEnd('/') + path;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Add("Authorization", $"Bearer {AppConfig.ChatGptProxyApiKey}");

            using var response = await Http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                BotLogger.Error("ChatGPT-прокси ошибка {StatusCode}: {Body}", status, responseBody);

                // Умершая авторизация видна как 401/403 — подсказываем в Discord-треде
                if (status is 401 or 403)
                {
                    BotLogger.LogAi(BotLogger.ChatGptThreadKey, "⚠️ Прокси вернул {Status} — проверь ключи, а если умерла авторизация Codex, выполни /chatgpt login", status);
                }

                return null;
            }

            return responseBody;
        }
        catch (Exception ex)
        {
            BotLogger.Error("ChatGPT-прокси недоступен ({Url}): {Message}", url, ex.Message);
            return null;
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
    /// Ужимает цитируемый текст в одну строку: шапка должна оставаться шапкой,
    /// а не пересказом всей переписки.
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
    /// модель могла править нарисованное («сделай его рыжим»): в истории её нет, потому что
    /// картинки из ассистентских сообщений прокси отбрасывает (в Responses API они уходят
    /// только от роли user). Но если пользователь принёс свои картинки, предмет разговора
    /// теперь они — прошлая только сбивала бы модель с толку.
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
    /// Собирает data-URL для мультимодальных частей и картинок в /v1/images/edits.
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

        return JsonSerializer.Serialize(request, JsonOptions);
    }

    /// <summary>
    /// Приводит уровень рассуждений к тому, что бэкенд точно понимает.
    /// Всё незнакомое отбрасывается, а не отправляется как есть: неизвестный уровень
    /// отвергается целиком, и вместо ответа пользователь получил бы ошибку.
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
            var response = JsonSerializer.Deserialize<ChatApiResponse>(json, JsonOptions);
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
            return EmptyReply;
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

    private class LoginStartResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }
    }

    private class OAuthCallbackRequest
    {
        [JsonPropertyName("provider")]
        public required string Provider { get; init; }

        [JsonPropertyName("redirect_url")]
        public required string RedirectUrl { get; init; }
    }

    private class AuthStatusResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }

    private class AuthFilesResponse
    {
        [JsonPropertyName("files")]
        public List<AuthFileEntry>? Files { get; init; }
    }

    private class AuthFileEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("provider")]
        public string? Provider { get; init; }

        [JsonPropertyName("email")]
        public string? Email { get; init; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; init; }

        [JsonPropertyName("unavailable")]
        public bool Unavailable { get; init; }

        [JsonPropertyName("status_message")]
        public string? StatusMessage { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion
}
