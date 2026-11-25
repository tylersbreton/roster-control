using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RosterControl.Desktop.Controls;

public static class Converters
{
    public static readonly IValueConverter KeystoneFontSize = new FuncValueConverter<bool, double>(keystone => keystone ? 15 : 13);
    /// <summary>Dims champions already used (e.g. already in a matchup list or the enemy team).</summary>
    public static readonly IValueConverter UsedOpacity = new FuncValueConverter<bool, double>(used => used ? 0.35 : 1.0);
    /// <summary>Subtle background for the "All roles" tag shown in a role view.</summary>
    public static readonly IValueConverter AllRolesTagBackground = new FuncValueConverter<bool, IBrush>(shown =>
        shown ? new SolidColorBrush(Color.Parse("#1F8C99AD")) : Brushes.Transparent);
    public static readonly IValueConverter BoolToSemiBold = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);
}
