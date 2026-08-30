using Discord.WebSocket;

namespace MewoDiscord.Helpers;

/// <summary>
/// Мелочи, которые нужны везде, где бот отдаёт в чат файл: сколько влезет и как это
/// назвать человеку. Собраны вместе, потому что правило одно на всех — правка формата
/// или способа считать лимит не должна разъезжаться по пяти копиям.
/// </summary>
internal static class DiscordLimits
{
    /// <summary>
    /// Размер человеку: мегабайты с одним знаком. Файлы бота меряются мегабайтами —
    /// байты и килобайты в сообщении читаются хуже, чем «12,4 МБ».
    /// </summary>
    internal static string FormatSize(long bytes) =>
        $"{bytes / 1024d / 1024d:F1} МБ";

    /// <summary>
    /// Длительность медиафайла человеку: «7:12», у длинного — «1:07:12». Это формат
    /// плеера, а не журнала голосовых: там разговор меряется словами «1ч 2мин».
    /// </summary>
    internal static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);

        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    /// <summary>
    /// Потолок вложения: зависит от уровня буста сервера. Запасное значение задаёт
    /// вызывающий — оно разное у постов соцсетей и у медиа-действий.
    /// </summary>
    internal static ulong UploadLimit(SocketUserMessage message, ulong fallback) =>
        UploadLimit(message.Channel, fallback);

    /// <inheritdoc cref="UploadLimit(SocketUserMessage, ulong)"/>
    internal static ulong UploadLimit(ISocketMessageChannel channel, ulong fallback) =>
        ((channel as SocketGuildChannel)?.Guild)?.MaxUploadLimit ?? fallback;

    /// <summary>
    /// Имя файла из ссылки. Запасное имя задаёт вызывающий: по нему в папке загрузок
    /// видно, что это за файл, когда адрес имени не дал.
    /// </summary>
    /// <param name="requireExtension">
    /// Считать безымянным и то, что пришло без расширения: ffmpeg выбирает разбор
    /// по расширению, а модели достаточно любого имени.
    /// </param>
    internal static string FileNameFromUrl(string url, string fallback, bool requireExtension = false)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var name = Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(name) || (requireExtension && !name.Contains('.')))
        {
            return fallback;
        }

        return name;
    }
}
