using MewoDiscord.Helpers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты разбора config.ini: секции, многострочные значения и защита от ложных ключей —
/// та же ловушка, что у messages.ini, поэтому и префикс имён общий (Messages_).
/// Файл на диске не нужен: разбор проверяется на строках.
/// </summary>
public class AppConfigParseTests
{
    [Fact]
    public void Messages_КонфигРазбираетСекцииИКлючи()
    {
        var parsed = AppConfig.Parse(
        [
            "# комментарий",
            "BotToken: до секции — мусор",
            "[COMMON]",
            "BotToken: abc123",
            "LogsChannel: 42",
            string.Empty,
            "[MEDIA]",
            "BudgetMb: 3800"
        ]);

        Assert.Equal("abc123", parsed["COMMON"]["BotToken"]);
        Assert.Equal("42", parsed["COMMON"]["LogsChannel"]);
        Assert.Equal("3800", parsed["MEDIA"]["BudgetMb"]);
    }

    [Fact]
    public void Messages_КонфигСтрокаБезКлючаПродолжаетЗначение()
    {
        var parsed = AppConfig.Parse(
        [
            "[CHATGPT_SETTINGS]",
            "SystemPrompt: Ты кот.",
            "  Отвечай коротко.",
            "MaxTokens: 2048"
        ]);

        Assert.Equal("Ты кот.\nОтвечай коротко.", parsed["CHATGPT_SETTINGS"]["SystemPrompt"]);
        Assert.Equal("2048", parsed["CHATGPT_SETTINGS"]["MaxTokens"]);
    }

    [Fact]
    public void Messages_КонфигРусскаяСтрокаСДвоеточиемНеКлюч()
    {
        // Ключом считается только латинский идентификатор: иначе строка промпта
        // оборвала бы значение и завела мусорный ключ «Важно»
        var parsed = AppConfig.Parse(
        [
            "[CHATGPT_SETTINGS]",
            "SystemPrompt: Ты кот.",
            "Важно: отвечай коротко.",
            "И по-русски."
        ]);

        Assert.Equal("Ты кот.\nВажно: отвечай коротко.\nИ по-русски.", parsed["CHATGPT_SETTINGS"]["SystemPrompt"]);
        Assert.Single(parsed["CHATGPT_SETTINGS"]);
    }

    [Fact]
    public void Messages_КонфигСсылкаВПродолженииНеКлюч()
    {
        // «https» — латинский идентификатор, но перед двоеточием стоит не только он:
        // ключ обязан занимать всю часть строки до двоеточия
        var parsed = AppConfig.Parse(
        [
            "[COMMON]",
            "SystemPrompt: Читай доки",
            "https://example.com/docs"
        ]);

        Assert.Equal("Читай доки\nhttps://example.com/docs", parsed["COMMON"]["SystemPrompt"]);
        Assert.Single(parsed["COMMON"]);
    }
}
