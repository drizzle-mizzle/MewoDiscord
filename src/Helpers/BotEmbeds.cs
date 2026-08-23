using Discord;

namespace MewoDiscord.Helpers;

/// <summary>
/// Оформление системных сообщений бота — ответов команд, уведомлений и ошибок — в embed'ы.
/// Цвет несёт смысл: зелёный — получилось, красный — не получилось, жёлтый — получилось
/// с оговоркой, синий — просто информация или шаг диалога. Тексты по-прежнему живут
/// в messages.ini: сюда приходит уже готовая строка.
/// Журнал голосовых каналов и ответы самой модели ChatGPT остаются обычным текстом:
/// первых слишком много, чтобы каждый был карточкой, вторые не влезают в лимит описания.
/// </summary>
public static class BotEmbeds
{
    /// <summary>Палитра Discord: зелёный «успех».</summary>
    public static readonly Color SuccessColor = new(0x57F287);

    /// <summary>Палитра Discord: красный «ошибка».</summary>
    public static readonly Color ErrorColor = new(0xED4245);

    /// <summary>Палитра Discord: жёлтый «предупреждение».</summary>
    public static readonly Color WarningColor = new(0xFEE75C);

    /// <summary>Палитра Discord: фирменный blurple — информация и шаги диалога.</summary>
    public static readonly Color InfoColor = new(0x5865F2);

    /// <summary>
    /// Лимит Discord на длину описания embed'а. Системные тексты в него укладываются
    /// с запасом, но обрезка страхует от неожиданно длинной подстановки (ошибка прокси).
    /// </summary>
    private const int MaxDescriptionLength = 4096;

    public static Embed Success(string text) => Build(SuccessColor, text);

    public static Embed Error(string text) => Build(ErrorColor, text);

    public static Embed Warning(string text) => Build(WarningColor, text);

    public static Embed Info(string text) => Build(InfoColor, text);

    /// <summary>
    /// Собирает embed заданного цвета. Заголовка нет намеренно: тексты в messages.ini
    /// самодостаточны и начинаются с эмодзи, а цвет полосы слева говорит об исходе.
    /// </summary>
    public static Embed Build(Color color, string text)
    {
        var description = text.Length > MaxDescriptionLength
            ? text[..(MaxDescriptionLength - 1)] + "…"
            : text;

        return new EmbedBuilder()
            .WithColor(color)
            .WithDescription(description)
            .Build();
    }
}
