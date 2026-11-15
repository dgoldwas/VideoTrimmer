using System.Drawing.Text;
using System.Globalization;

namespace VideoTrimmer;

/// <summary>
/// Colors and fonts. The window is a dark base that takes a little color from the current frame (see <see cref="Backdrop"/>);
/// panels and controls are flat translucent fills over it, so nothing is blurred at paint time.
/// </summary>
static class Theme
{
    public static readonly Color Base = Color.FromArgb(13, 15, 19);
    public static readonly Color Text = Color.FromArgb(242, 244, 248);
    public static readonly Color Muted = Color.FromArgb(166, 174, 189);
    public static readonly Color Faint = Color.FromArgb(140, 149, 166);
    /// <summary>The one accent: selection, the primary button, the trim handles. Labels on it use <see cref="OnAccent"/>.</summary>
    public static readonly Color Accent = Color.FromArgb(242, 181, 68);
    public static readonly Color OnAccent = Color.FromArgb(20, 22, 26);
    /// <summary>Status only: done and ready.</summary>
    public static readonly Color Good = Color.FromArgb(61, 220, 151);
    /// <summary>Cancel, remove and errors.</summary>
    public static readonly Color Caution = Color.FromArgb(255, 138, 138);

    public static readonly Color PanelFill = Color.FromArgb(14, 255, 255, 255);
    public static readonly Color PanelBorder = Color.FromArgb(26, 255, 255, 255);
    public static readonly Color PanelHighlight = Color.FromArgb(18, 255, 255, 255);
    public static readonly Color ControlFill = Color.FromArgb(16, 255, 255, 255);
    public static readonly Color ControlHover = Color.FromArgb(30, 255, 255, 255);
    public static readonly Color ControlPressed = Color.FromArgb(56, 0, 0, 0);
    public static readonly Color ControlBorder = Color.FromArgb(26, 255, 255, 255);
    public static readonly Color Selected = Color.FromArgb(36, 255, 255, 255);
    public static readonly Color Hairline = Color.FromArgb(20, 255, 255, 255);
    /// <summary>Fields and the tracks of segmented controls. Opaque, because a text box cannot be see-through.</summary>
    public static readonly Color Well = Color.FromArgb(18, 20, 25);
    public static readonly Color WellBorder = Color.FromArgb(44, 48, 56);
    /// <summary>Menus sit over video and thumbnails, so they are opaque.</summary>
    public static readonly Color Menu = Color.FromArgb(27, 31, 39);
    public static readonly Color MenuBorder = Color.FromArgb(54, 59, 70);

    static readonly Dictionary<string, Font> fonts = new();
    static readonly HashSet<string> installed = Installed();
    static readonly string mono = installed.Contains("Cascadia Mono") ? "Cascadia Mono" : "Consolas";
    static readonly string strong = installed.Contains("Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";

    /// <summary>Bold maps to Segoe UI Semibold, which reads calmer than the full bold on a dark window.</summary>
    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => style == FontStyle.Bold
        ? Get(strong, size, strong == "Segoe UI" ? FontStyle.Bold : FontStyle.Regular)
        : Get("Segoe UI", size, style);

    public static Font Mono(float size) => Get(mono, size, FontStyle.Regular);

    public static Color Lift(Color color, int amount) => Color.FromArgb(color.A, Clamp(color.R + amount), Clamp(color.G + amount), Clamp(color.B + amount));

    public static Color Darken(Color color, int amount) => Color.FromArgb(color.A, Clamp(color.R - amount), Clamp(color.G - amount), Clamp(color.B - amount));

    public static Color Mix(Color from, Color to, float amount)
    {
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }

    public static Color Alpha(Color color, int alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    static Font Get(string family, float size, FontStyle style)
    {
        var key = family + "|" + size.ToString("0.00", CultureInfo.InvariantCulture) + "|" + style;
        if (fonts.TryGetValue(key, out var font)) return font;
        font = new Font(family, size, style, GraphicsUnit.Point);
        fonts[key] = font;
        return font;
    }

    static HashSet<string> Installed()
    {
        try
        {
            using var collection = new InstalledFontCollection();
            return collection.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    static int Clamp(int value) => Math.Clamp(value, 0, 255);
}
