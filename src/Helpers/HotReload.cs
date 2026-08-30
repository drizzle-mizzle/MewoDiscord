namespace MewoDiscord.Helpers;

/// <summary>
/// Слежение за файлами поставки, которые правятся на ходу: config.ini, messages.ini,
/// каталог кастомных действий. Заводится один раз при старте, ошибки заведения гасятся —
/// перечитка на лету удобна, но не обязательна: без неё нужна перезагрузка бота.
/// </summary>
internal static class HotReload
{
    /// <summary>
    /// Пауза перед перечиткой: редактор успевает дописать файл, а мы не читаем половину.
    /// </summary>
    private const int SettleDelayMs = 100;

    /// <summary>
    /// Заводит вотчер на каталог и возвращает его. Возвращённую ссылку обязательно
    /// сохранить в статическое поле: рантайм держит работающий вотчер только слабой
    /// ссылкой (чтобы никому не нужный вотчер мог собраться), и без укоренения
    /// сборщик мусора молча выключает перечитку на первой же сборке.
    /// </summary>
    /// <param name="directory">Каталог слежения.</param>
    /// <param name="filter">Маска имён файлов.</param>
    /// <param name="reload">Что позвать при изменении.</param>
    /// <param name="watchNames">
    /// Следить и за появлением, удалением и переименованием файлов, а не только за записью.
    /// Нужно каталогу с набором файлов, где новый файл — это новая единица содержимого.
    /// </param>
    internal static FileSystemWatcher? Watch(string directory, string filter, Action reload, bool watchNames = false)
    {
        try
        {
            var notifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;

            if (watchNames)
            {
                notifyFilter |= NotifyFilters.FileName;
            }

            var watcher = new FileSystemWatcher(directory, filter)
            {
                NotifyFilter = notifyFilter,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, _) => DelayedReload(reload);

            if (watchNames)
            {
                watcher.Created += (_, _) => DelayedReload(reload);
                watcher.Deleted += (_, _) => DelayedReload(reload);
                watcher.Renamed += (_, _) => DelayedReload(reload);
            }

            return watcher;
        }
        catch
        {
            // Слежение не критично: файл прочитан при старте, а перезагрузка бота
            // подхватит правку в любом случае
            return null;
        }
    }

    private static void DelayedReload(Action reload)
    {
        Thread.Sleep(SettleDelayMs);
        reload();
    }
}
