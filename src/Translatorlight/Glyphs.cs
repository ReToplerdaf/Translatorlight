using System.Drawing.Text;

namespace Translatorlight;

/// <summary>
/// The little symbols on the header buttons. Windows 11 ships them in "Segoe Fluent Icons",
/// Windows 10 in "Segoe MDL2 Assets"; without either one plain characters are used instead.
/// </summary>
internal static class Glyphs
{
    private static readonly string? IconFamily = ResolveFamily();

    internal static bool Available => IconFamily is not null;

    internal static string FamilyName => IconFamily ?? "Segoe UI";

    internal static string Pin => Available ? "\uE718" : "^";

    internal static string Unpin => Available ? "\uE77A" : "v";

    internal static string Close => Available ? "\uE8BB" : "\u2715";

    private static string? ResolveFamily()
    {
        try
        {
            using var installed = new InstalledFontCollection();
            foreach (string candidate in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                foreach (FontFamily family in installed.Families)
                {
                    if (string.Equals(family.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch (Exception)
        {
            // A missing font collection is not worth failing over; plain characters will do.
        }

        return null;
    }
}
