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

        // Имена картинок модель видит строкой шапки: сами файлы уходят отдельными частями
        Assert.Equal("[приложил изображения: cat.png]\nЧто на картинке?", turn.Text);
        Assert.Single(turn.ImageDataUrls);
        Assert.StartsWith("data:image/png;base64,", turn.ImageDataUrls[0]);
    }

    [Fact]
    public void Gpt_ШапкаСообщенияСобираетсяПоФормату()
    {
        var turn = ChatGptClient.PrepareUserTurn(
            "@bot добавь ей шляпку",
            [
                new ChatGptClient.InputFile("cat1.png", PngBytes),
                new ChatGptClient.InputFile("cat2.png", PngBytes)
            ],
            new ChatGptClient.ChatContext("bot", "user2", "user1", "смотрите, моя кошка"));

        Assert.Equal(
            "[user2]\n[quotes user1: \"смотрите, моя кошка\"]\n[приложил изображения: cat1.png, cat2.png]\n@bot добавь ей шляпку",
            turn.Text);

        Assert.Equal(2, turn.ImageDataUrls.Count);
    }

    [Fact]
    public void Gpt_СвояКартинкаВытесняетПрошлуюСгенерированную()
    {
        var session = new ChatGptSession
        {
            LastImage = new ChatGptClient.GeneratedImage(PngBytes, "image/png", null)
        };

        // Без новых картинок прошлая подмешивается — иначе не поправить нарисованное
        var edit = ChatGptClient.PrepareUserTurn("сделай его рыжим", null);
        Assert.StartsWith("data:image/png;base64,", ChatGptClient.ResolveCarryImage(session, edit));

        // Пользователь принёс свою картинку — предмет разговора теперь она
        var withOwn = ChatGptClient.PrepareUserTurn("а этой добавь ушки", [new ChatGptClient.InputFile("gif.png", PngBytes)]);
        Assert.Null(ChatGptClient.ResolveCarryImage(session, withOwn));

        // Пустая сессия — подмешивать нечего
        Assert.Null(ChatGptClient.ResolveCarryImage(new ChatGptSession(), edit));
    }

    [Fact]
    public void Gpt_ШапкаБезЦитатыИКартинокТолькоАвтор()
    {
        var turn = ChatGptClient.PrepareUserTurn("@bot привет!", null, new ChatGptClient.ChatContext("bot", "user1"));

        Assert.Equal("[user1]\n@bot привет!", turn.Text);
    }

    [Fact]
    public void Gpt_ДлиннаяЦитатаУжимаетсяВОднуСтроку()
    {
        var quote = new string('я', ChatGptClient.MaxQuotedLength + 50);
        var turn = ChatGptClient.PrepareUserTurn("похвали", null, new ChatGptClient.ChatContext("bot", "user2", "user1", $"первая строка\nвторая {quote}"));

        var header = turn.Text.Split('\n')[1];

        Assert.StartsWith("[quotes user1: \"первая строка вторая", header);
        Assert.EndsWith("…\"]", header);
    }

    [Fact]
    public void Gpt_СистемныйПромптЗнаетИмяБотаИФормат()
    {
        var prompt = ChatGptClient.BuildSystemPrompt("REAL NEKO");

        Assert.Contains("Тебя зовут REAL NEKO", prompt);
        Assert.Contains("[quotes имя:", prompt);
        Assert.Contains("@имя", prompt);

        // Про Discord модели знать незачем — лишний повод для трактовок
        Assert.DoesNotContain("Discord", prompt, StringComparison.OrdinalIgnoreCase);

        // Имя не определилось — промпт всё равно осмысленный
        Assert.Contains("Тебя зовут bot", ChatGptClient.BuildSystemPrompt(null));
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

        // Уровень рассуждений не задан — поля в запросе нет, и бэкенд берёт свой
        // по умолчанию. Так уходят служебные запросы кастомных действий
        Assert.DoesNotContain("reasoning_effort", json);
    }

    [Fact]
    public void Gpt_УровеньРассужденийУходитВЗапрос()
    {
        var turn = new ChatGptClient.ChatTurn("user", "привет", []);
        var json = ChatGptClient.BuildChatRequestJson("gpt-5.5", 2048, [], turn, reasoningEffort: "high");

        Assert.Contains("\"reasoning_effort\":\"high\"", json);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData("HIGH", "high")]
    [InlineData("  medium  ", "medium")]
    [InlineData("minimal", "minimal")]
    [InlineData("xhigh", null)]
    [InlineData("выключено", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Gpt_НеизвестныйУровеньРассужденийНеОтправляется(string? effort, string? expected)
    {
        // Неизвестный уровень бэкенд отвергает целиком, и вместо ответа пользователь
        // получил бы ошибку — поэтому всё незнакомое просто не отправляем
        Assert.Equal(expected, ChatGptClient.NormalizeEffort(effort));
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

        var reply = ChatGptClient.ParseChatResponse(json);
        Assert.Equal("Привет!", reply.Text);
        Assert.Empty(reply.Images);

        Assert.Equal(string.Empty, ChatGptClient.ParseChatResponse("""{"choices":[]}""").Text);
        Assert.Equal(string.Empty, ChatGptClient.ParseChatResponse("не json").Text);
    }

    [Fact]
    public void Gpt_КартинкиИзОтветаЧатаИзвлекаются()
    {
        // Прокси кладёт нарисованное моделью в message.images, а не в content
        var dataUrl = ChatGptClient.BuildDataUrl("image/png", PngBytes);
        var json = """{"choices":[{"message":{"role":"assistant","content":"Готово","images":[{"type":"image_url","index":0,"image_url":{"url":"URL"}}]}}]}"""
            .Replace("URL", dataUrl);

        var reply = ChatGptClient.ParseChatResponse(json);

        Assert.Equal("Готово", reply.Text);
        Assert.Single(reply.Images);
        Assert.Equal(PngBytes, reply.Images[0].Content);
        Assert.Equal("image/png", reply.Images[0].MimeType);
    }

    [Fact]
    public void Gpt_БитыйDataUrlПропускается()
    {
        Assert.Null(ChatGptClient.ParseImageDataUrl(null));
        Assert.Null(ChatGptClient.ParseImageDataUrl("https://example.com/cat.png"));
        Assert.Null(ChatGptClient.ParseImageDataUrl("data:image/png,без-base64"));
        Assert.Null(ChatGptClient.ParseImageDataUrl("data:image/png;base64,не-base64!"));
        Assert.NotNull(ChatGptClient.ParseImageDataUrl(ChatGptClient.BuildDataUrl("image/png", PngBytes)));
    }

    [Fact]
    public void Gpt_ПоследняяКартинкаПодмешиваетсяВЗапрос()
    {
        var turn = new ChatGptClient.ChatTurn("user", "сделай его рыжим", []);
        var carry = ChatGptClient.BuildDataUrl("image/png", PngBytes);

        // Без carry картинок в запросе нет
        Assert.DoesNotContain("image_url", ChatGptClient.BuildChatRequestJson("gpt-5.5", 100, [], turn));

        // С carry — уходит частью текущего хода
        var json = ChatGptClient.BuildChatRequestJson("gpt-5.5", 100, [], turn, null, carry);
        Assert.Contains("\"type\":\"image_url\"", json);
        Assert.Contains(carry, json);
    }

    [Fact]
    public void Gpt_АссистентскийХодОписываетКартинки()
    {
        var image = new ChatGptClient.GeneratedImage(PngBytes, "image/png", null);

        // Только текст — как есть
        Assert.Equal("привет", ChatGptClient.BuildAssistantTurnText(new ChatGptClient.ChatReply("привет", [])));

        // Картинка без текста — пометка вместо байтов
        Assert.Equal("[сгенерировано изображение]", ChatGptClient.BuildAssistantTurnText(new ChatGptClient.ChatReply(string.Empty, [image])));

        // Текст и картинки — и то, и другое
        var mixed = ChatGptClient.BuildAssistantTurnText(new ChatGptClient.ChatReply("готово", [image, image]));
        Assert.Contains("готово", mixed);
        Assert.Contains("изображений: 2", mixed);
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
    public void Gpt_КартинкиОстаютсяТолькоУПоследнихХодов()
    {
        var session = new ChatGptSession();

        for (var i = 0; i < ChatGptClient.MaxImageTurns + 3; i++)
        {
            session.Append(new ChatGptClient.ChatTurn("user", $"ход {i}", ["data:image/png;base64,AAA"]));
        }

        var withImages = session.History.Count(turn => turn.ImageDataUrls.Count > 0);

        Assert.Equal(ChatGptClient.MaxImageTurns, withImages);

        // Картинки сняты со старых ходов, а сами ходы и их текст на месте
        Assert.Empty(session.History[0].ImageDataUrls);
        Assert.Equal("ход 0", session.History[0].Text);
        Assert.NotEmpty(session.History[^1].ImageDataUrls);
    }

    [Fact]
    public void Gpt_ЗапросНеТащитКартинкиИзДавнейИстории()
    {
        var session = new ChatGptSession();

        // Метки латиницей: сериализатор экранирует кириллицу в \uXXXX, и поиск
        // по подстроке ничего бы не нашёл ни в одном случае
        for (var i = 0; i < ChatGptClient.MaxImageTurns + 2; i++)
        {
            session.Append(new ChatGptClient.ChatTurn("user", $"ход {i}", [$"data:image/png;base64,IMG{i}"]));
        }

        var json = ChatGptClient.BuildChatRequestJson(
            "gpt-5.5", 100, session.History, new ChatGptClient.ChatTurn("user", "новый вопрос", []));

        Assert.DoesNotContain("IMG0", json);
        Assert.DoesNotContain("IMG1", json);
        Assert.Contains("IMG5", json);
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(2000)]
    public void Gpt_ТекстВЛимитНеРежется(int length)
    {
        var chunks = BotLogger.SplitMessage(new string('я', length));

        Assert.Single(chunks);
        Assert.Equal(length, chunks[0].Length);
    }

    [Fact]
    public void Gpt_ТекстБезПереносовРежетсяПоЛимиту()
    {
        var chunks = BotLogger.SplitMessage(new string('я', 2001));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(2000, chunks[0].Length);
        Assert.Single(chunks[1]);
    }

    [Fact]
    public void Gpt_ДлинныйТекстРежетсяПоПоследнемуПереносу()
    {
        var text = new string('а', 1500) + "\n" + new string('б', 600);

        var chunks = BotLogger.SplitMessage(text);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(1500, chunks[0].Length);
        Assert.Equal(600, chunks[1].Length);
    }

    [Fact]
    public void Gpt_ЧанкиОтветаВлезаютВЛимитИНеТеряютТекст()
    {
        // Сплошные переносы — вырожденный случай: TrimStart их съедает, но зациклиться
        // и выдать пустой чанк резка не должна ни при каких входных данных
        var text = new string('\n', 2500) + "конец";

        var chunks = BotLogger.SplitMessage(text);

        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 2000));
        Assert.Contains(chunks, chunk => chunk.Contains("конец"));
    }

    [Fact]
    public void Gpt_СбросСессииЧиститВсё()
    {
        var session = new ChatGptSession();
        session.Append(new ChatGptClient.ChatTurn("user", "привет", []));
        session.LastImage = new ChatGptClient.GeneratedImage(PngBytes, "image/png", null);

        session.Reset();

        Assert.Empty(session.History);
        Assert.False(session.HasImage);
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


    // ====================================================================
    // Живые тесты: нужен поднятый CLIProxyAPI и UseChatGpt: true в config.ini
    // ====================================================================

    /// <summary>
    /// Живой чат через прокси: короткий вопрос — непустой ответ.
    /// </summary>
    [Fact]
    public async Task АИ_ГптЧатОтвечает()
    {
        var session = new ChatGptSession();
        var reply = await ChatGptClient.ChatAsync(session, "Ответь одним словом: столица Франции?");

        _testOutputHelper.WriteLine($"Ответ: {reply.Text}");
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
        Assert.Equal(2, session.History.Count);
    }

    /// <summary>
    /// Живая генерация: модель сама переходит в режим рисования по просьбе в чате.
    /// </summary>
    [Fact]
    public async Task АИ_ГптРисуетПоПросьбеВЧате()
    {
        var session = new ChatGptSession();
        var reply = await ChatGptClient.ChatAsync(session, "Нарисуй простую иконку кота, минимализм");

        _testOutputHelper.WriteLine($"Текст: {reply.Text}; картинок: {reply.Images.Count}");
        Assert.NotEmpty(reply.Images);
        Assert.True(session.HasImage);
    }

    /// <summary>
    /// Живая правка в той же сессии: нарисованное продолжает править без новой сессии.
    /// </summary>
    [Fact]
    public async Task АИ_ГптПравитКартинкуВСессии()
    {
        var session = new ChatGptSession();
        var first = await ChatGptClient.ChatAsync(session, "Нарисуй простую иконку кота, минимализм");
        Assert.NotEmpty(first.Images);

        var second = await ChatGptClient.ChatAsync(session, "Сделай кота рыжим");

        _testOutputHelper.WriteLine($"Правка: картинок {second.Images.Count}");
        Assert.NotEmpty(second.Images);
        Assert.NotEqual(first.Images[0].Content, second.Images[0].Content);
    }
}
