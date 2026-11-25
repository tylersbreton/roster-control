using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RosterControl.Desktop.Controls;

/// <summary>Something that can be dragged (a champion, an item…).</summary>
public interface IDragPayload
{
    string? ImageUrl { get; }
    string Label { get; }
}

/// <summary>A drop zone (a roster group, an item block, the champion pool…).</summary>
public interface IDropTarget
{
    bool CanAccept(IDragPayload payload);
    /// <summary>Short description shown on the drag ghost, e.g. "Add to Top" or "Remove from roster".</summary>
    string? Describe(IDragPayload payload, bool copy);
    /// <param name="relativeTo">Item the pointer is over (the value of <see cref="Dnd.ItemProperty"/>), or null to append.</param>
    /// <param name="after">True to insert after <paramref name="relativeTo"/>, false for before.</param>
    /// <param name="copy">Ctrl was held.</param>
    void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy);
}

/// <summary>
/// In-window drag and drop with a live ghost (portrait + action hint), drop-zone highlighting, insertion indicators,
/// edge auto-scrolling and Esc to cancel. Configured entirely with attached properties:
/// <list type="bullet">
/// <item><c>Dnd.Payload</c> on a draggable element (click without dragging runs <c>Dnd.ClickCommand</c>)</item>
/// <item><c>Dnd.Target</c> on a drop zone, with <c>Dnd.Orientation</c> for the insertion axis</item>
/// <item><c>Dnd.Item</c> on items inside a zone (for insert-before/after)</item>
/// <item><c>Dnd.IsLayer</c> on a top-level, non-hit-testable Canvas that hosts the ghost</item>
/// </list>
/// </summary>
public static class Dnd
{
    public static readonly AttachedProperty<IDragPayload?> PayloadProperty = AvaloniaProperty.RegisterAttached<Control, IDragPayload?>("Payload", typeof(Dnd));
    public static readonly AttachedProperty<IDropTarget?> TargetProperty = AvaloniaProperty.RegisterAttached<Control, IDropTarget?>("Target", typeof(Dnd));
    public static readonly AttachedProperty<object?> ItemProperty = AvaloniaProperty.RegisterAttached<Control, object?>("Item", typeof(Dnd));
    public static readonly AttachedProperty<Orientation> OrientationProperty = AvaloniaProperty.RegisterAttached<Control, Orientation>("Orientation", typeof(Dnd), Orientation.Vertical);
    public static readonly AttachedProperty<ICommand?> ClickCommandProperty = AvaloniaProperty.RegisterAttached<Control, ICommand?>("ClickCommand", typeof(Dnd));
    public static readonly AttachedProperty<bool> IsLayerProperty = AvaloniaProperty.RegisterAttached<Canvas, bool>("IsLayer", typeof(Dnd));

    public static IDragPayload? GetPayload(Control c) => c.GetValue(PayloadProperty);
    public static void SetPayload(Control c, IDragPayload? v) => c.SetValue(PayloadProperty, v);
    public static IDropTarget? GetTarget(Control c) => c.GetValue(TargetProperty);
    public static void SetTarget(Control c, IDropTarget? v) => c.SetValue(TargetProperty, v);
    public static object? GetItem(Control c) => c.GetValue(ItemProperty);
    public static void SetItem(Control c, object? v) => c.SetValue(ItemProperty, v);
    public static Orientation GetOrientation(Control c) => c.GetValue(OrientationProperty);
    public static void SetOrientation(Control c, Orientation v) => c.SetValue(OrientationProperty, v);
    public static ICommand? GetClickCommand(Control c) => c.GetValue(ClickCommandProperty);
    public static void SetClickCommand(Control c, ICommand? v) => c.SetValue(ClickCommandProperty, v);
    public static bool GetIsLayer(Canvas c) => c.GetValue(IsLayerProperty);
    public static void SetIsLayer(Canvas c, bool v) => c.SetValue(IsLayerProperty, v);

    private const double DragThreshold = 6;
    private static readonly List<WeakReference<Control>> Targets = [];
    private static readonly ConditionalWeakTable<TopLevel, Canvas> Layers = new();
    private static Session? _session;

    public static bool IsDragging => _session?.Active == true;

    /// <summary>Recent drag events (diagnostics for tests).</summary>
    internal static readonly List<string> Diagnostics = [];

    private static void Note(string message)
    {
        lock (Diagnostics)
        {
            Diagnostics.Add(message);
            if (Diagnostics.Count > 50) Diagnostics.RemoveAt(0);
        }
    }

    static Dnd()
    {
        PayloadProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            var wasSource = e.OldValue is not null || GetClickCommand(c) is not null;
            if (!wasSource && e.NewValue is not null) Hook(c);
        });
        ClickCommandProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            var wasSource = e.OldValue is not null || GetPayload(c) is not null;
            if (!wasSource && e.NewValue is not null) Hook(c);
        });
        TargetProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            lock (Targets)
            {
                Targets.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, c));
                if (e.NewValue is not null) Targets.Add(new WeakReference<Control>(c));
            }
        });
        IsLayerProperty.Changed.AddClassHandler<Canvas>((canvas, e) =>
        {
            if (e.NewValue is not true) return;
            canvas.IsHitTestVisible = false;
            canvas.AttachedToVisualTree += (_, _) =>
            {
                if (TopLevel.GetTopLevel(canvas) is { } top) Layers.AddOrUpdate(top, canvas);
            };
            if (TopLevel.GetTopLevel(canvas) is { } existing) Layers.AddOrUpdate(existing, canvas);
        });
    }

    private static void Hook(Control c)
    {
        c.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        c.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        c.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        c.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static bool IsInteractive(object? source, Control owner)
    {
        for (var v = source as Visual; v is not null && !ReferenceEquals(v, owner); v = v.GetVisualParent())
            if (v is Button or ToggleButton or TextBox or StarRating or ComboBox or Slider or NotesEditor) return true;
        return false;
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        // A pending (not yet dragging) session whose release never arrived is simply replaced.
        if (sender is not Control source || _session is { Active: true }) return;
        if (_session is not null && ReferenceEquals(_session.PressEvent, e)) return; // an inner (nested) source already took this press
        var point = e.GetCurrentPoint(source);
        var left = point.Properties.IsLeftButtonPressed || point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        Note($"pressed on {source.GetType().Name} (source {e.Source?.GetType().Name}) left={left}");
        if (!left || IsInteractive(e.Source, source)) return;
        if (TopLevel.GetTopLevel(source) is not { } root) return;
        _session = new Session(source, GetPayload(source), e.GetPosition(root), e.Pointer, root) { PressEvent = e };
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        var s = _session;
        if (s is null || !ReferenceEquals(sender, s.Source)) return;
        var pos = e.GetPosition(s.Root);
        if (!s.Active) Note($"moved to {pos} (start {s.Start})");
        if (!s.Active)
        {
            if (s.Payload is null) return;
            if (Math.Abs(pos.X - s.Start.X) < DragThreshold && Math.Abs(pos.Y - s.Start.Y) < DragThreshold) return;
            Start(s, e);
        }
        s.LastPosition = pos;
        s.Copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        Update(s);
        e.Handled = true;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var s = _session;
        if (s is null || !ReferenceEquals(sender, s.Source)) return;
        if (s.Active)
        {
            s.Copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var target = s.Target;
            var payload = s.Payload!;
            var relative = s.RelativeItem;
            var after = s.After;
            var copy = s.Copy;
            End(s);
            e.Handled = true;
            if (target is not null)
            {
                try
                {
                    target.Drop(payload, relative, after, copy);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Drop failed");
                }
            }
        }
        else
        {
            End(s);
            if (e.InitialPressMouseButton == MouseButton.Left && GetClickCommand(s.Source) is { } command)
            {
                var parameter = s.Source.DataContext;
                if (command.CanExecute(parameter)) command.Execute(parameter);
            }
        }
    }

    private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        var s = _session;
        if (s is null || !s.Active || !ReferenceEquals(sender, s.Source) || !ReferenceEquals(e.Source, s.Source)) return;
        Note("capture lost");
        End(s); // window lost focus, etc. — cancel
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var s = _session;
        if (s is null || !s.Active) return;
        if (e.Key == Key.Escape)
        {
            End(s);
            e.Handled = true;
        }
        else if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            s.Copy = true;
            Update(s);
        }
    }

    private static void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var s = _session;
        if (s is null || !s.Active) return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            s.Copy = false;
            Update(s);
        }
    }

    private static void Start(Session s, PointerEventArgs e)
    {
        s.Active = true;
        e.Pointer.Capture(s.Source);
        HideCursor(s);
        Layers.TryGetValue(s.Root, out var layer);
        s.Layer = layer;
        s.Root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        s.Root.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);

        if (layer is not null)
        {
            s.HintText = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#0AC8B9")), FontWeight = FontWeight.SemiBold };
            var label = new TextBlock { Text = s.Payload!.Label, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#F0E6D2")) };
            var image = new AssetImage { Source = s.Payload.ImageUrl, Width = 36, Height = 36, CornerRadius = new CornerRadius(6), Zoom = 1.08, Ring = new SolidColorBrush(Color.Parse("#C8AA6E")) };
            s.Ghost = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#E6152031")),
                BorderBrush = new SolidColorBrush(Color.Parse("#785A28")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 6, 12, 6),
                BoxShadow = BoxShadows.Parse("0 10 28 -6 #C0000000"),
                IsHitTestVisible = false,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 9,
                    Children =
                    {
                        image,
                        new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { label, s.HintText } },
                    },
                },
            };
            s.Indicator = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0AC8B9")),
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false,
                IsVisible = false,
                BoxShadow = BoxShadows.Parse("0 0 8 0 #800AC8B9"),
            };
            layer.Children.Add(s.Indicator);
            layer.Children.Add(s.Ghost);
        }

        // Highlight every zone that accepts this payload.
        lock (Targets)
        {
            foreach (var weak in Targets.ToList())
            {
                if (!weak.TryGetTarget(out var c)) continue;
                if (!ReferenceEquals(TopLevel.GetTopLevel(c), s.Root) || !c.IsEffectivelyVisible) continue;
                if (GetTarget(c) is { } t && t.CanAccept(s.Payload!))
                {
                    c.Classes.Add("dnd-can-drop");
                    s.Highlighted.Add(c);
                }
            }
        }

        s.ScrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => AutoScroll(s));
        s.ScrollTimer.Start();
    }

    private static void Update(Session s)
    {
        var pos = s.LastPosition;
        var hit = s.Root.InputHitTest(pos);
        Control? itemElement = null;
        Control? targetElement = null;
        IDropTarget? target = null;
        for (var v = hit as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is not Control c) continue;
            if (itemElement is null && c.IsSet(ItemProperty)) itemElement = c;
            if (GetTarget(c) is { } t && t.CanAccept(s.Payload!))
            {
                target = t;
                targetElement = c;
                break;
            }
        }

        if (!ReferenceEquals(targetElement, s.TargetElement))
        {
            s.TargetElement?.Classes.Remove("dnd-over");
            targetElement?.Classes.Add("dnd-over");
            s.TargetElement = targetElement;
        }
        s.Target = target;
        s.RelativeItem = null;
        s.After = false;

        var layerPos = s.Layer is not null ? s.Root.TranslatePoint(pos, s.Layer) ?? pos : pos;
        if (s.Indicator is not null) s.Indicator.IsVisible = false;
        if (target is not null && itemElement is not null && targetElement is not null && s.Layer is not null
            && itemElement.TranslatePoint(new Point(0, 0), s.Layer) is { } topLeft)
        {
            var size = itemElement.Bounds.Size;
            var horizontal = GetOrientation(targetElement) == Orientation.Horizontal;
            s.RelativeItem = GetItem(itemElement);
            s.After = horizontal ? layerPos.X > topLeft.X + size.Width / 2 : layerPos.Y > topLeft.Y + size.Height / 2;
            if (s.Indicator is not null && s.RelativeItem is not null)
            {
                if (horizontal)
                {
                    s.Indicator.Width = 3;
                    s.Indicator.Height = size.Height;
                    Canvas.SetLeft(s.Indicator, (s.After ? topLeft.X + size.Width : topLeft.X) - 2.5);
                    Canvas.SetTop(s.Indicator, topLeft.Y);
                }
                else
                {
                    s.Indicator.Width = size.Width;
                    s.Indicator.Height = 3;
                    Canvas.SetLeft(s.Indicator, topLeft.X);
                    Canvas.SetTop(s.Indicator, (s.After ? topLeft.Y + size.Height : topLeft.Y) - 2.5);
                }
                s.Indicator.IsVisible = true;
            }
        }

        if (s.Ghost is not null)
        {
            // The cursor is hidden while dragging: the champion icon itself sits under the pointer.
            Canvas.SetLeft(s.Ghost, layerPos.X - GhostAnchor.X);
            Canvas.SetTop(s.Ghost, layerPos.Y - GhostAnchor.Y);
            s.Ghost.Opacity = target is null ? 0.7 : 1;
        }
        if (s.HintText is not null)
        {
            var hint = target?.Describe(s.Payload!, s.Copy);
            s.HintText.Text = hint ?? "Drop on a highlighted area";
            s.HintText.Foreground = target is null ? new SolidColorBrush(Color.Parse("#7D7A70")) : new SolidColorBrush(Color.Parse("#0AC8B9"));
        }
    }

    private static void AutoScroll(Session s)
    {
        if (!s.Active) return;
        var hit = s.Root.InputHitTest(s.LastPosition) as Visual;
        var scroller = hit?.FindAncestorOfType<ScrollViewer>(includeSelf: true);
        while (scroller is not null)
        {
            var p = s.Root.TranslatePoint(s.LastPosition, scroller);
            if (p is { } local)
            {
                const double edge = 36, step = 14;
                var dy = local.Y < edge ? -step : local.Y > scroller.Bounds.Height - edge ? step : 0;
                var dx = local.X < edge ? -step : local.X > scroller.Bounds.Width - edge ? step : 0;
                var canV = scroller.Extent.Height > scroller.Viewport.Height && dy != 0;
                var canH = scroller.Extent.Width > scroller.Viewport.Width && dx != 0;
                if (canV || canH)
                {
                    scroller.Offset = new Vector(
                        Math.Clamp(scroller.Offset.X + (canH ? dx : 0), 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width)),
                        Math.Clamp(scroller.Offset.Y + (canV ? dy : 0), 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
                    Update(s);
                    return;
                }
            }
            scroller = scroller.GetVisualParent()?.FindAncestorOfType<ScrollViewer>(includeSelf: true);
        }
    }

    private static void End(Session s)
    {
        Note($"end (active={s.Active})");
        _session = null;
        if (!s.Active) return;
        s.Active = false;
        s.ScrollTimer?.Stop();
        s.Root.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        s.Root.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        if (s.Layer is not null)
        {
            if (s.Ghost is not null) s.Layer.Children.Remove(s.Ghost);
            if (s.Indicator is not null) s.Layer.Children.Remove(s.Indicator);
        }
        s.TargetElement?.Classes.Remove("dnd-over");
        foreach (var c in s.Highlighted) c.Classes.Remove("dnd-can-drop");
        if (ReferenceEquals(s.Pointer.Captured, s.Source)) s.Pointer.Capture(null);
        RestoreCursor(s);
    }

    /// <summary>Centre of the ghost's champion icon (padding 6 + half of the 36 px image).</summary>
    private static readonly Point GhostAnchor = new(6 + 18, 6 + 18);

    private static readonly Cursor NoCursor = new(StandardCursorType.None);

    /// <summary>
    /// The visible cursor comes from the element under the pointer, which during a drag is the captured source; the
    /// window is set too in case capture is lost. Original values are restored afterwards.
    /// </summary>
    private static void HideCursor(Session s)
    {
        foreach (var element in new InputElement[] { s.Source, s.Root })
        {
            s.SavedCursors.Add((element, element.IsSet(InputElement.CursorProperty), element.Cursor));
            element.Cursor = NoCursor;
        }
    }

    private static void RestoreCursor(Session s)
    {
        foreach (var (element, wasSet, cursor) in s.SavedCursors)
        {
            if (wasSet) element.Cursor = cursor;
            else element.ClearValue(InputElement.CursorProperty);
        }
        s.SavedCursors.Clear();
    }

    private sealed class Session(Control source, IDragPayload? payload, Point start, IPointer pointer, TopLevel root)
    {
        public Control Source { get; } = source;
        public IDragPayload? Payload { get; } = payload;
        public Point Start { get; } = start;
        public IPointer Pointer { get; } = pointer;
        public TopLevel Root { get; } = root;
        public bool Active { get; set; }
        public PointerPressedEventArgs? PressEvent { get; init; }
        public Point LastPosition { get; set; }
        public bool Copy { get; set; }
        public Canvas? Layer { get; set; }
        public Border? Ghost { get; set; }
        public TextBlock? HintText { get; set; }
        public Border? Indicator { get; set; }
        public Control? TargetElement { get; set; }
        public IDropTarget? Target { get; set; }
        public object? RelativeItem { get; set; }
        public bool After { get; set; }
        public List<Control> Highlighted { get; } = [];
        public List<(InputElement Element, bool WasSet, Cursor? Cursor)> SavedCursors { get; } = [];
        public DispatcherTimer? ScrollTimer { get; set; }
    }
}
