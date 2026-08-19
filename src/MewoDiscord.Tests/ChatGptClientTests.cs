using System.Text;
using MewoDiscord.Helpers;
using MewoDiscord.Utils;

using Xunit.Abstractions;

namespace MewoDiscord.Tests;

/// <summary>
/// Тесты клиента ChatGPT (CLIProxyAPI). Gpt_* автономны и проверяют чистые функции
/// сборки запросов и разбора ответов. Тесты АИ_Гпт* ходят в реальный прокси:
/// нужен поднятый CLIProxyAPI, UseChatGpt: true и заполненные ChatGptProxy* в config.ini.
/// </summary>
public class ChatGptClientTests
{
    /// <summary>
    /// PNG-сигнатура — минимальное «содержимое картинки» для тестов.
    /// </summary>
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0];

    private readonly ITestOutputHelper _testOutputHelper;

    public ChatGptClientTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    /// <summary>
    /// Указываем AppConfig на папку Files/ основного проекта — нужно тестам АИ_Гпт*.
    /// </summary>
    static ChatGptClientTests()
    {
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

    [Fact]
    public void Gpt_DataUrlСодержитMimeИBase64()
    {
        var url = ChatGptClient.BuildDataUrl("image/png", PngBytes);

        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal(PngBytes, Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
    }

    [Theory]
    [InlineData("cat.png", "image/png")]
    [InlineData("cat.JPG", "image/jpeg")]
    [InlineData("cat.jpeg", "image/jpeg")]
    [InlineData("cat.webp", "image/webp")]
    [InlineData("cat.gif", "image/gif")]
    [InlineData("notes.txt", null)]
    public void Gpt_MimeОпределяетсяПоРасширению(string fileName, string? expected)
    {
        Assert.Equal(expected, ChatGptClient.GetImageMimeByFileName(fileName));
    }

    [Fact]
    public void Gpt_MimeОпределяетсяПоСигнатуре()
    {
        Assert.Equal("image/png", ChatGptClient.DetectImageMime(PngBytes));
        Assert.Equal("image/jpeg", ChatGptClient.DetectImageMime(JpegBytes));
        Assert.Equal("image/gif", ChatGptClient.DetectImageMime("GIF89a"u8.ToArray()));
        Assert.Equal("image/webp", ChatGptClient.DetectImageMime("RIFF0000WEBP"u8.ToArray()));
        Assert.Null(ChatGptClient.DetectImageMime("просто текст"u8.ToArray()));
    }

    [Fact]
    public void Gpt_ЯвныйMimeИмеетПриоритет()
    {
        // Расширение и сигнатура говорят JPEG, но явный тип важнее
        var file = new ChatGptClient.InputFile("cat.jpg", JpegBytes, "image/webp");

        Assert.Equal("image/webp", ChatGptClient.ResolveImageMime(file));
    }

    [Fact]
    public void Gpt_ТекстовыйФайлВклеиваетсяВТекст()
    {
        var file = new ChatGptClient.InputFile("notes.md", Encoding.UTF8.GetBytes("# Заметка"));
        var turn = ChatGptClient.PrepareUserTurn("Смотри файл", [file]);

        Assert.Contains("--- файл notes.md ---", turn.Text);
        Assert.Contains("# Заметка", turn.Text);
        Assert.Empty(turn.ImageDataUrls);
    }

    [Fact]
    public void Gpt_КартинкаУходитВDataUrl()
    {
        var file = new ChatGptClient.InputFile("cat.png", PngBytes);
        var turn = ChatGptClient.PrepareUserTurn("Что на картинке?", [file]);

        Assert.Equal("Что на картинке?", turn.Text);
        Assert.Single(turn.ImageDataUrls);
        Assert.StartsWith("data:image/png;base64,", turn.ImageDataUrls[0]);
    }

    [Fact]
    public void Gpt_НеподдерживаемыйФайлПропускаетсяСПометкой()
    {
        var file = new ChatGptClient.InputFile("virus.exe", [0x4D, 0x5A, 0x00]);
        var turn = ChatGptClient.PrepareUserTurn("Держи", [file]);

        Assert.Contains("virus.exe пропущен", turn.Text);
        Assert.Empty(turn.ImageDataUrls);
    }

    [Fact]
    public void Gpt_ЧатБезКартинокСериализуетсяСтрокой()
    {
        var turn = new ChatGptClient.ChatTurn("user", "привет", []);
        var json = ChatGptClient.BuildChatRequestJson("gpt-5.5", 2048, [], turn);

        Assert.Contains("\"model\":\"gpt-5.5\"", json);
        Assert.Contains("\"max_tokens\":2048", json);
        Assert.Contains("\"content\":\"\\u043F\\u0440\\u0438\\u0432\\u0435\\u0442\"", json);
        Assert.DoesNotContain("image_url", json);
    }

    [Fact]
    public void Gpt_ЧатСКартинкойСериализуетсяМассивомЧастей()
    {
        var dataUrl = ChatGptClient.BuildDataUrl("image/png", PngBytes);
        var turn = new ChatGptClient.ChatTurn("user", "что тут?", [dataUrl]);
        var json = ChatGptClient.BuildChatRequestJson("gpt-5.5", 2048, [], turn);

        Assert.Contains("\"type\":\"text\"", json);
        Assert.Contains("\"type\":\"image_url\"", json);
        Assert.Contains(dataUrl, json);
    }

    [Fact]
    public void Gpt_ИсторияПопадаетВЗапросПередНовымХодом()
    {
        var history = new List<ChatGptClient.ChatTurn>
        {
            new("user", "first", []),
            new("assistant", "second", [])
        };
        var turn = new ChatGptClient.ChatTurn("user", "third", []);
        var json = ChatGptClient.BuildChatRequestJson("gpt-5.5", 100, history, turn);

        var first = json.IndexOf("first", StringComparison.Ordinal);
        var second = json.IndexOf("second", StringComparison.Ordinal);
        var third = json.IndexOf("third", StringComparison.Ordinal);

        Assert.True(first >= 0 && first < second && second < third);
        Assert.Contains("\"role\":\"assistant\"", json);
    }

    [Fact]
    public void Gpt_ОтветЧатаИзвлекается()
    {
        const string json = """{"id":"x","choices":[{"index":0,"message":{"role":"assistant","content":"Привет!"}}]}""";

        Assert.Equal("Привет!", ChatGptClient.ParseChatResponse(json));
        Assert.Equal(string.Empty, ChatGptClient.ParseChatResponse("""{"choices":[]}"""));
        Assert.Equal(string.Empty, ChatGptClient.ParseChatResponse("не json"));
    }

    [Fact]
    public void Gpt_ЗапросГенерацииСодержитПараметры()
    {
        var json = ChatGptClient.BuildGenerationRequestJson("gpt-image-2", "кот в сапогах", "1024x1024", "high");

        Assert.Contains("\"model\":\"gpt-image-2\"", json);
        Assert.Contains("\"size\":\"1024x1024\"", json);
        Assert.Contains("\"quality\":\"high\"", json);
        Assert.Contains("\"response_format\":\"b64_json\"", json);
        Assert.DoesNotContain("\"images\"", json);
    }

    [Fact]
    public void Gpt_ЗапросПравкиСодержитВсеРеференсы()
    {
        var first = ChatGptClient.BuildDataUrl("image/png", PngBytes);
        var second = ChatGptClient.BuildDataUrl("image/jpeg", JpegBytes);
        var json = ChatGptClient.BuildEditRequestJson("gpt-image-2", "объедини", [first, second], "auto", "auto");

        Assert.Contains(first, json);
        Assert.Contains(second, json);
        Assert.Contains("\"image_url\"", json);
    }

    [Fact]
    public void Gpt_КартинкаИзвлекаетсяИзОтвета()
    {
        var b64 = Convert.ToBase64String(PngBytes);
        var json = $$"""{"created":1,"data":[{"b64_json":"{{b64}}","revised_prompt":"улучшенный промпт"}]}""";

        var image = ChatGptClient.ParseImageResponse(json);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image.Content);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("улучшенный промпт", image.RevisedPrompt);

        Assert.Null(ChatGptClient.ParseImageResponse("""{"data":[]}"""));
        Assert.Null(ChatGptClient.ParseImageResponse("не json"));
    }

    [Fact]
    public void Gpt_ИсторияОбрезаетсяДоЛимита()
    {
        var session = new ChatGptSession();

        for (var i = 0; i < ChatGptClient.MaxHistoryTurns + 10; i++)
        {
            session.Append(new ChatGptClient.ChatTurn("user", $"сообщение {i}", []));
        }

        Assert.Equal(ChatGptClient.MaxHistoryTurns, session.History.Count);

        // Вытеснены самые старые, свежие остались
        Assert.Equal("сообщение 10", session.History[0].Text);
    }

    [Fact]
    public void Gpt_СбросСессииЧиститВсё()
    {
        var session = new ChatGptSession();
        session.Append(new ChatGptClient.ChatTurn("user", "привет", []));
        session.LastImage = new ChatGptClient.GeneratedImage(PngBytes, "image/png", null);
        session.LastReferences = [new ChatGptClient.InputFile("cat.png", PngBytes)];

        session.Reset();

        Assert.Empty(session.History);
        Assert.False(session.HasImage);
        Assert.Empty(session.LastReferences);
    }

    [Fact]
    public void Gpt_SystemPromptПопадаетВЗапрос()
    {
        var turn = new ChatGptClient.ChatTurn("user", "hi", []);

        var withPrompt = ChatGptClient.BuildChatRequestJson("gpt-5.5", 100, [], turn, "system text");
        Assert.Contains("\"role\":\"system\"", withPrompt);
        Assert.Contains("system text", withPrompt);

        var withoutPrompt = ChatGptClient.BuildChatRequestJson("gpt-5.5", 100, [], turn);
        Assert.DoesNotContain("\"role\":\"system\"", withoutPrompt);
    }

    [Fact]
    public void Gpt_ExtraReferencesПопадаютВПравку()
    {
        var lastImage = new ChatGptClient.GeneratedImage(PngBytes, "image/png", null);
        var original = new ChatGptClient.InputFile("orig.png", PngBytes, "image/png");
        var extra = new ChatGptClient.InputFile("extra.jpg", JpegBytes, "image/jpeg");

        // Последняя картинка + доп-референс; исходные не включены
        var urls = ChatGptClient.CollectEditDataUrls(lastImage, [original], [extra], includeOriginalReferences: false);
        Assert.Equal(2, urls.Count);
        Assert.StartsWith("data:image/png", urls[0]);
        Assert.StartsWith("data:image/jpeg", urls[1]);

        // С исходными — все три
        var withOriginals = ChatGptClient.CollectEditDataUrls(lastImage, [original], [extra], includeOriginalReferences: true);
        Assert.Equal(3, withOriginals.Count);
    }

    [Fact]
    public void Gpt_СсылкаЛогинаРазбирается()
    {
        const string json = """{"status":"ok","url":"https://auth.openai.com/oauth/authorize?x=1","state":"abc123"}""";
        var start = ChatGptClient.ParseLoginStartResponse(json);

        Assert.NotNull(start);
        Assert.Equal("https://auth.openai.com/oauth/authorize?x=1", start.Url);
        Assert.Equal("abc123", start.State);

        Assert.Null(ChatGptClient.ParseLoginStartResponse("""{"status":"ok"}"""));
        Assert.Null(ChatGptClient.ParseLoginStartResponse("не json"));
    }

    [Theory]
    [InlineData("http://localhost:1455/auth/callback?code=ac_XXX&state=st_YYY", "st_YYY")]
    [InlineData("http://localhost:1455/auth/callback?state=st%2FYYY&code=ac", "st/YYY")]
    [InlineData("http://localhost:1455/auth/callback?code=ac_XXX", null)]
    [InlineData("http://localhost:1455/auth/callback?state=st_YYY", null)]
    [InlineData("просто текст без ссылки", null)]
    public void Gpt_StateИзвлекаетсяИзRedirectUrl(string url, string? expected)
    {
        Assert.Equal(expected, ChatGptClient.ExtractStateFromRedirectUrl(url));
    }

    [Fact]
    public void Gpt_CallbackJsonСодержитProviderИUrl()
    {
        var json = ChatGptClient.BuildOAuthCallbackJson("http://localhost:1455/auth/callback?code=1&state=2");

        Assert.Contains("\"provider\":\"codex\"", json);
        Assert.Contains("\"redirect_url\":\"http://localhost:1455/auth/callback?code=1\\u0026state=2\"", json);
    }

    [Fact]
    public void Gpt_СтатусАвторизацииРазбирается()
    {
        Assert.Equal("ok", ChatGptClient.ParseAuthStatusResponse("""{"status":"ok"}""").Status);
        Assert.Equal("wait", ChatGptClient.ParseAuthStatusResponse("""{"status":"wait"}""").Status);

        var error = ChatGptClient.ParseAuthStatusResponse("""{"status":"error","error":"unknown or expired state"}""");
        Assert.Equal("error", error.Status);
        Assert.Equal("unknown or expired state", error.Error);

        Assert.Equal("error", ChatGptClient.ParseAuthStatusResponse("не json").Status);
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

        var accounts = ChatGptClient.ParseAuthFilesResponse(json);

        Assert.Equal(2, accounts.Count);
        Assert.Equal("u@e.com", accounts[0].Email);
        Assert.False(accounts[0].Unavailable);
        Assert.True(accounts[1].Unavailable);
        Assert.Equal("token expired", accounts[1].StatusMessage);

        Assert.Empty(ChatGptClient.ParseAuthFilesResponse("не json"));
    }

    // ====================================================================
    // Живые тесты: нужен поднятый CLIProxyAPI и UseChatGpt: true в config.ini
    // ====================================================================

    /// <summary>
    /// Живой список аккаунтов через management API (нужен MANAGEMENT_PASSWORD у прокси).
    /// </summary>
    [Fact]
    public async Task АИ_ГптСписокАккаунтов()
    {
        var accounts = await ChatGptClient.GetAccountsAsync();

        Assert.NotNull(accounts);
        _testOutputHelper.WriteLine($"Аккаунтов: {accounts.Count}");

        foreach (var account in accounts)
        {
            _testOutputHelper.WriteLine($"• {account.Name} {account.Email} unavailable={account.Unavailable}");
        }
    }

    /// <summary>
    /// Живой чат через прокси: короткий вопрос — непустой ответ.
    /// </summary>
    [Fact]
    public async Task АИ_ГптЧатОтвечает()
    {
        var session = new ChatGptSession();
        var reply = await ChatGptClient.ChatAsync(session, "Ответь одним словом: столица Франции?");

        _testOutputHelper.WriteLine($"Ответ: {reply}");
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.Equal(2, session.History.Count);
    }

    /// <summary>
    /// Живая генерация: маленькая картинка с нуля.
    /// </summary>
    [Fact]
    public async Task АИ_ГптГенерируетКартинку()
    {
        var session = new ChatGptSession();
        var image = await ChatGptClient.GenerateImageAsync(session, "Простая иконка кота, минимализм");

        _testOutputHelper.WriteLine($"Картинка: {image?.MimeType}, {image?.Content.Length} байт");
        Assert.NotNull(image);
        Assert.True(image.Content.Length > 0);
        Assert.True(session.HasImage);
    }

    /// <summary>
    /// Живая правка в той же сессии: генерация, затем изменение без новой сессии.
    /// </summary>
    [Fact]
    public async Task АИ_ГптПравитКартинкуВСессии()
    {
        var session = new ChatGptSession();
        var first = await ChatGptClient.GenerateImageAsync(session, "Простая иконка кота, минимализм");
        Assert.NotNull(first);

        var second = await ChatGptClient.ContinueImageAsync(session, "Сделай кота рыжим");

        _testOutputHelper.WriteLine($"Правка: {second?.MimeType}, {second?.Content.Length} байт");
        Assert.NotNull(second);
        Assert.NotEqual(first.Content, second.Content);
        Assert.Same(second, session.LastImage);
    }
}
