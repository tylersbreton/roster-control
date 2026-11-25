namespace RosterControl.Core.Model;

/// <summary>Summoner's Rift positions. Used for the default roster groups and to tag builds.</summary>
public enum Position
{
    Top = 0,
    Jungle = 1,
    Middle = 2,
    Bottom = 3,
    Support = 4,
}

/// <summary>How important a champion is within a roster group.</summary>
public enum ChampionPriority
{
    Main = 0,
    Secondary = 1,
    Pocket = 2,
    Learning = 3,
}

public enum MatchupKind
{
    /// <summary>The champion does well against the opponent.</summary>
    GoodAgainst = 0,
    /// <summary>The champion struggles against the opponent.</summary>
    WeakAgainst = 1,
}

public static class PositionInfo
{
    public static readonly IReadOnlyList<Position> All = [Position.Top, Position.Jungle, Position.Middle, Position.Bottom, Position.Support];

    public static string DisplayName(this Position p) => p switch
    {
        Position.Top => "Top",
        Position.Jungle => "Jungle",
        Position.Middle => "Middle",
        Position.Bottom => "Bottom",
        Position.Support => "Support",
        _ => p.ToString(),
    };

    /// <summary>Riot / Meraki position key (TOP, JUNGLE, MIDDLE, BOTTOM, SUPPORT/UTILITY).</summary>
    public static Position? Parse(string? key) => key?.ToUpperInvariant() switch
    {
        "TOP" => Position.Top,
        "JUNGLE" => Position.Jungle,
        "MIDDLE" or "MID" => Position.Middle,
        "BOTTOM" or "BOT" or "ADC" => Position.Bottom,
        "SUPPORT" or "UTILITY" => Position.Support,
        _ => null,
    };
}

public static class PriorityInfo
{
    public static readonly IReadOnlyList<ChampionPriority> All = [ChampionPriority.Main, ChampionPriority.Secondary, ChampionPriority.Pocket, ChampionPriority.Learning];

    public static string Description(this ChampionPriority p) => p switch
    {
        ChampionPriority.Main => "First pick for this group",
        ChampionPriority.Secondary => "Reliable alternative",
        ChampionPriority.Pocket => "Situational / counter pick",
        ChampionPriority.Learning => "Still practising",
        _ => "",
    };
}
