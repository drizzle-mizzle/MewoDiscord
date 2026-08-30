using MewoDiscord.Utils;
using Xunit.Abstractions;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты management API прокси: разбор ответов OAuth-логина и списка аккаунтов.
/// Автономны — сеть нужна только живому тесту с префиксом АИ_.
/// </summary>
public class ChatGptAuthClientTests(ITestOutputHelper testOutputHelper)
{
    private readonly ITestOutputHelper _testOutputHelper = testOutputHelper;

    [Fact]
    public void Gpt_СсылкаЛогинаРазбирается()
    {
        const string json = """{"status":"ok","url":"https://auth.openai.com/oauth/authorize?x=1","state":"abc123"}""";
        var start = ChatGptAuthClient.ParseLoginStartResponse(json);

        Assert.NotNull(start);
        Assert.Equal("https://auth.openai.com/oauth/authorize?x=1", start.Url);
        Assert.Equal("abc123", start.State);

        Assert.Null(ChatGptAuthClient.ParseLoginStartResponse("""{"status":"ok"}"""));
        Assert.Null(ChatGptAuthClient.ParseLoginStartResponse("не json"));
    }

    [Theory]
    [InlineData("http://localhost:1455/auth/callback?code=ac_XXX&state=st_YYY", "st_YYY")]
    [InlineData("http://localhost:1455/auth/callback?state=st%2FYYY&code=ac", "st/YYY")]
    [InlineData("http://localhost:1455/auth/callback?code=ac_XXX", null)]
    [InlineData("http://localhost:1455/auth/callback?state=st_YYY", null)]
    [InlineData("просто текст без ссылки", null)]
    public void Gpt_StateИзвлекаетсяИзRedirectUrl(string url, string? expected)
    {
        Assert.Equal(expected, ChatGptAuthClient.ExtractStateFromRedirectUrl(url));
    }

    [Fact]
    public void Gpt_CallbackJsonСодержитProviderИUrl()
    {
        var json = ChatGptAuthClient.BuildOAuthCallbackJson("http://localhost:1455/auth/callback?code=1&state=2");

        Assert.Contains("\"provider\":\"codex\"", json);
        Assert.Contains("\"redirect_url\":\"http://localhost:1455/auth/callback?code=1\\u0026state=2\"", json);
    }

    [Fact]
    public void Gpt_СтатусАвторизацииРазбирается()
    {
        Assert.Equal("ok", ChatGptAuthClient.ParseAuthStatusResponse("""{"status":"ok"}""").Status);
        Assert.Equal("wait", ChatGptAuthClient.ParseAuthStatusResponse("""{"status":"wait"}""").Status);

        var error = ChatGptAuthClient.ParseAuthStatusResponse("""{"status":"error","error":"unknown or expired state"}""");
        Assert.Equal("error", error.Status);
        Assert.Equal("unknown or expired state", error.Error);

        Assert.Equal("error", ChatGptAuthClient.ParseAuthStatusResponse("не json").Status);
    }

    [Fact]
    public void Gpt_СписокАккаунтовФильтруетсяПоCodex()
    {
        const string json = """
            {"files":[
                {"name":"codex-user.json","provider":"codex","email":"u@e.com","disabled":false,"unavailable":false},
                {"name":"claude-x.json","provider":"claude","email":"other@e.com"},
                {"name":"codex-dead.json","provider":"codex","unavailable":true,"status_message":"token expired"}
            ]}
            """;

        var accounts = ChatGptAuthClient.ParseAuthFilesResponse(json);

        Assert.Equal(2, accounts.Count);
        Assert.Equal("u@e.com", accounts[0].Email);
        Assert.False(accounts[0].Unavailable);
        Assert.True(accounts[1].Unavailable);
        Assert.Equal("token expired", accounts[1].StatusMessage);

        Assert.Empty(ChatGptAuthClient.ParseAuthFilesResponse("не json"));
    }

    // ====================================================================
    // Живой тест: нужен поднятый CLIProxyAPI и UseChatGpt: true в config.ini
    // ====================================================================

    /// <summary>
    /// Живой список аккаунтов через management API (нужен MANAGEMENT_PASSWORD у прокси).
    /// </summary>
    [Fact]
    public async Task АИ_ГптСписокАккаунтов()
    {
        var accounts = await ChatGptAuthClient.GetAccountsAsync();

        Assert.NotNull(accounts);
        _testOutputHelper.WriteLine($"Аккаунтов: {accounts.Count}");

        foreach (var account in accounts)
        {
            _testOutputHelper.WriteLine($"• {account.Name} {account.Email} unavailable={account.Unavailable}");
        }
    }

}
