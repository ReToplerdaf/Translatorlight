using System.Net;
using System.Text;
using System.Text.Json;

namespace Translatorlight;

/// <summary>A translation that failed for a reason worth putting in front of the user.</summary>
internal sealed class TranslationException : Exception
{
    internal TranslationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>One online service able to translate a piece of text.</summary>
internal interface ITranslationProvider
{
    /// <summary>Shown in the menu and in error messages.</summary>
    string Name { get; }

    /// <summary>How much text the service takes in a single request.</summary>
    int MaxChunkLength { get; }

    Task<string> TranslateAsync(string text, string from, string to, CancellationToken token);
}

/// <summary>The single HttpClient every provider talks through.</summary>
internal static class TranslationHttp
{
    private static readonly HttpClient Client = Create();

    internal static async Task<string> GetStringAsync(string url, CancellationToken token)
    {
        HttpResponseMessage response;
        try
        {
            response = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, token);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException("нет связи", ex);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TranslationException("не ответил вовремя", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationException(Describe(response.StatusCode));
            }

            try
            {
                return await response.Content.ReadAsStringAsync(token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new TranslationException("ответ оборвался", ex);
            }
        }
    }

    internal static JsonDocument ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException("вернул непонятный ответ", ex);
        }
    }

    private static HttpClient Create()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Translatorlight/0.1");
        return client;
    }

    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => "просит подождать",
        HttpStatusCode.Forbidden => "отклонил запрос",
        HttpStatusCode.ServiceUnavailable => "недоступен",
        _ => $"ответил ошибкой {(int)status}",
    };
}

/// <summary>
/// The endpoint the Google Translate web page itself calls. No key, no registration, and it
/// keeps sentence boundaries, so it goes first.
/// </summary>
internal sealed class GoogleProvider : ITranslationProvider
{
    public string Name => "Google";

    public int MaxChunkLength => 1200;

    public async Task<string> TranslateAsync(string text, string from, string to, CancellationToken token)
    {
        string url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t"
            + $"&sl={from}&tl={to}&q={Uri.EscapeDataString(text)}";

        string json = await TranslationHttp.GetStringAsync(url, token);
        return Parse(json);
    }

    /// <summary>The answer is a nested array; the pieces of the translation sit in the first slot.</summary>
    private static string Parse(string json)
    {
        using JsonDocument document = TranslationHttp.ParseJson(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0
            || root[0].ValueKind != JsonValueKind.Array)
        {
            throw new TranslationException("вернул непонятный ответ");
        }

        var builder = new StringBuilder();
        foreach (JsonElement segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0
                && segment[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(segment[0].GetString());
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// A documented free API with a daily allowance. It only takes short queries, so the text is
/// handed over in small pieces.
/// </summary>
internal sealed class MyMemoryProvider : ITranslationProvider
{
    public string Name => "MyMemory";

    public int MaxChunkLength => 400;

    public async Task<string> TranslateAsync(string text, string from, string to, CancellationToken token)
    {
        string url = "https://api.mymemory.translated.net/get"
            + $"?langpair={from}%7C{to}&q={Uri.EscapeDataString(text)}";

        string json = await TranslationHttp.GetStringAsync(url, token);

        using JsonDocument document = TranslationHttp.ParseJson(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("responseData", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("translatedText", out JsonElement translated)
            || translated.ValueKind != JsonValueKind.String)
        {
            throw new TranslationException("вернул непонятный ответ");
        }

        // The service answers in HTML entities and reports its limits inside the translation itself.
        string result = WebUtility.HtmlDecode(translated.GetString() ?? string.Empty);

        if (result.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
            || result.Contains("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase)
            || result.Contains("INVALID LANGUAGE PAIR", StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationException("исчерпал дневной лимит");
        }

        return result;
    }
}

/// <summary>
/// A community front end for Google Translate. Instances come and go, so a couple of them are
/// tried in turn before the provider gives up.
/// </summary>
internal sealed class LingvaProvider : ITranslationProvider
{
    private static readonly string[] Hosts =
    [
        "lingva.ml",
        "translate.plausibility.cloud",
        "lingva.garudalinux.org",
    ];

    public string Name => "Lingva";

    public int MaxChunkLength => 900;

    public async Task<string> TranslateAsync(string text, string from, string to, CancellationToken token)
    {
        TranslationException? lastFailure = null;

        foreach (string host in Hosts)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                string url = $"https://{host}/api/v1/{from}/{to}/{Uri.EscapeDataString(text)}";
                string json = await TranslationHttp.GetStringAsync(url, token);

                using JsonDocument document = TranslationHttp.ParseJson(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("translation", out JsonElement translation)
                    && translation.ValueKind == JsonValueKind.String)
                {
                    return translation.GetString() ?? string.Empty;
                }

                lastFailure = new TranslationException("вернул непонятный ответ");
            }
            catch (TranslationException ex)
            {
                lastFailure = ex;
            }
        }

        throw lastFailure ?? new TranslationException("недоступен");
    }
}
