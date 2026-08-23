using Discord.WebSocket;

using MewoDiscord.AiActionsProcessors;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Handlers;

/// <summary>
/// Пайплайн кастомных действий (модуль CustomAiActions): распознаёт по тексту пинга,
/// что пользователь просит не «поговорить», а выполнить сценарий, и передаёт управление
/// процессору. Ловля двухступенчатая: сначала системный гейт (без сети), затем HIT_PROMPT
/// в дешёвую инстант-модель, от которой ждём «ДА». Действия с прошедшим гейтом пробуются
/// по очереди до первого попадания; если не попало ни одно — сообщение уходит дальше
/// по пайпу, в обычную сессию.
/// Проверка попадания ждётся под канальным замком MessageHandler: от её исхода зависит,
/// потреблено сообщение или нет. Держать замок секунду допустимо только потому, что
/// до неё доходят единицы сообщений — гейт отсекает остальные. Сам процессор работает
/// минутами и уходит в фон.
/// </summary>
public static class CustomAiActionHandler
{
    /// <summary>
    /// Пытается распознать и запустить кастомное действие.
    /// true — действие сработало и запущено в фоне, сообщение потреблено.
    /// </summary>
    public static async Task<bool> TryHandleAsync(SocketUserMessage message, ulong botId)
    {
        var candidates = new List<CustomAiAction>();

        foreach (var gate in Enum.GetValues<CustomAiActionGate>())
        {
            if (PassesGate(gate, message, botId))
            {
                candidates.AddRange(CustomAiActionStore.ByGate(gate));
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // Упоминание бота — это обращение, а не часть запроса; остальные превращаем в имена
        var text = DiscordMentions.Humanize(ChatGptSessionHandler.StripBotMention(message.Content, botId), message);

        if (text.Length == 0)
        {
            return false;
        }

        foreach (var action in candidates)
        {
            var answer = await ChatGptClient.AskInstantAsync(
                action.HitPrompt.Replace(CustomAiActionStore.MessagePlaceholder, text));

            if (!IsPositive(answer))
            {
                continue;
            }

            var processor = CustomAiActionProcessors.Find(action.Id);

            if (processor == null)
            {
                // Файл действия есть, а кода под него нет — не роняем пайп, пробуем следующее
                BotLogger.Warning("Для действия {Id} нет процессора — пропускаем", action.Id);
                continue;
            }

            BotLogger.LogAi(BotLogger.ChatGptThreadKey, "🎯 Сработало действие «{Name}» от {User}", action.Name, message.Author.Username);
            Start(processor, new CustomAiActionContext(message, text, action));

            return true;
        }

        return false;
    }

    /// <summary>
    /// Проверяет системное условие действия. Гейт должен быть дешёвым: он отсекает
    /// сообщения до похода в ИИ, поэтому никаких сетевых вызовов здесь.
    /// </summary>
    internal static bool PassesGate(CustomAiActionGate gate, SocketUserMessage message, ulong botId) => gate switch
    {
        // Только упоминания из текста сообщения: реплай с включённым «@» подставляет
        // в MentionedUsers автора цитаты, и гейт срабатывал бы там, где никого не звали.
        // Упоминание самого бота не в счёт — это обращение, а не цель действия
        CustomAiActionGate.HasUserMention => DiscordMentions.ExplicitUserIds(message.Content).Any(id => id != botId),
        _ => false
    };

    /// <summary>
    /// Ответ инстант-модели на HIT_PROMPT считается попаданием, только если начинается
    /// со слова «да». Модель может обрамить его разметкой или продолжить фразой —
    /// поэтому берётся первое слово, а не всё сообщение целиком.
    /// </summary>
    internal static bool IsPositive(string answer)
    {
        var firstWord = new string(answer.SkipWhile(c => !char.IsLetter(c)).TakeWhile(char.IsLetter).ToArray());

        return string.Equals(firstWord, "да", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Запускает процессор в фоне: он ходит в ИИ минутами и не должен держать
    /// канальный замок MessageHandler.
    /// </summary>
    private static void Start(Func<CustomAiActionContext, Task> processor, CustomAiActionContext context)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await processor(context);
            }
            catch (Exception ex)
            {
                BotLogger.Error(ex, "Ошибка выполнения действия {Id}", context.Action.Id);
            }
        });
    }
}
