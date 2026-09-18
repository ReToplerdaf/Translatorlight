using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Translatorlight;

/// <summary>
/// Pulls plain text out of whatever was dragged onto the widget. Selected text arrives as
/// Unicode text from almost every program; browsers and Word add an HTML flavour, and a file
/// dragged from Explorer arrives as a path.
/// </summary>
internal static class DroppedText
{
    private const int MaxFileBytes = 512 * 1024;

    private static readonly string[] TextFileExtensions =
        [".txt", ".md", ".log", ".csv", ".srt", ".vtt", ".json", ".xml", ".html", ".htm"];

    /// <summary>True when the drag carries something the widget knows how to read.</summary>
    internal static bool CanAccept(IDataObject? data)
    {
        if (data is null)
        {
            return false;
        }

        try
        {
            return data.GetDataPresent(DataFormats.UnicodeText)
                || data.GetDataPresent(DataFormats.Text)
                || data.GetDataPresent(DataFormats.Html)
                || data.GetDataPresent(DataFormats.FileDrop);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Returns the dropped text, or an empty string when there is nothing to translate.</summary>
    internal static string Extract(IDataObject? data)
    {
        if (data is null)
        {
            return string.Empty;
        }

        string text = ReadFormat(data, DataFormats.UnicodeText);

        if (text.Length == 0)
        {
            text = ReadFormat(data, DataFormats.Text);
        }

        if (text.Length == 0)
        {
            text = FromClipboardHtml(ReadFormat(data, DataFormats.Html));
        }

        if (text.Length == 0)
        {
            text = FromFiles(data);
        }

        return text.Trim();
    }

    private static string ReadFormat(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format))
            {
                return string.Empty;
            }

            return data.GetData(format) switch
            {
                string value => value,

                // Some sources hand the HTML flavour over as a stream rather than a string.
                MemoryStream stream => ReadStream(stream),
                _ => string.Empty,
            };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
            or NotSupportedException or IOException or OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    private static string ReadStream(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
        return reader.ReadToEnd().TrimEnd('\0');
    }

    /// <summary>
    /// The clipboard HTML flavour is a small header followed by a document; the part the user
    /// actually selected is fenced off by StartFragment and EndFragment markers.
    /// </summary>
    private static string FromClipboardHtml(string clipboardHtml)
    {
        if (clipboardHtml.Length == 0)
        {
            return string.Empty;
        }

        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";

        string html = clipboardHtml;
        int start = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        int end = html.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            html = html[(start + startMarker.Length)..end];
        }

        return HtmlToPlainText(html);
    }

    private static string HtmlToPlainText(string html)
    {
        string text = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|/p|/div|/li|/tr|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // Collapse the runs of spaces that mark-up leaves behind, keeping paragraph breaks.
        text = Regex.Replace(text, @"[^\S\n]+", " ");
        text = Regex.Replace(text, @" ?\n ?", "\n");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string FromFiles(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths)
            {
                return string.Empty;
            }

            foreach (string path in paths)
            {
                string extension = Path.GetExtension(path);
                if (!TextFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var file = new FileInfo(path);
                if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes)
                {
                    continue;
                }

                string content = File.ReadAllText(path);
                if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                {
                    content = HtmlToPlainText(content);
                }

                if (content.Trim().Length > 0)
                {
                    return content;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // A file that cannot be read is simply not a source of text.
        }

        return string.Empty;
    }
}
