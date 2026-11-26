using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RosterControl.Data;
using RosterControl.Desktop.ViewModels;

namespace RosterControl.Desktop.Views;

public partial class MainWindow : Window
{
    private const string PlacementKey = "ui.window";
    private const double CaptionFallbackWidth = 196; // four 46 px caption buttons + a little breathing room
    private static readonly string[] CaptionButtonNames = ["PART_FullScreenButton", "PART_MinimizeButton", "PART_MaximizeButton", "PART_CloseButton"];

    private readonly PreferencesStore? _preferences;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _captionLogged;

    public MainWindow()
    {
        InitializeComponent();
        ExitFullScreenButton.Click += (_, _) => SetFullScreen(false);
        LayoutUpdated += (_, _) => UpdateCaptionArea();
        // Tunnel: global shortcuts are seen before text boxes or lists consume the keys.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.Palette.IsOpen)
        {
            OnPaletteKeyDown(sender, e);
            return;
        }
        if (e.Key is Key.F11 or Key.Escape) return; // handled in OnKeyDown (full screen, dialogs)
        var typing = FocusManager?.GetFocusedElement() is TextBox;
        if (vm.HandleShortcut(e.Key, e.KeyModifiers, typing)) e.Handled = true;
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        switch (e.Key)
        {
            case Key.Down:
                vm.Palette.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Palette.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.Palette.ExecuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                vm.Palette.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.Palette.Close();
    }

    public MainWindow(PreferencesStore preferences) : this()
    {
        _preferences = preferences;
        RestorePlacement();
        // Remember the last normal (not maximised / full screen) bounds, so closing while maximised keeps them.
        PositionChanged += (_, _) => TrackNormalBounds();
        SizeChanged += (_, _) => TrackNormalBounds();
        TitleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Button) return;
            if (WindowState == WindowState.FullScreen) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm) return;
        vm.AttachTopLevel(() => this);
        vm.PagesReset += (_, _) => Pages.Clear();
        vm.Palette.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && vm.Palette.IsOpen)
                Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
        };
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.UiScale)) UpdateCaptionArea();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            // Full screen can also be entered from the window's own caption button: always offer a visible way out.
            ExitFullScreenButton.IsVisible = WindowState == WindowState.FullScreen;
            UpdateCaptionArea();
        }
    }

    // ── Full screen ────────────────────────────────────────────────────────────────────────────────

    private void SetFullScreen(bool on)
    {
        if (on == (WindowState == WindowState.FullScreen)) return;
        if (on)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _stateBeforeFullScreen == WindowState.FullScreen ? WindowState.Normal : _stateBeforeFullScreen;
        }
    }

    /// <summary>
    /// Keeps the title bar's right edge clear of the caption buttons. Avalonia draws them itself (full screen, minimise,
    /// maximise, close), so measure where they start instead of assuming a fixed width.
    /// </summary>
    private void UpdateCaptionArea()
    {
        if (TitleGrid.ColumnDefinitions.Count < 6) return;
        double reserved;
        if (WindowState == WindowState.FullScreen)
        {
            reserved = 12; // caption buttons are hidden in full screen (they appear in a popover at the top edge)
        }
        else
        {
            Visual root = this;
            while (root.GetVisualParent() is { } parent) root = parent;
            var left = double.MaxValue;
            foreach (var button in root.GetVisualDescendants().OfType<Control>())
            {
                if (button.Name is not { } name || !CaptionButtonNames.Contains(name) || !button.IsEffectivelyVisible || button.Bounds.Width <= 0) continue;
                if (button.TranslatePoint(new Point(0, 0), this) is { } p) left = Math.Min(left, p.X);
            }
            reserved = left == double.MaxValue ? CaptionFallbackWidth : Math.Max(0, Bounds.Width - left) + 10;
            // The title bar sits inside the UI zoom; convert window pixels to title bar units.
            if (DataContext is MainWindowViewModel { UiScale: > 0 } scaled) reserved /= scaled.UiScale;
            if (!_captionLogged && left != double.MaxValue)
            {
                _captionLogged = true;
                Serilog.Log.Information("Caption buttons start at {Left:0} (window {Width:0}); reserving {Reserved:0}px", left, Bounds.Width, reserved);
            }
        }
        var column = TitleGrid.ColumnDefinitions[5];
        if (Math.Abs(column.Width.Value - reserved) > 0.5) column.Width = new GridLength(reserved);
    }

    // ── Keyboard & mouse ───────────────────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var vm = DataContext as MainWindowViewModel;
        if (vm?.Dialog is { } dialog && e.Key == Key.Escape)
        {
            dialog.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm?.WhatsNew is { } whatsNew && e.Key == Key.Escape)
        {
            whatsNew.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && WindowState != WindowState.FullScreen && vm?.CurrentPage is IKeyboardPage page
                 && page.HandleKey(Key.Escape, e.KeyModifiers, FocusManager?.GetFocusedElement() is TextBox))
        {
            e.Handled = true; // e.g. clears the roster selection
        }
        else if (e.Key == Key.F11)
        {
            SetFullScreen(WindowState != WindowState.FullScreen);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            SetFullScreen(false);
            e.Handled = true;
        }
        else if (vm is not null && ((e.Key == Key.Back && e.KeyModifiers == KeyModifiers.Alt) || e.Key == Key.BrowserBack))
        {
            vm.GoBackCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed && DataContext is MainWindowViewModel vm)
            vm.GoBackCommand.Execute(null);
    }

    // ── Placement ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Placement(int X, int Y, double Width, double Height, bool Maximized);

    private Placement? _normal;

    private void TrackNormalBounds()
    {
        if (WindowState == WindowState.Normal && IsVisible && Width > 0 && Height > 0)
            _normal = new Placement(Position.X, Position.Y, Width, Height, false);
    }

    private void RestorePlacement()
    {
        var p = _preferences?.Get<Placement?>(PlacementKey, null);
        if (p is null || p.Width < 200 || p.Height < 150) return;
        // Only restore where the title bar is reachable (a monitor may have been unplugged or rearranged).
        var titleBar = new PixelRect(p.X, p.Y, Math.Max(1, (int)p.Width), 40);
        var reachable = Screens.All.Any(s =>
        {
            var visible = s.WorkingArea.Intersect(titleBar);
            return visible.Width >= 120 && visible.Height >= 20;
        });
        if (!reachable)
        {
            // Keep the size and maximised state, centred on the main screen.
            Width = Math.Max(MinWidth, p.Width);
            Height = Math.Max(MinHeight, p.Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (p.Maximized) WindowState = WindowState.Maximized;
            return;
        }
        _normal = p with { Maximized = false };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(p.X, p.Y);
        Width = Math.Max(MinWidth, p.Width);
        Height = Math.Max(MinHeight, p.Height);
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_preferences is not null)
        {
            // Never reopen in full screen; when maximised keep the last normal size so un-maximising restores it.
            var state = WindowState == WindowState.FullScreen ? _stateBeforeFullScreen : WindowState;
            var maximized = state == WindowState.Maximized;
            var placement = WindowState == WindowState.Normal
                ? new Placement(Position.X, Position.Y, Width, Height, false)
                : (_normal ?? _preferences.Get<Placement?>(PlacementKey, null) ?? new Placement(Position.X, Position.Y, Width, Height, false)) with { Maximized = maximized };
            _preferences.Set(PlacementKey, placement);
        }
        base.OnClosing(e);
    }
}
