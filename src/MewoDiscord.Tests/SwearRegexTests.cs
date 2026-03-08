using MewoDiscord.Handlers;
using MewoDiscord.Helpers;

using Xunit.Abstractions;

namespace MewoDiscord.Tests;

/// <summary>
/// Ручные тесты для проверки регулярки и ИИ-верификации мата.
/// Запуск: dotnet test --filter "FullyQualifiedName~SwearRegexTests.ИмяТеста"
/// </summary>
public class SwearRegexTests
{
    private readonly ITestOutputHelper _testOutputHelper;
    public SwearRegexTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }
    /// <summary>
    /// Указываем AppConfig на папку Files/ основного проекта по относительному пути.
    /// Работает и из Rider, и из командной строки.
    /// </summary>
    static SwearRegexTests()
    {
        // Из bin/Debug/net10.0/ поднимаемся до src/, где лежит Files/
        var dir = AppDomain.CurrentDomain.BaseDirectory;

        while (dir != null && !Directory.Exists(Path.Combine(dir, "Files")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        if (dir != null)
        {
            AppConfig.FilesDirectory = Path.Combine(dir, "Files");
        }
    }

    // ====================================================================
    // СЮДА ВПИСЫВАЙ СЛОВА ДЛЯ ПРОВЕРКИ
    // ====================================================================

    /// <summary>
    /// Проверяет, что регулярка находит мат в указанных словах/фразах.
    /// Вписывай свои варианты в InlineData.
    /// </summary>
    [Theory]
    [InlineData("пиздец", true)]
    [InlineData("нахуй", true)]
    [InlineData("ебать", true)]
    [InlineData("блять", true)]
    [InlineData("привет", false)]
    [InlineData("хорошо", false)]
    [InlineData("да пошёл ты нахуй уёбок", true)]
    [InlineData("какой прекрасный день", false)]
    public void Regex_НаходитМат(string text, bool swearExpected)
    {
        var found = MessageHandler.FindSwearsByRegex(text.ToLowerInvariant());

        if (swearExpected)
        {
            Assert.NotEmpty(found);

            foreach (var word in found)
            {
                _testOutputHelper.WriteLine($"  Найдено: \"{word}\"");
            }
        }
        else
        {
            Assert.Empty(found);
        }
    }

    /// <summary>
    /// Свободный тест — впиши любой текст и посмотри, что найдёт регулярка.
    /// </summary>
    [Fact]
    public void Regex_СвободнаяПроверка()
    {
        // ВПИШИ СВОЙ ТЕКСТ СЮДА:
        var text = "ну ты и дебил конечно";

        var found = MessageHandler.FindSwearsByRegex(text.ToLowerInvariant());

        _testOutputHelper.WriteLine($"Текст: \"{text}\"");
        _testOutputHelper.WriteLine($"Найдено совпадений: {found.Count}");

        foreach (var word in found)
        {
            _testOutputHelper.WriteLine($"  → \"{word}\"");
        }
    }

    /// <summary>
    /// Проверяет полный цикл: регулярка + ИИ-верификация.
    /// Использует config.ini из основного проекта (нужен рабочий AnthropicApiKey).
    /// </summary>
    [Theory]
    [InlineData("ебать")]
    [InlineData("пиздец")]
    [InlineData("нахуй")]
    [InlineData("блять")]
    public async Task АИ_ПодтверждаетМат(string text)
    {
        var found = MessageHandler.FindSwearsByRegex(text.ToLowerInvariant());
        Assert.NotEmpty(found);

        _testOutputHelper.WriteLine($"Текст: \"{text}\"");
        _testOutputHelper.WriteLine($"Регулярка нашла: {string.Join(", ", found)}");

        var confirmed = await MessageHandler.VerifySwearsWithAiAsync(found);

        _testOutputHelper.WriteLine($"ИИ подтвердил мат: {confirmed}");
        Assert.True(confirmed);
    }

    /// <summary>
    /// Проверяет, что ИИ НЕ подтверждает безобидные слова,
    /// которые регулярка могла ошибочно поймать.
    /// </summary>
    [Theory]
    [InlineData("бляха-муха")]
    [InlineData("блин")]
    public async Task АИ_НеПодтверждаетЛожныеСрабатывания(string text)
    {
        var found = MessageHandler.FindSwearsByRegex(text.ToLowerInvariant());

        _testOutputHelper.WriteLine($"Текст: \"{text}\"");
        _testOutputHelper.WriteLine($"Регулярка нашла: {string.Join(", ", found)}");

        if (found.Count == 0)
        {
            _testOutputHelper.WriteLine("Регулярка ничего не нашла — тест пройден автоматически");
            return;
        }

        var confirmed = await MessageHandler.VerifySwearsWithAiAsync(found);

        _testOutputHelper.WriteLine($"ИИ подтвердил мат: {confirmed}");
        Assert.False(confirmed);
    }
}
