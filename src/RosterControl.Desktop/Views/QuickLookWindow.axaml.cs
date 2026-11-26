using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using RosterControl.Data;
using RosterControl.Desktop.ViewModels;

namespace RosterControl.Desktop.Views;

/// <summary>
/// The always-on-top quick-look window. Closing only hides it (the main window owns it for the whole session); it
/// remembers where it was and reloads builds when it gets focus, in case they were edited in the main window.
/// </summary>
public partial class QuickLookWindow : Window
{
    private const string PlacementKey = "quicklook.window";

    private readonly PreferencesStore? _preferences;
    private bool _allowClose;

    public QuickLookWindow()
    {
        InitializeComponent();
    }

    public QuickLookWindow(PreferencesStore preferences) : this()
    {
        _preferences = preferences;
        RestorePlacement();
        Activated += (_, _) =>
        {
            if (DataContext is QuickLookViewModel vm) _ = vm.RefreshAsync();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is QuickLookViewModel vm) vm.FocusSearchRequested += (_, _) => FocusSearch();
    }

    public void FocusSearch() => Dispatcher.UIThread.Post(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }, DispatcherPriority.Input);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape) return;
        if (DataContext is QuickLookViewModel { Search.Length: > 0 } vm) vm.Search = ""; // first Esc clears the search
        else Hide();
        e.Handled = true;
    }

    /// <summary>Really closes it (when the app exits).</summary>
    public void CloseForGood()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SavePlacement();
        if (!_allowClose && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true; // keep it for next time: the shortcut shows it again instantly
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    // ── Placement ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Placement(int X, int Y, double Width, double Height);

    private void SavePlacement()
    {
        if (_preferences is null || WindowState != WindowState.Normal || Width <= 0 || Height <= 0) return;
        _preferences.Set(PlacementKey, new Placement(Position.X, Position.Y, Width, Height));
    }

    private void RestorePlacement()
    {
        var p = _preferences?.Get<Placement?>(PlacementKey, null);
        if (p is null || p.Width < MinWidth || p.Height < MinHeight) return;
        Width = p.Width;
        Height = p.Height;
        var titleBar = new PixelRect(p.X, p.Y, Math.Max(1, (int)p.Width), 32);
        if (!Screens.All.Any(s => s.WorkingArea.Intersect(titleBar) is { Width: >= 100, Height: >= 16 })) return; // that monitor is gone
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(p.X, p.Y);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is false) SavePlacement();
    }
}
