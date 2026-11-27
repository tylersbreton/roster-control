using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace RosterControl.Desktop.Services;

public enum ThemeMode
{
    Dark = 0,
    Light = 1,
    /// <summary>Follows the Windows light/dark setting.</summary>
    System = 2,
}

public sealed record AccentPreset(string Name, string Hex);

/// <summary>
/// Light/dark theme and the accent colour. Surfaces and text come from the theme dictionaries in Styles/Tokens.axaml; every
/// accent-derived resource (Brush.Gold…, the gold frame, Fluent's accent keys) is generated here for both themes, so a
/// custom accent reads well on dark and light backgrounds alike. Views use DynamicResource, so changes apply instantly.
/// </summary>
public static class AppTheme
{
    public const string DefaultAccent = "#C8AA6E";

    public static readonly IReadOnlyList<AccentPreset> Presets =
    [
        new("Hextech gold", DefaultAccent),
        new("Arcane blue", "#5B9BF0"),
        new("Hextech teal", "#22B8AA"),
        new("Crimson", "#E05263"),
        new("Void purple", "#A07CF0"),
        new("Jade", "#56B866"),
    ];

    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;
    public static Color Accent { get; private set; } = Color.Parse(DefaultAccent);

    public static string Normalize(string? hex) =>
        hex is not null && Color.TryParse(hex.Trim(), out var c) ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : DefaultAccent;

    public static void Apply(Application app, ThemeMode mode, string? accentHex)
    {
        Mode = mode;
        Accent = Color.Parse(Normalize(accentHex));
        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = AccentResources(Accent, dark: true);
        app.Resources.ThemeDictionaries[ThemeVariant.Light] = AccentResources(Accent, dark: false);
    }

    /// <summary>The accent-derived resources for one theme.</summary>
    internal static ResourceDictionary AccentResources(Color accent, bool dark)
    {
        var p = Derive(accent, dark);
        var d = new ResourceDictionary();
        void Brush(string key, Color c) => d[key] = new SolidColorBrush(c);

        d["Color.Gold"] = p.Gold;
        d["Color.GoldLight"] = p.Light;
        d["Color.GoldDark"] = p.Dark;
        d["Color.GoldDeep"] = p.Deep;
        Brush("Brush.Gold", p.Gold);
        Brush("Brush.GoldLight", p.Light);
        Brush("Brush.GoldDark", p.Dark);
        Brush("Brush.GoldDeep", p.Deep);
        Brush("Brush.GoldHover", p.Hover);
        Brush("Brush.GoldSoft", WithAlpha(p.Gold, 0x1F));
        Brush("Brush.GoldSelected", p.Selected);
        Brush("Brush.GoldSelectedHover", p.SelectedHover);
        Brush("Brush.OnGold", p.OnGold);
        d["Brush.GoldFrame"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(p.Gold, 0), new GradientStop(dark ? p.Dark : p.Light, 1) },
        };
        d["Brush.GoldLine"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(WithAlpha(p.Dark, 0), 0), new GradientStop(p.Gold, 0.5), new GradientStop(WithAlpha(p.Dark, 0), 1) },
        };
        d["Shadow.GoldGlow"] = BoxShadows.Parse($"0 0 18 0 #{(dark ? 0x40 : 0x30):X2}{p.Gold.R:X2}{p.Gold.G:X2}{p.Gold.B:X2}");

        // Fluent controls that use the accent.
        foreach (var key in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked", "ToggleSwitchFillOn", "SliderTrackValueFill", "SliderThumbBackground",
                     "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedStroke", "TextControlBorderBrushFocused" })
            Brush(key, p.Gold);
        foreach (var key in new[] { "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
                     "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "SliderThumbBackgroundPointerOver",
                     "SliderThumbBackgroundPressed", "RadioButtonOuterEllipseCheckedFillPointerOver", "RadioButtonOuterEllipseCheckedStrokePointerOver",
                     "RadioButtonOuterEllipseCheckedFillPressed", "RadioButtonOuterEllipseCheckedStrokePressed" })
            Brush(key, p.Hover);
        foreach (var key in new[] { "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
                     "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed", "RadioButtonCheckGlyphFill",
                     "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed" })
            Brush(key, p.OnGold);
        Brush("ToolTipBorderBrush", p.Dark);
        Brush("TextControlSelectionHighlightColor", WithAlpha(p.Gold, 0x66));
        d["SystemAccentColor"] = p.Gold;
        d["SystemAccentColorDark1"] = Shade(p.Gold, -0.08);
        d["SystemAccentColorDark2"] = Shade(p.Gold, -0.16);
        d["SystemAccentColorDark3"] = Shade(p.Gold, -0.24);
        d["SystemAccentColorLight1"] = Shade(p.Gold, 0.08);
        d["SystemAccentColorLight2"] = Shade(p.Gold, 0.16);
        d["SystemAccentColorLight3"] = Shade(p.Gold, 0.24);
        return d;
    }

    internal sealed record Palette(Color Gold, Color Light, Color Dark, Color Deep, Color Hover, Color Selected, Color SelectedHover, Color OnGold);

    /// <summary>
    /// The accent family for a theme. Dark: the accent itself, a pale tint for headings, deep shades for frames. Light: a
    /// darker accent for contrast, a near-black tint for headings, pale tints for frames.
    /// </summary>
    internal static Palette Derive(Color accent, bool dark)
    {
        var (h, s, l) = ToHsl(accent);
        if (dark)
        {
            // The original hextech palette, exactly, for the default gold.
            if (Normalize($"#{accent.R:X2}{accent.G:X2}{accent.B:X2}") == DefaultAccent)
                return new Palette(accent, Color.Parse("#F0E6D2"), Color.Parse("#785A28"), Color.Parse("#463714"), Color.Parse("#D8BC82"),
                    Color.Parse("#2A2616"), Color.Parse("#332D1A"), Color.Parse("#0C131D"));
            var gold = FromHsl(h, s, Math.Clamp(l, 0.5, 0.72));
            return new Palette(gold, FromHsl(h, s * 0.5, 0.9), FromHsl(h, s * 0.55, 0.32), FromHsl(h, s * 0.55, 0.18), FromHsl(h, s, Math.Min(0.82, ToHsl(gold).L + 0.08)),
                Mix(Color.Parse("#0C131D"), gold, 0.16), Mix(Color.Parse("#0C131D"), gold, 0.23), Color.Parse("#0C131D"));
        }
        var lightGold = FromHsl(h, Math.Min(1, s * 1.05), Math.Clamp(l, 0.3, 0.42));
        return new Palette(lightGold, FromHsl(h, s * 0.6, 0.2), FromHsl(h, s * 0.45, 0.66), FromHsl(h, s * 0.4, 0.86), FromHsl(h, s, Math.Max(0.2, ToHsl(lightGold).L - 0.07)),
            Mix(Colors.White, lightGold, 0.14), Mix(Colors.White, lightGold, 0.22), Colors.White);
    }

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color Shade(Color c, double delta)
    {
        var (h, s, l) = ToHsl(c);
        return FromHsl(h, s, Math.Clamp(l + delta, 0, 1));
    }

    /// <summary>A colour made readable on a light background (same hue, darker), for text colours chosen for dark backgrounds.</summary>
    public static Color ForLightBackground(Color c)
    {
        var (h, s, l) = ToHsl(c);
        return l <= 0.42 ? c : FromHsl(h, Math.Min(1, s * (s < 0.15 ? 1 : 1.1)), s < 0.15 ? Math.Min(l, 0.3) : 0.38);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (Math.Abs(max - min) < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h / 6, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s < 1e-9)
        {
            var v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return Color.FromRgb((byte)Math.Round(Hue(h + 1.0 / 3) * 255), (byte)Math.Round(Hue(h) * 255), (byte)Math.Round(Hue(h - 1.0 / 3) * 255));
    }
}
