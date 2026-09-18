namespace Translatorlight;

/// <summary>Which edge of the screen the strip is glued to, if any.</summary>
internal enum DockEdge
{
    None,
    Top,
    Bottom,
}

/// <summary>
/// Turns the widget into a desktop toolbar: the shell reserves a strip of screen along one
/// edge for it, keeps maximised windows out of that strip and tells the bar when the edges
/// move. It is as close to living in the taskbar as a program that is not the shell can get.
/// </summary>
/// <remarks>
/// The reserved strip belongs to the shell until it is given back, so every way out of the
/// docked state - undocking, hiding, closing the window, leaving the program - ends in
/// <see cref="Release"/>. The handle is remembered rather than read from the form, because by
/// the time the last of those happens the form may have no handle left to ask.
/// </remarks>
internal sealed class DesktopBar
{
    private const int PositionChanged = 0x0001;
    private const int FullScreenApp = 0x0002;
    private const int WindowArrange = 0x0003;

    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;

    private readonly Form _form;
    private readonly Func<bool> _wantsTopMost;

    private IntPtr _handle;
    private bool _repositioning;

    internal DesktopBar(Form form, Func<bool> wantsTopMost)
    {
        _form = form;
        _wantsTopMost = wantsTopMost;

        // A last line of defence: an ordinary exit that somehow skipped the window teardown
        // must still hand the strip back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();
    }

    internal DockEdge Edge { get; private set; } = DockEdge.None;

    internal bool IsDocked => _handle != IntPtr.Zero;

    /// <summary>Docks the window to an edge, or lets it float again.</summary>
    internal void Apply(DockEdge edge)
    {
        if (edge == DockEdge.None)
        {
            Release();
            return;
        }

        Edge = edge;

        if (!_form.IsHandleCreated)
        {
            // Nothing to register yet; docking happens once the window has a handle.
            return;
        }

        if (!IsDocked)
        {
            if (!NativeMethods.AppBarNew(_form.Handle))
            {
                Edge = DockEdge.None;
                return;
            }

            _handle = _form.Handle;
        }

        Reposition();
    }

    internal void Release()
    {
        Edge = DockEdge.None;

        if (_handle == IntPtr.Zero)
        {
            return;
        }

        IntPtr handle = _handle;
        _handle = IntPtr.Zero;
        NativeMethods.AppBarRemove(handle);
    }

    /// <summary>Asks the shell for the strip again and moves the window onto it.</summary>
    internal void Reposition()
    {
        if (!IsDocked || _repositioning || !_form.IsHandleCreated)
        {
            return;
        }

        _repositioning = true;
        try
        {
            Rectangle screen = Screen.FromHandle(_handle).Bounds;
            int thickness = _form.Height;

            Rectangle wanted = Edge == DockEdge.Top
                ? new Rectangle(screen.Left, screen.Top, screen.Width, thickness)
                : new Rectangle(screen.Left, screen.Bottom - thickness, screen.Width, thickness);

            int edge = Edge == DockEdge.Top ? NativeMethods.EdgeTop : NativeMethods.EdgeBottom;
            _form.Bounds = NativeMethods.AppBarSetPosition(_handle, edge, wanted);
        }
        finally
        {
            _repositioning = false;
        }
    }

    /// <summary>Everything the shell has to say to a docked bar arrives here.</summary>
    internal void OnMessage(Message message)
    {
        if (!IsDocked)
        {
            return;
        }

        if (message.Msg == (int)NativeMethods.AppBarCallback)
        {
            switch ((int)message.WParam)
            {
                case PositionChanged:
                case WindowArrange:
                    Reposition();
                    break;

                case FullScreenApp:
                    // Step out of the way of a full-screen program, and come back after it.
                    _form.TopMost = message.LParam == IntPtr.Zero && _wantsTopMost();
                    break;
            }

            return;
        }

        switch (message.Msg)
        {
            case WmActivate:
                NativeMethods.AppBarActivate(_handle);
                break;

            case WmWindowPosChanged:
                NativeMethods.AppBarWindowPosChanged(_handle);
                break;
        }
    }
}
