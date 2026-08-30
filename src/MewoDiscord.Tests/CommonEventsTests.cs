using MewoDiscord.Helpers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты шины общих событий. Discord ей нужен только как тип поля записи, которое она
/// не трогает, поэтому проверка автономна. Префикс Events_ свой: ни один существующий
/// по смыслу сюда не подходит, а в фильтр офлайн-прогона он добавлен.
/// </summary>
public class CommonEventsTests
{
    [Fact]
    public async Task Events_ОшибкаПодписчикаНеГаситОстальных()
    {
        // Доставка в общий чат необязательна: падение подписчика не должно ронять
        // журнал канала, ради которого событие и случилось
        var reached = false;

        Task Failing(CommonEvent _) => throw new InvalidOperationException("подписчик упал");

        Task Working(CommonEvent _)
        {
            reached = true;
            return Task.CompletedTask;
        }

        CommonEvents.Published += Failing;
        CommonEvents.Published += Working;

        try
        {
            await CommonEvents.PublishAsync(new CommonEvent(null!, "текст события"));

            Assert.True(reached);
        }
        finally
        {
            // Событие статическое: без отписки подписчики протекли бы в соседние тесты
            CommonEvents.Published -= Failing;
            CommonEvents.Published -= Working;
        }
    }

    [Fact]
    public async Task Events_БезПодписчиковПубликацияНичегоНеЛомает()
    {
        await CommonEvents.PublishAsync(new CommonEvent(null!, "некому слушать"));
    }
}
