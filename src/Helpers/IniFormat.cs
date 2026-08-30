namespace MewoDiscord.Helpers;

/// <summary>
/// Общее правило обоих ini-файлов поставки: где кончается ключ и начинается текст.
/// Форматы у config.ini и messages.ini разные (секции против плоского списка), а вот
/// ловушка одна — строка-продолжение с двоеточием, — и правило для неё должно быть одно.
/// </summary>
internal static class IniFormat
{
    /// <summary>
    /// Начинает ли строка новый ключ. Ключ — латинский идентификатор, занимающий всю
    /// часть строки до двоеточия: иначе продолжение вроде «Используй: yyyy-MM-dd»
    /// приняли бы за новый ключ и молча оборвали многострочное значение.
    /// Отдельно отсекается голая ссылка: в «https://…» до двоеточия тоже стоит
    /// латинское слово, но «//» следом за ним ключом не бывает.
    /// </summary>
    internal static bool IsKey(string line, int colonIndex)
    {
        if (colonIndex <= 0 || line.AsSpan(colonIndex + 1).StartsWith("//"))
        {
            return false;
        }

        var candidate = line[..colonIndex].Trim();

        if (candidate.Length == 0 || !char.IsAsciiLetter(candidate[0]))
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
