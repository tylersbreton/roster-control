namespace RosterControl.Core.Model;

// Persistent user data. These are plain classes; the Data project maps them with EF Core (SQLite).
// Champions, items, runes and spells are referenced by their stable Data Dragon identifiers.

/// <summary>A named group of champions (by default one per role).</summary>
public sealed class RosterGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Optional role the group represents (drives the group icon and the role of new builds).</summary>
    public Position? Role { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Accent colour (#RRGGBB).</summary>
    public string? Color { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RosterEntry> Entries { get; set; } = [];
}

/// <summary>A champion placed in a roster group, with the player's own proficiency and priority for that placement.</summary>
public sealed class RosterEntry
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    /// <summary>Data Dragon champion id (e.g. "MonkeyKing").</summary>
    public string ChampionId { get; set; } = "";
    public int SortOrder { get; set; }
    /// <summary>0 (unrated) to 5.</summary>
    public int Proficiency { get; set; }
    public ChampionPriority Priority { get; set; } = ChampionPriority.Secondary;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Champion-level notes and tags (shared by all placements of the champion).</summary>
public sealed class ChampionProfile
{
    public string ChampionId { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// "<see cref="ChampionId"/> is good/weak against <see cref="OpponentId"/>", either in every role (<see cref="Role"/>
/// null) or only when played in one role. A role-specific matchup overrides the all-roles one for that role.
/// </summary>
public sealed class Matchup
{
    public int Id { get; set; }
    public string ChampionId { get; set; } = "";
    public string OpponentId { get; set; } = "";
    /// <summary>Role this matchup applies to; null = all roles.</summary>
    public Position? Role { get; set; }
    public MatchupKind Kind { get; set; }
    public string Note { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A saved build for a champion: rune page, item set, summoner spells and skill order.</summary>
public sealed class Build
{
    public int Id { get; set; }
    public string ChampionId { get; set; } = "";
    public string Name { get; set; } = "New build";
    public Position? Role { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Data Dragon version current when the build was last edited (used to draw items removed since).</summary>
    public string? GameVersion { get; set; }
    public RunePageData Runes { get; set; } = new();
    public ItemSetData Items { get; set; } = new();
    /// <summary>Two summoner spell ids (Data Dragon ids such as "SummonerFlash"); empty string = empty slot.</summary>
    public List<string> SummonerSpells { get; set; } = ["", ""];
    /// <summary>One character per champion level: Q, W, E, R, or '-' for unassigned. Up to 18 characters.</summary>
    public string SkillOrder { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Build CloneAsNew(string name) => new()
    {
        ChampionId = ChampionId,
        Name = name,
        Role = Role,
        GameVersion = GameVersion,
        Runes = Runes.Clone(),
        Items = Items.Clone(),
        SummonerSpells = [.. SummonerSpells],
        SkillOrder = SkillOrder,
        Notes = Notes,
    };
}

public sealed class RunePageData
{
    public int? PrimaryStyleId { get; set; }
    public int? SubStyleId { get; set; }
    /// <summary>Keystone + three primary runes, one per row (0 = not chosen).</summary>
    public List<int> PrimaryPerks { get; set; } = [0, 0, 0, 0];
    /// <summary>Up to two secondary runes in selection order (oldest first).</summary>
    public List<int> SubPerks { get; set; } = [];
    /// <summary>Offense, flex and defense stat shards (0 = not chosen).</summary>
    public List<int> Shards { get; set; } = [0, 0, 0];

    public RunePageData Clone() => new()
    {
        PrimaryStyleId = PrimaryStyleId,
        SubStyleId = SubStyleId,
        PrimaryPerks = [.. PrimaryPerks],
        SubPerks = [.. SubPerks],
        Shards = [.. Shards],
    };
}

public sealed class ItemSetData
{
    public List<ItemBlockData> Blocks { get; set; } = [];

    public ItemSetData Clone() => new() { Blocks = Blocks.Select(b => new ItemBlockData { Name = b.Name, Items = [.. b.Items] }).ToList() };

    public static ItemSetData CreateDefault() => new()
    {
        Blocks =
        [
            new ItemBlockData { Name = "Starting Items" },
            new ItemBlockData { Name = "Core Build" },
            new ItemBlockData { Name = "Situational" },
        ],
    };
}

public sealed class ItemBlockData
{
    public string Name { get; set; } = "";
    /// <summary>Data Dragon item ids, in order. Duplicates are allowed (e.g. two potions).</summary>
    public List<string> Items { get; set; } = [];
}

public sealed class Preference
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
}
