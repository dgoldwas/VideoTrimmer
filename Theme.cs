using System.Globalization;

namespace VideoTrimmer;

static class Theme
{
    public static readonly Color Background = Color.FromArgb(22, 24, 29);
    public static readonly Color Surface = Color.FromArgb(34, 38, 46);
    public static readonly Color Border = Color.FromArgb(52, 58, 70);
    public static readonly Color Text = Color.FromArgb(242, 244, 248);
    public static readonly Color Muted = Color.FromArgb(154, 163, 178);
    public static readonly Color Accent = Color.FromArgb(76, 141, 255);
    public static readonly Color InPoint = Color.FromArgb(61, 220, 151);
    public static readonly Color OutPoint = Color.FromArgb(255, 107, 107);
    public static readonly Color Wave = Color.FromArgb(120, 178, 255);

    static readonly Dictionary<string, Font> fonts = new();

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => Get("Segoe UI", size, style);

    public static Font Mono(float size) => Get("Consolas", size, FontStyle.Regular);

    /// <summary>For transport glyphs such as ▶ ⏸ ⏮ ⏭, which Segoe UI itself lacks.</summary>
    public static Font Symbol(float size) => Get("Segoe UI Symbol", size, FontStyle.Regular);

    public static Color Lift(Color color, int amount) => Color.FromArgb(color.A, Clamp(color.R + amount), Clamp(color.G + amount), Clamp(color.B + amount));

    public static Color Darken(Color color, int amount) => Color.FromArgb(color.A, Clamp(color.R - amount), Clamp(color.G - amount), Clamp(color.B - amount));

    public static Color Mix(Color from, Color to, float amount)
    {
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }

    static Font Get(string family, float size, FontStyle style)
    {
        var key = family + "|" + size.ToString("0.00", CultureInfo.InvariantCulture) + "|" + style;
        if (fonts.TryGetValue(key, out var font)) return font;
        font = new Font(family, size, style, GraphicsUnit.Point);
        fonts[key] = font;
        return font;
    }

    static int Clamp(int value) => Math.Clamp(value, 0, 255);
}
