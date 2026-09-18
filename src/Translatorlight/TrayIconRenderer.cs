using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Translatorlight;

/// <summary>What the tray icon is currently saying.</summary>
internal enum IconState
{
    /// <summary>Waiting for something to be dropped on the widget.</summary>
    Idle,

    /// <summary>A translation is on its way.</summary>
    Busy,

    /// <summary>The last translation failed.</summary>
    Error,
}

/// <summary>
/// Draws the notification-area icon: a speech bubble with the letter the text is translated
/// into. The shape lives on a 100x100 design canvas and is scaled to whatever size the tray
/// asks for, so it stays crisp at 100 % and at 150 %.
/// </summary>
internal static class TrayIconRenderer
{
    private const float DesignSize = 100f;

    // Rendered at 4x and shrunk back down: cheap supersampling keeps 16 px icons crisp.
    private const int SupersampleFactor = 4;

    private static readonly RectangleF Bubble = new(5f, 7f, 90f, 68f);

    private static readonly PointF[] Tail =
    [
        new(26f, 68f),
        new(26f, 95f),
        new(52f, 71f),
    ];

    /// <summary>Renders a square icon bitmap of the requested edge length.</summary>
    internal static Bitmap Render(IconState state, bool lightBackground, int size)
    {
        int large = Math.Max(size, 1) * SupersampleFactor;

        using var canvas = new Bitmap(large, large, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(large / DesignSize, large / DesignSize);
            Draw(graphics, state, lightBackground);
        }

        var icon = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(icon))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(canvas, new Rectangle(0, 0, size, size));
        }

        return icon;
    }

    private static void Draw(Graphics graphics, IconState state, bool lightBackground)
    {
        Color fill = state switch
        {
            IconState.Busy => lightBackground
                ? Color.FromArgb(0x6E, 0x6E, 0x7C)
                : Color.FromArgb(0xAE, 0xAE, 0xBC),
            IconState.Error => lightBackground
                ? Color.FromArgb(0xC3, 0x2B, 0x2B)
                : Color.FromArgb(0xFF, 0x8B, 0x82),
            _ => lightBackground
                ? Color.FromArgb(0x24, 0x5D, 0xD2)
                : Color.FromArgb(0x84, 0xB2, 0xFF),
        };

        // The bubble is filled with the strong colour, so the letter takes the opposite one.
        Color ink = lightBackground ? Color.White : Color.FromArgb(0x14, 0x17, 0x20);

        using var shape = new GraphicsPath { FillMode = FillMode.Winding };
        AddRoundedRectangle(shape, Bubble, 20f);
        shape.AddPolygon(Tail);

        using (var brush = new SolidBrush(fill))
        {
            graphics.FillPath(brush, shape);
        }

        (string caption, float fontSize) = state switch
        {
            IconState.Busy => ("…", 44f),
            IconState.Error => ("!", 52f),
            _ => ("Я", 50f),
        };

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        using var inkBrush = new SolidBrush(ink);
        graphics.DrawString(caption, font, inkBrush, Bubble, format);
    }

    private static void AddRoundedRectangle(GraphicsPath path, RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
    }
}
