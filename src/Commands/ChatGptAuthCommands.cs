using Discord;
using Discord.Interactions;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

namespace MewoDiscord.Commands;

/// <summary>
/// Управление подключением к ChatGPT: OAuth-логин Codex через CLIProxyAPI и статус аккаунтов.
/// Флоу логина: /chatgpt-auth login выдаёт ссылку, после входа пользователь вставляет
/// redirect-URL (localhost:1455/...) в модалку, прокси меняет код на токены сам.
/// Группа отделена от публичной /chatgpt: у сабкоманд одной группы не может быть разных прав.
/// </summary>
[Group("chatgpt-auth", "Авторизация ChatGPT (для админов)")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
public class ChatGptAuthCommands : InteractionModuleBase<SocketInteractionContext>
{
    private const string PasteButtonId = "chatgpt_login_paste";
    private const string LoginModalId = "chatgpt_login_modal";

    [SlashCommand("login", "Войти в аккаунт ChatGPT (OAuth)")]
    public async Task Login()
    {
        await DeferAsync(ephemeral: true);

        var start = await ChatGptAuthClient.BeginLoginAsync();

        BotLogger.LogCommand("/chatgpt-auth login — {User}: {Result}", Context.User.Username, start == null ? "не удалось начать" : "ссылка выдана");

        if (start == null)
        {
            await FollowupAsync(embed: BotEmbeds.Error(BotMessages.ChatGptLoginStartFailed()), ephemeral: true);
            return;
        }

        var components = new ComponentBuilder()
            .WithButton("Вставить ссылку", PasteButtonId, ButtonStyle.Primary)
            .Build();

        await FollowupAsync(
            embed: BotEmbeds.Info(BotMessages.ChatGptLoginInstructions(start.Url)),
            components: components,
            ephemeral: true);
    }

    [SlashCommand("status", "Показать подключённые аккаунты ChatGPT")]
    public async Task Status()
    {
        await DeferAsync(ephemeral: true);

        var accounts = await ChatGptAuthClient.GetAccountsAsync();

        BotLogger.LogCommand("/chatgpt-auth status — {User}: аккаунтов {Count}", Context.User.Username, accounts?.Count.ToString() ?? "н/д");

        if (accounts == null)
        {
            await FollowupAsync(embed: BotEmbeds.Error(BotMessages.ChatGptLoginStartFailed()), ephemeral: true);
            return;
        }

        if (accounts.Count == 0)
        {
            await FollowupAsync(embed: BotEmbeds.Warning(BotMessages.ChatGptStatusEmpty()), ephemeral: true);
            return;
        }

        var lines = new List<string> { BotMessages.ChatGptStatusHeader(accounts.Count.ToString()) };
        var healthy = true;

        foreach (var account in accounts)
        {
            var broken = account.Disabled || account.Unavailable;
            healthy &= !broken;

            var email = string.IsNullOrEmpty(account.Email) ? string.Empty : $" — {account.Email}";
            var marker = broken ? $" {BotMessages.ChatGptStatusUnavailable()}" : string.Empty;
            var note = string.IsNullOrEmpty(account.StatusMessage) ? string.Empty : $" ({account.StatusMessage})";
            lines.Add($"• `{account.Name}`{email}{marker}{note}");
        }

        // Хотя бы один недоступный аккаунт — жёлтая карточка вместо зелёной
        var text = string.Join("\n", lines);

        await FollowupAsync(
            embed: healthy ? BotEmbeds.Success(text) : BotEmbeds.Warning(text),
            ephemeral: true);
    }

    // ignoreGroupNames обязателен: [Group] префиксует пути компонент-обработчиков,
    // и с дефолтным InteractionServiceConfig такой custom id никогда не совпадёт
    [ComponentInteraction(PasteButtonId, ignoreGroupNames: true)]
    public Task PasteButton() =>
        RespondWithModalAsync<LoginModal>(LoginModalId); // первый ответ на кнопку, без Defer

    [ModalInteraction(LoginModalId, ignoreGroupNames: true)]
    public async Task LoginModalSubmit(LoginModal modal)
    {
        await DeferAsync(ephemeral: true); // ack в пределах 3 секунд — до любых HTTP-вызовов

        var result = await ChatGptAuthClient.CompleteLoginAsync(modal.RedirectUrl.Trim());

        BotLogger.LogCommand("/chatgpt-auth login (вставка ссылки) — {User}: {Result}", Context.User.Username, result.Ok ? "успех" : result.Error ?? "ошибка");

        await FollowupAsync(
            embed: result.Ok
                ? BotEmbeds.Success(BotMessages.ChatGptLoginDone())
                : BotEmbeds.Error(BotMessages.ChatGptLoginFailed(result.Error ?? "неизвестная ошибка")),
            ephemeral: true);
    }
}

/// <summary>
/// Модалка для вставки ссылки после логина. Тексты — константы в атрибутах:
/// осознанное исключение из правила BotMessages (атрибуты требуют compile-time констант).
/// </summary>
public class LoginModal : IModal
{
    public string Title => "Логин ChatGPT";

    [InputLabel("Ссылка после логина")]
    [ModalTextInput(
        "chatgpt_redirect_url",
        TextInputStyle.Paragraph,
        placeholder: "http://localhost:1455/auth/callback?code=...",
        minLength: 10,
        maxLength: 2000)]
    public string RedirectUrl { get; set; } = string.Empty;
}
