using Discord;
using Discord.WebSocket;

namespace MewoDiscord.Helpers;

/// <summary>
/// Эмодзи приложения — единственный способ показать свою картинку прямо в тексте
/// сообщения: серверные живут в одной гильдии, а эти Discord отдаёт боту везде.
/// Заводятся один раз за всю жизнь бота.
/// </summary>
public static class BotEmotes
{
    /// <summary>
    /// Имена эмодзи у приложения. Менять только вместе с картинками: по имени и ищем.
    /// Короче двух символов имя Discord не принимает — оттого «x_logo», а не «x».
    /// </summary>
    private const string TelegramName = "telegram";

    private const string XName = "x_logo";

    /// <summary>
    /// Логотип Telegram. null — эмодзи не завелось (нет прав, нет картинки),
    /// и подписи останутся без иконки.
    /// </summary>
    public static Emote? Telegram { get; private set; }

    public static Emote? X { get; private set; }

    /// <summary>
    /// Тот же логотип картинкой — для иконки футера embed'а, куда разметка эмодзи не годится:
    /// футер Discord рисует как обычный текст, эмодзи там не разворачивается.
    /// </summary>
    public static string? IconUrl(Emote? emote) =>
        emote == null ? null : $"https://cdn.discordapp.com/emojis/{emote.Id}.png";

    /// <summary>
    /// Ищет эмодзи у приложения, а при отсутствии — заводит их из Files. Неудача не фатальна.
    /// </summary>
    public static async Task EnsureAsync(DiscordSocketClient client)
    {
        IReadOnlyCollection<Emote> existing;

        try
        {
            existing = await client.GetApplicationEmotesAsync();
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось получить эмодзи приложения: {Message}", ex.Message);
            return;
        }

        Telegram = await ResolveAsync(client, existing, TelegramName, "telegram.png");
        X = await ResolveAsync(client, existing, XName, "x.png");
    }

    /// <summary>
    /// Берёт готовое эмодзи или заводит новое из картинки в Files.
    /// </summary>
    private static async Task<Emote?> ResolveAsync(
        DiscordSocketClient client, IReadOnlyCollection<Emote> existing, string name, string imageFile)
    {
        var found = existing.FirstOrDefault(emote => emote.Name == name);

        if (found != null)
        {
            return found;
        }

        var path = Path.Combine(AppConfig.FilesDirectory, imageFile);

        if (!File.Exists(path))
        {
            BotLogger.Warning("Картинка {File} не найдена — подписи останутся без иконки", path);
            return null;
        }

        try
        {
            var created = await client.CreateApplicationEmoteAsync(name, new Image(path));
            BotLogger.Information("Эмодзи приложения {Name} загружено", name);
            return created;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось завести эмодзи приложения {Name}: {Message}", name, ex.Message);
            return null;
        }
    }
}
