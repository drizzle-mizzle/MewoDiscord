using Discord;
using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

/// <summary>
/// Ретранслятор общих событий в общий чат сервера (<c>GeneralChatChannel</c>).
/// Единственный подписчик шины: журнал голосовых каналов не знает про общий чат,
/// а общий чат — про журнал.
/// </summary>
public static class GeneralChatRelay
{
    /// <summary>
    /// Подписывает ретранслятор на шину общих событий. Вызывается один раз при старте.
    /// </summary>
    public static void Subscribe()
    {
        CommonEvents.Published += RelayAsync;
    }

    private static async Task RelayAsync(CommonEvent commonEvent)
    {
        var generalChatId = AppConfig.GeneralChatChannel;

        // Общий чат совпадает со статусным каналом журнала — событие там уже есть,
        // вторая копия подряд только мешает
        if (generalChatId == 0 || generalChatId == AppConfig.VoiceStatusChannel)
        {
            return;
        }

        var generalChat = commonEvent.Guild.GetTextChannel(generalChatId);

        if (generalChat == null)
        {
            return;
        }

        await generalChat.SendMessageAsync(commonEvent.Text, allowedMentions: AllowedMentions.None);
    }
}
