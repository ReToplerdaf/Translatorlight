using Microsoft.Win32;

namespace Translatorlight;

/// <summary>The handful of colours the widget is painted with.</summary>
internal readonly record struct Palette(
    Color Window,
    Color Header,
    Color Surface,
    Color Border,
    Color Text,
    Color Muted,
    Color Accent,
    Color AccentSoft,
    Color Danger);

/// <summary>Picks light or dark colours, following the Windows personalisation settings.</summary>
internal static class Theme
{
    internal static readonly Palette Light = new(
        Window: Color.FromArgb(0xF6, 0xF6, 0xF9),
        Header: Color.FromArgb(0xEA, 0xEA, 0xF0),
        Surface: Color.FromArgb(0xFF, 0xFF, 0xFF),
        Border: Color.FromArgb(0xD3, 0xD3, 0xDB),
        Text: Color.FromArgb(0x1A, 0x1A, 0x1F),
        Muted: Color.FromArgb(0x69, 0x69, 0x75),
        Accent: Color.FromArgb(0x2F, 0x6F, 0xEC),
        AccentSoft: Color.FromArgb(0xE2, 0xEB, 0xFD),
        Danger: Color.FromArgb(0xC3, 0x2B, 0x2B));

    internal static readonly Palette Dark = new(
        Window: Color.FromArgb(0x1E, 0x1E, 0x23),
        Header: Color.FromArgb(0x2A, 0x2A, 0x31),
        Surface: Color.FromArgb(0x27, 0x27, 0x2E),
        Border: Color.FromArgb(0x3B, 0x3B, 0x45),
        Text: Color.FromArgb(0xF0, 0xF0, 0xF5),
        Muted: Color.FromArgb(0x9B, 0x9B, 0xA8),
        Accent: Color.FromArgb(0x6C, 0xA0, 0xFF),
        AccentSoft: Color.FromArgb(0x2C, 0x38, 0x50),
        Danger: Color.FromArgb(0xFF, 0x7B, 0x72));

    /// <summary>Colours for the widget window, which follows the "app mode" setting.</summary>
    internal static Palette Current => UsesLightTheme("AppsUseLightTheme") ? Light : Dark;

    /// <summary>True when the taskbar is light, which decides the colour of the tray icon.</summary>
    internal static bool IsTaskbarLight() => UsesLightTheme("SystemUsesLightTheme");

    private static bool UsesLightTheme(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(valueName) is not int value || value != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }
}
