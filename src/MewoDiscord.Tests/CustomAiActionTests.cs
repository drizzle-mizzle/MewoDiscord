using MewoDiscord.Handlers;
using MewoDiscord.Helpers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты модуля CustomAiActions: разбор файлов действий, распознавание ответа
/// инстант-модели и подстановка имён вместо упоминаний. Сеть не нужна.
/// </summary>
public class CustomAiActionTests
{
    private static readonly string[] SampleAction =
    [
        "# комментарий",
        "[ACTION]",
        "Отредактировать аватарку пользователя",
        string.Empty,
        "[GATE]",
        "HAS_USER_MENTION",
        string.Empty,
        "[HIT_PROMPT]",
        "Отвечай строго ДА или НЕТ.",
        "\"\"\"",
        "{{message}}",
        "\"\"\""
    ];

    [Fact]
    public void Action_ФайлДействияРазбирается()
    {
        var action = CustomAiActionStore.Parse("edit_profile_picture", SampleAction);

        Assert.NotNull(action);
        Assert.Equal("edit_profile_picture", action.Id);
        Assert.Equal("Отредактировать аватарку пользователя", action.Name);
        Assert.Equal(CustomAiActionGate.HasUserMention, action.Gate);

        // Тело секции — многострочное, крайние пустые строки обрезаны
        Assert.Equal("Отвечай строго ДА или НЕТ.\n\"\"\"\n{{message}}\n\"\"\"", action.HitPrompt);
    }

    [Fact]
    public void Action_ПлейсхолдерПодставляется()
    {
        var action = CustomAiActionStore.Parse("edit_profile_picture", SampleAction);

        Assert.NotNull(action);
        var prompt = action.HitPrompt.Replace(CustomAiActionStore.MessagePlaceholder, "добавь @Флауэр ушки");

        Assert.Contains("добавь @Флауэр ушки", prompt);
        Assert.DoesNotContain(CustomAiActionStore.MessagePlaceholder, prompt);
    }

    [Fact]
    public void Action_БезОбязательнойСекцииОтбрасывается()
    {
        Assert.Null(CustomAiActionStore.Parse("broken", ["[ACTION]", "Название", "[GATE]", "HAS_USER_MENTION"]));
        Assert.Null(CustomAiActionStore.Parse("broken", ["[GATE]", "HAS_USER_MENTION", "[HIT_PROMPT]", "Вопрос?"]));
        Assert.Null(CustomAiActionStore.Parse("broken", []));
    }

    [Fact]
    public void Action_НеизвестныйГейтОтбрасывается()
    {
        Assert.Null(CustomAiActionStore.Parse("broken", ["[ACTION]", "Название", "[GATE]", "HAS_MAGIC", "[HIT_PROMPT]", "Вопрос?"]));

        Assert.True(CustomAiActionStore.TryParseGate("HAS_USER_MENTION", out var gate));
        Assert.Equal(CustomAiActionGate.HasUserMention, gate);

        Assert.True(CustomAiActionStore.TryParseGate("HAS_MEDIA_ATTACHED", out gate));
        Assert.Equal(CustomAiActionGate.HasMediaAttached, gate);

        Assert.True(CustomAiActionStore.TryParseGate("HAS_YOUTUBE_LINK", out gate));
        Assert.Equal(CustomAiActionGate.HasYoutubeLink, gate);

        Assert.False(CustomAiActionStore.TryParseGate("HAS_MAGIC", out _));
        Assert.False(CustomAiActionStore.TryParseGate(string.Empty, out _));
    }

    [Theory]
    [InlineData("ДА", true)]
    [InlineData("да", true)]
    [InlineData("Да.", true)]
    [InlineData("**ДА**", true)]
    [InlineData("Да, это запрос отредактировать изображение", true)]
    [InlineData("НЕТ", false)]
    [InlineData("нет.", false)]
    [InlineData("Не могу ответить", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Action_ОтветИнстантМоделиРаспознаётся(string answer, bool expected)
    {
        Assert.Equal(expected, CustomAiActionHandler.IsPositive(answer));
    }

    [Fact]
    public void Action_ФайлИзПоставкиВалиден()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;

        while (dir != null && !Directory.Exists(Path.Combine(dir, "Files")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        Assert.NotNull(dir);

        var path = Path.Combine(dir, "Files", "custom_ai_actions", "edit_profile_picture.ini");
        Assert.True(File.Exists(path), $"нет файла действия: {path}");

        var action = CustomAiActionStore.Parse("edit_profile_picture", File.ReadAllLines(path));

        Assert.NotNull(action);
        Assert.Equal(CustomAiActionGate.HasUserMention, action.Gate);
        Assert.Contains(CustomAiActionStore.MessagePlaceholder, action.HitPrompt);
    }
}
