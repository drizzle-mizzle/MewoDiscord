using Discord;

using MewoDiscord.Helpers;

namespace MewoDiscord.Handlers;

/// <summary>
/// Фоновый индикатор перевода. Текст держит статичные ⏳; изображения каждые три секунды
/// меняют ⏳ на ⌛ и обратно, перед переключением снимая оба варианта реакций всех пользователей.
/// </summary>
internal sealed class XTranslationLoadingIndicator
{
    private static readonly TimeSpan _switchInterval = TimeSpan.FromSeconds(3);
    private static readonly Emoji _waiting = new("⏳");
    private static readonly Emoji _running = new("⌛");

    private readonly IUserMessage _message;
    private readonly CancellationTokenSource _cancellation = new();
    private Task _animation = Task.CompletedTask;
    private int _stopped;

    private XTranslationLoadingIndicator(IUserMessage message)
    {
        _message = message;
    }

    public static async Task<XTranslationLoadingIndicator> StartAsync(IUserMessage message, bool animate)
    {
        var indicator = new XTranslationLoadingIndicator(message);
        await indicator.AddQuietlyAsync(_waiting);

        if (animate)
        {
            indicator._animation = indicator.AnimateAsync();
        }

        return indicator;
    }

    /// <summary>
    /// Событие успешного редактирования: больше не рисуем загрузку поверх готового перевода.
    /// </summary>
    public void MessageEdited() => _cancellation.Cancel();

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();

        try
        {
            await _animation;
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка после редактирования или ошибки перевода.
        }

        await ClearAsync();
        _cancellation.Dispose();
    }

    private async Task AnimateAsync()
    {
        var next = _running;

        while (true)
        {
            await Task.Delay(_switchInterval, _cancellation.Token);
            await ClearAsync();
            _cancellation.Token.ThrowIfCancellationRequested();
            await AddQuietlyAsync(next);
            next = next == _waiting ? _running : _waiting;
        }
    }

    private async Task AddQuietlyAsync(Emoji emoji)
    {
        try
        {
            await _message.AddReactionAsync(emoji);
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
        await ClearAsync(_waiting);
        await ClearAsync(_running);
    }

    private async Task ClearAsync(Emoji emoji)
    {
        try
        {
            await _message.RemoveAllReactionsForEmoteAsync(emoji);
        }
        catch
        {
            // Без Manage Messages Discord позволяет снять хотя бы собственную реакцию.
            try
            {
                await _message.RemoveReactionAsync(emoji, _message.Author);
            }
            catch
            {
                // Индикатор не должен сломать сам перевод или следующий его кадр.
            }
        }
    }
}
