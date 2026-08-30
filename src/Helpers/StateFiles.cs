namespace MewoDiscord.Helpers;

/// <summary>
/// Запись файлов состояния в state/ ради одного инварианта: пишем во временный файл
/// и подменяем им настоящий, чтобы краш посреди записи не оставил обрезанную БД.
/// Замок внутрь не спрятан: одни хранилища держат свой, другие пишут под общим
/// замком стора — блокировка остаётся заботой вызывающего.
/// </summary>
internal static class StateFiles
{
    /// <summary>
    /// Пишет строки. <paramref name="what"/> — что записывалось, для сообщения об ошибке.
    /// </summary>
    internal static void WriteAtomic(string path, IEnumerable<string> lines, string what) =>
        Write(path, what, temp => File.WriteAllLines(temp, lines));

    /// <summary>
    /// Пишет текст целиком. <paramref name="what"/> — что записывалось, для сообщения об ошибке.
    /// </summary>
    internal static void WriteAtomic(string path, string content, string what) =>
        Write(path, what, temp => File.WriteAllText(temp, content));

    private static void Write(string path, string what, Action<string> writeTo)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + ".tmp";
            writeTo(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            BotLogger.Error("Ошибка записи {What}: {Message}", what, ex.Message);
        }
    }
}
