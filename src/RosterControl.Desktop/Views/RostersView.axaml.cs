using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RosterControl.Desktop.ViewModels;

namespace RosterControl.Desktop.Views;

public partial class RostersView : UserControl
{
    private const double MinColumnWidth = 198;
    private const double MaxColumnWidth = 300;
    private const double ColumnGap = 12;
    private RostersViewModel? _vm;

    public RostersView()
    {
        InitializeComponent();
        GroupsScroller.SizeChanged += (_, _) => UpdateColumnWidth();
        HeaderGrid.SizeChanged += (_, _) => UpdateHeaderExtras();
    }

    private double _extrasWidth;

    /// <summary>Shows the priority legend only when it fits next to the title and buttons.</summary>
    private void UpdateHeaderExtras()
    {
        if (HeaderExtras.IsVisible)
        {
            HeaderExtras.Measure(Size.Infinity);
            _extrasWidth = Math.Max(_extrasWidth, HeaderExtras.DesiredSize.Width);
        }
        var available = HeaderGrid.Bounds.Width - HeaderTitle.DesiredSize.Width - HeaderActions.DesiredSize.Width - 8;
        HeaderExtras.IsVisible = _extrasWidth <= available;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.Groups.CollectionChanged -= OnGroupsChanged;
        _vm = DataContext as RostersViewModel;
        if (_vm is not null) _vm.Groups.CollectionChanged += OnGroupsChanged;
        UpdateColumnWidth();
    }

    private void OnGroupsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateColumnWidth();

    /// <summary>Groups share the available width; below the minimum width the board scrolls horizontally.</summary>
    private void UpdateColumnWidth()
    {
        if (_vm is null) return;
        var available = GroupsScroller.Bounds.Width;
        var count = Math.Max(1, _vm.Groups.Count);
        if (available <= 0) return;
        var width = Math.Floor((available - ColumnGap * (count - 1) - 1) / count);
        _vm.ColumnWidth = Math.Clamp(width, MinColumnWidth, MaxColumnWidth);
    }

    // ── Right-click menus (built when opened, one entry per roster group) ─────────────────────────

    private void OnPoolTileMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not ChampionTileViewModel tile || _vm is not { } vm) return;
        var items = new List<Control>
        {
            MenuItem("Open", "book-open", () => vm.OpenChampionCommand.Execute(tile)),
            new Separator(),
        };
        foreach (var group in vm.Groups)
        {
            var contains = group.Entries.Any(x => x.ChampionId == tile.Id);
            var item = MenuItem(contains ? $"Already in {group.Name}" : $"Add to {group.Name}", "plus", () => vm.AddToGroup(tile.Champion, group));
            item.IsEnabled = !contains;
            items.Add(item);
        }
        menu.ItemsSource = items;
    }

    private void OnCardMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not RosterEntryViewModel entry || _vm is not { } vm) return;
        // Remove the submenus added last time, then rebuild them for the current groups.
        foreach (var old in menu.Items.OfType<Control>().Where(c => Equals(c.Tag, "dynamic")).ToList()) menu.Items.Remove(old);
        var others = vm.Groups.Where(g => !ReferenceEquals(g, entry.Group)).ToList();
        if (others.Count == 0) return;
        var move = new MenuItem { Header = "Move to", Tag = "dynamic", Icon = Icon("move") };
        var copy = new MenuItem { Header = "Copy to", Tag = "dynamic", Icon = Icon("copy") };
        foreach (var group in others)
        {
            move.Items.Add(MenuItem(group.Name, null, () => vm.MoveOrCopy(entry, group, copy: false)));
            copy.Items.Add(MenuItem(group.Name, null, () => vm.MoveOrCopy(entry, group, copy: true)));
        }
        var index = Math.Max(0, menu.Items.Count - 2); // just above "Remove from group"
        menu.Items.Insert(index, new Separator { Tag = "dynamic" });
        menu.Items.Insert(index, copy);
        menu.Items.Insert(index, move);
    }

    private static MenuItem MenuItem(string header, string? icon, Action action) => new()
    {
        Header = header,
        Icon = icon is null ? null : Icon(icon),
        Command = new CommunityToolkit.Mvvm.Input.RelayCommand(action),
    };

    private static Controls.LucideIcon Icon(string kind) => new() { Kind = kind };

    // ── Hover tracking for keyboard shortcuts (1–5 rate, Delete removes) ──────────────────────────

    private void OnCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is RosterEntryViewModel entry) _vm.HoveredEntry = entry;
    }

    private void OnCardPointerExited(object? sender, PointerEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is RosterEntryViewModel entry && ReferenceEquals(_vm.HoveredEntry, entry)) _vm.HoveredEntry = null;
    }

    private void OnGroupNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RosterGroupViewModel g) g.BeginRenameCommand.Execute(null);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RosterGroupViewModel g) return;
        if (e.Key == Key.Enter)
        {
            g.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            g.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RosterGroupViewModel { IsEditing: true } g) g.CommitRenameCommand.Execute(null);
    }

    private void OnRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box) return;
        box.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && box.IsVisible)
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                }, DispatcherPriority.Input);
        };
        if (box.IsVisible) Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }
}
