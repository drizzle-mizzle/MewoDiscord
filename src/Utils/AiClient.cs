using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Точка входа для ИИ-запросов через OpenRouter API.
/// Логирует промпты и ответы в соответствующий AI-тред.
/// </summary>
public static class AiClient
{
    /// <summary>
    /// Отправляет одно сообщение и возвращает текстовый ответ.
    /// </summary>
    public static async Task<string> CompleteAsync(AppConfig.AiSectionConfig cfg, string userMessage, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        BotLogger.LogAi(cfg.SectionName, "📤 Промпт: {Prompt}", userMessage);

        var reply = await OpenRouterClient.CompleteAsync(
            AppConfig.OpenRouterApiKey, cfg.Model, userMessage, systemPrompt, maxTokens, temperature);

        if (!string.IsNullOrEmpty(reply))
        {
            BotLogger.LogAi(cfg.SectionName, "📥 Ответ: {Reply}", reply);
        }

        return reply;
    }

    /// <summary>
    /// Отправляет историю сообщений и возвращает текстовый ответ.
    /// </summary>
    public static async Task<string> CompleteAsync(AppConfig.AiSectionConfig cfg, List<OpenRouterClient.ChatMessage> messages, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        var lastMessage = messages.LastOrDefault()?.Content ?? "(пусто)";
        BotLogger.LogAi(cfg.SectionName, "📤 Промпт: {Prompt}", lastMessage);

        var reply = await OpenRouterClient.CompleteAsync(
            AppConfig.OpenRouterApiKey, cfg.Model, messages, systemPrompt, maxTokens, temperature);

        if (!string.IsNullOrEmpty(reply))
        {
            BotLogger.LogAi(cfg.SectionName, "📥 Ответ: {Reply}", reply);
        }

        return reply;
    }
}
