using System.Text.Json;
using MewoDiscord.Utils;

namespace MewoDiscord.Tests;

/// <summary>
/// Автономные тесты Responses API: сеть и реальный прокси не нужны.
/// </summary>
public class ChatGptResponsesTests
{
    private static readonly byte[] _pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Gpt_ResponsesЗапросСодержитПоискИГенерацию()
    {
        var turn = new ChatGptClient.ChatTurn("user", "Что нового?", []);
        var json = ChatGptClient.BuildResponsesRequestJson(
            "gpt-6-luna", 4096, [], turn, "system text", reasoningEffort: "high");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
        Assert.Equal(4096, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("system text", root.GetProperty("instructions").GetString());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());

        var tools = root.GetProperty("tools").EnumerateArray()
            .Select(item => item.GetProperty("type").GetString())
            .ToArray();

        Assert.Equal(2, tools.Length);
        Assert.Contains("web_search", tools);
        Assert.Contains("image_generation", tools);
    }

    [Fact]
    public void Gpt_ResponsesИсторияИКартинкиКонвертируются()
    {
        var ownImage = ChatGptClient.BuildDataUrl("image/png", _pngBytes);
        var carryImage = ChatGptClient.BuildDataUrl("image/png", [.. _pngBytes, 0x01]);
        var history = new List<ChatGptClient.ChatTurn>
        {
            new("user", "первый вопрос", []),
            new("assistant", "первый ответ", [])
        };
        var current = new ChatGptClient.ChatTurn("user", "новый вопрос", [ownImage]);

        var json = ChatGptClient.BuildResponsesRequestJson(
            "gpt-6-luna", 2048, history, current, carryImageDataUrl: carryImage);

        using var document = JsonDocument.Parse(json);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToArray();

        Assert.Equal(3, input.Length);
        Assert.Equal(JsonValueKind.String, input[0].GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.String, input[1].GetProperty("content").ValueKind);

        var currentParts = input[2].GetProperty("content").EnumerateArray().ToArray();

        Assert.Equal(3, currentParts.Length);
        Assert.Equal("input_text", currentParts[0].GetProperty("type").GetString());
        Assert.Equal("input_image", currentParts[1].GetProperty("type").GetString());
        Assert.Equal(ownImage, currentParts[1].GetProperty("image_url").GetString());
        Assert.Equal(carryImage, currentParts[2].GetProperty("image_url").GetString());
    }

    [Fact]
    public void Gpt_ResponsesТекстПоискИЦитатаИзвлекаются()
    {
        const string json = """
            {"output":[
              {"type":"web_search_call","status":"completed"},
              {"type":"message","content":[{
                "type":"output_text",
                "text":"Astra launched today.",
                "annotations":[{
                  "type":"url_citation",
                  "start_index":0,
                  "end_index":5,
                  "url":"https://openai.com/news",
                  "title":"OpenAI"
                }]
              }]}
            ]}
            """;

        var result = ChatGptClient.ParseResponsesResponse(json);

        Assert.True(result.SearchUsed);
        Assert.Equal("Astra ([OpenAI](https://openai.com/news)) launched today.", result.Reply.Text);
        Assert.Empty(result.Reply.Images);
    }

    [Fact]
    public void Gpt_ResponsesЦитатаПослеЭмодзиВстаётПоCodePoint()
    {
        var annotations = new List<ChatGptClient.ResponsesAnnotation>
        {
            new()
            {
                Type = "url_citation",
                StartIndex = 2,
                EndIndex = 7,
                Url = "https://example.com/source",
                Title = "source"
            }
        };

        var rendered = ChatGptClient.RenderResponseCitations("😀 Astra", annotations);

        Assert.Equal("😀 Astra ([source](https://example.com/source))", rendered);
    }

    [Fact]
    public void Gpt_ResponsesБитыйДиапазонЦитатыДаётСписокИсточников()
    {
        var annotations = new List<ChatGptClient.ResponsesAnnotation>
        {
            new()
            {
                Type = "url_citation",
                StartIndex = 90,
                EndIndex = 100,
                Url = "https://example.com/source",
                Title = "source"
            }
        };

        var rendered = ChatGptClient.RenderResponseCitations("Ответ", annotations);

        Assert.StartsWith("Ответ\n\nИсточники:", rendered);
        Assert.Contains("[источник](https://example.com/source)", rendered);
    }

    [Fact]
    public void Gpt_ResponsesКартинкаИзвлекаетсяИзBase64()
    {
        var json = JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                new
                {
                    type = "image_generation_call",
                    result = Convert.ToBase64String(_pngBytes),
                    output_format = "png",
                    revised_prompt = "minimal cat"
                }
            }
        });

        var result = ChatGptClient.ParseResponsesResponse(json);

        Assert.False(result.SearchUsed);
        Assert.Equal(string.Empty, result.Reply.Text);
        var image = Assert.Single(result.Reply.Images);
        Assert.Equal(_pngBytes, image.Content);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("minimal cat", image.RevisedPrompt);
    }

    [Fact]
    public void Gpt_ResponsesБитаяКартинкаНеЗатираетТекст()
    {
        const string json = """
            {"output":[
              {"type":"image_generation_call","result":"not-base64"},
              {"type":"message","content":[{"type":"output_text","text":"Не получилось"}]}
            ]}
            """;

        var result = ChatGptClient.ParseResponsesResponse(json);

        Assert.Equal("Не получилось", result.Reply.Text);
        Assert.Empty(result.Reply.Images);
    }

    [Fact]
    public void Gpt_ResponsesПустойИБитыйОтветБезопасны()
    {
        var malformed = ChatGptClient.ParseResponsesResponse("не json");
        var empty = ChatGptClient.ParseResponsesResponse("""{"output":[]}""");
        var fallback = ChatGptClient.ParseResponsesResponse("""{"output_text":"короткий ответ"}""");

        Assert.Equal(string.Empty, malformed.Reply.Text);
        Assert.False(malformed.SearchUsed);
        Assert.Equal(string.Empty, empty.Reply.Text);
        Assert.Equal("короткий ответ", fallback.Reply.Text);
    }
}
