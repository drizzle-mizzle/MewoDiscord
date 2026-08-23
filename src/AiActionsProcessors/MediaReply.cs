using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;

namespace MewoDiscord.AiActionsProcessors;

/// <summary>
/// Отправка результатов работы с медиа в чат. Вынесено из процессоров, потому что
/// у них одинаковые требования: ответ реплаем на исходное сообщение, никаких пингов
/// и файл, который читается с диска, а не из памяти.
/// </summary>
public static class MediaReply
{
    /// <summary>
    /// Свой таймаут на заливку: у Discord.Net он рассчитан на обычные запросы,
    /// а полсотни мегабайт по скромному каналу уезжают заметно дольше. Иначе получается
    /// худший из отказов — всё сработало, а в чат ничего не приехало.
    /// </summary>
    private const int UploadTimeoutMs = 300_000;

    /// <summary>
    /// Отправляет файл с диска. Вызывать обязательно до того, как рабочий каталог
    /// будет убран: вложение читается потоком, а не копируется в память.
    /// </summary>
    public static async Task SendFileAsync(SocketUserMessage message, string path, string fileName, string? text = null)
    {
        await using var stream = File.OpenRead(path);
        using var attachment = new FileAttachment(stream, fileName);

        try
        {
            await message.Channel.SendFilesAsync(
                [attachment],
                text: text,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(message.Id, failIfNotExists: false),
                options: new RequestOptions { Timeout = UploadTimeoutMs });
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось отправить результат обработки медиа: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Отправляет системный embed (отказ, предупреждение, карточку прогресса).
    /// Возвращает отправленное сообщение — карточку прогресса потом надо убрать.
    /// </summary>
    public static async Task<IUserMessage?> SendEmbedAsync(SocketUserMessage message, Embed embed)
    {
        try
        {
            return await message.Channel.SendMessageAsync(
                embed: embed,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(message.Id, failIfNotExists: false));
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось отправить сообщение действия: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Убирает карточку прогресса. Не получилось — не беда: лишний embed в чате
    /// не стоит того, чтобы ронять результат.
    /// </summary>
    public static async Task DeleteAsync(IUserMessage? message)
    {
        if (message == null)
        {
            return;
        }

        try
        {
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось убрать карточку прогресса: {Message}", ex.Message);
        }
    }
}
