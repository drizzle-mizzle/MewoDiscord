using Discord;
using Discord.WebSocket;

using MewoDiscord.Helpers;

namespace MewoDiscord.AiActionsProcessors;

/// <summary>
/// Отправка результатов работы с медиа в чат: ответ реплаем на исходное сообщение,
/// без пингов, файл читается с диска, а не из памяти.
/// </summary>
public static class MediaReply
{
    /// <summary>
    /// Свой таймаут на заливку: дефолтный у Discord.Net рассчитан на обычные запросы,
    /// а полсотни мегабайт по скромному каналу уезжают дольше.
    /// </summary>
    private const int UploadTimeoutMs = 300_000;

    /// <summary>
    /// Отправляет файл с диска. Вызывать обязательно до того, как рабочий каталог
    /// будет убран: вложение читается потоком, а не копируется в память.
    /// </summary>
    public static async Task<IUserMessage?> SendFileAsync(
        SocketUserMessage message,
        string path,
        string fileName,
        string? text = null)
    {
        await using var stream = File.OpenRead(path);
        using var attachment = new FileAttachment(stream, fileName);

        try
        {
            return await message.Channel.SendFilesAsync(
                [attachment],
                text: text,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(message.Id, failIfNotExists: false),
                options: new RequestOptions { Timeout = UploadTimeoutMs });
        }
        catch (Exception ex)
        {
            BotLogger.Error("Не удалось отправить результат обработки медиа: {Message}", ex.Message);
            return null;
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
    /// Меняет текст карточки прогресса: карточка «качаю» на получасовом пережатии
    /// выглядит зависшей. Не получилось — не беда.
    /// </summary>
    public static async Task EditEmbedAsync(IUserMessage? message, Embed embed)
    {
        if (message == null)
        {
            return;
        }

        try
        {
            await message.ModifyAsync(properties => properties.Embed = embed);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось обновить карточку прогресса: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Убирает карточку прогресса. Не получилось — не беда.
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
