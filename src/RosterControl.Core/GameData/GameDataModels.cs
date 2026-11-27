using RosterControl.Core.Model;

namespace RosterControl.Core.GameData;

/// <summary>A champion from Data Dragon (plus optional position data from Meraki Analytics).</summary>
public sealed record ChampionInfo(
    string Id,
    int Key,
    string Name,
    string Title,
    IReadOnlyList<string> Tags,
    string ImageFile,
    IReadOnlyList<Position> Positions,
    ChampionStats? Stats = null)
{
    /// <summary>Lower-case name without punctuation, for forgiving search ("kogmaw" finds "Kog'Maw").</summary>
    public string SearchKey { get; } = Normalize(Name) + " " + Normalize(Id);

    public static string Normalize(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return SearchKey.Contains(Normalize(query), StringComparison.Ordinal);
    }
}

public enum ItemTier
{
    Starter,
    Boots,
    Basic,
    Epic,
    Legendary,
    Consumable,
    Trinket,
}

public sealed record ItemInfo(
    string Id,
    string Name,
    string Description,
    string PlainText,
    int TotalGold,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> From,
    IReadOnlyList<string> Into,
    string ImageFile,
    bool OnSummonersRift,
    bool OnAram,
    ItemTier Tier,
    int CombineCost = 0)
{
    public string SearchKey { get; } = ChampionInfo.Normalize(Name);
}

/// <summary>A champion's base stats and growth per level (Data Dragon <c>stats</c>).</summary>
/// <param name="AdaptiveAp">Adaptive force goes to AP when bonus AD and AP are equal (a magic-leaning champion).</param>
/// <param name="AttackDamageGrowthUnknown">
/// Data Dragon reported 0 AD per level (as it does for every champion since 16.22) and no other source had a value, so
/// the 0 may be wrong. Senna's real 0 comes with this false.
/// </param>
public sealed record ChampionStats(
    double Health, double HealthPerLevel,
    double Mana, double ManaPerLevel,
    double MoveSpeed,
    double Armor, double ArmorPerLevel,
    double MagicResist, double MagicResistPerLevel,
    double AttackRange,
    double AttackDamage, double AttackDamagePerLevel,
    double AttackSpeed, double AttackSpeedPerLevel,
    bool AdaptiveAp = false,
    string Resource = "",
    bool AttackDamageGrowthUnknown = false)
{
    /// <summary>Ranged champions get smaller numbers from some runes (e.g. Grasp, Fleet Footwork).</summary>
    public bool IsRanged => AttackRange > 350;
}

/// <summary>One node of an item's recipe tree (the item, its components, their components…).</summary>
public sealed record RecipeNode(ItemInfo Item, IReadOnlyList<RecipeNode> Components)
{
    public int Depth => Components.Count == 0 ? 1 : 1 + Components.Max(c => c.Depth);
    public int LeafCount => Components.Count == 0 ? 1 : Components.Sum(c => c.LeafCount);
}

public sealed record RuneInfo(int Id, string Key, string Name, string IconPath, string ShortDesc, string LongDesc, int TreeId, int Row)
{
    public bool IsKeystone => Row == 0;
}

public sealed record RuneTreeInfo(
    int Id,
    string Key,
    string Name,
    string IconPath,
    string Color,
    string Tagline,
    IReadOnlyList<IReadOnlyList<RuneInfo>> Rows,
    IReadOnlyDictionary<string, string> Backgrounds)
{
    /// <summary>Client-style background art for this tree as primary, with the given secondary tree and keystone.</summary>
    public string? BackgroundUrl(int? subStyleId, int keystoneId)
    {
        var sub = subStyleId ?? 0;
        foreach (var key in new[] { $"p{Id}_s{sub}_k{keystoneId}", $"p{Id}_s0_k{keystoneId}", $"p{Id}_s{sub}_k0", $"p{Id}_s0_k0" })
            if (Backgrounds.TryGetValue(key, out var url)) return url;
        return null;
    }
}

public sealed record StatShardInfo(int Id, string Name, string IconUrl, string ShortDesc);

public sealed record StatShardRow(string Label, IReadOnlyList<StatShardInfo> Options);

public sealed record SummonerSpellInfo(string Id, string Name, string Description, double Cooldown, string ImageFile, bool OnSummonersRift, bool OnAram);

/// <param name="CooldownText">Per rank, e.g. "11/10/9/8/7" (Data Dragon); empty when none.</param>
/// <param name="CostText">e.g. "80/85/90/95/100 Mana", "No cost"; empty when unknown.</param>
/// <param name="RangeText">e.g. "1075", "Global"; empty when none.</param>
public sealed record AbilityInfo(char Slot, string Name, string Description, string ImageUrl, int MaxRank,
    string CooldownText = "", string CostText = "", string RangeText = "")
{
    public string Key => Slot == 'P' ? "Passive" : Slot.ToString();
}

/// <summary>A share of a stat added to a value, per rank (e.g. 0.5 × AP). See <see cref="StatKind"/> for stat numbers.</summary>
/// <param name="Formula">0 = total, 1 = base, 2 = bonus.</param>
public sealed record StatRatio(int Stat, int Formula, IReadOnlyList<double> Coefficient);

/// <summary>
/// How an ability number is calculated: a value per rank, plus an optional value per champion level (1–18), plus stat
/// ratios. Percent values are fractions (0.08 = 8%).
/// </summary>
public sealed record AbilityFormula(
    IReadOnlyList<double> Base,
    IReadOnlyList<StatRatio> Ratios,
    IReadOnlyList<double>? ByLevel,
    bool IsPercent,
    int? Precision,
    double RangedMultiplier = 1)
{
    public int Ranks => Base.Count;
}

/// <summary>One number of an ability from the game files, e.g. "Total damage: 35/60/85/110/135 (+50% AP)".</summary>
/// <param name="Formula">How to calculate it for a given rank, level and stats; null when only the text is known.</param>
public sealed record AbilityValue(string Label, string Text, AbilityFormula? Formula = null);

/// <summary>Numbers of the runes that have formulas in the game files (keystones such as Electrocute or Grasp), by rune id.</summary>
public sealed record RuneNumbers(string Patch, IReadOnlyDictionary<int, IReadOnlyList<AbilityValue>> Values)
{
    public IReadOnlyList<AbilityValue> this[int runeId] => Values.TryGetValue(runeId, out var v) ? v : [];
}

/// <summary>Ability numbers per slot (P, Q, W, E, R) from the game files, for the game patch <paramref name="Patch"/>.</summary>
/// <param name="AttackDamagePerLevel">The champion's AD growth from its game file (Data Dragon reports 0 since 16.22); null when not found.</param>
public sealed record AbilityNumbers(string ChampionId, string Patch, IReadOnlyDictionary<char, IReadOnlyList<AbilityValue>> Values, double? AttackDamagePerLevel = null)
{
    public IReadOnlyList<AbilityValue> this[char slot] => Values.TryGetValue(slot, out var v) ? v : [];
}

public sealed record ChampionAbilities(string ChampionId, IReadOnlyList<AbilityInfo> Abilities)
{
    public AbilityInfo? this[char slot] => Abilities.FirstOrDefault(a => a.Slot == slot);
}

/// <summary>Immutable set of reference data for one Data Dragon version.</summary>
public sealed class GameDataSnapshot
{
    public GameDataSnapshot(
        string version,
        IEnumerable<ChampionInfo> champions,
        IEnumerable<ItemInfo> items,
        IEnumerable<string> shopItemIds,
        IEnumerable<RuneTreeInfo> runeTrees,
        IEnumerable<StatShardRow> shardRows,
        IEnumerable<SummonerSpellInfo> summonerSpells,
        bool positionsAvailable,
        IEnumerable<string>? newChampionIds = null)
    {
        NewChampionIds = new HashSet<string>(newChampionIds ?? [], StringComparer.OrdinalIgnoreCase);
        Version = version;
        Champions = champions.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ChampionsById = Champions.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        Items = items.ToDictionary(i => i.Id);
        ShopItems = shopItemIds.Where(Items.ContainsKey).Select(id => Items[id]).ToList();
        ShopItemIds = ShopItems.Select(i => i.Id).ToHashSet();
        RuneTrees = runeTrees.ToList();
        TreesById = RuneTrees.ToDictionary(t => t.Id);
        Runes = RuneTrees.SelectMany(t => t.Rows.SelectMany(r => r)).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        ShardRows = shardRows.ToList();
        Shards = ShardRows.SelectMany(r => r.Options).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        SummonerSpells = summonerSpells.ToList();
        SummonerSpellsById = SummonerSpells.ToDictionary(s => s.Id);
        PositionsAvailable = positionsAvailable;
    }

    public string Version { get; }
    public IReadOnlyList<ChampionInfo> Champions { get; }
    public IReadOnlyDictionary<string, ChampionInfo> ChampionsById { get; }
    public IReadOnlyDictionary<string, ItemInfo> Items { get; }
    /// <summary>Items shown in the item set editor's shop (purchasable, de-duplicated, Summoner's Rift or ARAM).</summary>
    public IReadOnlyList<ItemInfo> ShopItems { get; }
    public IReadOnlySet<string> ShopItemIds { get; }
    public IReadOnlyList<RuneTreeInfo> RuneTrees { get; }
    public IReadOnlyDictionary<int, RuneTreeInfo> TreesById { get; }
    public IReadOnlyDictionary<int, RuneInfo> Runes { get; }
    public IReadOnlyList<StatShardRow> ShardRows { get; }
    public IReadOnlyDictionary<int, StatShardInfo> Shards { get; }
    public IReadOnlyList<SummonerSpellInfo> SummonerSpells { get; }
    public IReadOnlyDictionary<string, SummonerSpellInfo> SummonerSpellsById { get; }
    public bool PositionsAvailable { get; }
    /// <summary>Champions added in this patch (not in the previous patch's champion list).</summary>
    public IReadOnlySet<string> NewChampionIds { get; }

    public ChampionInfo? Champion(string? id) => id is not null && ChampionsById.TryGetValue(id, out var c) ? c : null;
    public ItemInfo? Item(string? id) => id is not null && Items.TryGetValue(id, out var i) ? i : null;
    public RuneInfo? Rune(int id) => Runes.TryGetValue(id, out var r) ? r : null;
    public RuneTreeInfo? Tree(int? id) => id is { } v && TreesById.TryGetValue(v, out var t) ? t : null;
    public StatShardInfo? Shard(int id) => Shards.TryGetValue(id, out var s) ? s : null;
    public SummonerSpellInfo? Spell(string? id) => id is not null && SummonerSpellsById.TryGetValue(id, out var s) ? s : null;
}

/// <summary>Public, keyless image URLs (Data Dragon and Community Dragon).</summary>
public static class AssetUrls
{
    public const string DataDragon = "https://ddragon.leagueoflegends.com/";
    public const string CommunityDragonGameData = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/";
    public const string CommunityDragonClash = "https://raw.communitydragon.org/latest/plugins/rcp-fe-lol-clash/global/default/";

    public static string ChampionSquare(string version, string imageFile) => $"{DataDragon}cdn/{version}/img/champion/{imageFile}";
    public static string ChampionCentered(string championId) => $"{DataDragon}cdn/img/champion/centered/{championId}_0.jpg";
    public static string ChampionSplash(string championId) => $"{DataDragon}cdn/img/champion/splash/{championId}_0.jpg";
    public static string Item(string version, string itemId) => $"{DataDragon}cdn/{version}/img/item/{itemId}.png";
    public static string Rune(string iconPath) => $"{DataDragon}cdn/img/{iconPath}";
    public static string Spell(string version, string imageFile) => $"{DataDragon}cdn/{version}/img/spell/{imageFile}";
    public static string Passive(string version, string imageFile) => $"{DataDragon}cdn/{version}/img/passive/{imageFile}";

    /// <summary>Converts a client asset path ("/lol-game-data/assets/v1/...") to its Community Dragon URL.</summary>
    public static string CommunityDragon(string clientPath)
    {
        const string prefix = "/lol-game-data/assets/";
        var rel = clientPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? clientPath[prefix.Length..] : clientPath.TrimStart('/');
        return CommunityDragonGameData + rel.ToLowerInvariant();
    }

    public static string PositionIcon(Position position) => CommunityDragonClash +
        "assets/images/position-selector/positions/icon-position-" + position switch
        {
            Position.Top => "top",
            Position.Jungle => "jungle",
            Position.Middle => "middle",
            Position.Bottom => "bottom",
            Position.Support => "utility",
            _ => "fill",
        } + ".png";
}
