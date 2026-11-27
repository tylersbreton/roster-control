using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace RosterControl.Desktop.Controls;

/// <summary>One line on a <see cref="LevelChart"/>: a value per level 1–18 (NaN = no value at that level).</summary>
/// <param name="ColorKey">Theme brush key, e.g. "Brush.Gold".</param>
public sealed record ChartSeries(string Name, IReadOnlyList<double> Values, string ColorKey, Func<double, string> Format);

/// <summary>Something that happens at a level (an item finished), drawn as a tick on the level axis.</summary>
public sealed record ChartMarker(int Level, string Label, string ColorKey = "Brush.TextMuted");

/// <summary>
/// A line chart over champion levels 1–18 for the power curve. Hovering shows each line's value at that level and what
/// was finished there; <see cref="HighlightLevel"/> marks the calculator's level.
/// </summary>
public sealed class LevelChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<LevelChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<IReadOnlyList<ChartMarker>?> MarkersProperty =
        AvaloniaProperty.Register<LevelChart, IReadOnlyList<ChartMarker>?>(nameof(Markers));

    public static readonly StyledProperty<int> HighlightLevelProperty =
        AvaloniaProperty.Register<LevelChart, int>(nameof(HighlightLevel));

    private const int Levels = 18;
    private const double Left = 54, Right = 14, Top = 12, Bottom = 40;
    private int _hover;

    static LevelChart()
    {
        AffectsRender<LevelChart>(SeriesProperty, MarkersProperty, HighlightLevelProperty);
    }

    public LevelChart()
    {
        ClipToBounds = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IReadOnlyList<ChartMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public int HighlightLevel
    {
        get => GetValue(HighlightLevelProperty);
        set => SetValue(HighlightLevelProperty, value);
    }

    /// <summary>The level under the pointer (0 = none); exposed for tests.</summary>
    public int HoverLevel => _hover;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 220 : Math.Min(availableSize.Height, 220));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var level = LevelAt(e.GetPosition(this).X);
        if (level == _hover) return;
        _hover = level;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = 0;
        InvalidateVisual();
    }

    private double PlotWidth => Math.Max(1, Bounds.Width - Left - Right);
    private double PlotHeight => Math.Max(1, Bounds.Height - Top - Bottom);
    private double X(int level) => Left + (level - 1) * PlotWidth / (Levels - 1);

    private int LevelAt(double x)
    {
        if (x < Left - 12 || x > Bounds.Width - Right + 12) return 0;
        return Math.Clamp((int)Math.Round((x - Left) / PlotWidth * (Levels - 1)) + 1, 1, Levels);
    }

    private IBrush Brush(string key, string fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        return new SolidColorBrush(Color.Parse(fallback));
    }

    private FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var series = Series ?? [];
        var muted = Brush("Brush.TextMuted", "#7D7A70");
        var secondary = Brush("Brush.TextSecondary", "#A09B8C");
        var grid = new Pen(Brush("Brush.BorderSubtle", "#2A2E35"), 1);
        var axis = new Pen(Brush("Brush.Border", "#3C3C41"), 1);

        // Background so the pointer is tracked over the whole chart.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var values = series.SelectMany(s => s.Values).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
        if (values.Count == 0)
        {
            var empty = Text("Nothing to draw for this number.", 12, muted);
            context.DrawText(empty, new Point((Bounds.Width - empty.Width) / 2, (Bounds.Height - empty.Height) / 2));
            return;
        }
        var (min, max, step) = NiceRange(values.Min(), values.Max());
        double Y(double v) => Top + PlotHeight - (v - min) / (max - min) * PlotHeight;
        var format = series[0].Format;

        // Grid and value labels.
        for (var v = min; v <= max + step / 2; v += step)
        {
            var y = Y(v);
            context.DrawLine(grid, new Point(Left, y), new Point(Left + PlotWidth, y));
            var label = Text(format(v), 10.5, muted);
            context.DrawText(label, new Point(Left - 8 - label.Width, y - label.Height / 2));
        }
        // Level axis.
        context.DrawLine(axis, new Point(Left, Top + PlotHeight), new Point(Left + PlotWidth, Top + PlotHeight));
        for (var level = 1; level <= Levels; level++)
        {
            var x = X(level);
            var strong = level == HighlightLevel || level == _hover;
            var label = Text(level.ToString(CultureInfo.InvariantCulture), 10.5, strong ? Brush("Brush.TextPrimary", "#F0E6D2") : muted, strong ? FontWeight.Bold : FontWeight.Normal);
            context.DrawText(label, new Point(x - label.Width / 2, Top + PlotHeight + 5));
        }

        // Item markers: a small diamond under the axis at the level each item is finished.
        var markers = Markers ?? [];
        foreach (var group in markers.GroupBy(m => m.Level))
        {
            var x = X(group.Key);
            var y = Top + PlotHeight + 26;
            var brush = Brush(group.First().ColorKey, "#C8AA6E");
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(x, y - 4.5), true);
                g.LineTo(new Point(x + 4.5, y));
                g.LineTo(new Point(x, y + 4.5));
                g.LineTo(new Point(x - 4.5, y));
                g.EndFigure(true);
            }
            context.DrawGeometry(brush, null, geometry);
            if (group.Count() > 1)
            {
                var count = Text(group.Count().ToString(CultureInfo.InvariantCulture), 9, secondary);
                context.DrawText(count, new Point(x + 6, y - count.Height / 2));
            }
            context.DrawLine(new Pen(brush, 1, new DashStyle([2, 3], 0)) { }, new Point(x, Top), new Point(x, Top + PlotHeight));
        }

        // The calculator's level.
        if (HighlightLevel is >= 1 and <= Levels)
        {
            var x = X(HighlightLevel);
            context.FillRectangle(Brush("Brush.GoldSoft", "#22C8AA6E"), new Rect(x - 7, Top, 14, PlotHeight));
        }

        // Lines.
        foreach (var s in series)
        {
            var pen = new Pen(Brush(s.ColorKey, "#C8AA6E"), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            Point? previous = null;
            for (var level = 1; level <= Math.Min(Levels, s.Values.Count); level++)
            {
                var v = s.Values[level - 1];
                if (double.IsNaN(v))
                {
                    previous = null;
                    continue;
                }
                var p = new Point(X(level), Y(v));
                if (previous is { } prev) context.DrawLine(pen, prev, p);
                previous = p;
            }
            for (var level = 1; level <= Math.Min(Levels, s.Values.Count); level++)
            {
                var v = s.Values[level - 1];
                if (double.IsNaN(v)) continue;
                var r = level == _hover || level == HighlightLevel ? 4 : 2.4;
                context.DrawEllipse(pen.Brush, null, new Point(X(level), Y(v)), r, r);
            }
        }

        if (_hover > 0) DrawHover(context, series, markers, muted);
    }

    private void DrawHover(DrawingContext context, IReadOnlyList<ChartSeries> series, IReadOnlyList<ChartMarker> markers, IBrush muted)
    {
        var x = X(_hover);
        context.DrawLine(new Pen(Brush("Brush.BorderStrong", "#5B5A56"), 1), new Point(x, Top), new Point(x, Top + PlotHeight));

        var lines = new List<(string Text, IBrush Brush, bool Bold)> { ($"Level {_hover}", Brush("Brush.TextPrimary", "#F0E6D2"), true) };
        foreach (var s in series)
        {
            var v = _hover <= s.Values.Count ? s.Values[_hover - 1] : double.NaN;
            var prefix = series.Count > 1 ? s.Name + ": " : "";
            lines.Add((prefix + (double.IsNaN(v) ? "—" : s.Format(v)), Brush(s.ColorKey, "#C8AA6E"), false));
        }
        foreach (var m in markers.Where(m => m.Level == _hover))
            lines.Add(("Finished: " + m.Label, muted, false));

        var texts = lines.Select(l => Text(l.Text, 11.5, l.Brush, l.Bold ? FontWeight.SemiBold : FontWeight.Normal)).ToList();
        var width = texts.Max(t => t.Width) + 16;
        var height = texts.Sum(t => t.Height + 2) + 10;
        var left = x + 12 + width > Bounds.Width ? x - 12 - width : x + 12;
        var box = new Rect(Math.Max(0, left), Top + 4, width, height);
        context.DrawRectangle(Brush("Brush.SurfaceElevated", "#1E2328"), new Pen(Brush("Brush.Border", "#3C3C41"), 1), box, 5, 5);
        var y = box.Top + 5;
        foreach (var t in texts)
        {
            context.DrawText(t, new Point(box.Left + 8, y));
            y += t.Height + 2;
        }
    }

    /// <summary>A rounded axis range with about four steps (starting at 0 when that's close to the data).</summary>
    internal static (double Min, double Max, double Step) NiceRange(double low, double high)
    {
        if (low >= 0 && low < high * 0.4) low = 0;
        if (Math.Abs(high - low) < 1e-9)
        {
            var pad = Math.Abs(high) < 1e-9 ? 1 : Math.Abs(high) * 0.1;
            low -= pad;
            high += pad;
            if (low < 0 && high > 0 && low > -pad * 1.5) low = Math.Max(low, 0);
        }
        var raw = (high - low) / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        var min = Math.Floor(low / step) * step;
        var max = Math.Ceiling(high / step) * step;
        if (max - min < step) max = min + step;
        return (min, max, step);
    }
}
