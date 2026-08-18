using MewoDiscord.Handlers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты матрицы решений вотчера имён каналов. Decide — чистая функция,
/// сеть и Discord не нужны.
/// </summary>
public class ChannelRenameWatcherTests
{
    private const string Alone = ChannelRenameWatcher.AloneChannelName;
    private const string Original = "Мурчальня #1";
    private const string Foreign = "Имя от админа";

    [Fact]
    public void Watcher_БезЗаписиИНеОдин_НичегоНеДелает()
    {
        var decision = ChannelRenameWatcher.Decide(Original, originalName: null, wantAlone: false);

        Assert.Equal(ChannelRenameWatcher.RenameDecision.None, decision);
    }

    [Fact]
    public void Watcher_ОдинВКанале_ПереименовываетВОдинокое()
    {
        var decision = ChannelRenameWatcher.Decide(Original, originalName: null, wantAlone: true);

        Assert.Equal(ChannelRenameWatcher.RenameDecision.ToAlone, decision);
    }

    [Fact]
    public void Watcher_АдминСамНазвалКаналОдинокимИменем_НеТрогает()
    {
        // Записи в БД нет — бот этот канал не переименовывал, имя совпало случайно
        var decision = ChannelRenameWatcher.Decide(Alone, originalName: null, wantAlone: true);

        Assert.Equal(ChannelRenameWatcher.RenameDecision.None, decision);
    }

    [Fact]
    public void Watcher_УжеОдинокоеИОдин_НичегоНеДелает()
    {
        // Сценарий кулдауна: за 10 минут в канале началась новая сессия из одного человека —
        // возвращать родное имя не надо
        var decision = ChannelRenameWatcher.Decide(Alone, Original, wantAlone: true);

        Assert.Equal(ChannelRenameWatcher.RenameDecision.None, decision);
    }

    [Fact]
    public void Watcher_ВернулосьРодноеНоСноваОдин_ПереименовываетОбратно()
    {
        var decision = ChannelRenameWatcher.Decide(Original, Original, wantAlone: true);

        Assert.Equal(ChannelRenameWatcher.RenameDecision.ToAlone, decision);
    }

    [Fact]
    public void Watcher_ОдинокоеИмяАЛюдейНеОдин_ВозвращаетРодное()
    {
        // Сценарий кулдауна: новая сессия из двух человек — имя надо вернуть
        var decision = ChannelRenameWatcher.Decide(Alone, Original, wantAlone: false);

        Assert.Equal(ChannelRenameWatcher.RenameDecision.ToOriginal, decision);
    }

    [Fact]
    public void Watcher_РодноеИмяУжеНаМесте_ЗабываетЗапись()
    {
        var decision = ChannelRenameWatcher.Decide(Original, Original, wantAlone: false);

        Assert.Equal(ChannelRenameWatcher.RenameDecision.Forget, decision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Watcher_АдминПереименовалПокаВисело_УступаетИЗабывает(bool wantAlone)
    {
        // Есть запись в БД, но текущее имя — не «одинокое» и не родное: вмешался админ
        var decision = ChannelRenameWatcher.Decide(Foreign, Original, wantAlone);

        Assert.Equal(ChannelRenameWatcher.RenameDecision.Forget, decision);
    }
}
