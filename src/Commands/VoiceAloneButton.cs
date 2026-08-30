using Discord;
using Discord.Interactions;
using MewoDiscord.Handlers;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

/// <summary>
/// Кнопка «я ещё тут» под вопросом «приём-приём?». Команд в модуле нет — только обработчик
/// компонента, поэтому в Discord он ничего не регистрирует.
/// </summary>
public class VoiceAloneButton : InteractionModuleBase<SocketInteractionContext>
{
    // ignoreGroupNames здесь не нужен — модуль без [Group], — но звёздочки обязательны:
    // канал и пользователь едут в самом custom id, состояния между рестартами у кнопки нет
    [ComponentInteraction(VoiceStatusHandler.AloneButtonPrefix + ":*:*")]
    public async Task StillThere(string channelId, string userId)
    {
        // Спрашивали не его — тихий отказ ephemeral'ом. Просто промолчать нельзя:
        // неотвеченное взаимодействие Discord рисует как «Interaction failed»
        if (Context.User.Id.ToString() != userId)
        {
            await RespondAsync(embed: BotEmbeds.Warning(BotMessages.VoiceAloneNotYours()), ephemeral: true);
            return;
        }

        if (!ulong.TryParse(channelId, out var channel)
            || !VoiceStatusHandler.ConfirmAlone(channel, Context.User.Id))
        {
            await RespondAsync(embed: BotEmbeds.Warning(BotMessages.VoiceAloneStale()), ephemeral: true);
            return;
        }

        BotLogger.Information("{User} подтвердил присутствие в голосовом канале {Channel}",
            Context.User.Username, channelId);

        // Правим само сообщение с кнопкой: вопрос снят, отвечать больше не на что
        await ((IComponentInteraction)Context.Interaction).UpdateAsync(properties =>
        {
            properties.Content = BotMessages.VoiceAloneConfirmed(Context.User.Mention);
            properties.Components = new ComponentBuilder().Build();
            properties.AllowedMentions = AllowedMentions.None;
        });
    }
}
