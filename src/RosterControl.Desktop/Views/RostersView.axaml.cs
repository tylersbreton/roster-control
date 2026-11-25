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
