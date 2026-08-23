using MewoDiscord.Handlers;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты БД сессий ChatGPT: каждый тест работает во временном каталоге состояния.
/// Коллекция "state-directory" сериализует классы, переставляющие общий AppConfig.StateDirectory.
/// </summary>
[Collection("state-directory")]
public class ChatGptSessionStoreTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly string _stateDirectory;

    public ChatGptSessionStoreTests()
    {
        _stateDirectory = Path.Combine(Path.GetTempPath(), "mewo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stateDirectory);

        AppConfig.StateDirectory = _stateDirectory;
        ChatGptSessionStore.Load();
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
    public void Gpt_СессияСохраняетсяИЗагружается()
    {
        var entry = ChatGptSessionStore.Create(1, 2, 100);
        entry.Runtime.Append(new ChatGptClient.ChatTurn("user", "нарисуй кота", []));
        entry.Runtime.LastImage = new ChatGptClient.GeneratedImage(PngBytes, "image/png", "рыжий кот");
        ChatGptSessionStore.Rebind(entry, 200);

        // Перезагрузка с диска — как после рестарта бота
        ChatGptSessionStore.Load();

        var restored = ChatGptSessionStore.FindByMessageId(200);
        Assert.NotNull(restored);
        Assert.Equal(entry.Id, restored.Id);
        Assert.Equal(1ul, restored.GuildId);
        Assert.Equal(2ul, restored.ChannelId);
        Assert.Single(restored.Runtime.History);
        Assert.Equal("нарисуй кота", restored.Runtime.History[0].Text);
        Assert.NotNull(restored.Runtime.LastImage);
        Assert.Equal(PngBytes, restored.Runtime.LastImage.Content);
        Assert.Equal("рыжий кот", restored.Runtime.LastImage.RevisedPrompt);
    }

    [Fact]
    public void Gpt_СчётчикСообщенийПереживаетОбрезкуИсторииИРестарт()
    {
        var entry = ChatGptSessionStore.Create(1, 2, 100);
        var total = ChatGptClient.MaxHistoryTurns + 5;

        for (var i = 0; i < total; i++)
        {
            entry.Runtime.Append(new ChatGptClient.ChatTurn("user", $"сообщение {i}", []));
        }

        ChatGptSessionStore.Rebind(entry, 200);
        ChatGptSessionStore.Load();

        var restored = ChatGptSessionStore.FindByMessageId(200);
        Assert.NotNull(restored);

        // История обрезана лимитом, а счётчик помнит и вытесненные сообщения
        Assert.Equal(ChatGptClient.MaxHistoryTurns, restored.Runtime.History.Count);
        Assert.Equal(total, restored.Runtime.TotalTurns);
    }

    [Fact]
    public void Gpt_ПривязкаПереезжаетПриRebind()
    {
        var entry = ChatGptSessionStore.Create(1, 2, 100);

        ChatGptSessionStore.Rebind(entry, 200);

        Assert.Null(ChatGptSessionStore.FindByMessageId(100));
        Assert.Same(entry, ChatGptSessionStore.FindByMessageId(200));
    }

    [Fact]
    public void Gpt_ЛимитСессийВытесняетСтарейшую()
    {
        var first = ChatGptSessionStore.Create(1, 2, 1);

        for (var i = 2; i <= ChatGptSessionStore.MaxSessions + 1; i++)
        {
            ChatGptSessionStore.Create(1, 2, (ulong)i);
        }

        Assert.Equal(ChatGptSessionStore.MaxSessions, ChatGptSessionStore.All().Count);
        Assert.Null(ChatGptSessionStore.FindByMessageId(1));

        // Файл состояния вытесненной сессии удалён
        var stateFile = Path.Combine(_stateDirectory, "chatgpt_sessions", first.Id + ".json");
        Assert.False(File.Exists(stateFile));
    }

    [Fact]
    public void Gpt_ПоследняяАктивнаяВыбираетсяПоКаналу()
    {
        var first = ChatGptSessionStore.Create(1, 10, 100);
        var second = ChatGptSessionStore.Create(1, 10, 200);
        ChatGptSessionStore.Create(1, 99, 300);

        Assert.Same(second, ChatGptSessionStore.FindLastActive(10));

        // Хит в первую делает её последней активной
        ChatGptSessionStore.Rebind(first, 101);
        Assert.Same(first, ChatGptSessionStore.FindLastActive(10));

        Assert.True(ChatGptSessionStore.HasSessions(10));
        Assert.True(ChatGptSessionStore.HasSessions(99));
        Assert.False(ChatGptSessionStore.HasSessions(42));

        // Список — свежие сверху
        var all = ChatGptSessionStore.All();
        Assert.Equal(3, all.Count);
        Assert.Same(first, all[0]);
    }

    [Fact]
    public void Gpt_БитыеСтрокиИндексаПропускаются()
    {
        var valid = ChatGptSessionStore.Create(1, 2, 100);

        var indexPath = Path.Combine(_stateDirectory, "chatgpt_sessions.txt");
        File.AppendAllLines(indexPath,
        [
            "мусор",
            "a|b|c|d|e",
            "id2|1|2|3|не-дата",
            "id3|1|2|3|2026-01-01T00:00:00.0000000Z|лишнее-поле",
            string.Empty
        ]);

        ChatGptSessionStore.Load();

        var all = ChatGptSessionStore.All();
        Assert.Single(all);
        Assert.Equal(valid.Id, all[0].Id);
    }

    [Fact]
    public void Gpt_УпоминаниеБотаВырезаетсяИзТекста()
    {
        Assert.Equal("сколько будет 2 + 2?", ChatGptSessionHandler.StripBotMention("<@111> сколько будет 2 + 2?", 111));
        Assert.Equal("привет", ChatGptSessionHandler.StripBotMention("<@!111> привет", 111));

        // Чужие упоминания не трогаем
        Assert.Equal("спроси у <@222>", ChatGptSessionHandler.StripBotMention("<@111> спроси у <@222>", 111));
    }
}
