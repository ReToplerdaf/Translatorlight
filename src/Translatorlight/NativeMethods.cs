using System.Runtime.InteropServices;

namespace Translatorlight;

/// <summary>Win32 entry points used by the widget.</summary>
internal static class NativeMethods
{
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HitTestCaption = 0x0002;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE, honoured by Windows 11 and ignored before it.</summary>
    private const int WindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND and DWMWCP_DONOTROUND.</summary>
    private const int CornerPreferenceRound = 2;
    private const int CornerPreferenceSquare = 1;

    private const int AbmNew = 0x00000000;
    private const int AbmRemove = 0x00000001;
    private const int AbmQueryPos = 0x00000002;
    private const int AbmSetPos = 0x00000003;
    private const int AbmActivate = 0x00000006;
    private const int AbmWindowPosChanged = 0x00000009;

    /// <summary>ABE_TOP and ABE_BOTTOM.</summary>
    internal const int EdgeTop = 1;
    internal const int EdgeBottom = 3;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRectangle rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    /// <summary>
    /// The private message the shell uses to tell the bar that the screen edges have changed.
    /// Registered rather than picked by hand, so it cannot collide with anything.
    /// </summary>
    internal static uint AppBarCallback { get; } = RegisterWindowMessage("TranslatorlightDesktopBar");

    /// <summary>Registers the window with the shell as a desktop toolbar.</summary>
    internal static bool AppBarNew(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        AppBarData data = Describe(handle);
        data.uCallbackMessage = AppBarCallback;
        return SHAppBarMessage(AbmNew, ref data) != IntPtr.Zero;
    }

    /// <summary>
    /// Hands the reserved strip of screen back. Skipping this leaves the desktop a band short
    /// until the shell is restarted, so it has to happen on every way out.
    /// </summary>
    internal static void AppBarRemove(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        AppBarData data = Describe(handle);
        SHAppBarMessage(AbmRemove, ref data);
    }

    /// <summary>
    /// Asks the shell for a strip along one edge and returns the rectangle it granted, which is
    /// what the window has to move to.
    /// </summary>
    internal static Rectangle AppBarSetPosition(IntPtr handle, int edge, Rectangle wanted)
    {
        AppBarData data = Describe(handle);
        data.uEdge = (uint)edge;
        data.rc = new NativeRectangle
        {
            Left = wanted.Left,
            Top = wanted.Top,
            Right = wanted.Right,
            Bottom = wanted.Bottom,
        };

        // The shell moves the edge in past anything already docked there; the thickness asked
        // for is then measured from wherever it ended up.
        SHAppBarMessage(AbmQueryPos, ref data);

        if (edge == EdgeTop)
        {
            data.rc.Bottom = data.rc.Top + wanted.Height;
        }
        else
        {
            data.rc.Top = data.rc.Bottom - wanted.Height;
        }

        SHAppBarMessage(AbmSetPos, ref data);
        return Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
    }

    internal static void AppBarActivate(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            AppBarData data = Describe(handle);
            SHAppBarMessage(AbmActivate, ref data);
        }
    }

    internal static void AppBarWindowPosChanged(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            AppBarData data = Describe(handle);
            SHAppBarMessage(AbmWindowPosChanged, ref data);
        }
    }

    private static AppBarData Describe(IntPtr handle) => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = handle,
    };

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

    /// <summary>Asks Windows 11 to round the corners of a borderless window, or to leave them square.</summary>
    internal static void UseRoundedCorners(IntPtr handle, bool rounded)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int preference = rounded ? CornerPreferenceRound : CornerPreferenceSquare;
            DwmSetWindowAttribute(handle, WindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows simply keeps square corners.
        }
    }
}
