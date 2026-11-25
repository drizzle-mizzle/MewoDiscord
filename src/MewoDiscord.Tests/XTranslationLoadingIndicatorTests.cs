using Discord;

using MewoDiscord.Handlers;

namespace MewoDiscord.Tests;

public class XTranslationLoadingIndicatorTests
{
    [Fact]
    public void X_ИндикаторИспользуетAnimatedЭмодзиПриложения()
    {
        var loading = new Emote(123, "translation_loading", true);

        var reaction = XTranslationLoadingIndicator.ReactionFor(loading);

        Assert.Same(loading, reaction);
        Assert.True(loading.Animated);
    }

    [Fact]
    public void X_ИндикаторБезЭмодзиПриложенияИспользуетПесочныеЧасы()
    {
        var reaction = XTranslationLoadingIndicator.ReactionFor(null);

        var emoji = Assert.IsType<Emoji>(reaction);
        Assert.Equal("⏳", emoji.Name);
    }
}
