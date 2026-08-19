using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Клиент ChatGPT через CLIProxyAPI — локальный OpenAI-совместимый прокси, который расходует
/// квоту подписки ChatGPT Plus (Codex OAuth) вместо API-биллинга.
/// Чат — POST /v1/chat/completions, генерация с нуля — /v1/images/generations,
/// генерация с референсами и правки — /v1/images/edits (JSON-вариант с data-URL).
/// Прокси stateless, история диалога живёт в <see cref="ChatGptSession"/> на нашей стороне.
/// </summary>
public static class ChatGptClient
{
    private const string ChatCompletionsPath = "/v1/chat/completions";
    private const string ImageGenerationsPath = "/v1/images/generations";
    private const string ImageEditsPath = "/v1/images/edits";

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
    /// Ход диалога: роль, текст и data-URL приложенных картинок.
    /// </summary>
    internal record ChatTurn(string Role, string Text, IReadOnlyList<string> ImageDataUrls);

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
    /// пропускаются с пометкой. Возвращает пустую строку при любой ошибке.
    /// </summary>
    public static async Task<string> ChatAsync(ChatGptSession session, string text, IReadOnlyList<InputFile>? files = null)
    {
        if (!IsReady())
        {
            return string.Empty;
        }

        var cfg = AppConfig.ChatGptSettings;
        var turn = PrepareUserTurn(text, files);
        var json = BuildChatRequestJson(cfg.ChatModel, cfg.MaxTokens, session.History, turn, cfg.SystemPrompt);

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "📤 Чат ({Model}, картинок: {Images}):\n{Text}", cfg.ChatModel, turn.ImageDataUrls.Count, turn.Text);

        var responseBody = await PostJsonAsync(ChatCompletionsPath, json);

        if (responseBody == null)
        {
            return string.Empty;
        }

        var reply = ParseChatResponse(responseBody);

        if (string.IsNullOrEmpty(reply))
        {
            BotLogger.LogAi(BotLogger.ChatGptThreadKey, "⚠️ Пустой ответ от ChatGPT");
            return string.Empty;
        }

        session.Append(turn);
        session.Append(new ChatTurn("assistant", reply, []));
        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "📥 Ответ:\n{Reply}", reply);

        return reply;
    }

    /// <summary>
    /// Генерирует картинку с нуля по промпту. Результат запоминается в сессии,
    /// дальше его можно править через <see cref="ContinueImageAsync"/>.
    /// Возвращает null при любой ошибке.
    /// </summary>
    public static async Task<GeneratedImage?> GenerateImageAsync(ChatGptSession session, string prompt)
    {
        if (!IsReady())
        {
            return null;
        }

        var cfg = AppConfig.ChatGptSettings;
        var json = BuildGenerationRequestJson(cfg.ImageModel, prompt, cfg.ImageSize, cfg.ImageQuality);

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "🎨 Генерация ({Model}, {Size}, {Quality}): {Prompt}", cfg.ImageModel, cfg.ImageSize, cfg.ImageQuality, prompt);

        var responseBody = await PostJsonAsync(ImageGenerationsPath, json);
        var image = responseBody == null ? null : ParseImageResponse(responseBody);

        if (image != null)
        {
            RememberGeneration(session, prompt, image, references: []);
        }

        return image;
    }

    /// <summary>
    /// Генерирует картинку по промпту с опорой на референсные изображения (несколько).
    /// Результат и референсы запоминаются в сессии. Возвращает null при любой ошибке.
    /// </summary>
    public static async Task<GeneratedImage?> GenerateImageAsync(ChatGptSession session, string prompt, IReadOnlyList<InputFile> referenceImages)
    {
        if (!IsReady())
        {
            return null;
        }

        var references = TakeValidImages(referenceImages);

        if (references.Count == 0)
        {
            BotLogger.Error("Генерация с референсами: среди {Count} файлов нет пригодных картинок", referenceImages.Count);
            return null;
        }

        var dataUrls = references.Select(r => BuildDataUrl(ResolveImageMime(r)!, r.Content)).ToList();
        var image = await EditImageAsync(prompt, dataUrls, $"референсов: {references.Count}");

        if (image != null)
        {
            RememberGeneration(session, prompt, image, references);
        }

        return image;
    }

    /// <summary>
    /// Продолжает сессию: правит последнюю сгенерированную картинку по новой инструкции,
    /// не создавая новую сессию. extraReferences — дополнительные картинки к этой правке,
    /// includeOriginalReferences добавляет референсы последней генерации.
    /// Возвращает null при любой ошибке.
    /// </summary>
    public static async Task<GeneratedImage?> ContinueImageAsync(ChatGptSession session, string instruction, IReadOnlyList<InputFile>? extraReferences = null, bool includeOriginalReferences = false)
    {
        if (!IsReady())
        {
            return null;
        }

        if (session.LastImage == null)
        {
            BotLogger.Warning("Правка картинки: в сессии ещё нет сгенерированного изображения");
            return null;
        }

        var dataUrls = CollectEditDataUrls(session.LastImage, session.LastReferences, extraReferences, includeOriginalReferences);
        var image = await EditImageAsync(instruction, dataUrls, "правка последней картинки");

        if (image != null)
        {
            RememberGeneration(session, instruction, image, session.LastReferences);
        }

        return image;
    }

    /// <summary>
    /// Собирает картинки правки: последняя сгенерированная, затем дополнительные,
    /// затем исходные референсы — всё в пределах <see cref="MaxImagesPerRequest"/>.
    /// </summary>
    internal static List<string> CollectEditDataUrls(GeneratedImage lastImage, IReadOnlyList<InputFile> originalReferences, IReadOnlyList<InputFile>? extraReferences, bool includeOriginalReferences)
    {
        var dataUrls = new List<string>
        {
            BuildDataUrl(lastImage.MimeType, lastImage.Content)
        };

        foreach (var reference in TakeValidImages(extraReferences ?? []))
        {
            if (dataUrls.Count >= MaxImagesPerRequest)
            {
                break;
            }

            dataUrls.Add(BuildDataUrl(ResolveImageMime(reference)!, reference.Content));
        }

        if (includeOriginalReferences)
        {
            foreach (var reference in originalReferences)
            {
                if (dataUrls.Count >= MaxImagesPerRequest)
                {
                    break;
                }

                // Референсы с диска могли повредиться — непригодные пропускаем
                var mime = ResolveImageMime(reference);

                if (mime != null)
                {
                    dataUrls.Add(BuildDataUrl(mime, reference.Content));
                }
            }
        }

        return dataUrls;
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

    #region Внутренности

    /// <summary>
    /// Общий путь правки: POST /v1/images/edits с картинками в виде data-URL.
    /// </summary>
    private static async Task<GeneratedImage?> EditImageAsync(string prompt, IReadOnlyList<string> imageDataUrls, string logNote)
    {
        var cfg = AppConfig.ChatGptSettings;
        var json = BuildEditRequestJson(cfg.ImageModel, prompt, imageDataUrls, cfg.ImageSize, cfg.ImageQuality);

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "🖌️ Правка ({Model}, {Note}): {Prompt}", cfg.ImageModel, logNote, prompt);

        var responseBody = await PostJsonAsync(ImageEditsPath, json);

        return responseBody == null ? null : ParseImageResponse(responseBody);
    }

    /// <summary>
    /// Обновляет сессию после удачной генерации: картинка, референсы и текстовый след
    /// в истории чата (байты картинок в историю не попадают).
    /// </summary>
    private static void RememberGeneration(ChatGptSession session, string prompt, GeneratedImage image, IReadOnlyList<InputFile> references)
    {
        session.LastImage = image;
        session.LastReferences = references;
        session.Append(new ChatTurn("user", prompt, []));
        session.Append(new ChatTurn("assistant", $"[сгенерировано изображение: {image.RevisedPrompt ?? prompt}]", []));

        BotLogger.LogAi(BotLogger.ChatGptThreadKey, "📥 Картинка {Mime}, {Bytes} байт{Revised}", image.MimeType, image.Content.Length, image.RevisedPrompt == null ? string.Empty : $"\nrevised prompt: {image.RevisedPrompt}");
    }

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
    internal static ChatTurn PrepareUserTurn(string text, IReadOnlyList<InputFile>? files)
    {
        var sb = new StringBuilder(text ?? string.Empty);
        var images = new List<string>();

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
                continue;
            }

            if (IsTextLikeFile(file))
            {
                sb.Append($"\n\n--- файл {file.FileName} ---\n{Encoding.UTF8.GetString(file.Content)}\n--- конец файла ---");
                continue;
            }

            sb.Append($"\n[файл {file.FileName} пропущен: формат не поддерживается]");
        }

        return new ChatTurn("user", sb.ToString(), images);
    }

    /// <summary>
    /// Оставляет из списка только пригодные картинки, не больше <see cref="MaxImagesPerRequest"/>.
    /// </summary>
    private static List<InputFile> TakeValidImages(IReadOnlyList<InputFile> files)
    {
        var result = new List<InputFile>();

        foreach (var file in files)
        {
            if (result.Count >= MaxImagesPerRequest)
            {
                break;
            }

            if (file.Content.Length <= MaxInputFileBytes && ResolveImageMime(file) != null)
            {
                result.Add(file);
            }
        }

        return result;
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
    /// Непустой systemPrompt уходит первым сообщением с ролью system.
    /// </summary>
    internal static string BuildChatRequestJson(string model, int maxTokens, IReadOnlyList<ChatTurn> history, ChatTurn userTurn, string? systemPrompt = null)
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

        messages.Add(ToApiMessage(userTurn));

        var request = new ChatApiRequest
        {
            Model = model,
            MaxTokens = maxTokens,
            Messages = messages
        };

        return JsonSerializer.Serialize(request, JsonOptions);
    }

    /// <summary>
    /// Извлекает текст ответа чата. Пустая строка — ответа нет.
    /// </summary>
    internal static string ParseChatResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ChatApiResponse>(json, JsonOptions);

            return response?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// JSON запроса генерации с нуля.
    /// </summary>
    internal static string BuildGenerationRequestJson(string model, string prompt, string size, string quality)
    {
        var request = new ImageApiRequest
        {
            Model = model,
            Prompt = prompt,
            Size = size,
            Quality = quality
        };

        return JsonSerializer.Serialize(request, JsonOptions);
    }

    /// <summary>
    /// JSON запроса правки: те же параметры плюс картинки в data-URL
    /// (формат images[].image_url принимает CLIProxyAPI).
    /// </summary>
    internal static string BuildEditRequestJson(string model, string prompt, IReadOnlyList<string> imageDataUrls, string size, string quality)
    {
        var request = new ImageApiRequest
        {
            Model = model,
            Prompt = prompt,
            Size = size,
            Quality = quality,
            Images = imageDataUrls.Select(url => new ImageRef { ImageUrl = url }).ToList()
        };

        return JsonSerializer.Serialize(request, JsonOptions);
    }

    /// <summary>
    /// Извлекает картинку из ответа генерации/правки. MIME определяется по содержимому,
    /// по умолчанию — PNG. null — картинки в ответе нет.
    /// </summary>
    internal static GeneratedImage? ParseImageResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ImageApiResponse>(json, JsonOptions);
            var item = response?.Data?.FirstOrDefault();

            if (string.IsNullOrEmpty(item?.B64Json))
            {
                return null;
            }

            var content = Convert.FromBase64String(item.B64Json);
            var mime = DetectImageMime(content) ?? "image/png";

            return new GeneratedImage(content, mime, item.RevisedPrompt);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
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
    }

    private class ImageApiRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }

        [JsonPropertyName("size")]
        public required string Size { get; init; }

        [JsonPropertyName("quality")]
        public required string Quality { get; init; }

        [JsonPropertyName("response_format")]
        public string ResponseFormat { get; init; } = "b64_json";

        /// <summary>
        /// Референсы для /v1/images/edits; null — запрос генерации с нуля.
        /// </summary>
        [JsonPropertyName("images")]
        public List<ImageRef>? Images { get; init; }
    }

    private class ImageRef
    {
        [JsonPropertyName("image_url")]
        public required string ImageUrl { get; init; }
    }

    private class ImageApiResponse
    {
        [JsonPropertyName("data")]
        public List<ImageApiItem>? Data { get; init; }
    }

    private class ImageApiItem
    {
        [JsonPropertyName("b64_json")]
        public string? B64Json { get; init; }

        [JsonPropertyName("revised_prompt")]
        public string? RevisedPrompt { get; init; }
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
