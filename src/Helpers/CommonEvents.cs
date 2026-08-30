using Discord.WebSocket;

namespace MewoDiscord.Helpers;

/// <summary>
/// Общее событие: то, что интересно всему серверу, а не только журналу канала,
/// в котором оно случилось. Текст берётся готовым — в общий чат уходит ровно та же
/// строка, что и в журнал, второго набора формулировок не заводим.
/// </summary>
/// <param name="Guild">Сервер, где случилось событие: общий чат ищется в нём.</param>
/// <param name="Text">Текст сообщения аудита.</param>
public sealed record CommonEvent(SocketGuild Guild, string Text);

/// <summary>
/// Шина общих событий. Издатель (журнал голосовых каналов) объявляет событие общим
/// и не знает, кто и куда его доставит; подписчик (<c>GeneralChatRelay</c>) не знает,
/// откуда событие пришло. Новый способ рассылки — это ещё один подписчик, а не правка
/// каждого места, где событие рождается.
/// </summary>
public static class CommonEvents
{
    /// <summary>
    /// Подписчики доставки. Подписываются один раз при старте.
    /// </summary>
    public static event Func<CommonEvent, Task>? Published;

    /// <summary>
    /// Объявляет событие общим. Ошибка подписчика гасится и уходит в лог: доставка
    /// в общий чат — необязательное ответвление, ронять из-за неё журнал канала нельзя.
    /// </summary>
    public static async Task PublishAsync(CommonEvent commonEvent)
    {
        var subscribers = Published;

        if (subscribers == null)
        {
            return;
        }

        foreach (var subscriber in subscribers.GetInvocationList().Cast<Func<CommonEvent, Task>>())
        {
            try
            {
                await subscriber(commonEvent);
            }
            catch (Exception ex)
            {
                BotLogger.Error(ex, "Подписчик общих событий упал: {Message}", ex.Message);
            }
        }
    }
}
