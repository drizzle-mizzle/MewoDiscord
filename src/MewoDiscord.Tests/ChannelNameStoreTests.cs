using MewoDiscord.Helpers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты БД исходных имён голосовых каналов. Автономны: только файловая система,
/// без обращений к Discord и ИИ.
/// Коллекция "state-directory" сериализует классы, переставляющие общий AppConfig.StateDirectory.
/// </summary>
[Collection("state-directory")]
public class ChannelNameStoreTests : IDisposable
{
    private const ulong ChannelId = 1234567890123456789;

    private readonly string _stateDirectory;

    public ChannelNameStoreTests()
    {
        _stateDirectory = Path.Combine(Path.GetTempPath(), "mewo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stateDirectory);

        AppConfig.StateDirectory = _stateDirectory;
        ChannelNameStore.Load();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch
        {
            // Уборка временного каталога не должна ронять тест
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Store_ЗапомненноеИмяПереживаетПерезагрузку()
    {
        ChannelNameStore.Remember(ChannelId, "Общение");

        ChannelNameStore.Load();

        Assert.Equal("Общение", ChannelNameStore.GetOriginalName(ChannelId));
    }

    [Fact]
    public void Store_ИмяСДвоеточиемЧитаетсяЦеликом()
    {
        ChannelNameStore.Remember(ChannelId, "Общение: 18+");

        ChannelNameStore.Load();

        Assert.Equal("Общение: 18+", ChannelNameStore.GetOriginalName(ChannelId));
    }

    [Fact]
    public void Store_ForgetУдаляетЗапись()
    {
        ChannelNameStore.Remember(ChannelId, "Общение");

        ChannelNameStore.Forget(ChannelId);
        ChannelNameStore.Load();

        Assert.Null(ChannelNameStore.GetOriginalName(ChannelId));
    }

    [Fact]
    public void Store_БитыеСтрокиНеЛомаютЗагрузку()
    {
        var path = Path.Combine(_stateDirectory, "voice_channels.txt");
        File.WriteAllLines(path, ["мусор без двоеточия", "неЧисло: Имя", string.Empty, $"{ChannelId}: Общение"]);

        ChannelNameStore.Load();

        Assert.Equal("Общение", ChannelNameStore.GetOriginalName(ChannelId));
        Assert.Single(ChannelNameStore.All());
    }
}
