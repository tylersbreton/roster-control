using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RosterControl.Desktop.Services;

namespace RosterControl.Desktop.Controls;

/// <summary>Renders a Lucide stroke icon (24×24 design grid) at any size with a crisp stroke.</summary>
public sealed class LucideIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<LucideIcon, string?>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<LucideIcon, IBrush?>(nameof(Foreground), inherits: true);
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<LucideIcon, double>(nameof(StrokeWidth), 2.0);
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<LucideIcon, IBrush?>(nameof(Fill));

    static LucideIcon() => AffectsRender<LucideIcon>(KindProperty, ForegroundProperty, StrokeWidthProperty, FillProperty);

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    /// <summary>Optional fill (e.g. a filled star).</summary>
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Kind is null || Foreground is null) return;
        if (!this.TryFindResource("Icon." + Kind, out var res) || res is not Geometry geometry) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var scale = size / 24.0;
        var offset = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            var pen = new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(Fill, pen, geometry);
        }
    }
}

/// <summary>Displays a cached remote image (champion portrait, item, rune, splash…), loading it in the background.</summary>
public sealed class AssetImage : Control
{
    public static AssetImageService? Images { get; set; }

    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<AssetImage, string?>(nameof(Source));
    public static readonly StyledProperty<string?> FallbackSourceProperty = AvaloniaProperty.Register<AssetImage, string?>(nameof(FallbackSource));
    public static readonly StyledProperty<int> DecodeWidthProperty = AvaloniaProperty.Register<AssetImage, int>(nameof(DecodeWidth), 96);
    public static readonly StyledProperty<bool> GrayscaleProperty = AvaloniaProperty.Register<AssetImage, bool>(nameof(Grayscale));
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = AvaloniaProperty.Register<AssetImage, CornerRadius>(nameof(CornerRadius), new CornerRadius(6));
    public static readonly StyledProperty<IBrush?> RingProperty = AvaloniaProperty.Register<AssetImage, IBrush?>(nameof(Ring));
    public static readonly StyledProperty<double> RingThicknessProperty = AvaloniaProperty.Register<AssetImage, double>(nameof(RingThickness), 1.5);
    public static readonly StyledProperty<string?> FallbackTextProperty = AvaloniaProperty.Register<AssetImage, string?>(nameof(FallbackText));
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<AssetImage, double>(nameof(Zoom), 1.0);
    /// <summary>Vertical focus for cropped images (0 = top, 0.5 = centre).</summary>
    public static readonly StyledProperty<double> FocusYProperty = AvaloniaProperty.Register<AssetImage, double>(nameof(FocusY), 0.5);
    public static readonly StyledProperty<bool> ShowPlaceholderProperty = AvaloniaProperty.Register<AssetImage, bool>(nameof(ShowPlaceholder), true);

    private Bitmap? _bitmap;

    /// <summary>The image has been loaded (used to wait before capturing an off-screen image).</summary>
    public bool HasImage => _bitmap is not null;
    private string? _loadedKey;

    static AssetImage()
    {
        AffectsRender<AssetImage>(RingProperty, CornerRadiusProperty, FallbackTextProperty, RingThicknessProperty, ZoomProperty, FocusYProperty);
    }

    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string? FallbackSource { get => GetValue(FallbackSourceProperty); set => SetValue(FallbackSourceProperty, value); }
    public int DecodeWidth { get => GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }
    public bool Grayscale { get => GetValue(GrayscaleProperty); set => SetValue(GrayscaleProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public IBrush? Ring { get => GetValue(RingProperty); set => SetValue(RingProperty, value); }
    public double RingThickness { get => GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
    public string? FallbackText { get => GetValue(FallbackTextProperty); set => SetValue(FallbackTextProperty, value); }
    /// <summary>Crops into the image (Data Dragon champion squares have a border; 1.1 hides it).</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double FocusY { get => GetValue(FocusYProperty); set => SetValue(FocusYProperty, value); }
    public bool ShowPlaceholder { get => GetValue(ShowPlaceholderProperty); set => SetValue(ShowPlaceholderProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == GrayscaleProperty || change.Property == DecodeWidthProperty || change.Property == FallbackSourceProperty)
            Load();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Load();
    }

    private void Load()
    {
        var source = Source;
        var key = source is null ? null : AssetImageService.Key(source, DecodeWidth, Grayscale);
        if (key == _loadedKey) return;
        _loadedKey = key;
        if (Images is null || source is null)
        {
            _bitmap = null;
            InvalidateVisual();
            return;
        }
        var immediate = Images.TryGet(source, DecodeWidth, Grayscale);
        if (immediate is not null)
        {
            _bitmap = immediate;
            InvalidateVisual();
            return;
        }
        // Keep the previous image until the new one arrives (no flicker).
        var requested = key;
        var fallback = FallbackSource;
        var width = DecodeWidth;
        var gray = Grayscale;
        _ = Task.Run(async () =>
        {
            var bmp = await Images.GetAsync(source, width, gray).ConfigureAwait(false);
            if (bmp is null && fallback is not null) bmp = await Images.GetAsync(fallback, width, gray).ConfigureAwait(false);
            return bmp;
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_loadedKey != requested) return;
                _bitmap = t.IsCompletedSuccessfully ? t.Result : null;
                InvalidateVisual();
            });
        }, TaskScheduler.Default);
    }

    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.Parse("#152031"));
    private static readonly IBrush PlaceholderText = new SolidColorBrush(Color.Parse("#7D7A70"));

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var rounded = new RoundedRect(rect, CornerRadius);
        using (context.PushClip(rounded))
        {
            if (_bitmap is not null)
            {
                // Uniform-to-fill with optional zoom (crop) and vertical focus.
                var src = _bitmap.Size;
                var scale = Math.Max(rect.Width / src.Width, rect.Height / src.Height) * Math.Max(1, Zoom);
                var w = rect.Width / scale;
                var h = rect.Height / scale;
                var x = (src.Width - w) / 2;
                var y = Math.Clamp((src.Height - h) * FocusY, 0, Math.Max(0, src.Height - h));
                context.DrawImage(_bitmap, new Rect(x, y, w, h), rect);
            }
            else if (ShowPlaceholder)
            {
                context.FillRectangle(PlaceholderBrush, rect);
                if (!string.IsNullOrEmpty(FallbackText))
                {
                    var text = new FormattedText(FallbackText[..Math.Min(2, FallbackText.Length)].ToUpperInvariant(), System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold), Math.Max(8, rect.Height * 0.34), PlaceholderText);
                    context.DrawText(text, new Point((rect.Width - text.Width) / 2, (rect.Height - text.Height) / 2));
                }
            }
        }
        if (Ring is not null && RingThickness > 0)
            context.DrawRectangle(null, new Pen(Ring, RingThickness), rounded.Deflate(RingThickness / 2, RingThickness / 2));
    }
}

/// <summary>Five-star proficiency rating. Click a star to set it; click the current value again to clear.</summary>
public sealed class StarRating : Control
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<StarRating, int>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> StarSizeProperty = AvaloniaProperty.Register<StarRating, double>(nameof(StarSize), 12);
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<StarRating, bool>(nameof(IsReadOnly));
    public static readonly StyledProperty<IBrush?> FilledBrushProperty = AvaloniaProperty.Register<StarRating, IBrush?>(nameof(FilledBrush), new SolidColorBrush(Color.Parse("#E8C26A")));
    public static readonly StyledProperty<IBrush?> EmptyBrushProperty = AvaloniaProperty.Register<StarRating, IBrush?>(nameof(EmptyBrush), new SolidColorBrush(Color.Parse("#3A4558")));

    private int _hover = -1;

    static StarRating()
    {
        AffectsRender<StarRating>(ValueProperty, FilledBrushProperty, EmptyBrushProperty);
        AffectsMeasure<StarRating>(StarSizeProperty);
    }

    public StarRating()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Proficiency — how good you are on this champion");
    }

    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double StarSize { get => GetValue(StarSizeProperty); set => SetValue(StarSizeProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public IBrush? FilledBrush { get => GetValue(FilledBrushProperty); set => SetValue(FilledBrushProperty, value); }
    public IBrush? EmptyBrush { get => GetValue(EmptyBrushProperty); set => SetValue(EmptyBrushProperty, value); }

    private double Gap => StarSize * 0.22;

    protected override Size MeasureOverride(Size availableSize) => new(StarSize * 5 + Gap * 4, StarSize);

    private int StarAt(Point p) => Math.Clamp((int)(p.X / (StarSize + Gap)), 0, 4) + 1;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (IsReadOnly) return;
        var star = StarAt(e.GetPosition(this));
        if (star != _hover)
        {
            _hover = star;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsReadOnly || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var star = StarAt(e.GetPosition(this));
        Value = star == Value ? 0 : star;
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var shown = _hover > 0 && !IsReadOnly ? _hover : Value;
        for (var i = 0; i < 5; i++)
        {
            var x = i * (StarSize + Gap);
            var geometry = Star(new Point(x + StarSize / 2, StarSize / 2), StarSize / 2, StarSize / 4.6);
            var filled = i < shown;
            var brush = filled ? FilledBrush : EmptyBrush;
            if (filled && _hover > 0 && _hover != Value) brush = new SolidColorBrush(Color.Parse("#B89B5C"));
            context.DrawGeometry(brush, null, geometry);
        }
        // Transparent hit area so the gaps are clickable.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
    }

    private static StreamGeometry Star(Point c, double outer, double inner)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = Math.PI / 5 * i - Math.PI / 2;
            var p = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
            if (i == 0) ctx.BeginFigure(p, true);
            else ctx.LineTo(p);
        }
        ctx.EndFigure(true);
        return g;
    }
}
