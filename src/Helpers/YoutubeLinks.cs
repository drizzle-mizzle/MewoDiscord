using System.Text.RegularExpressions;

namespace MewoDiscord.Helpers;

/// <summary>
/// Распознавание ссылок на YouTube и проверка, что рядом с ней есть просьба.
/// Всё чистым кодом и без сети: этим пользуется системный гейт кастомного действия,
/// который работает до похода в ИИ.
/// Узнаём только YouTube, а не «любой из полутора тысяч сайтов yt-dlp»: такую регулярку
/// невозможно держать верной, и промахиваться она будет молча.
/// </summary>
public static partial class YoutubeLinks
{
    /// <summary>
    /// Сколько букв и цифр должно остаться после вычёркивания ссылки, упоминаний
    /// и эмодзи, чтобы счесть сообщение просьбой. Голая ссылка — это не просьба.
    /// </summary>
    private const int MinRequestLetters = 2;

    /// <summary>
    /// Идентификатор видео из первой ссылки в тексте. null — ссылки нет.
    /// </summary>
    public static string? FirstVideoId(string text)
    {
        var match = LinkRegex().Match(text);

        return match.Success ? match.Groups["id"].Value : null;
    }

    /// <summary>
    /// Адрес, который мы отдаём yt-dlp. Строится кодом из проверенного идентификатора:
    /// исходная строка пользователя в аргументы не попадает никогда. Список аргументов
    /// спасает от подстановки команд, но не от строки, начинающейся с дефиса
    /// (это была бы подстановка опции), от file:// и от ссылки на плейлист.
    /// Тот же принцип, что «модель не пишет аргументы ffmpeg».
    /// </summary>
    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    /// <summary>
    /// Идентификатор видео YouTube — ровно одиннадцать символов из безопасного набора.
    /// Всё, что уходит в командную строку, обязано пройти эту проверку.
    /// </summary>
    public static bool IsValidVideoId(string? id) => id != null && VideoIdRegex().IsMatch(id);

    /// <summary>
    /// Условие гейта: в тексте есть ссылка на видео и, кроме неё, есть ещё какая-то просьба.
    /// Упоминание бота к этому моменту уже снято — оно обращение, а не текст запроса.
    /// </summary>
    public static bool HasRequestBesidesLink(string textWithoutBotMention)
    {
        if (!LinkRegex().IsMatch(textWithoutBotMention))
        {
            return false;
        }

        var rest = NoiseRegex().Replace(textWithoutBotMention, " ");
        rest = RemoveLinks(rest);

        return rest.Count(char.IsLetterOrDigit) >= MinRequestLetters;
    }

    #region Internals

    /// <summary>
    /// Вычёркивает ссылки вместе с хвостом до ближайшего пробела: за идентификатором
    /// остаются «?si=...», «&amp;t=42s» и закрывающие скобки, и без них обрывок
    /// сошёл бы за текст просьбы.
    /// </summary>
    private static string RemoveLinks(string text)
    {
        var result = text;

        while (true)
        {
            var match = LinkRegex().Match(result);

            if (!match.Success)
            {
                return result;
            }

            var end = match.Index + match.Length;

            while (end < result.Length && !char.IsWhiteSpace(result[end]))
            {
                end++;
            }

            result = result.Remove(match.Index, end - match.Index);
        }
    }

    /// <summary>
    /// Ссылка на видео в любом виде: watch, youtu.be, shorts, live, embed; со схемой
    /// и без, с поддоменами m и music. Обёртки Discord — угловые скобки и markdown —
    /// отваливаются сами: шаблон не заякорен.
    /// Ровно одиннадцать символов идентификатора и запрет буквы следом обязательны.
    /// Без запрета «watch?v=dQw4w9WgXcQEXTRA» молча совпал бы первыми одиннадцатью,
    /// и скачалось бы чужое видео; без счётчика длины совпали бы «/@канал»
    /// и «playlist?list=...», и yt-dlp получил бы плейлист на пятьсот роликов.
    /// Взгляд назад отсекает чужие домены вроде notyoutube.com.
    /// </summary>
    [GeneratedRegex(
        """(?<![\w.\-])(?:https?://)?(?:(?:www\.|m\.|music\.)?(?:youtube\.com|youtube-nocookie\.com)/(?:watch\?(?:[^\s]*?&)?v=|shorts/|live/|embed/|v/)|youtu\.be/)(?<id>[A-Za-z0-9_\-]{11})(?![A-Za-z0-9_\-])""",
        RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("""^[A-Za-z0-9_\-]{11}$""")]
    private static partial Regex VideoIdRegex();

    /// <summary>
    /// Упоминания и кастомные эмодзи: инструкцией они не являются, а букв в них хватает.
    /// </summary>
    [GeneratedRegex("""<a?:[A-Za-z0-9_]+:\d+>|<@[!&]?\d+>|<#\d+>""")]
    private static partial Regex NoiseRegex();

    #endregion
}
