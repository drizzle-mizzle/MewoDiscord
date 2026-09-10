using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MewoDiscord.DevBot;

public sealed class DiscordApiException(string message) : Exception(message);

/// <summary>
/// Тонкий REST-клиент Discord: только GET. На 429 ждёт, сколько велел Discord, и повторяет.
/// </summary>
public sealed class DiscordApi : IDisposable
{
    private const string BaseUrl = "https://discord.com/api/v10/";

    private const int MaxAttempts = 5;

    /// <summary>
    /// Такой вид User-Agent Discord требует от ботов.
    /// </summary>
    private const string UserAgent = "DiscordBot (https://github.com/drizzle-mizzle/MewoDiscord, 1.0)";

    private readonly HttpClient _http;

    public DiscordApi(string token)
    {
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>
    /// GET по пути API. null — 404 или 403: такого нет или бот этого не видит. При обходе
    /// сервера это обычный ответ, а не ошибка. Всё остальное неожиданное — исключение.
    /// </summary>
    public async Task<JsonElement?> GetAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await _http.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
            {
                await Task.Delay(RetryAfter(body));
                continue;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new DiscordApiException("Токен не подошёл (401) — проверь BotToken в devbot.ini");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DiscordApiException($"Discord ответил {(int)response.StatusCode} на {path}: {body}");
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Сколько ждать перед повтором: Discord пишет это в теле ответа, в секундах.
    /// </summary>
    private static TimeSpan RetryAfter(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("retry_after", out var value) && value.TryGetDouble(out var seconds))
            {
                return TimeSpan.FromSeconds(Math.Max(seconds, 0.1));
            }
        }
        catch (JsonException)
        {
            // Тело не JSON — ждём по умолчанию
        }

        return TimeSpan.FromSeconds(1);
    }
}
