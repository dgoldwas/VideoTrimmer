using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VideoTrimmer;

/// <summary>
/// What every control paints first: the dark base, tinted by the current frame. The frame is shrunk to a handful of
/// pixels and stretched across the window once, into a window-sized bitmap; controls then copy their own patch of it.
/// It is rebuilt when the still settles or the window resizes, never per paint.
/// </summary>
static class Backdrop
{
    // A handful of pixels, so the tint is broad washes of color with none of the picture's detail.
    const int TintWidth = 8;
    const int TintHeight = 5;
    const float TintStrength = 0.22f;
    // Muted, so a loud frame colors the window without competing with the video.
    const float TintSaturation = 0.55f;

    static Bitmap? tint;
    static Bitmap? image;

    /// <summary>Takes the color of <paramref name="frame"/>, or goes back to the plain base when it is null.</summary>
    public static void SetFrame(Image? frame, Size client)
    {
        tint?.Dispose();
        tint = null;
        if (frame is not null)
        {
            try
            {
                tint = new Bitmap(TintWidth, TintHeight, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(tint);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(frame, new Rectangle(0, 0, TintWidth, TintHeight), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, attributes);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException)
            {
                // The still was released while it was being sampled; keep the plain base.
                tint?.Dispose();
                tint = null;
            }
        }

        Resize(client);
    }

    public static void Resize(Size client)
    {
        image?.Dispose();
        image = null;
        if (tint is null || client.Width < 1 || client.Height < 1) return;
        image = new Bitmap(client.Width, client.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(image);
        g.Clear(Theme.Base);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        const float r = 0.2126f * (1 - TintSaturation), gr = 0.7152f * (1 - TintSaturation), b = 0.0722f * (1 - TintSaturation);
        attributes.SetColorMatrix(new ColorMatrix(
        [
            [r + TintSaturation, r, r, 0, 0],
            [gr, gr + TintSaturation, gr, 0, 0],
            [b, b, b + TintSaturation, 0, 0],
            [0, 0, 0, TintStrength, 0],
            [0, 0, 0, 0, 1],
        ]));
        g.DrawImage(tint, new Rectangle(0, 0, client.Width, client.Height), 0, 0, TintWidth, TintHeight, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>Fills <paramref name="control"/> with its patch of the window's backdrop.</summary>
    public static void Paint(Graphics g, Control control)
    {
        var form = control.FindForm();
        if (image is null || form is null)
        {
            g.Clear(Theme.Base);
            return;
        }

        var origin = control == form ? Point.Empty : form.PointToClient(control.PointToScreen(Point.Empty));
        var size = control.ClientSize;
        // Mid-resize the bitmap can lag the window by a frame; fill what it does not cover with the base.
        if (origin.X < 0 || origin.Y < 0 || origin.X + size.Width > image.Width || origin.Y + size.Height > image.Height)
        {
            g.Clear(Theme.Base);
            var covered = Rectangle.Intersect(new Rectangle(origin, size), new Rectangle(Point.Empty, image.Size));
            if (covered.Width < 1 || covered.Height < 1) return;
            g.DrawImage(image, new Rectangle(covered.X - origin.X, covered.Y - origin.Y, covered.Width, covered.Height), covered, GraphicsUnit.Pixel);
            return;
        }

        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(image, new Rectangle(Point.Empty, size), new Rectangle(origin, size), GraphicsUnit.Pixel);
        g.CompositingMode = mode;
    }
}

static class PaintUtil
{
    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        if (bounds.Width <= diameter || bounds.Height <= diameter)
        {
            diameter = Math.Max(1, Math.Min(bounds.Width, bounds.Height));
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static GraphicsPath Rounded(Rectangle bounds, int radius) => Rounded((RectangleF)bounds, radius);

    /// <summary>Rounded on the left or the right side only: the two ends of the trim frame.</summary>
    public static GraphicsPath RoundedSide(RectangleF bounds, float radius, bool left)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width * 2, bounds.Height)));
        if (left)
        {
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddLine(bounds.Right, bounds.Top, bounds.Right, bounds.Bottom);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        }
        else
        {
            path.AddLine(bounds.Left, bounds.Top, bounds.Right - d / 2, bounds.Top);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddLine(bounds.Right - d / 2, bounds.Bottom, bounds.Left, bounds.Bottom);
        }

        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics g, RectangleF bounds, float radius, Color color)
    {
        using var path = Rounded(bounds, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void Stroke(Graphics g, RectangleF bounds, float radius, Color color, float width = 1)
    {
        using var path = Rounded(bounds, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    /// <summary>A glass panel: a faint white fill, a hairline border and a one-pixel highlight along the top.</summary>
    public static void Panel(Graphics g, Rectangle bounds, int radius)
    {
        var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1);
        Fill(g, rect, radius, Theme.PanelFill);
        Stroke(g, rect, radius, Theme.PanelBorder);
        using var pen = new Pen(Theme.PanelHighlight);
        g.DrawLine(pen, rect.Left + radius, rect.Top + 1, rect.Right - radius, rect.Top + 1);
    }

    public static void FocusRing(Graphics g, Rectangle bounds, int radius)
    {
        var rect = new RectangleF(bounds.X + 1, bounds.Y + 1, bounds.Width - 3, bounds.Height - 3);
        Stroke(g, rect, radius, Theme.Text, 2);
    }
}

enum Icon { None, Previous, Play, Pause, Next, Split, Plus, Minus, More, Reset, Close, Chevron, Check }

/// <summary>Line icons drawn on a 24-unit grid, so they stay crisp at any scale and take any color.</summary>
static class Icons
{
    public static void Draw(Graphics g, Icon icon, RectangleF box, Color color, float weight = 2f)
    {
        if (icon == Icon.None) return;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var scale = Math.Min(box.Width, box.Height) / 24f;
        g.TranslateTransform(box.X + (box.Width - 24 * scale) / 2, box.Y + (box.Height - 24 * scale) / 2);
        g.ScaleTransform(scale, scale);
        using var pen = new Pen(color, weight) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (icon)
        {
            case Icon.Play:
                g.FillPolygon(brush, new PointF[] { new(7, 4.5f), new(20, 12), new(7, 19.5f) });
                break;
            case Icon.Pause:
                g.FillRectangle(brush, 6.5f, 5, 4, 14);
                g.FillRectangle(brush, 13.5f, 5, 4, 14);
                break;
            case Icon.Previous:
                g.FillPolygon(brush, new PointF[] { new(19, 19), new(9, 12), new(19, 5) });
                g.DrawLine(pen, 5.5f, 18, 5.5f, 6);
                break;
            case Icon.Next:
                g.FillPolygon(brush, new PointF[] { new(5, 5), new(15, 12), new(5, 19) });
                g.DrawLine(pen, 18.5f, 6, 18.5f, 18);
                break;
            case Icon.Split:
                g.DrawEllipse(pen, 3, 3, 6, 6);
                g.DrawEllipse(pen, 3, 15, 6, 6);
                g.DrawLine(pen, 20, 4, 8.12f, 15.88f);
                g.DrawLine(pen, 14.47f, 14.48f, 20, 20);
                g.DrawLine(pen, 8.12f, 8.12f, 12, 12);
                break;
            case Icon.Plus:
                g.DrawLine(pen, 12, 5, 12, 19);
                g.DrawLine(pen, 5, 12, 19, 12);
                break;
            case Icon.Minus:
                g.DrawLine(pen, 5, 12, 19, 12);
                break;
            case Icon.More:
                g.FillEllipse(brush, 3.2f, 10.2f, 3.6f, 3.6f);
                g.FillEllipse(brush, 10.2f, 10.2f, 3.6f, 3.6f);
                g.FillEllipse(brush, 17.2f, 10.2f, 3.6f, 3.6f);
                break;
            case Icon.Reset:
                g.DrawLines(pen, new PointF[] { new(2, 4), new(2, 10), new(8, 10) });
                g.DrawArc(pen, 3, 3, 18, 18, 160.5f, -295.5f);
                g.DrawLine(pen, 5.64f, 5.64f, 2, 10);
                break;
            case Icon.Close:
                g.DrawLine(pen, 6, 6, 18, 18);
                g.DrawLine(pen, 18, 6, 6, 18);
                break;
            case Icon.Chevron:
                g.DrawLines(pen, new PointF[] { new(6, 9), new(12, 15), new(18, 9) });
                break;
            case Icon.Check:
                g.DrawLines(pen, new PointF[] { new(5, 12), new(10, 17), new(19, 7) });
                break;
        }

        g.Restore(state);
    }
}

/// <summary>Makes the native title bar the same dark color as the window, so the two read as one surface (Windows 11).</summary>
static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Form form)
    {
        try
        {
            var on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = ColorTranslator.ToWin32(Theme.Base);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
            var border = ColorTranslator.ToWin32(Color.FromArgb(40, 44, 52));
            DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
            var text = ColorTranslator.ToWin32(Theme.Muted);
            DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: the title bar keeps the system color.
        }
    }
}
