using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Точка входа для ИИ-запросов через OpenRouter API.
/// Логирует полный запрос (system + prompt) и ответ в соответствующий AI-тред.
/// </summary>
public static class AiClient
{
    /// <summary>
    /// Отправляет одно сообщение и возвращает текстовый ответ.
    /// </summary>
    public static async Task<string> CompleteAsync(AppConfig.AiSectionConfig cfg, string userMessage, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        LogRequest(cfg.SectionName, systemPrompt, userMessage);

        var reply = await OpenRouterClient.CompleteAsync(
            AppConfig.OpenRouterApiKey, cfg.Model, userMessage, systemPrompt, maxTokens, temperature);

        LogResponse(cfg.SectionName, reply);
        return reply;
    }

    /// <summary>
    /// Отправляет историю сообщений и возвращает текстовый ответ.
    /// </summary>
    public static async Task<string> CompleteAsync(AppConfig.AiSectionConfig cfg, List<OpenRouterClient.ChatMessage> messages, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        var lastMessage = messages.LastOrDefault()?.Content ?? "(пусто)";
        LogRequest(cfg.SectionName, systemPrompt, lastMessage);

        var reply = await OpenRouterClient.CompleteAsync(
            AppConfig.OpenRouterApiKey, cfg.Model, messages, systemPrompt, maxTokens, temperature);

        LogResponse(cfg.SectionName, reply);
        return reply;
    }

    /// <summary>
    /// Логирует полный запрос к ИИ: system prompt + user prompt в одном сообщении.
    /// </summary>
    private static void LogRequest(string sectionName, string? systemPrompt, string userMessage)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            parts.Add($"🧠 System:\n{systemPrompt}");
        }

        parts.Add($"📤 Prompt:\n{userMessage}");

        BotLogger.LogAi(sectionName, string.Join("\n\n", parts));
    }

    /// <summary>
    /// Логирует ответ от ИИ.
    /// </summary>
    private static void LogResponse(string sectionName, string reply)
    {
        if (!string.IsNullOrEmpty(reply))
        {
            BotLogger.LogAi(sectionName, "📥 Ответ: {Reply}", reply);
        }
        else
        {
            BotLogger.LogAi(sectionName, "⚠️ Пустой ответ от ИИ");
        }
    }
}
