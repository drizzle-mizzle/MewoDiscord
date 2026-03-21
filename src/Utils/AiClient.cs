using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Диспетчер ИИ-запросов: выбирает провайдера (Anthropic/OpenRouter) по конфигу.
/// </summary>
public static class AiClient
{
    /// <summary>
    /// Отправляет одно сообщение и возвращает текстовый ответ через выбранного провайдера.
    /// </summary>
    public static Task<string> CompleteAsync(AppConfig.AiSectionConfig cfg, string userMessage, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        var messages = new List<AnthropicClient.ChatMessage>
        {
            new() { Role = "user", Content = userMessage }
        };

        return CompleteAsync(cfg, messages, systemPrompt, maxTokens, temperature);
    }

    /// <summary>
    /// Отправляет историю сообщений и возвращает текстовый ответ через выбранного провайдера.
    /// </summary>
    public static Task<string> CompleteAsync(AppConfig.AiSectionConfig cfg, List<AnthropicClient.ChatMessage> messages, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        var provider = AppConfig.AiProvider.ToLowerInvariant();

        return provider switch
        {
            "openrouter" => OpenRouterClient.CompleteAsync(
                AppConfig.OpenRouterApiKey, cfg.OpenRouterModel, messages, systemPrompt, maxTokens, temperature),
            _ => AnthropicClient.CompleteAsync(
                AppConfig.AnthropicApiKey, cfg.AnthropicModel, messages, systemPrompt, maxTokens, temperature),
        };
    }
}
