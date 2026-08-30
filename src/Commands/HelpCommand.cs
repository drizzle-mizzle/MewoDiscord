using Discord.Interactions;
using MewoDiscord.Helpers;

namespace MewoDiscord.Commands;

/// <summary>
/// Публичная справка. Почти всё, что умеет бот, — это не слеш-команды, а просьбы через
/// упоминание и автоответ на ссылки, и узнать о них иначе неоткуда: в списке команд
/// у обычного участника видны только сессии ChatGPT.
/// Ответ ephemeral: справка нужна спросившему, а не всему каналу.
/// </summary>
public class HelpCommand : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("help", "Что умеет бот и как его просить")]
    public async Task Help()
    {
        // При выключенной ChatGPT-части упоминания бота никто не слушает — вторая
        // половина справки обещала бы неработающее
        var text = AppConfig.UseChatGpt
            ? BotMessages.HelpText() + "\n" + BotMessages.HelpTextChatGpt()
            : BotMessages.HelpText();

        BotLogger.LogCommand("{User} использовал /help в #{Channel}", Context.User.Username, Context.Channel.Name);
        await RespondAsync(embed: BotEmbeds.Info(text), ephemeral: true);
    }
}
