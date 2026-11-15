using System.Drawing.Text;
using System.Globalization;

namespace VideoTrimmer;

/// <summary>
/// Colors and fonts. The window is a base that takes a little color from the current frame (see <see cref="Backdrop"/>);
/// panels and controls are flat translucent fills over it, so nothing is blurred at paint time. The colors come in a light
/// and a dark theme with one of several accents, chosen in Settings; <see cref="Apply"/> switches them and raises
/// <see cref="Changed"/>. Whatever sits on the video itself (cut shading, timecode tags, the playhead) stays the same in both.
/// </summary>
static class Theme
{
    /// <summary>An accent: its fill in the dark theme, its fill in the light theme, and a darker ink for lines and icons on light surfaces.</summary>
    public sealed record Tint(string Name, Color Dark, Color LightFill, Color LightInk);

    /// <summary>The same accents as WB Tunes, so the apps match.</summary>
    public static readonly Tint[] Tints =
    [
        new("Butter", Hex(0xF5C451), Hex(0xF5C451), Hex(0x8F6400)),
        new("Graphite", Hex(0xE6E6E6), Hex(0x2B2B2B), Hex(0x2B2B2B)),
        new("Sky", Hex(0x6CB6FF), Hex(0x3B82D9), Hex(0x2F6FC0)),
        new("Mint", Hex(0x7FD1A6), Hex(0x3FA676), Hex(0x2B7F57)),
        new("Rose", Hex(0xF29BB0), Hex(0xD9577A), Hex(0xB8405F)),
        new("Violet", Hex(0xB9A4F5), Hex(0x7C62D6), Hex(0x6A4FC8)),
        new("Coral", Hex(0xFF9B7A), Hex(0xE5673F), Hex(0xC2502C)),
    ];

    public static Tint FindTint(string? name) => Tints.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Tints[0];

    public static bool IsDark { get; private set; } = true;
    public static Tint CurrentTint { get; private set; } = Tints[0];

    /// <summary>Raised after <see cref="Apply"/> changes the colors. Cached brushes rebuild on it, and the window repaints.</summary>
    public static event Action? Changed;

    public static Color Base { get; private set; }
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Faint { get; private set; }
    /// <summary>The one accent: selection, the primary button, the trim handles. Labels on it use <see cref="OnAccent"/>.</summary>
    public static Color Accent { get; private set; }
    public static Color OnAccent { get; private set; }
    /// <summary>The accent for thin lines and icons on the window itself, where the light theme needs it darker to show.</summary>
    public static Color AccentInk { get; private set; }
    /// <summary>Status only: done and ready.</summary>
    public static Color Good { get; private set; }
    /// <summary>Cancel, remove and errors.</summary>
    public static Color Caution { get; private set; }

    public static Color PanelFill { get; private set; }
    public static Color PanelBorder { get; private set; }
    public static Color PanelHighlight { get; private set; }
    public static Color ControlFill { get; private set; }
    public static Color ControlHover { get; private set; }
    public static Color ControlPressed { get; private set; }
    public static Color ControlBorder { get; private set; }
    public static Color Selected { get; private set; }
    public static Color Hairline { get; private set; }
    /// <summary>Faint fills and outlines: key caps, the switch's track, the list's scroll thumb, unmarked badges.</summary>
    public static Color Outline { get; private set; }
    public static Color OutlineStrong { get; private set; }
    /// <summary>Fields and the tracks of segmented controls. Opaque, because a text box cannot be see-through.</summary>
    public static Color Well { get; private set; }
    public static Color WellBorder { get; private set; }
    /// <summary>Menus sit over video and thumbnails, so they are opaque.</summary>
    public static Color Menu { get; private set; }
    public static Color MenuBorder { get; private set; }
    /// <summary>The window's one-pixel border, drawn by Windows.</summary>
    public static Color WindowBorder { get; private set; }
    /// <summary>How strongly the backdrop takes the frame's color: less on the light base, where a tint shows more.</summary>
    public static float BackdropStrength { get; private set; }

    static Dictionary<Color, Color> remap = new();

    // Before any setting is read the window is dark with the first accent, as it has always been.
    static Theme() => Set(dark: true, Tints[0]);

    /// <summary>Switches to the light or dark theme with the named accent and raises <see cref="Changed"/> if anything changed.</summary>
    public static void Apply(bool dark, string? tintName)
    {
        var tint = FindTint(tintName);
        if (dark == IsDark && tint == CurrentTint) return;
        var before = Roles();
        Set(dark, tint);
        var after = Roles();
        remap = new Dictionary<Color, Color>();
        for (var i = 0; i < before.Length; i++) remap.TryAdd(before[i], after[i]);
        Changed?.Invoke();
    }

    /// <summary>
    /// The color that replaces <paramref name="color"/> after the last <see cref="Apply"/>, when it was one of the theme's
    /// colors; otherwise the color itself. Used to bring controls' ForeColor and BackColor along with a switch.
    /// </summary>
    public static Color Remap(Color color) => remap.TryGetValue(color, out var replacement) ? replacement : color;

    /// <summary>Brings <paramref name="root"/> and its children along with the last switch: colors set from the theme are replaced.</summary>
    public static void Recolor(Control root)
    {
        var fore = Remap(root.ForeColor);
        if (fore != root.ForeColor) root.ForeColor = fore;
        var back = Remap(root.BackColor);
        if (back != root.BackColor) root.BackColor = back;
        foreach (Control child in root.Controls) Recolor(child);
    }

    static Color[] Roles() =>
    [
        Base, Text, Muted, Faint, Accent, OnAccent, AccentInk, Good, Caution, PanelFill, PanelBorder, PanelHighlight, ControlFill,
        ControlHover, ControlPressed, ControlBorder, Selected, Hairline, Outline, OutlineStrong, Well, WellBorder, Menu, MenuBorder,
        WindowBorder,
    ];

    static void Set(bool dark, Tint tint)
    {
        IsDark = dark;
        CurrentTint = tint;
        Accent = dark ? tint.Dark : tint.LightFill;
        AccentInk = dark ? tint.Dark : tint.LightInk;
        OnAccent = ReadableOn(Accent);
        if (dark)
        {
            Base = Color.FromArgb(13, 15, 19);
            Text = Color.FromArgb(242, 244, 248);
            Muted = Color.FromArgb(166, 174, 189);
            Faint = Color.FromArgb(140, 149, 166);
            Good = Color.FromArgb(61, 220, 151);
            Caution = Color.FromArgb(255, 138, 138);
            PanelFill = Color.FromArgb(14, 255, 255, 255);
            PanelBorder = Color.FromArgb(26, 255, 255, 255);
            PanelHighlight = Color.FromArgb(18, 255, 255, 255);
            ControlFill = Color.FromArgb(16, 255, 255, 255);
            ControlHover = Color.FromArgb(30, 255, 255, 255);
            ControlPressed = Color.FromArgb(56, 0, 0, 0);
            ControlBorder = Color.FromArgb(26, 255, 255, 255);
            Selected = Color.FromArgb(36, 255, 255, 255);
            Hairline = Color.FromArgb(20, 255, 255, 255);
            Outline = Color.FromArgb(40, 255, 255, 255);
            OutlineStrong = Color.FromArgb(110, 255, 255, 255);
            Well = Color.FromArgb(18, 20, 25);
            WellBorder = Color.FromArgb(44, 48, 56);
            Menu = Color.FromArgb(27, 31, 39);
            MenuBorder = Color.FromArgb(54, 59, 70);
            WindowBorder = Color.FromArgb(40, 44, 52);
            BackdropStrength = 0.22f;
        }
        else
        {
            Base = Color.FromArgb(240, 240, 243);
            Text = Color.FromArgb(27, 27, 30);
            Muted = Color.FromArgb(85, 85, 92);
            Faint = Color.FromArgb(118, 118, 126);
            Good = Color.FromArgb(46, 125, 79);
            Caution = Color.FromArgb(179, 38, 30);
            PanelFill = Color.FromArgb(158, 255, 255, 255);
            PanelBorder = Color.FromArgb(24, 0, 0, 0);
            PanelHighlight = Color.FromArgb(220, 255, 255, 255);
            ControlFill = Color.FromArgb(13, 0, 0, 0);
            ControlHover = Color.FromArgb(23, 0, 0, 0);
            ControlPressed = Color.FromArgb(36, 0, 0, 0);
            ControlBorder = Color.FromArgb(31, 0, 0, 0);
            Selected = Color.White;
            Hairline = Color.FromArgb(22, 0, 0, 0);
            Outline = Color.FromArgb(40, 0, 0, 0);
            OutlineStrong = Color.FromArgb(100, 0, 0, 0);
            Well = Color.FromArgb(232, 232, 236);
            WellBorder = Color.FromArgb(206, 206, 213);
            Menu = Color.FromArgb(252, 252, 253);
            MenuBorder = Color.FromArgb(214, 214, 219);
            WindowBorder = Color.FromArgb(200, 200, 206);
            BackdropStrength = 0.16f;
        }
    }

    /// <summary>Near-black or white, whichever reads better on <paramref name="fill"/>.</summary>
    static Color ReadableOn(Color fill)
    {
        var dark = Color.FromArgb(20, 22, 26);
        return Contrast(dark, fill) >= Contrast(Color.White, fill) ? dark : Color.White;
    }

    /// <summary>The WCAG contrast ratio of two opaque colors, from 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(Color c)
    {
        static double Channel(int value)
        {
            var v = value / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        var (dark, tint) = (IsDark, CurrentTint);
        foreach (var theme in new[] { true, false })
        {
            foreach (var candidate in Tints)
            {
                Set(theme, candidate);
                var name = $"{candidate.Name} ({(theme ? "dark" : "light")})";
                if (Contrast(OnAccent, Accent) < 4.5) errors.Add($"{name}: labels on the accent are below 4.5:1");
                if (Contrast(AccentInk, Base) < 3) errors.Add($"{name}: the accent's lines are below 3:1 on the window");
            }

            if (Contrast(Text, Base) < 7 || Contrast(Muted, Base) < 4.5) errors.Add($"{(theme ? "dark" : "light")}: text contrast is too low");
        }

        Set(dark, tint);
        if (FindTint("sky").Name != "Sky" || FindTint("nonsense") != Tints[0]) errors.Add("accent names are matched loosely and fall back to the first");
        return errors;
    }

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
