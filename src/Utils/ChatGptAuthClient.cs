using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Management API CLIProxyAPI: OAuth-логин Codex прямо из Discord и список подключённых
/// к прокси аккаунтов. Отделён от <see cref="ChatGptClient"/> намеренно: у management
/// свои ручки, свой заголовок X-Management-Key и свой пароль, а к обращениям к ИИ он
/// не относится вовсе — искать логин в клиенте чата читателю неоткуда.
/// Обмен кода на токены и дальнейший их рефреш прокси делает сам.
/// </summary>
public static class ChatGptAuthClient
{
    private const string AuthUrlPath = "/v0/management/codex-auth-url";
    private const string CallbackPath = "/v0/management/oauth-callback";
    private const string AuthStatusPath = "/v0/management/get-auth-status";
    private const string AuthFilesPath = "/v0/management/auth-files";

    /// <summary>
    /// Сколько раз и с каким шагом опрашивать статус логина после вставки ссылки
    /// (обмен кода на токены занимает считанные секунды).
    /// </summary>
    private const int LoginPollAttempts = 30;

    private const int LoginPollDelayMs = 1000;

    /// <summary>
    /// Потолок одного запроса к management API. Все четыре ручки отвечают мгновенно —
    /// прокси стоит соседним контейнером, — а долгая часть логина у него фоновая
    /// и опрашивается циклом. Таймаут клиента чата (минуты — там рисуются картинки)
    /// заставил бы команду логина висеть впустую.
    /// </summary>
    private const int RequestTimeoutSeconds = 15;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
    /// Начинает OAuth-логин Codex: возвращает ссылку для браузера и state сессии.
    /// null — прокси недоступен или management API выключен.
    /// </summary>
    public static async Task<LoginStart?> BeginLoginAsync()
    {
        if (!IsReady())
        {
            return null;
        }

        var response = await SendAsync(HttpMethod.Get, AuthUrlPath);

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
        if (!IsReady())
        {
            return new LoginResult(false, "прокси недоступен или не задан ChatGptManagementKey");
        }

        var state = ExtractStateFromRedirectUrl(redirectUrl);

        if (state == null)
        {
            return new LoginResult(false, "в ссылке нет параметров code и state — нужен полный URL из адресной строки");
        }

        var response = await SendAsync(HttpMethod.Post, CallbackPath, BuildOAuthCallbackJson(redirectUrl));

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

            var statusResponse = await SendAsync(HttpMethod.Get, $"{AuthStatusPath}?state={Uri.EscapeDataString(state)}");

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
        if (!IsReady())
        {
            return null;
        }

        var response = await SendAsync(HttpMethod.Get, AuthFilesPath);

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
    /// Пароль тут свой, не ключ клиента: у прокси это разные двери.
    /// </summary>
    private static bool IsReady()
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
    private static async Task<(int Status, string Body)?> SendAsync(HttpMethod method, string path, string? json = null)
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

            using var response = await _http.SendAsync(request);
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
            var response = JsonSerializer.Deserialize<LoginStartResponse>(json, _jsonOptions);

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

        return JsonSerializer.Serialize(request, _jsonOptions);
    }

    /// <summary>
    /// Разбирает ответ get-auth-status. Нечитаемый ответ считается ошибкой.
    /// </summary>
    internal static AuthStatus ParseAuthStatusResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<AuthStatusResponse>(json, _jsonOptions);

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
            var response = JsonSerializer.Deserialize<AuthFilesResponse>(json, _jsonOptions);
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

    #endregion

    #region Модели данных

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

    #endregion
}
