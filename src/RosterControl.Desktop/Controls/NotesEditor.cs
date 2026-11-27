using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.Controls;

/// <summary>
/// Notes with light formatting (see <see cref="NoteFormat"/>): shows bullets, checkboxes and bold text, and turns into
/// a plain text box when clicked. Checkboxes can be ticked without editing. In the editor, Enter continues a list,
/// Ctrl+B toggles bold and Esc finishes editing. <see cref="Compact"/> is the inline, toolbar-less variant.
/// </summary>
public sealed class NotesEditor : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<NotesEditor, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<NotesEditor, string?>(nameof(PlaceholderText));

    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<NotesEditor, bool>(nameof(Compact));

    /// <summary>Formatted text only: no editing, no toolbar, checkboxes can't be ticked (e.g. release notes).</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<NotesEditor, bool>(nameof(IsReadOnly));

    private readonly TextBox _box;
    private readonly StackPanel _lines = new() { Spacing = 3 };
    private readonly Border _view;
    private readonly StackPanel _toolbar;
    private readonly Grid _root;
    private bool _editing;
    private bool _syncing;

    public NotesEditor()
    {
        _box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            NewLine = "\n",
        };
        _box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        _box.LostFocus += (_, _) => SetEditing(false);
        _box.GotFocus += (_, _) => _editing = true;
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _syncing) return;
            _syncing = true;
            try { Text = _box.Text; }
            finally { _syncing = false; }
        };

        _view = new Border
        {
            Cursor = new Cursor(StandardCursorType.Ibeam),
            Background = Brushes.Transparent,
            Child = new ScrollViewer { Content = _lines, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
        };
        _view.PointerReleased += OnViewPointerReleased;
        ActualThemeVariantChanged += (_, _) => Refresh(); // formatted text uses theme brushes
        _view.PointerEntered += OnCompactHover;
        _view.PointerExited += OnCompactHover;

        _toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
        _toolbar.Children.Add(new TextBlock
        {
            Text = "**bold**  ·  - bullet  ·  [ ] checkbox",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        ((Control)_toolbar.Children[^1]).Bind(TextBlock.ForegroundProperty, _toolbar.Children[^1].GetResourceObservable("Brush.TextMuted"));
        _toolbar.Children.Add(ToolButton("bold", "Bold (Ctrl+B)", () => Apply(e => NoteFormat.ToggleBold(e.Text, e.SelectionStart, e.SelectionEnd))));
        _toolbar.Children.Add(ToolButton("list", "Bulleted list", () => Apply(e => NoteFormat.ToggleList(e.Text, e.SelectionStart, e.SelectionEnd, NoteLineKind.Bullet))));
        _toolbar.Children.Add(ToolButton("list-checks", "Checklist", () => Apply(e => NoteFormat.ToggleList(e.Text, e.SelectionStart, e.SelectionEnd, NoteLineKind.Check))));

        _root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        _root.Children.Add(_toolbar);
        var content = new Panel { Children = { _box, _view } };
        Grid.SetRow(content, 1);
        _root.Children.Add(content);
        Content = _root;
        Focusable = true;
        IsTabStop = false;
        ApplyLayout();
        Refresh();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>The text box (for tests and focus).</summary>
    public TextBox Editor => _box;
    public bool IsEditing => _box.IsVisible;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            if (!_syncing)
            {
                var normalized = Normalize(Text);
                if (_box.Text != normalized)
                {
                    _syncing = true;
                    try { _box.Text = normalized; }
                    finally { _syncing = false; }
                }
            }
            Refresh();
        }
        else if (change.Property == PlaceholderTextProperty)
        {
            _box.PlaceholderText = PlaceholderText;
        }
        else if (change.Property == CompactProperty || change.Property == IsReadOnlyProperty)
        {
            ApplyLayout();
            Refresh();
        }
    }

    private void ApplyLayout()
    {
        _toolbar.IsVisible = !Compact && !IsReadOnly;
        _view.Cursor = IsReadOnly ? Cursor.Default : new Cursor(StandardCursorType.Ibeam);
        if (Compact)
        {
            _box.Classes.Add("inline");
            _box.FontSize = 12;
            _box.MaxHeight = 120;
            _view.Padding = new Thickness(6, 4);
            _view.CornerRadius = new CornerRadius(5);
            _view.BorderThickness = new Thickness(1);
            _view.BorderBrush = Brushes.Transparent;
            _view.Background = Brushes.Transparent;
        }
        else
        {
            _box.Classes.Remove("inline");
            _box.Padding = new Thickness(12, 10);
            _view.Padding = new Thickness(13, 11);
            _view.CornerRadius = new CornerRadius(6);
            _view.BorderThickness = new Thickness(1);
            _view.Background = Resource("TextControlBackground");
            _view.BorderBrush = Resource("TextControlBorderBrush");
        }
    }

    private void OnCompactHover(object? sender, PointerEventArgs e)
    {
        if (!Compact) return;
        var over = _view.IsPointerOver;
        _view.Background = over ? Resource("Brush.SurfaceSunken") : Brushes.Transparent;
        _view.BorderBrush = over ? Resource("Brush.Border") : Brushes.Transparent;
    }

    // ── Rendering ──────────────────────────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var showEditor = !IsReadOnly && (_editing || string.IsNullOrWhiteSpace(Text));
        _box.IsVisible = showEditor;
        _view.IsVisible = !showEditor;
        if (!showEditor) Render();
    }

    private void Render()
    {
        _lines.Children.Clear();
        var fontSize = Compact ? 12 : 13.5;
        foreach (var line in NoteFormat.Parse(Text))
        {
            switch (line.Kind)
            {
                case NoteLineKind.Blank:
                    _lines.Children.Add(new Border { Height = Compact ? 2 : 6 });
                    break;
                case NoteLineKind.Text:
                    _lines.Children.Add(Paragraph(line.Content, fontSize, muted: false));
                    break;
                case NoteLineKind.Bullet:
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(line.Indent * 16, 0, 0, 0) };
                    row.Children.Add(new TextBlock { Text = "•", FontSize = fontSize, Width = 14, Foreground = Resource("Brush.Gold"), FontWeight = FontWeight.Bold });
                    var text = Paragraph(line.Content, fontSize, muted: false);
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    _lines.Children.Add(row);
                    break;
                }
                case NoteLineKind.Check:
                {
                    var index = line.Index;
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(line.Indent * 16, 0, 0, 0) };
                    var box = CheckMark(line.Checked, Compact ? 13 : 16);
                    box.Margin = new Thickness(0, Compact ? 1.5 : 2, 7, 0);
                    box.IsHitTestVisible = !IsReadOnly;
                    box.Click += (_, _) => Text = NoteFormat.ToggleCheck(Normalize(Text), index);
                    row.Children.Add(box);
                    var text = Paragraph(line.Content, fontSize, muted: line.Checked);
                    if (line.Checked) text.TextDecorations = TextDecorations.Strikethrough;
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    _lines.Children.Add(row);
                    break;
                }
            }
        }
    }

    /// <summary>A compact gold check box (the Fluent one is 32 px tall and doesn't fit a line of text).</summary>
    private Button CheckMark(bool isChecked, double size)
    {
        var mark = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Resource(isChecked ? "Brush.Gold" : "Brush.TextMuted"),
            Background = isChecked ? Resource("Brush.Gold") : Brushes.Transparent,
            Child = isChecked
                ? new LucideIcon { Kind = "check", Width = size - 5, Height = size - 5, StrokeWidth = 3.2, Foreground = new SolidColorBrush(Color.Parse("#060A11")) }
                : null,
        };
        var button = new Button
        {
            Content = mark,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = "notecheck",
        };
        button.Classes.Add("plain");
        ToolTip.SetTip(button, isChecked ? "Mark as not done" : "Mark as done");
        return button;
    }

    private TextBlock Paragraph(string content, double fontSize, bool muted)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            Foreground = Resource(muted ? "Brush.TextMuted" : "Brush.TextPrimary"),
        };
        var inlines = new InlineCollection();
        foreach (var span in NoteFormat.ParseInline(content))
        {
            var run = new Run(span.Text);
            if (span.Bold)
            {
                run.FontWeight = FontWeight.Bold;
                if (!muted) run.Foreground = Resource("Brush.GoldLight");
            }
            inlines.Add(run);
        }
        block.Inlines = inlines;
        return block;
    }

    // ── Editing ────────────────────────────────────────────────────────────────────────────────────

    private void OnViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || IsReadOnly) return;
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, _view); v = v.GetVisualParent())
            if (v is Button) return; // ticking a box doesn't start editing
        SetEditing(true);
        e.Handled = true;
    }

    private void SetEditing(bool editing)
    {
        if (_editing == editing && _box.IsVisible == (editing || string.IsNullOrWhiteSpace(Text))) return;
        _editing = editing;
        Refresh();
        if (editing)
            Dispatcher.UIThread.Post(() =>
            {
                _box.Focus();
                _box.CaretIndex = _box.Text?.Length ?? 0;
            }, DispatcherPriority.Input);
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (NoteFormat.ContinueList(_box.Text ?? "", _box.CaretIndex) is { } edit)
            {
                Set(edit);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.B && e.KeyModifiers == KeyModifiers.Control)
        {
            Apply(x => NoteFormat.ToggleBold(x.Text, x.SelectionStart, x.SelectionEnd));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrWhiteSpace(_box.Text))
        {
            Focus(); // move focus off the text box (the control itself is focusable but not a tab stop)
            SetEditing(false);
            e.Handled = true;
        }
    }

    private void Apply(Func<NoteEdit, NoteEdit> action)
    {
        if (!_box.IsVisible) SetEditing(true);
        var current = new NoteEdit(_box.Text ?? "", _box.SelectionStart, _box.SelectionEnd);
        Set(action(current));
        _box.Focus();
    }

    private void Set(NoteEdit edit)
    {
        _box.Text = edit.Text;
        _box.SelectionStart = edit.SelectionStart;
        _box.SelectionEnd = edit.SelectionEnd;
        _box.CaretIndex = edit.SelectionEnd;
    }

    private Button ToolButton(string icon, string tip, Action action)
    {
        var button = new Button
        {
            Width = 26,
            Height = 26,
            Focusable = false, // keep focus (and the selection) in the text box
            Content = new LucideIcon { Kind = icon, Width = 14, Height = 14 },
        };
        button.Classes.Add("ghost");
        button.Classes.Add("icon");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A theme brush for the current light/dark theme (colours live in per-theme dictionaries).</summary>
    private IBrush Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush
        : Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var appValue) && appValue is IBrush appBrush ? appBrush
        : Brushes.Gray;

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
}
