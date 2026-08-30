using MewoDiscord.Handlers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты чистых функций журнала голосовых каналов: матрица сторожа одиночества
/// и формат длительности разговора. Сеть и Discord не нужны — префикс Watcher_
/// общий с матрицей вотчера имён, задача у них одна.
/// </summary>
public class VoiceStatusHandlerTests
{
    private const ulong Loner = 100;
    private const ulong Other = 200;

    [Fact]
    public void Watcher_КанализПустел_СторожОдиночестваСнимается()
    {
        var decision = VoiceStatusHandler.DecideWatch(usersCount: 0, lonerId: 0, watchedUserId: Loner);

        Assert.Equal(VoiceStatusHandler.AloneWatchState.Drop, decision);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Watcher_НародуБольшеОдного_ОтсчётНеТрогается(int usersCount)
    {
        // Пришедший на минуту сосед получаса не обнуляет: решение примет срабатывание
        var decision = VoiceStatusHandler.DecideWatch(usersCount, lonerId: 0, watchedUserId: Loner);

        Assert.Equal(VoiceStatusHandler.AloneWatchState.Keep, decision);
    }

    [Fact]
    public void Watcher_ТотЖеОдиночка_ОтсчётПродолжается()
    {
        var decision = VoiceStatusHandler.DecideWatch(usersCount: 1, lonerId: Loner, watchedUserId: Loner);

        Assert.Equal(VoiceStatusHandler.AloneWatchState.Keep, decision);
    }

    [Fact]
    public void Watcher_ОдиночкаСменился_ОтсчётЗаводитсяЗаново()
    {
        // Тот ушёл, этот остался: его одиночество началось только что
        var decision = VoiceStatusHandler.DecideWatch(usersCount: 1, lonerId: Other, watchedUserId: Loner);

        Assert.Equal(VoiceStatusHandler.AloneWatchState.Restart, decision);
    }

    [Fact]
    public void Watcher_ПервоеОдиночество_ЗаводитСторож()
    {
        var decision = VoiceStatusHandler.DecideWatch(usersCount: 1, lonerId: Loner, watchedUserId: null);

        Assert.Equal(VoiceStatusHandler.AloneWatchState.Restart, decision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Watcher_КСрабатываниюЧеловекНеОдин_СторожСнимается(bool asked)
    {
        // Решение не зависит от того, был ли задан вопрос: спрашивать больше не о чем
        var decision = VoiceStatusHandler.DecideAlarm(usersCount: 2, lonerId: 0, watchedUserId: Loner, asked);

        Assert.Equal(VoiceStatusHandler.AloneAlarm.Drop, decision);
    }

    [Fact]
    public void Watcher_КСрабатываниюОсталсяДругой_СторожСнимается()
    {
        var decision = VoiceStatusHandler.DecideAlarm(usersCount: 1, lonerId: Other, watchedUserId: Loner, asked: true);

        Assert.Equal(VoiceStatusHandler.AloneAlarm.Drop, decision);
    }

    [Fact]
    public void Watcher_ПервоеСрабатывание_Спрашивает()
    {
        var decision = VoiceStatusHandler.DecideAlarm(usersCount: 1, lonerId: Loner, watchedUserId: Loner, asked: false);

        Assert.Equal(VoiceStatusHandler.AloneAlarm.Ask, decision);
    }

    [Fact]
    public void Watcher_ВопросБезОтвета_Отключает()
    {
        var decision = VoiceStatusHandler.DecideAlarm(usersCount: 1, lonerId: Loner, watchedUserId: Loner, asked: true);

        Assert.Equal(VoiceStatusHandler.AloneAlarm.Disconnect, decision);
    }

    [Theory]
    [InlineData(0, "0сек")]
    [InlineData(59, "59сек")]
    [InlineData(60, "1мин 0сек")]
    [InlineData(3600, "1ч 0сек")]
    [InlineData(3723, "1ч 2мин 3сек")]
    public void Watcher_ДлительностьРазговораФорматируется(int seconds, string expected)
    {
        var text = VoiceStatusHandler.FormatDuration(TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Watcher_РазговорДольшеСуток_ЧасыНеТеряются()
    {
        // TimeSpan.Hours обрезается по модулю 24, а сессия может висеть и дольше суток
        var text = VoiceStatusHandler.FormatDuration(TimeSpan.FromHours(26) + TimeSpan.FromMinutes(5));

        Assert.Equal("26ч 5мин 0сек", text);
    }
}
