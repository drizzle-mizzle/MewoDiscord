using System.Net;
using System.Net.Sockets;
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
    /// Потолок на всё скачивание медиа целиком, включая чтение тела: файл в лимит
    /// вложения приезжает за секунды, и всё, что идёт дольше, — это не медленный
    /// канал, а замолчавший сервер.
    /// </summary>
    private const int DownloadTimeoutSeconds = 120;

    /// <summary>
    /// Потолок ответа на запрос страницы или JSON: разбираем мы текст, а не файлы,
    /// и мегабайты тут означают, что нам отдают что-то не то. Скачивания медиа он
    /// не касается: те читаются потоком (ResponseHeadersRead), а буфер клиента
    /// применяется только к ответам, которые он вычитывает целиком сам.
    /// </summary>
    private const int MaxPageBytes = 10 * 1024 * 1024;

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
        if (!IsSafeUrl(url))
        {
            return null;
        }

        try
        {
            // Таймаут клиента покрывает только путь до заголовков: дальше мы читаем тело
            // потоком, а замолчавший CDN с живым соединением подвесил бы это чтение
            // навсегда — вместе с фоновой задачей ответа на пост
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(DownloadTimeoutSeconds));

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var declaredSize = response.Content.Headers.ContentLength;

            if (declaredSize > (long)maxBytes)
            {
                return new MediaDownload(null, declaredSize.Value);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;

            while (true)
            {
                var read = await source.ReadAsync(chunk, cts.Token);

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
        if (!IsSafeUrl(url))
        {
            return null;
        }

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

    /// <summary>
    /// Можно ли идти по этому адресу. Адреса медиа приходят из чужого HTML и JSON —
    /// разметки виджета Telegram, ответов витрины X и стороннего читателя, — то есть
    /// им доверяют ровно настолько, насколько доверяют этим источникам. Проверка
    /// защищает в глубину: внутренняя сеть бота (сам прокси ChatGPT со своим management
    /// API) для запросов «наружу» недосягаема даже при подмене ответа апстримом.
    /// </summary>
    internal static bool IsSafeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            LogUnsafe(url, "адрес не разбирается");
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            LogUnsafe(url, "схема не http(s)");
            return false;
        }

        var host = uri.DnsSafeHost;

        // Имя без точки — это имя контейнера в docker-сети или машины в локальной сети:
        // публичных адресов такого вида не бывает
        if (!host.Contains('.') && !host.Contains(':'))
        {
            LogUnsafe(url, "внутреннее имя хоста");
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localdomain", StringComparison.OrdinalIgnoreCase))
        {
            LogUnsafe(url, "внутренний домен");
            return false;
        }

        if (IPAddress.TryParse(host, out var address) && IsPrivate(address))
        {
            LogUnsafe(url, "приватный адрес");
            return false;
        }

        return true;
    }

    private static void LogUnsafe(string url, string reason) =>
        BotLogger.Warning("Адрес {Url} не годится для скачивания: {Reason}", url, reason);

    /// <summary>
    /// Адрес своей же сети: loopback, приватные диапазоны и link-local (там же живут
    /// метаданные облачных провайдеров).
    /// </summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal
                || (address.IsIPv4MappedToIPv6 && IsPrivate(address.MapToIPv4()));
        }

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            127 => true,
            169 => octets[1] == 254,
            172 => octets[1] >= 16 && octets[1] <= 31,
            192 => octets[1] == 168,
            _ => false
        };
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),

            // Страницы виджетов и ответы ручек — это текст: без потолка буфера
            // чужой хост мог бы скормить нам гигабайты в память
            MaxResponseContentBufferSize = MaxPageBytes
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }
}
