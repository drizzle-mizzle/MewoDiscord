using Discord;

namespace MewoDiscord.Helpers;

/// <summary>
/// Оформление системных сообщений бота — ответов команд, уведомлений и ошибок — в embed'ы.
/// Цвет несёт смысл: зелёный — получилось, красный — нет, жёлтый — с оговоркой, синий —
/// информация или шаг диалога. Тексты живут в messages.ini, сюда приходит готовая строка.
/// Журнал голосовых и ответы модели остаются обычным текстом: первых слишком много,
/// вторые не влезают в лимит описания.
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
    /// Лимит Discord на длину описания embed'а: обрезка страхует от длинной подстановки.
    /// </summary>
    private const int MaxDescriptionLength = 4096;

    public static Embed Success(string text) => Build(SuccessColor, text);

    public static Embed Error(string text) => Build(ErrorColor, text);

    public static Embed Warning(string text) => Build(WarningColor, text);

    public static Embed Info(string text) => Build(InfoColor, text);

    /// <summary>
    /// Собирает embed заданного цвета. Заголовка нет: тексты самодостаточны и начинаются
    /// с эмодзи, а об исходе говорит цвет полосы.
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
