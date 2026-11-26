using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using RosterControl.Core.Model;
using RosterControl.Desktop.ViewModels;

namespace RosterControl.Desktop.Controls;

/// <summary>
/// A shareable picture of the roster board: every group as a column with every champion (no scrolling), the roster's
/// name and the priority legend. Built off-screen and rendered to a PNG.
/// </summary>
public static class RosterImage
{
    private const double ColumnWidth = 216;

    public static Control Build(RostersViewModel vm)
    {
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var group in vm.Groups) columns.Children.Add(Column(group));

        var champions = vm.Groups.SelectMany(g => g.Entries).Select(e => e.ChampionId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 18) };
        var title = new StackPanel { Spacing = 3 };
        title.Children.Add(new TextBlock { Text = vm.RosterHeading, FontSize = 24, FontWeight = FontWeight.Bold, LetterSpacing = 1.5, Foreground = Brush("#F0E6D2") });
        title.Children.Add(new TextBlock
        {
            Text = $"{champions} champion{(champions == 1 ? "" : "s")} · {vm.Groups.Count} group{(vm.Groups.Count == 1 ? "" : "s")} · patch {vm.Game.Version} · {DateTime.Now:d MMM yyyy}",
            FontSize = 12.5,
            Foreground = Brush("#A09B8C"),
        });
        header.Children.Add(title);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        brand.Children.Add(new Image { Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://RosterControl/Assets/roster-256.png"))), Width = 22, Height = 22 });
        brand.Children.Add(new TextBlock { Text = "ROSTER CONTROL", FontSize = 12, FontWeight = FontWeight.Bold, LetterSpacing = 1.6, Foreground = Brush("#C8AA6E"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(brand, 1);
        header.Children.Add(brand);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var item in PriorityLegend.Items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(Badge(item.Priority, 14));
            row.Children.Add(new TextBlock { Text = item.Label, FontSize = 11.5, Foreground = Brush("#A09B8C"), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(row);
        }

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(columns);
        content.Children.Add(legend);
        return new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#0B1320"), 0), new GradientStop(Color.Parse("#060A11"), 1) },
            },
            Padding = new Thickness(28, 24),
            Child = content,
        };
    }

    private static Control Column(RosterGroupViewModel group)
    {
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(8, 8, 8, 10) };
        foreach (var entry in group.Entries) stack.Children.Add(Card(entry));
        if (group.Entries.Count == 0)
            stack.Children.Add(new TextBlock { Text = "—", Foreground = Brush("#5B5A56"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8) });

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 10, 12, 9) };
        Control icon = group.RoleIconUrl is { } url
            ? new AssetImage { Source = url, Width = 18, Height = 18, ShowPlaceholder = false }
            : new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = group.Accent, VerticalAlignment = VerticalAlignment.Center };
        icon.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(icon);
        var name = new TextBlock { Text = group.Name, FontWeight = FontWeight.Bold, FontSize = 13.5, LetterSpacing = 0.6, Foreground = Brush("#F0E6D2"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var count = new TextBlock { Text = group.Entries.Count.ToString(), FontSize = 11, Foreground = Brush("#A09B8C"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 2);
        head.Children.Add(count);

        var column = new StackPanel();
        column.Children.Add(head);
        column.Children.Add(new Border { Height = 2, Background = group.Accent, Opacity = 0.85 });
        column.Children.Add(stack);
        return new Border
        {
            Width = ColumnWidth,
            Background = Brush("#0E1622"),
            BorderBrush = Brush("#1E2B3C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Child = column,
        };
    }

    private static Control Card(RosterEntryViewModel entry)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var portrait = new Panel { Width = 36, Height = 36 };
        portrait.Children.Add(new AssetImage { Source = entry.IconUrl, Width = 36, Height = 36, CornerRadius = new CornerRadius(6), Zoom = 1.1, Ring = entry.PriorityBrush, RingThickness = 1.5, FallbackText = entry.Name });
        var badge = Badge(entry.Priority, 16);
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        badge.VerticalAlignment = VerticalAlignment.Bottom;
        badge.Margin = new Thickness(0, 0, -5, -5);
        portrait.Children.Add(badge);
        grid.Children.Add(portrait);

        var text = new StackPanel { Spacing = 4, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = Brush("#F0E6D2"), TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        meta.Children.Add(new StarRating { Value = entry.Proficiency, StarSize = 9.5, IsReadOnly = true, VerticalAlignment = VerticalAlignment.Center });
        meta.Children.Add(new TextBlock { Text = entry.PriorityLabel, FontSize = 8.5, FontWeight = FontWeight.Bold, LetterSpacing = 0.2, Foreground = entry.PriorityBrush, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(meta);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Background = Brush("#131D2B"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 6, 8, 6),
            Child = grid,
        };
    }

    private static Border Badge(ChampionPriority priority, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = Palette.Hex(Palette.PriorityHex(priority)),
        BorderBrush = Brush("#0E1622"),
        BorderThickness = new Thickness(size > 15 ? 2 : 0),
        Child = new LucideIcon { Kind = PriorityLegend.IconFor(priority), Width = size * 0.48, Height = size * 0.48, Foreground = Brush("#060A11"), StrokeWidth = 2.6 },
    };

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    /// <summary>Lays out the image inside <paramref name="host"/> (an off-screen canvas), waits for portraits, renders at 2×.</summary>
    public static async Task<RenderTargetBitmap> RenderAsync(Control image, Panel host, TimeSpan timeout)
    {
        host.Children.Add(image);
        try
        {
            image.Measure(Size.Infinity);
            image.Arrange(new Rect(image.DesiredSize));
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline
                   && image.GetVisualDescendants().OfType<AssetImage>().Any(i => !string.IsNullOrEmpty(i.Source) && !i.HasImage))
                await Task.Delay(50);
            image.Measure(Size.Infinity);
            image.Arrange(new Rect(image.DesiredSize));
            const double scale = 2;
            var size = image.Bounds.Size;
            var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)), new Vector(96 * scale, 96 * scale));
            bitmap.Render(image);
            return bitmap;
        }
        finally
        {
            host.Children.Remove(image);
        }
    }
}
