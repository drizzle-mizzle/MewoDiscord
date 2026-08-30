using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Право на работу с медиа: единственный слот, свой временный каталог и бюджет диска —
/// один объект. Каталог нельзя получить, не заняв слот, а <see cref="Dispose"/> и слот
/// отпускает, и каталог сносит. Поэтому «не больше одной операции сразу» и «после работы
/// ничего не остаётся» — свойства типа, а не соглашение, о котором кто-то забудет.
/// Операции идут по одной, потому что параллельные yt-dlp и ffmpeg кладут маленький
/// сервер быстрее всего прочего, а два двухгигабайтных исходника попросту не влезут
/// в выделенное место.
/// </summary>
public sealed class MediaWorkspace : IDisposable
{
    /// <summary>
    /// Сколько ждать слот короткой операции над вложением из чата: та занимает
    /// секунды, и небольшая очередь лучше отказа.
    /// </summary>
    public static readonly TimeSpan ConvertGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Скачиванию ждать бессмысленно: впереди могут быть минуты работы,
    /// и честнее сразу попросить зайти позже.
    /// </summary>
    public static readonly TimeSpan DownloadGrace = TimeSpan.Zero;

    /// <summary>
    /// Свободное место, которое не трогаем ни при каких условиях: заполнить диск
    /// хоста под ноль — худшее, что может сделать бот.
    /// </summary>
    private const long ReserveBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Пауза перед повтором удаления: только что убитый процесс может ещё держать хэндл.
    /// </summary>
    private const int DeleteRetryMs = 200;

    private static readonly SemaphoreSlim _slot = new(1, 1);

    /// <summary>
    /// Чем занят слот прямо сейчас. Пишется и чистится только под захваченным слотом,
    /// поэтому читателю достаётся либо актуальное описание, либо «свободно».
    /// </summary>
    private static volatile string? _busy;

    private int _released;

    /// <summary>
    /// Описание текущей операции для отказа тому, кому слота не хватило: «занят» без
    /// объяснения выглядит зависанием, а ждать может понадобиться и полчаса.
    /// </summary>
    public static string BusyWith => _busy ?? "другим файлом";

    private MediaWorkspace(string fullPath)
    {
        FullPath = fullPath;
    }

    /// <summary>
    /// Каталог этой операции. Всё, что в нём появилось, будет удалено.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// Сколько байт уже занято операцией.
    /// </summary>
    public long UsedBytes => DirectorySize(FullPath);

    /// <summary>
    /// Корень рабочей области. Внутри — по каталогу на операцию.
    /// </summary>
    private static string RootDirectory => AppConfig.MediaSettings.WorkDirectory;

    private static long BudgetBytes => (long)AppConfig.MediaSettings.BudgetMb * 1024 * 1024;

    /// <summary>
    /// Занимает слот и выдаёт чистый каталог. null — слот занят другой операцией.
    /// <paramref name="what"/> — чем заняли слот, это увидит следующий за нами.
    /// </summary>
    public static async Task<MediaWorkspace?> TryAcquireAsync(TimeSpan wait, string what)
    {
        if (!await _slot.WaitAsync(wait))
        {
            return null;
        }

        _busy = what;

        try
        {
            Sweep();

            var path = Path.Combine(RootDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);

            return new MediaWorkspace(path);
        }
        catch
        {
            // Каталог не создался — значит и слот не наш
            _busy = null;
            _slot.Release();
            throw;
        }
    }

    /// <summary>
    /// Путь внутри каталога операции. Имя строит код: пользовательское имя файла
    /// в файловую систему не попадает.
    /// </summary>
    public string PathFor(string name) => Path.Combine(FullPath, name);

    /// <summary>
    /// Влезет ли ещё столько байт. Считается и свой бюджет, и настоящее свободное место:
    /// жёсткого предела у тома нет (обычному тому Docker размер не задать), поэтому
    /// наша бухгалтерия — единственное, что стоит между разогнавшимся качанием и полным
    /// диском, а видит она только свои файлы.
    /// </summary>
    public bool HasRoomFor(long bytes)
    {
        var budgetRoom = BudgetBytes - UsedBytes;
        var diskRoom = FreeSpace() - ReserveBytes;

        return bytes <= Math.Min(budgetRoom, diskRoom);
    }

    /// <summary>
    /// Сносит неудачный артефакт: следующий круг пережатия начинается с чистого места.
    /// </summary>
    public void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось убрать промежуточный файл {Path}: {Message}", path, ex.Message);
        }
    }

    public void Dispose()
    {
        // Ровно один раз: Dispose вызывают и using, и ручной откат по ошибке
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        ForceDelete(FullPath);
        _busy = null;
        _slot.Release();
    }

    #region Internals

    /// <summary>
    /// Контрольная уборка перед началом работы. Слот эксклюзивен, поэтому в этот момент
    /// любой подкаталог корня — мусор операции, умершей до Dispose: краш, OOM-kill,
    /// остановка контейнера посреди качания. Ненулевой объём логируется: это стоит знать.
    /// </summary>
    private static void Sweep()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);

            var reclaimed = 0L;

            foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
            {
                reclaimed += ForceDelete(directory);
            }

            if (reclaimed > 0)
            {
                BotLogger.Warning(
                    "Подмёл {Size} мусора от прошлой операции в {Root}",
                    DiscordLimits.FormatSize(reclaimed),
                    RootDirectory);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось прибраться в рабочем каталоге: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Удаляет каталог со всем содержимым и возвращает освобождённый объём.
    /// </summary>
    private static long ForceDelete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var size = DirectorySize(directory);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                ClearReadOnly(directory);
                Directory.Delete(directory, recursive: true);

                return size;
            }
            catch (IOException) when (attempt == 0)
            {
                // Только что убитый процесс может ещё держать файл открытым
                Thread.Sleep(DeleteRetryMs);
            }
            catch (Exception ex)
            {
                BotLogger.Warning("Не удалось убрать каталог {Directory}: {Message}", directory, ex.Message);
                return 0;
            }
        }

        BotLogger.Warning("Каталог {Directory} не удалился со второй попытки", directory);

        return 0;
    }

    private static void ClearReadOnly(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось посчитать размер каталога {Directory}: {Message}", directory, ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Свободное место на томе рабочего каталога. Точка монтирования ищется по самому
    /// длинному совпадающему префиксу: на Linux корень пути /media — это /,
    /// то есть совсем другая файловая система.
    /// </summary>
    private static long FreeSpace()
    {
        try
        {
            var full = Path.GetFullPath(RootDirectory);

            var mount = DriveInfo.GetDrives()
                .Where(drive => full.StartsWith(drive.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(drive => drive.Name.Length)
                .FirstOrDefault();

            return mount?.AvailableFreeSpace ?? long.MaxValue;
        }
        catch (Exception ex)
        {
            // Псевдо-точки монтирования умеют бросать на любой вопрос: тогда
            // полагаемся на один бюджет
            BotLogger.Warning("Не удалось узнать свободное место: {Message}", ex.Message);
            return long.MaxValue;
        }
    }


    #endregion
}
