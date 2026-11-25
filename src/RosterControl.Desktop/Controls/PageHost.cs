using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RosterControl.Desktop.Controls;

/// <summary>
/// Shows the current page and keeps recently shown page views alive (hidden) instead of rebuilding them, so scroll
/// positions, expanded sections and focus survive navigation, and switching pages is instant.
/// </summary>
public sealed class PageHost : Panel
{
    public static readonly StyledProperty<object?> PageProperty = AvaloniaProperty.Register<PageHost, object?>(nameof(Page));

    private readonly List<(object Page, Control View)> _views = [];

    public object? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    /// <summary>How many page views to keep (least recently shown are dropped first).</summary>
    public int MaxCached { get; set; } = 10;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) Show(change.NewValue);
    }

    /// <summary>Drops every cached view (e.g. after all pages were rebuilt).</summary>
    public void Clear()
    {
        var current = Page;
        foreach (var (page, view) in _views.ToList())
        {
            if (ReferenceEquals(page, current)) continue;
            Children.Remove(view);
            _views.RemoveAll(v => ReferenceEquals(v.Page, page));
        }
    }

    private void Show(object? page)
    {
        foreach (var (_, v) in _views) v.IsVisible = false;
        if (page is null) return;
        var index = _views.FindIndex(v => ReferenceEquals(v.Page, page));
        Control view;
        if (index >= 0)
        {
            view = _views[index].View;
            _views.RemoveAt(index);
        }
        else
        {
            view = this.FindDataTemplate(page)?.Build(page) ?? new TextBlock { Text = page.GetType().Name };
            view.DataContext = page;
            Children.Add(view);
        }
        _views.Add((page, view));
        view.IsVisible = true;
        while (_views.Count > MaxCached)
        {
            Children.Remove(_views[0].View);
            _views.RemoveAt(0);
        }
    }
}

/// <summary>Attached helpers for text boxes: <c>c:InputHelpers.EnterCommand</c> runs a command when Enter is pressed.</summary>
public static class InputHelpers
{
    public static readonly AttachedProperty<ICommand?> EnterCommandProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("EnterCommand", typeof(InputHelpers));

    static InputHelpers()
    {
        EnterCommandProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (e.OldValue is null && e.NewValue is not null)
                box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        });
    }

    public static ICommand? GetEnterCommand(TextBox box) => box.GetValue(EnterCommandProperty);
    public static void SetEnterCommand(TextBox box, ICommand? value) => box.SetValue(EnterCommandProperty, value);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || sender is not TextBox box) return;
        if (GetEnterCommand(box) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
