using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

public static class OpenRouterClient
{
    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";
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
            BotLogger.Warning("OpenRouterApiKey не задан");
            return string.Empty;
        }

        // OpenRouter использует формат OpenAI: system-промпт — это первое сообщение с role=system
        var apiMessages = new List<ApiMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            apiMessages.Add(new ApiMessage { Role = "system", Content = systemPrompt });
        }

        foreach (var msg in messages)
        {
            apiMessages.Add(new ApiMessage { Role = msg.Role, Content = msg.Content });
        }

        var requestBody = new ApiRequest
        {
            Model = model,
            MaxTokens = maxTokens ?? DefaultMaxTokens,
            Temperature = temperature ?? DefaultTemperature,
            Messages = apiMessages
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await Http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            BotLogger.Error("OpenRouter API ошибка {StatusCode}: {Body}", (int)response.StatusCode, responseBody);
            return string.Empty;
        }

        var apiResponse = JsonSerializer.Deserialize<ApiResponse>(responseBody, JsonOptions);
        var choice = apiResponse?.Choices?.FirstOrDefault();

        return choice?.Message?.Content ?? string.Empty;
    }

    #region Модели данных

    /// <summary>
    /// Сообщение чата (user/assistant).
    /// </summary>
    public class ChatMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }

    private class ApiMessage
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

        [JsonPropertyName("messages")]
        public required List<ApiMessage> Messages { get; init; }
    }

    private class ApiResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; init; }
    }

    private class Choice
    {
        [JsonPropertyName("message")]
        public ChoiceMessage? Message { get; init; }
    }

    private class ChoiceMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion
}
