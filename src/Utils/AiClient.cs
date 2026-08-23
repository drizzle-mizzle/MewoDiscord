using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Точка входа для ИИ-запросов через OpenRouter API.
/// Логирует полный запрос (system + prompt) и ответ в соответствующий AI-тред.
/// **Код недостижим**: точки входа ИИ-части убраны из бота, а её настройки удалены
/// из config.ini (архив промптов — Files/ai_prompts.legacy.ini). Оставлен до переезда
/// ИИ-части на прокси-API; секции читаются напрямую, поэтому оживает возвратом
/// секций и ключей в config.ini.
/// </summary>
public static class AiClient
{
    /// <summary>
    /// Ключ OpenRouter из [COMMON]. Читается напрямую: в AppConfig свойства нет,
    /// чтобы мёртвая настройка не мозолила глаза.
    /// </summary>
    private static string ApiKey => AppConfig.Get("COMMON", "OpenRouterApiKey");

    public static AiSectionConfig CensorSettings { get; } = new("AI_CENSOR_SETTINGS");
    public static AiSectionConfig SwearsCheckerSettings { get; } = new("AI_SWEARS_CHECKER_SETTINGS");
    public static AiSectionConfig ChatSettings { get; } = new("AI_CHAT_SETTINGS");
    public static AiSectionConfig ContinuationCheckerSettings { get; } = new("AI_CONTINUATION_CHECKER_SETTINGS");

    /// <summary>
    /// Типизированная секция настроек ИИ-задачи (модель, токены, промпты).
    /// </summary>
    public record AiSectionConfig(string SectionName)
    {
        public string Model => AppConfig.Get(SectionName, "Model", "x-ai/grok-3-mini");

        public int MaxTokens => AppConfig.GetInt(SectionName, "MaxTokens", 50);

        public double Temperature => AppConfig.GetDouble(SectionName, "Temperature", 1.0);

        public string SystemPrompt => AppConfig.Get(SectionName, "SystemPrompt");

        public string MessagePrompt => AppConfig.Get(SectionName, "MessagePrompt");
    }

    /// <summary>
    /// Отправляет одно сообщение и возвращает текстовый ответ.
    /// </summary>
    public static async Task<string> CompleteAsync(AiSectionConfig cfg, string userMessage, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        LogRequest(cfg.SectionName, systemPrompt, userMessage);

        var reply = await OpenRouterClient.CompleteAsync(
            ApiKey, cfg.Model, userMessage, systemPrompt, maxTokens, temperature);

        LogResponse(cfg.SectionName, reply);
        return reply;
    }

    /// <summary>
    /// Отправляет историю сообщений и возвращает текстовый ответ.
    /// </summary>
    public static async Task<string> CompleteAsync(AiSectionConfig cfg, List<OpenRouterClient.ChatMessage> messages, string? systemPrompt = null, int? maxTokens = null, double? temperature = null)
    {
        var lastMessage = messages.LastOrDefault()?.Content ?? "(пусто)";
        LogRequest(cfg.SectionName, systemPrompt, lastMessage);

        var reply = await OpenRouterClient.CompleteAsync(
            ApiKey, cfg.Model, messages, systemPrompt, maxTokens, temperature);

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
