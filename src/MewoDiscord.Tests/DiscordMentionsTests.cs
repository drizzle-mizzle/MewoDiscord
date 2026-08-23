using MewoDiscord.Helpers;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты перевода упоминаний туда и обратно: в промпт уходит @имя,
/// из ответа модели @имя возвращается настоящим упоминанием.
/// </summary>
public class DiscordMentionsTests
{
    private static readonly Dictionary<ulong, string> Names = new()
    {
        [111] = "Флауэр",
        [222] = "Мяу",
        [333] = "Иван Петрович",
        [444] = "Иван"
    };

    private static readonly Dictionary<string, ulong> Mentionable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Флауэр"] = 111,
        ["Мяу"] = 222,
        ["Иван Петрович"] = 333,
        ["Иван"] = 444
    };

    [Fact]
    public void Mentions_УпоминанияПревращаютсяВИмена()
    {
        Assert.Equal(
            "измени @Флауэр аватарку",
            DiscordMentions.Humanize("измени <@111> аватарку", id => Names.GetValueOrDefault(id)));

        // Формат с восклицательным знаком (старые ники) — тоже упоминание
        Assert.Equal("@Мяу привет", DiscordMentions.Humanize("<@!222> привет", id => Names.GetValueOrDefault(id)));

        // Неизвестный пользователь остаётся как есть: сырой id лучше потерянного смысла
        Assert.Equal("кто это <@999>", DiscordMentions.Humanize("кто это <@999>", id => Names.GetValueOrDefault(id)));

        // Роли и каналы не трогаем
        Assert.Equal("<@&555> в <#777>", DiscordMentions.Humanize("<@&555> в <#777>", id => Names.GetValueOrDefault(id)));
    }

    [Fact]
    public void Mentions_ОтветМоделиВозвращаетНастоящиеУпоминания()
    {
        var (text, mentioned) = DiscordMentions.Restore("@Флауэр ты большой молодец", Mentionable);

        Assert.Equal("<@111> ты большой молодец", text);
        Assert.Equal([111ul], mentioned);
    }

    [Fact]
    public void Mentions_ДлинноеИмяПобеждаетКороткое()
    {
        var (text, mentioned) = DiscordMentions.Restore("привет, @Иван Петрович!", Mentionable);

        Assert.Equal("привет, <@333>!", text);
        Assert.Equal([333ul], mentioned);
    }

    [Fact]
    public void Mentions_ЧастичноеСовпадениеНеЛовится()
    {
        // «@Иванов» — не «@Иван»: имя обязано кончаться на границе слова
        var (text, mentioned) = DiscordMentions.Restore("@Иванов пришёл", Mentionable);

        Assert.Equal("@Иванов пришёл", text);
        Assert.Empty(mentioned);
    }

    [Fact]
    public void Mentions_ЧужиеИменаНеУпоминаются()
    {
        // Модель может назвать кого угодно, но упоминанием станут только участники обмена
        var (text, mentioned) = DiscordMentions.Restore("@Постороннийчеловек, привет", Mentionable);

        Assert.Equal("@Постороннийчеловек, привет", text);
        Assert.Empty(mentioned);
    }

    [Fact]
    public void Mentions_ПовторОдногоЧеловекаСчитаетсяОдин()
    {
        var (text, mentioned) = DiscordMentions.Restore("@Мяу и ещё раз @Мяу", Mentionable);

        Assert.Equal("<@222> и ещё раз <@222>", text);
        Assert.Equal([222ul], mentioned);
    }

    [Fact]
    public void Mentions_ТекстБезУпоминанийНеТрогается()
    {
        var (text, mentioned) = DiscordMentions.Restore("просто ответ без обращений", Mentionable);

        Assert.Equal("просто ответ без обращений", text);
        Assert.Empty(mentioned);
    }
}
