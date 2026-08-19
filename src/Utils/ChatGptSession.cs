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
    /// Последняя сгенерированная картинка — её правит <see cref="ChatGptClient.ContinueImageAsync"/>.
    /// </summary>
    public ChatGptClient.GeneratedImage? LastImage { get; internal set; }

    /// <summary>
    /// Референсы последней генерации — для правок с исходными изображениями.
    /// </summary>
    public IReadOnlyList<ChatGptClient.InputFile> LastReferences { get; internal set; } = [];

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
        LastReferences = [];
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
