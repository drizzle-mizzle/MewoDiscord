using Discord;

using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

/// <summary>
/// Индикатор перевода из одной animated application emoji: кадры проигрывает клиент Discord,
/// поэтому бот не меняет реакции по таймеру. Если эмодзи недоступно, остаётся статичный ⏳.
/// </summary>
internal sealed class XTranslationLoadingIndicator
{
    private static readonly Emoji _fallback = new("⏳");

    private readonly IUserMessage _message;
    private readonly IEmote _reaction;
    private int _stopped;

    private XTranslationLoadingIndicator(IUserMessage message)
    {
        _message = message;
        _reaction = ReactionFor(BotEmotes.Loading);
    }

    public static async Task<XTranslationLoadingIndicator> StartAsync(IUserMessage message)
    {
        var indicator = new XTranslationLoadingIndicator(message);
        await indicator.AddQuietlyAsync();
        return indicator;
    }

    internal static IEmote ReactionFor(Emote? loading) => loading ?? _fallback;

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await ClearAsync();
    }

    private async Task AddQuietlyAsync()
    {
        try
        {
            await _message.AddReactionAsync(_reaction);
        }
        catch (Exception ex)
        {
            BotLogger.Information(
                "Не удалось поставить индикатор перевода {MessageId}: {Message}",
                _message.Id,
                ex.Message);
        }
    }

    private async Task ClearAsync()
    {
        try
        {
            await _message.RemoveAllReactionsForEmoteAsync(_reaction);
        }
        catch
        {
            // Без Manage Messages Discord позволяет снять хотя бы собственную реакцию.
            try
            {
                await _message.RemoveReactionAsync(_reaction, _message.Author);
            }
            catch
            {
                // Индикатор не должен сломать сам перевод или редактирование сообщения.
            }
        }
    }
}
