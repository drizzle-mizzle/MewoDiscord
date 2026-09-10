using MewoDiscord.Helpers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты разбора messages.ini: многострочные значения и защита от ложных ключей.
/// Сети и файлов не требуют — разбор проверяется на строках.
/// </summary>
public class BotMessagesTests
{
    [Fact]
    public void Messages_СтрокаБезКлючаПродолжаетПредыдущее()
    {
        var parsed = BotMessages.Parse(
        [
            "# комментарий",
            "Simple: 👋 привет",
            string.Empty,
            "Steps: 🔐 **Заголовок**",
            "  1️⃣ первый шаг",
            "  2️⃣ второй шаг",
            "Next: конец"
        ]);

        Assert.Equal("👋 привет", parsed["Simple"]);
        Assert.Equal("🔐 **Заголовок**\n1️⃣ первый шаг\n2️⃣ второй шаг", parsed["Steps"]);
        Assert.Equal("конец", parsed["Next"]);
    }

    [Fact]
    public void Messages_ДвоеточиеВТекстеНеСчитаетсяКлючом()
    {
        var parsed = BotMessages.Parse(
        [
            "Format: ❌ Неверный формат",
            "Используй: yyyy-MM-dd HH:mm",
            "Порт: 1455"
        ]);

        // Русские слова перед двоеточием — продолжение, а не новые ключи
        Assert.Single(parsed);
        Assert.Equal("❌ Неверный формат\nИспользуй: yyyy-MM-dd HH:mm\nПорт: 1455", parsed["Format"]);
    }

    [Fact]
    public void Messages_ЗначенияМогутНачинатьсяСоСледующейСтроки()
    {
        var parsed = BotMessages.Parse(
        [
            "Multi:",
            "первая строка",
            "вторая строка"
        ]);

        Assert.Equal("первая строка\nвторая строка", parsed["Multi"]);
    }

    [Theory]
    [InlineData(1, "ViewsOne")]
    [InlineData(21, "ViewsOne")]
    [InlineData(101, "ViewsOne")]
    [InlineData(2, "ViewsFew")]
    [InlineData(4, "ViewsFew")]
    [InlineData(22, "ViewsFew")]
    [InlineData(0, "ViewsMany")]
    [InlineData(5, "ViewsMany")]
    [InlineData(11, "ViewsMany")]
    [InlineData(12, "ViewsMany")]
    [InlineData(14, "ViewsMany")]
    [InlineData(111, "ViewsMany")]
    [InlineData(1_813_879_918, "ViewsMany")]
    public void Messages_ФормаСловаСогласуетсяСЧислом(long count, string expected)
    {
        // 11–14 кончаются на 1–4, но по-русски это «просмотров»
        Assert.Equal(expected, BotMessages.PluralKey("Views", count));
    }

    [Fact]
    public void Messages_МусорДоПервогоКлючаИгнорируется()
    {
        var parsed = BotMessages.Parse(["просто текст", "Key: значение"]);

        Assert.Single(parsed);
        Assert.Equal("значение", parsed["Key"]);
    }
}
