using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace RosterControl.Desktop.Controls;

/// <summary>
/// Renders Riot's tooltip markup (item and rune descriptions: &lt;stats&gt;, &lt;passive&gt;, &lt;magicDamage&gt;,
/// &lt;font color&gt;, &lt;br&gt;…) as styled inlines on a TextBlock, coloured like the League client.
/// Usage: <c>&lt;TextBlock c:RiotText.Markup="{Binding Description}" /&gt;</c>
/// </summary>
public static partial class RiotText
{
    public static readonly AttachedProperty<string?> MarkupProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Markup", typeof(RiotText));

    static RiotText()
    {
        MarkupProperty.Changed.AddClassHandler<TextBlock>((tb, e) => Apply(tb, e.NewValue as string));
    }

    public static string? GetMarkup(TextBlock element) => element.GetValue(MarkupProperty);
    public static void SetMarkup(TextBlock element, string? value) => element.SetValue(MarkupProperty, value);

    private sealed record Style(IBrush? Brush, bool Bold, bool Italic);

    private static readonly Dictionary<string, Style> Styles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["attention"] = new(Brush("#F0E6D2"), true, false),
        ["b"] = new(null, true, false),
        ["i"] = new(null, false, true),
        ["passive"] = new(Brush("#C8AA6E"), true, false),
        ["active"] = new(Brush("#C8AA6E"), true, false),
        ["unique"] = new(Brush("#C8AA6E"), true, false),
        ["spellName"] = new(Brush("#C8AA6E"), true, false),
        ["spellPassive"] = new(Brush("#C8AA6E"), true, false),
        ["keywordMajor"] = new(Brush("#E3C98A"), false, false),
        ["keyword"] = new(Brush("#E3C98A"), false, false),
        ["keywordStealth"] = new(Brush("#B48AE8"), false, false),
        ["rarityLegendary"] = new(Brush("#E08A3C"), true, false),
        ["rarityMythic"] = new(Brush("#E06A3C"), true, false),
        ["rarityGeneric"] = new(Brush("#C8AA6E"), true, false),
        ["magicDamage"] = new(Brush("#6FA8F0"), false, false),
        ["physicalDamage"] = new(Brush("#F29D5B"), false, false),
        ["trueDamage"] = new(Brush("#F5F5F5"), true, false),
        ["healing"] = new(Brush("#62D27F"), false, false),
        ["health"] = new(Brush("#62D27F"), false, false),
        ["scaleHealth"] = new(Brush("#62D27F"), false, false),
        ["shield"] = new(Brush("#9CC0E8"), false, false),
        ["lifeSteal"] = new(Brush("#E06B6B"), false, false),
        ["omnivamp"] = new(Brush("#E06B6B"), false, false),
        ["speed"] = new(Brush("#E2D47F"), false, false),
        ["MS"] = new(Brush("#E2D47F"), false, false),
        ["status"] = new(Brush("#B48AE8"), false, false),
        ["scaleAD"] = new(Brush("#F29D5B"), false, false),
        ["scaleAP"] = new(Brush("#6FA8F0"), false, false),
        ["scaleArmor"] = new(Brush("#E8C26A"), false, false),
        ["scaleMR"] = new(Brush("#A78BF0"), false, false),
        ["scaleMana"] = new(Brush("#5DA9F2"), false, false),
        ["scaleLevel"] = new(Brush("#C8C3B5"), false, false),
        ["flavorText"] = new(Brush("#8B877C"), false, true),
        ["rules"] = new(Brush("#8B877C"), false, true),
        ["danger"] = new(Brush("#E8556A"), false, false),
        ["gold"] = new(Brush("#E8C26A"), false, false),
        ["onhit"] = new(Brush("#C8C3B5"), false, false),
    };

    [GeneratedRegex(@"<(/?)([a-zA-Z][\w-]*)([^>]*?)(/?)>|([^<]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"color\s*=\s*['""]?(#[0-9a-fA-F]{6})", RegexOptions.CultureInvariant)]
    private static partial Regex ColorRegex();

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static void Apply(TextBlock tb, string? markup)
    {
        tb.Inlines ??= [];
        tb.Inlines.Clear();
        if (string.IsNullOrEmpty(markup))
        {
            tb.Text = null;
            return;
        }
        foreach (var inline in Parse(markup)) tb.Inlines.Add(inline);
    }

    /// <summary>Converts markup to plain text (for search or accessibility).</summary>
    public static string ToPlainText(string? markup)
    {
        if (string.IsNullOrEmpty(markup)) return "";
        var text = Regex.Replace(markup, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    internal static List<Inline> Parse(string markup)
    {
        var inlines = new List<Inline>();
        var stack = new List<(string Tag, Style Style)>();
        var pendingBreaks = 0;

        void Break()
        {
            if (inlines.Count > 0) pendingBreaks++;
        }

        foreach (Match m in TokenRegex().Matches(markup))
        {
            if (m.Groups[5].Success)
            {
                var text = WebUtility.HtmlDecode(m.Groups[5].Value);
                if (text.Length == 0) continue;
                if (pendingBreaks > 0 && string.IsNullOrWhiteSpace(text)) continue;
                for (var i = 0; i < Math.Min(pendingBreaks, 2); i++) inlines.Add(new LineBreak());
                pendingBreaks = 0;
                var run = new Run(text);
                IBrush? brush = null;
                bool bold = false, italic = false;
                foreach (var (_, s) in stack)
                {
                    brush = s.Brush ?? brush;
                    bold |= s.Bold;
                    italic |= s.Italic;
                }
                if (brush is not null) run.Foreground = brush;
                if (bold) run.FontWeight = FontWeight.SemiBold;
                if (italic) run.FontStyle = FontStyle.Italic;
                inlines.Add(run);
                continue;
            }

            var closing = m.Groups[1].Value == "/";
            var tag = m.Groups[2].Value;
            var attrs = m.Groups[3].Value;
            var selfClosing = m.Groups[4].Value == "/";

            switch (tag.ToLowerInvariant())
            {
                case "br":
                    Break();
                    continue;
                case "hr":
                    Break();
                    Break();
                    continue;
                case "li":
                    if (!closing)
                    {
                        Break();
                        inlines.Add(new Run(new string(' ', 0) + "• "));
                        pendingBreaks = 0;
                    }
                    continue;
            }

            if (closing)
            {
                var index = stack.FindLastIndex(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) stack.RemoveRange(index, stack.Count - index);
                continue;
            }
            if (selfClosing) continue;

            if (string.Equals(tag, "font", StringComparison.OrdinalIgnoreCase))
            {
                var color = ColorRegex().Match(attrs);
                stack.Add((tag, new Style(color.Success ? Brush(color.Groups[1].Value) : null, false, false)));
            }
            else
            {
                stack.Add((tag, Styles.TryGetValue(tag, out var style) ? style : new Style(null, false, false)));
            }
        }
        return inlines;
    }
}
