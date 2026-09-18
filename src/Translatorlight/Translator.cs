using System.Text;

namespace Translatorlight;

internal enum TranslationDirection
{
    EnglishToRussian,
    RussianToEnglish,
}

/// <summary>A finished translation and how it was made.</summary>
internal readonly record struct TranslationOutcome(
    string Text,
    string Service,
    TranslationDirection Direction,
    bool Truncated);

/// <summary>One request-sized piece of the text, plus the whitespace that followed it.</summary>
internal readonly record struct TextChunk(string Text, string Separator);

/// <summary>
/// Turns dropped text into a translation. The services are tried in turn, so a service that
/// has run out of free requests only costs a second before the next one takes over.
/// </summary>
internal static class Translator
{
    /// <summary>Longer drops are cut: the widget is for snippets, not for whole books.</summary>
    internal const int MaxCharacters = 5000;

    private static readonly ITranslationProvider[] Providers =
    [
        new GoogleProvider(),
        new MyMemoryProvider(),
        new LingvaProvider(),
    ];

    internal static IReadOnlyList<string> ServiceNames { get; } =
        Providers.Select(provider => provider.Name).ToArray();

    internal static string Describe(TranslationDirection direction) =>
        direction == TranslationDirection.EnglishToRussian ? "EN → RU" : "RU → EN";

    /// <summary>Which way to translate: what the settings say, or what the text itself suggests.</summary>
    internal static TranslationDirection Resolve(string setting, string text) => setting switch
    {
        AppSettings.DirectionEnglishToRussian => TranslationDirection.EnglishToRussian,
        AppSettings.DirectionRussianToEnglish => TranslationDirection.RussianToEnglish,
        _ => Detect(text),
    };

    /// <summary>Mostly Cyrillic goes to English; everything else is treated as English.</summary>
    internal static TranslationDirection Detect(string text)
    {
        int cyrillic = 0;
        int latin = 0;

        foreach (char character in text)
        {
            if (character >= (char)0x0400 && character <= (char)0x04FF)
            {
                cyrillic++;
            }
            else if (char.IsAsciiLetter(character))
            {
                latin++;
            }
        }

        return cyrillic > latin
            ? TranslationDirection.RussianToEnglish
            : TranslationDirection.EnglishToRussian;
    }

    internal static async Task<TranslationOutcome> TranslateAsync(
        string text,
        TranslationDirection direction,
        string preferredService,
        CancellationToken token)
    {
        string prepared = text.Trim();
        bool truncated = prepared.Length > MaxCharacters;
        if (truncated)
        {
            prepared = prepared[..MaxCharacters];
        }

        if (prepared.Length == 0)
        {
            throw new TranslationException("в этом фрагменте нет текста");
        }

        (string from, string to) = direction == TranslationDirection.EnglishToRussian
            ? ("en", "ru")
            : ("ru", "en");

        var failures = new List<string>();

        foreach (ITranslationProvider provider in Order(preferredService))
        {
            token.ThrowIfCancellationRequested();

            try
            {
                string translated = await TranslateWithAsync(provider, prepared, from, to, token);
                if (translated.Trim().Length > 0)
                {
                    return new TranslationOutcome(translated.Trim(), provider.Name, direction, truncated);
                }

                failures.Add($"{provider.Name}: пустой ответ");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (TranslationException ex)
            {
                failures.Add($"{provider.Name}: {ex.Message}");
            }
        }

        throw new TranslationException("не перевёл ни один сервис — " + string.Join(", ", failures));
    }

    private static async Task<string> TranslateWithAsync(
        ITranslationProvider provider,
        string text,
        string from,
        string to,
        CancellationToken token)
    {
        var builder = new StringBuilder();

        foreach (TextChunk chunk in Split(text, provider.MaxChunkLength))
        {
            token.ThrowIfCancellationRequested();
            builder.Append(await provider.TranslateAsync(chunk.Text, from, to, token));
            builder.Append(chunk.Separator);
        }

        return builder.ToString();
    }

    private static IEnumerable<ITranslationProvider> Order(string preferredService)
    {
        ITranslationProvider? preferred = Providers.FirstOrDefault(
            provider => string.Equals(provider.Name, preferredService, StringComparison.OrdinalIgnoreCase));

        if (preferred is null)
        {
            return Providers;
        }

        // The chosen service goes first; the others stay as a safety net behind it.
        var ordered = new List<ITranslationProvider> { preferred };
        ordered.AddRange(Providers.Where(provider => !ReferenceEquals(provider, preferred)));
        return ordered;
    }

    /// <summary>
    /// Cuts the text into request-sized pieces, preferring to break at a paragraph, then at a
    /// sentence, then at a space. The whitespace between pieces is carried across untranslated,
    /// so paragraphs survive the trip.
    /// </summary>
    internal static List<TextChunk> Split(string text, int maxLength)
    {
        var chunks = new List<TextChunk>();
        int index = 0;

        while (index < text.Length)
        {
            int end = Math.Min(index + Math.Max(maxLength, 1), text.Length);
            if (end < text.Length)
            {
                end = FindBreak(text, index, end);
            }

            string piece = text[index..end];
            string body = piece.TrimEnd();
            string separator = piece[body.Length..];

            if (body.Length > 0)
            {
                chunks.Add(new TextChunk(body, separator));
            }
            else if (chunks.Count > 0)
            {
                // Whitespace only: hang it on the previous piece instead of translating a blank.
                TextChunk previous = chunks[^1];
                chunks[^1] = previous with { Separator = previous.Separator + piece };
            }

            index = end;
        }

        return chunks;
    }

    private static int FindBreak(string text, int start, int end)
    {
        for (int position = end - 1; position > start; position--)
        {
            if (text[position] == '\n')
            {
                return position + 1;
            }
        }

        for (int position = end - 1; position > start; position--)
        {
            if (text[position] is '.' or '!' or '?' or ';' or '…')
            {
                int cut = position + 1;
                while (cut < end && (text[cut] == '"' || text[cut] == ')' || text[cut] == '»' || text[cut] == '\''))
                {
                    cut++;
                }

                return cut;
            }
        }

        for (int position = end - 1; position > start; position--)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                return position + 1;
            }
        }

        return end;
    }
}
