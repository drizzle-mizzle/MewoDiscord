using MewoDiscord.Utils;

namespace MewoDiscord.Helpers;

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
    /// Сколько всего сообщений прошло через сессию (вопросы и ответы вместе).
    /// Считается отдельно от History: та обрезается до
    /// <see cref="ChatGptClient.MaxHistoryTurns"/> и о прошлом уже не помнит.
    /// </summary>
    public int TotalTurns { get; internal set; }

    /// <summary>
    /// Полный сброс сессии: история, картинка и референсы.
    /// </summary>
    public void Reset()
    {
        History.Clear();
        LastImage = null;
        TotalTurns = 0;
    }

    /// <summary>
    /// Дописывает ход в историю, вытесняя самые старые сверх лимита и снимая картинки
    /// со всех ходов, кроме последних <see cref="ChatGptClient.MaxImageTurns"/>.
    /// Картинки хранятся в истории целиком, base64-строками, и уходят в запрос при каждом
    /// следующем обмене: без чистки пара присланных фотографий раздувала бы и запрос,
    /// и json состояния до десятков мегабайт, пока прокси не начнёт отвергать их вовсе.
    /// О чём шла речь, история помнит и без них — служебная строка «приложил изображения»
    /// остаётся в тексте хода.
    /// </summary>
    internal void Append(ChatGptClient.ChatTurn turn)
    {
        History.Add(turn);
        TotalTurns++;

        while (History.Count > ChatGptClient.MaxHistoryTurns)
        {
            History.RemoveAt(0);
        }

        for (var i = 0; i < History.Count - ChatGptClient.MaxImageTurns; i++)
        {
            if (History[i].ImageDataUrls.Count > 0)
            {
                History[i] = History[i] with { ImageDataUrls = [] };
            }
        }
    }
}
