using System.Text.Json;
using System.Text.Json.Serialization;

namespace MewoDiscord.Utils;

/// <summary>
/// Responses API для обычного чата со встроенными web_search и image_generation.
/// Файл выделен только как формат провода: сессия и запасной Chat Completions остаются
/// в основной части <see cref="ChatGptClient"/>.
/// </summary>
public static partial class ChatGptClient
{
    private const string ResponsesPath = "/v1/responses";

    /// <summary>
    /// Собирает Responses-запрос. Системный промпт уходит в instructions, история —
    /// обычными сообщениями, а картинки текущего хода — частями input_image.
    /// </summary>
    internal static string BuildResponsesRequestJson(
        string model,
        int maxTokens,
        IReadOnlyList<ChatTurn> history,
        ChatTurn userTurn,
        string? systemPrompt = null,
        string? carryImageDataUrl = null,
        string? reasoningEffort = null)
    {
        var input = new List<ResponsesInputMessage>();

        foreach (var turn in history)
        {
            input.Add(ToResponsesInputMessage(turn));
        }

        var current = carryImageDataUrl != null && userTurn.ImageDataUrls.Count < MaxImagesPerRequest
            ? userTurn with { ImageDataUrls = [.. userTurn.ImageDataUrls, carryImageDataUrl] }
            : userTurn;

        input.Add(ToResponsesInputMessage(current));

        var effort = NormalizeEffort(reasoningEffort);
        var request = new ResponsesApiRequest
        {
            Model = model,
            MaxOutputTokens = maxTokens,
            Instructions = string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
            Reasoning = effort == null ? null : new ResponsesReasoning { Effort = effort },
            Input = input,
            Tools =
            [
                new ResponsesTool { Type = "web_search" },
                new ResponsesTool { Type = "image_generation" }
            ],
            ToolChoice = "auto",
            Stream = false,
            Store = false
        };

        return JsonSerializer.Serialize(request, _jsonOptions);
    }

    /// <summary>
    /// Разбирает сообщения, вызовы поиска и картинки Responses API. Цитаты из аннотаций
    /// превращаются в кликабельные ссылки Discord, а не остаются невидимыми метаданными.
    /// </summary>
    internal static ResponsesParseResult ParseResponsesResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ResponsesApiResponse>(json, _jsonOptions);
            var textParts = new List<string>();
            var images = new List<GeneratedImage>();
            var searchUsed = false;

            foreach (var item in response?.Output ?? [])
            {
                if (item.Type == "web_search_call")
                {
                    searchUsed = true;
                }

                if (item.Type == "image_generation_call")
                {
                    var image = ParseResponsesImage(item);

                    if (image != null)
                    {
                        images.Add(image);
                    }
                }

                if (item.Type != "message")
                {
                    continue;
                }

                foreach (var part in item.Content ?? [])
                {
                    if (part.Type == "output_text" && !string.IsNullOrEmpty(part.Text))
                    {
                        textParts.Add(RenderResponseCitations(part.Text, part.Annotations));
                    }
                }
            }

            if (textParts.Count == 0 && !string.IsNullOrEmpty(response?.OutputText))
            {
                textParts.Add(response.OutputText);
            }

            var reply = new ChatReply(string.Join('\n', textParts), images);

            return new ResponsesParseResult(reply, searchUsed);
        }
        catch (JsonException)
        {
            return new ResponsesParseResult(_emptyReply, false);
        }
    }

    /// <summary>
    /// Переводит диапазоны url_citation в Markdown-ссылки. Индексы OpenAI относятся
    /// к Unicode code points, поэтому перед вставкой переводим их в индексы UTF-16.
    /// </summary>
    internal static string RenderResponseCitations(string text, IReadOnlyList<ResponsesAnnotation>? annotations)
    {
        var citations = new List<ResponseCitation>();

        foreach (var annotation in annotations ?? [])
        {
            if (annotation.Type != "url_citation"
                || annotation.StartIndex == null
                || annotation.EndIndex == null
                || annotation.StartIndex > annotation.EndIndex
                || !Uri.TryCreate(annotation.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            citations.Add(new ResponseCitation(
                annotation.StartIndex.Value,
                annotation.EndIndex.Value,
                annotation.Title,
                uri.AbsoluteUri));
        }

        var rendered = text;
        var inserted = false;

        foreach (var citation in citations
                     .OrderByDescending(item => item.EndIndex)
                     .ThenByDescending(item => item.StartIndex))
        {
            var start = CodePointToUtf16Index(rendered, citation.StartIndex);
            var end = CodePointToUtf16Index(rendered, citation.EndIndex);

            if (start < 0 || end < start || rendered.Contains(citation.Url, StringComparison.Ordinal))
            {
                continue;
            }

            var link = $" ([{EscapeMarkdownLabel(citation.Title)}]({EscapeMarkdownUrl(citation.Url)}))";
            rendered = rendered.Insert(end, link);
            inserted = true;
        }

        if (inserted || citations.Count == 0)
        {
            return rendered;
        }

        // Битые диапазоны не должны лишать пользователя источников совсем.
        var sources = citations
            .Select(item => item.Url)
            .Distinct(StringComparer.Ordinal)
            .Select(url => $"[источник]({EscapeMarkdownUrl(url)})");

        return rendered + "\n\nИсточники: " + string.Join(", ", sources);
    }

    /// <summary>
    /// Сообщение в формат Responses: строка для текста и мультимодальные части для
    /// пользовательских картинок. Ассистентские картинки в истории не хранятся.
    /// </summary>
    private static ResponsesInputMessage ToResponsesInputMessage(ChatTurn turn)
    {
        if (turn.ImageDataUrls.Count == 0)
        {
            return new ResponsesInputMessage { Role = turn.Role, Content = turn.Text };
        }

        var parts = new List<ResponsesInputContentPart>();

        if (!string.IsNullOrEmpty(turn.Text))
        {
            parts.Add(new ResponsesInputContentPart { Type = "input_text", Text = turn.Text });
        }

        foreach (var url in turn.ImageDataUrls)
        {
            parts.Add(new ResponsesInputContentPart { Type = "input_image", ImageUrl = url });
        }

        return new ResponsesInputMessage { Role = turn.Role, Content = parts };
    }

    /// <summary>
    /// Достаёт base64 картинки из image_generation_call. Один битый результат не должен
    /// обнулять остальной ответ модели.
    /// </summary>
    private static GeneratedImage? ParseResponsesImage(ResponsesOutputItem item)
    {
        if (string.IsNullOrEmpty(item.Result))
        {
            return null;
        }

        try
        {
            var content = Convert.FromBase64String(item.Result);

            if (content.Length == 0)
            {
                return null;
            }

            var mime = DetectImageMime(content) ?? ImageMimeFromFormat(item.OutputFormat);

            return new GeneratedImage(content, mime, item.RevisedPrompt);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Индекс Unicode code point → индекс UTF-16. -1 означает выход за строку.
    /// </summary>
    private static int CodePointToUtf16Index(string text, int codePointIndex)
    {
        if (codePointIndex < 0)
        {
            return -1;
        }

        var current = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (current == codePointIndex)
            {
                return i;
            }

            if (char.IsHighSurrogate(text[i])
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            current++;
        }

        return current == codePointIndex ? text.Length : -1;
    }

    private static string EscapeMarkdownLabel(string? title)
    {
        var label = string.IsNullOrWhiteSpace(title) ? "источник" : title.ReplaceLineEndings(" ").Trim();
        label = label.Replace("[", "(").Replace("]", ")");

        return label.Length <= 100 ? label : label[..100] + "…";
    }

    private static string EscapeMarkdownUrl(string url) => url.Replace("(", "%28").Replace(")", "%29");

    private static string ImageMimeFromFormat(string? format) => format?.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        _ => "image/png"
    };

    internal record ResponsesParseResult(ChatReply Reply, bool SearchUsed);

    private record ResponseCitation(int StartIndex, int EndIndex, string? Title, string Url);

    internal class ResponsesAnnotation
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("start_index")]
        public int? StartIndex { get; init; }

        [JsonPropertyName("end_index")]
        public int? EndIndex { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }

    private class ResponsesApiRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("max_output_tokens")]
        public required int MaxOutputTokens { get; init; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; init; }

        [JsonPropertyName("reasoning")]
        public ResponsesReasoning? Reasoning { get; init; }

        [JsonPropertyName("input")]
        public required List<ResponsesInputMessage> Input { get; init; }

        [JsonPropertyName("tools")]
        public required List<ResponsesTool> Tools { get; init; }

        [JsonPropertyName("tool_choice")]
        public required string ToolChoice { get; init; }

        [JsonPropertyName("stream")]
        public required bool Stream { get; init; }

        [JsonPropertyName("store")]
        public required bool Store { get; init; }
    }

    private class ResponsesReasoning
    {
        [JsonPropertyName("effort")]
        public required string Effort { get; init; }
    }

    private class ResponsesInputMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required object Content { get; init; }
    }

    private class ResponsesInputContentPart
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; init; }
    }

    private class ResponsesTool
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }
    }

    private class ResponsesApiResponse
    {
        [JsonPropertyName("output")]
        public List<ResponsesOutputItem>? Output { get; init; }

        [JsonPropertyName("output_text")]
        public string? OutputText { get; init; }
    }

    private class ResponsesOutputItem
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("content")]
        public List<ResponsesOutputContent>? Content { get; init; }

        [JsonPropertyName("result")]
        public string? Result { get; init; }

        [JsonPropertyName("revised_prompt")]
        public string? RevisedPrompt { get; init; }

        [JsonPropertyName("output_format")]
        public string? OutputFormat { get; init; }
    }

    private class ResponsesOutputContent
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("annotations")]
        public List<ResponsesAnnotation>? Annotations { get; init; }
    }
}
