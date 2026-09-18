namespace Translatorlight;

/// <summary>
/// A panel that repaints itself whole when it is resized, and does it off screen.
/// </summary>
/// <remarks>
/// A plain panel only repaints the strip of itself that has just been uncovered, so a border
/// drawn along its right edge stays behind as a stray line when the widget is made wider. These
/// two switches are protected, which is the only reason this class exists.
/// </remarks>
internal sealed class SmoothPanel : Panel
{
    internal SmoothPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}

/// <summary>A table layout with the same repaint behaviour as <see cref="SmoothPanel"/>.</summary>
internal sealed class SmoothTable : TableLayoutPanel
{
    internal SmoothTable()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}
