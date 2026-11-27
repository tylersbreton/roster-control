using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// The Items page: every shop item, filtered by stat, tier and map, with gold efficiency and the builds that use it.
/// </summary>
public sealed partial class ItemsViewModel : ObservableObject, IPage
{
    private const string PrefsKey = "items.filters";
    public const string AllTiers = "All tiers";
    public const string AnyMap = "Any map";
    public const string SummonersRift = "Summoner's Rift";
    public const string Aram = "ARAM";

    private readonly IShell _shell;
    private readonly IReadOnlyDictionary<(string Name, bool IsPercent), StatGoldValue> _references;
    private readonly List<ItemTileViewModel> _all;
    private Dictionary<string, List<ItemUsage>> _usage = [];
    private bool _restoring;

    public ItemsViewModel(IShell shell)
    {
        _shell = shell;
        var game = shell.Game;
        _references = GoldEfficiency.References(game);
        _all = game.ShopItems.Select(i => new ItemTileViewModel(i, game.Version, GoldEfficiency.Of(i, _references))).ToList();
        var present = _all.SelectMany(t => t.StatNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        StatFilters = StatOrder.Where(present.Contains).Select(n => new ItemStatFilter(n, ItemStats.ShortName(n), Refresh)).ToList();
        ReferenceText = "Gold values from basic items: " + string.Join(", ", _references.Values
            .OrderBy(r => Array.IndexOf(StatOrder, r.Name) is var i && i >= 0 ? i : 99).Select(r => r.Text)) + ".";

        _restoring = true;
        var prefs = shell.Preferences.Get(PrefsKey, new ItemFilterPrefs());
        _selectedTier = TierOptions.Contains(prefs.Tier) ? prefs.Tier : AllTiers;
        _selectedMap = MapOptions.Contains(prefs.Map) ? prefs.Map : SummonersRift;
        _selectedSort = SortOptions.Contains(prefs.Sort) ? prefs.Sort : "Cost";
        _onlyInBuilds = prefs.OnlyInBuilds;
        foreach (var f in StatFilters) f.IsOn = prefs.Stats.Contains(f.Name, StringComparer.OrdinalIgnoreCase);
        _restoring = false;
        Refresh();
    }

    public string Title => "Items";

    private static readonly string[] StatOrder =
    [
        "Attack Damage", "Ability Power", "Attack Speed", "Critical Strike Chance", "Lethality", "Armor Penetration", "Magic Penetration",
        "Ability Haste", "Health", "Armor", "Magic Resist", "Mana", "Move Speed", "Life Steal", "Omnivamp", "Heal and Shield Power", "Tenacity",
    ];

    public IReadOnlyList<string> TierOptions { get; } = [AllTiers, "Legendary", "Epic", "Basic", "Boots", "Starter", "Consumables"];
    public IReadOnlyList<string> MapOptions { get; } = [SummonersRift, Aram, AnyMap];
    public IReadOnlyList<string> SortOptions { get; } = ["Cost", "Name", "Gold efficiency", "Used in builds"];
    public IReadOnlyList<ItemStatFilter> StatFilters { get; }
    public string ReferenceText { get; }

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private string _selectedTier;

    [ObservableProperty]
    private string _selectedMap;

    [ObservableProperty]
    private string _selectedSort;

    [ObservableProperty]
    private bool _onlyInBuilds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmpty))]
    private IReadOnlyList<ItemTileViewModel> _tiles = [];

    public string CountText => $"{Tiles.Count} item{(Tiles.Count == 1 ? "" : "s")}";
    public bool IsEmpty => Tiles.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ItemTileViewModel? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    private ItemDetailsViewModel? _details;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEfficiency))]
    private IReadOnlyList<ItemWorthRow> _worthRows = [];

    public bool HasEfficiency => WorthRows.Count > 0;

    [ObservableProperty]
    private string _efficiencyText = "";

    [ObservableProperty]
    private string _unvaluedText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage), nameof(UsageTitle))]
    private IReadOnlyList<ItemUsage> _usageRows = [];

    public bool HasUsage => UsageRows.Count > 0;
    public string UsageTitle => UsageRows.Count == 0 ? "IN YOUR BUILDS" : $"IN YOUR BUILDS · {UsageRows.Count}";

    public bool HasActiveFilters => Search.Length > 0 || SelectedTier != AllTiers || SelectedMap != SummonersRift || OnlyInBuilds || StatFilters.Any(f => f.IsOn);

    partial void OnSearchChanged(string value) => Refresh();
    partial void OnSelectedTierChanged(string value) => Refresh();
    partial void OnSelectedMapChanged(string value) => Refresh();
    partial void OnSelectedSortChanged(string value) => Refresh();
    partial void OnOnlyInBuildsChanged(bool value) => Refresh();

    partial void OnSelectedChanged(ItemTileViewModel? oldValue, ItemTileViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        ShowDetails();
    }

    public async Task OnNavigatedToAsync()
    {
        // Builds may have changed since the last visit.
        var builds = await _shell.Repository.GetAllBuildsAsync();
        var game = _shell.Game;
        var usage = new Dictionary<string, List<ItemUsage>>();
        foreach (var b in builds.OrderBy(b => game.Champion(b.ChampionId)?.Name ?? b.ChampionId, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.SortOrder))
        {
            if (game.Champion(b.ChampionId) is not { } champion) continue;
            foreach (var block in b.Items.Blocks)
                foreach (var id in block.Items.Distinct())
                {
                    if (!usage.TryGetValue(id, out var list)) usage[id] = list = [];
                    if (list.Any(u => u.BuildId == b.Id)) continue;
                    list.Add(new ItemUsage(champion.Id, champion.Name, AssetUrls.ChampionSquare(game.Version, champion.ImageFile), b.Id, b.Name, block.Name));
                }
        }
        _usage = usage;
        foreach (var t in _all) t.BuildCount = usage.TryGetValue(t.Item.Id, out var u) ? u.Count : 0;
        Refresh();
        ShowDetails();
    }

    /// <summary>Selects an item (from search); clears filters that would hide it.</summary>
    public void Show(string itemId)
    {
        var tile = _all.FirstOrDefault(t => t.Item.Id == itemId);
        if (tile is null) return;
        if (!Tiles.Contains(tile)) ClearFilters();
        Selected = tile;
    }

    [RelayCommand]
    private void Select(ItemTileViewModel tile) => Selected = tile;

    [RelayCommand]
    private void SelectFirst()
    {
        if (Tiles.Count > 0) Selected = Tiles[0];
    }

    [RelayCommand]
    private void OpenUsage(ItemUsage usage) => _shell.OpenChampion(usage.ChampionId, ChampionTab.Builds, buildId: usage.BuildId);

    [RelayCommand]
    private void ClearFilters()
    {
        _restoring = true;
        Search = "";
        SelectedTier = AllTiers;
        SelectedMap = SummonersRift;
        OnlyInBuilds = false;
        foreach (var f in StatFilters) f.IsOn = false;
        _restoring = false;
        Refresh();
    }

    private void Refresh()
    {
        if (_restoring) return;
        var query = ChampionInfo.Normalize(Search.Trim());
        var stats = StatFilters.Where(f => f.IsOn).Select(f => f.Name).ToList();
        IEnumerable<ItemTileViewModel> list = _all.Where(t =>
            (query.Length == 0 || t.Item.SearchKey.Contains(query, StringComparison.Ordinal) || ChampionInfo.Normalize(t.Item.PlainText).Contains(query, StringComparison.Ordinal)) &&
            TierMatches(t.Item) &&
            (SelectedMap switch { SummonersRift => t.Item.OnSummonersRift, Aram => t.Item.OnAram, _ => true }) &&
            (!OnlyInBuilds || t.BuildCount > 0) &&
            stats.All(s => t.StatNames.Contains(s, StringComparer.OrdinalIgnoreCase)));
        list = SelectedSort switch
        {
            "Name" => list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "Gold efficiency" => list.OrderByDescending(t => t.Efficiency?.Percent ?? -1).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "Used in builds" => list.OrderByDescending(t => t.BuildCount).ThenByDescending(t => t.Item.TotalGold).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            _ => list.OrderByDescending(t => t.Item.TotalGold).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
        };
        Tiles = list.ToList();
        OnPropertyChanged(nameof(HasActiveFilters));
        _shell.Preferences.Set(PrefsKey, new ItemFilterPrefs
        {
            Tier = SelectedTier,
            Map = SelectedMap,
            Sort = SelectedSort,
            OnlyInBuilds = OnlyInBuilds,
            Stats = stats,
        });
    }

    private bool TierMatches(ItemInfo item) => SelectedTier switch
    {
        "Legendary" => item.Tier == ItemTier.Legendary,
        "Epic" => item.Tier == ItemTier.Epic,
        "Basic" => item.Tier == ItemTier.Basic,
        "Boots" => item.Tier == ItemTier.Boots,
        "Starter" => item.Tier == ItemTier.Starter,
        "Consumables" => item.Tier is ItemTier.Consumable or ItemTier.Trinket,
        _ => true,
    };

    private void ShowDetails()
    {
        if (Selected is not { } tile)
        {
            Details = null;
            WorthRows = [];
            UsageRows = [];
            EfficiencyText = "";
            UnvaluedText = "";
            return;
        }
        Details = new ItemDetailsViewModel(_shell.Game, tile.Item);
        if (tile.Efficiency is { } e)
        {
            WorthRows = e.Parts.Select(p => new ItemWorthRow(p.Stat.Text, ChampionMath.Number(p.Gold, 0) + " g")).ToList();
            EfficiencyText = $"Stats worth {ChampionMath.Number(e.Worth, 0)} of {e.Cost:N0} gold: {e.PercentText} gold efficient";
            UnvaluedText = e.Unvalued.Count > 0 ? "Not valued (no basic item gives only this): " + string.Join(", ", e.Unvalued.Select(s => s.Text)) + ". Passives aren't valued either." : "Passives aren't valued.";
        }
        else
        {
            WorthRows = [];
            EfficiencyText = "";
            UnvaluedText = "";
        }
        UsageRows = _usage.TryGetValue(tile.Item.Id, out var u) ? u : [];
    }

    public sealed class ItemFilterPrefs
    {
        public string Tier { get; set; } = AllTiers;
        public string Map { get; set; } = SummonersRift;
        public string Sort { get; set; } = "Cost";
        public bool OnlyInBuilds { get; set; }
        public List<string> Stats { get; set; } = [];
    }
}

/// <summary>An item tile on the Items page.</summary>
public sealed partial class ItemTileViewModel : ObservableObject
{
    public ItemTileViewModel(ItemInfo item, string version, ItemEfficiency? efficiency)
    {
        Item = item;
        IconUrl = AssetUrls.Item(version, item.Id);
        Efficiency = efficiency;
        StatNames = ItemStats.Parse(item.Description).Select(s => s.Name).ToList();
    }

    public ItemInfo Item { get; }
    public string Name => Item.Name;
    public string IconUrl { get; }
    public string CostText => Item.TotalGold == 0 ? "Free" : Item.TotalGold.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public ItemEfficiency? Efficiency { get; }
    public IReadOnlyList<string> StatNames { get; }
    public bool HasEfficiency => Efficiency is not null;
    public string EfficiencyText => Efficiency?.PercentText ?? "";
    public IBrush EfficiencyBrush => Palette.Hex(Efficiency?.Percent switch { >= 100 => Palette.Good, >= 85 => Palette.Gold, _ => Palette.Muted });
    public string Tooltip => Efficiency is { } e ? $"{Name} · {CostText} gold · {e.PercentText} gold efficient" : $"{Name} · {CostText} gold";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuilds))]
    private int _buildCount;

    public bool HasBuilds => BuildCount > 0;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class ItemStatFilter(string name, string label, Action changed) : ObservableObject
{
    public string Name { get; } = name;
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => changed();
}

public sealed record ItemWorthRow(string Stat, string Gold);

/// <summary>A build that uses the selected item.</summary>
public sealed record ItemUsage(string ChampionId, string ChampionName, string IconUrl, int BuildId, string BuildName, string BlockName)
{
    public string Text => $"{ChampionName} · {BuildName}";
    public string Detail => string.IsNullOrWhiteSpace(BlockName) ? "" : BlockName;
}
