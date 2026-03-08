using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

public static class AnthropicClient
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const int DefaultMaxTokens = 1024;
    private const double DefaultTemperature = 1.0;

    private static readonly HttpClient Http = new();

    /// <summary>
    /// Отправляет одно сообщение и возвращает текстовый ответ.
    /// </summary>
    public static Task<string> CompleteAsync(string apiKey, string model, string userMessage, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = userMessage }
        };

        return CompleteAsync(apiKey, model, messages, systemPrompt, maxTokens, temperature);
    }

    /// <summary>
    /// Отправляет историю сообщений и возвращает текстовый ответ.
    /// </summary>
    public static async Task<string> CompleteAsync(string apiKey, string model, List<ChatMessage> messages, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            BotLogger.Warning("AnthropicApiKey не задан");
            return string.Empty;
        }

        var requestBody = new ApiRequest
        {
            Model = model,
            MaxTokens = maxTokens ?? DefaultMaxTokens,
            Temperature = temperature ?? DefaultTemperature,
            System = systemPrompt,
            Messages = messages
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        using var response = await Http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            BotLogger.Error("Anthropic API ошибка {StatusCode}: {Body}", (int)response.StatusCode, responseBody);
            return string.Empty;
        }

        var apiResponse = JsonSerializer.Deserialize<ApiResponse>(responseBody, JsonOptions);

        var textBlock = apiResponse?.Content?.FirstOrDefault(c => c.Type == "text");

        return textBlock?.Text ?? string.Empty;
    }

    #region Модели данных

    public class ChatMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }

    private class ApiRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("max_tokens")]
        public required int MaxTokens { get; init; }

        [JsonPropertyName("temperature")]
        public required double Temperature { get; init; }

        [JsonPropertyName("system")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? System { get; init; }

        [JsonPropertyName("messages")]
        public required List<ChatMessage> Messages { get; init; }
    }

    private class ApiResponse
    {
        [JsonPropertyName("content")]
        public List<ContentBlock>? Content { get; init; }
    }

    private class ContentBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion
}
