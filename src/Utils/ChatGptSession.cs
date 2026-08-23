namespace MewoDiscord.Utils;

/// <summary>
/// Состояние диалога с ChatGPT на стороне бота: история сообщений и последняя
/// сгенерированная картинка. Прокси stateless, поэтому вся память сессии живёт здесь.
/// Не потокобезопасна: вызовы по одной сессии сериализует владелец.
/// </summary>
public class ChatGptSession
{
    /// <summary>
    /// История ходов диалога. Обрезается до <see cref="ChatGptClient.MaxHistoryTurns"/>.
    /// </summary>
    internal List<ChatGptClient.ChatTurn> History { get; } = new();

    /// <summary>
    /// Последняя сгенерированная картинка: подмешивается в следующий запрос,
    /// чтобы модель могла её править («сделай его рыжим»).
    /// </summary>
    public ChatGptClient.GeneratedImage? LastImage { get; internal set; }

    /// <summary>
    /// Есть ли в сессии картинка, которую можно править.
    /// </summary>
    public bool HasImage => LastImage != null;

    /// <summary>
    /// Полный сброс сессии: история, картинка и референсы.
    /// </summary>
    public void Reset()
    {
        History.Clear();
        LastImage = null;
    }

    /// <summary>
    /// Дописывает ход в историю, вытесняя самые старые сверх лимита.
    /// </summary>
    internal void Append(ChatGptClient.ChatTurn turn)
    {
        History.Add(turn);

        while (History.Count > ChatGptClient.MaxHistoryTurns)
        {
            History.RemoveAt(0);
        }
    }
}
