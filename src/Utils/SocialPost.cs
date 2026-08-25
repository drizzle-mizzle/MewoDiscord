using MewoDiscord.Helpers;

namespace MewoDiscord.Utils;

/// <summary>
/// Медиафайл поста соцсети.
/// </summary>
public record SocialMedia(string Url, bool IsVideo, string? ThumbnailUrl);

/// <summary>
/// Разобранный пост: автор, текст, медиа и время публикации. Вид общий для всех источников —
/// дальше с ним работает PostMediaHandler, которому уже всё равно, из Telegram пост или из X.
/// Логин автора лежит отдельно от имени: подписью ссылки идёт он, а имя — строкой ниже.
/// Логин из латиницы и цифр, и ссылка с ним рисуется всегда; имя бывает каким угодно,
/// вплоть до одних эмодзи, с которыми Discord ссылку не рисует вовсе.
/// </summary>
public record SocialPost(
    string? AuthorName,
    string? AuthorHandle,
    string? Caption,
    IReadOnlyList<SocialMedia> Media,
    DateTimeOffset? PublishedAt);

/// <summary>
/// Скачанный медиафайл. Content == null означает, что файл не влез в лимит Discord.
/// </summary>
public record MediaDownload(MemoryStream? Content, long SizeBytes);

/// <summary>
/// Общая работа с сетью для постов соцсетей: один HttpClient на всех, скачивание с потолком
/// и запрос размера без тела.
/// </summary>
public static class SocialMediaHttp
{
    private const int RequestTimeoutSeconds = 20;

    /// <summary>
    /// И Telegram, и X отдают свои страницы-виджеты только «браузерным» клиентам.
    /// </summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    public static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Скачивает медиа, если оно укладывается в maxBytes.
    /// Возвращает null при ошибке сети; при превышении лимита — размер без содержимого.
    /// </summary>
    public static async Task<MediaDownload?> TryDownloadAsync(string url, ulong maxBytes)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var declaredSize = response.Content.Headers.ContentLength;

            if (declaredSize > (long)maxBytes)
            {
                return new MediaDownload(null, declaredSize.Value);
            }

            await using var source = await response.Content.ReadAsStreamAsync();
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;

            while (true)
            {
                var read = await source.ReadAsync(chunk);

                if (read == 0)
                {
                    break;
                }

                total += read;

                // Длина могла не прийти в заголовках — обрываем по факту
                if (total > (long)maxBytes)
                {
                    await buffer.DisposeAsync();
                    return new MediaDownload(null, total);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read));
            }

            buffer.Position = 0;
            return new MediaDownload(buffer, total);
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось скачать медиа {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Размер файла без скачивания. Нужен там, где из лесенки качеств надо выбрать
    /// подходящее до траты байтов. null — сервер размера не назвал или не ответил.
    /// </summary>
    public static async Task<long?> TryGetSizeAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            return response.Content.Headers.ContentLength;
        }
        catch (Exception ex)
        {
            BotLogger.Warning("Не удалось узнать размер {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }
}
