using System.Runtime.InteropServices;

namespace Translatorlight;

/// <summary>Win32 entry points used by the widget.</summary>
internal static class NativeMethods
{
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HitTestCaption = 0x0002;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE, honoured by Windows 11 and ignored before it.</summary>
    private const int WindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND.</summary>
    private const int CornerPreferenceRound = 2;

    private const int EmSetSelection = 0x00B1;
    private const int EmScrollCaret = 0x00B7;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Hands the drag over to Windows, which then moves the window exactly as it moves any
    /// title bar. Doing it by hand from mouse events lags behind the cursor.
    /// </summary>
    internal static void BeginWindowDrag(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(handle, WmNcLeftButtonDown, (IntPtr)HitTestCaption, IntPtr.Zero);
    }

    /// <summary>
    /// Selects the whole contents of a text box while leaving the view at the start of it.
    /// Setting the selection the ordinary way puts the caret at the end, and a one-line box then
    /// shows the tail of a long translation instead of its beginning.
    /// </summary>
    internal static void SelectAllShowingStart(IntPtr handle, int length)
    {
        if (handle == IntPtr.Zero || length <= 0)
        {
            return;
        }

        // EM_SETSEL anchors the selection at the first value and leaves the caret at the second,
        // so anchoring at the end scrolls the box back to the first character.
        SendMessage(handle, EmSetSelection, (IntPtr)length, IntPtr.Zero);
        SendMessage(handle, EmScrollCaret, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Asks Windows 11 to round the corners of a borderless window.</summary>
    internal static void UseRoundedCorners(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int preference = CornerPreferenceRound;
            DwmSetWindowAttribute(handle, WindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows simply keeps square corners.
        }
    }
}
