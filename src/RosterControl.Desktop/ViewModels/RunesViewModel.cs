using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// The Runes page: every rune and stat shard by path and slot, with its description, its formulas from the game files
/// (as text, not calculated) and the builds of yours that use it.
/// </summary>
public sealed partial class RunesViewModel : ObservableObject, IPage
{
    private const string PrefsKey = "runes.filters";
    public const string AllPaths = "All paths";
    public const string AllSlots = "All slots";
    public const string Keystones = "Keystones";
    public const string StatShards = "Stat shards";

    private readonly IShell _shell;
    private readonly List<RuneTileViewModel> _all = [];
    private readonly List<(RuneTreeInfo? Tree, string RowLabel, RuneTileViewModel Tile)> _layout = [];
    private Dictionary<string, List<RuneUsage>> _usage = [];
    private RuneNumbers? _numbers;
    private NumbersState _numbersState;

    private enum NumbersState { NotLoaded, Loading, Loaded, Failed }
    private bool _restoring;

    public RunesViewModel(IShell shell)
    {
        _shell = shell;
        var game = shell.Game;
        foreach (var tree in game.RuneTrees)
            for (var row = 0; row < tree.Rows.Count; row++)
            {
                var label = row == 0 ? "KEYSTONES" : $"SLOT {row}";
                foreach (var rune in tree.Rows[row])
                {
                    var tile = new RuneTileViewModel(RuneTileViewModel.RuneKey(rune.Id), rune.Name, AssetUrls.Rune(rune.IconPath), rune.IsKeystone, tree.Name, row)
                    { Rune = rune, Tree = tree };
                    _all.Add(tile);
                    _layout.Add((tree, label, tile));
                }
            }
        for (var row = 0; row < game.ShardRows.Count; row++)
        {
            var shardRow = game.ShardRows[row];
            foreach (var shard in shardRow.Options)
            {
                var tile = new RuneTileViewModel(RuneTileViewModel.ShardKey(shard.Id, row), shard.Name, shard.IconUrl, false, StatShards, row) { Shard = shard, ShardRow = shardRow.Label };
                _all.Add(tile);
                _layout.Add((null, shardRow.Label.ToUpperInvariant(), tile));
            }
        }
        PathOptions = [AllPaths, .. game.RuneTrees.Select(t => t.Name), StatShards];

        _restoring = true;
        var prefs = shell.Preferences.Get(PrefsKey, new RuneFilterPrefs());
        _selectedPath = PathOptions.Contains(prefs.Path) ? prefs.Path : AllPaths;
        _selectedSlot = SlotOptions.Contains(prefs.Slot) ? prefs.Slot : AllSlots;
        _onlyInBuilds = prefs.OnlyInBuilds;
        _restoring = false;
        Refresh();
    }

    public string Title => "Runes";

    public IReadOnlyList<string> PathOptions { get; }
    public IReadOnlyList<string> SlotOptions { get; } = [AllSlots, Keystones, "Slot 1", "Slot 2", "Slot 3"];

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private string _selectedPath;

    [ObservableProperty]
    private string _selectedSlot;

    [ObservableProperty]
    private bool _onlyInBuilds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<RuneSection> _sections = [];

    [ObservableProperty]
    private string _countText = "";

    public bool IsEmpty => Sections.Count == 0;
    public bool HasActiveFilters => Search.Length > 0 || SelectedPath != AllPaths || SelectedSlot != AllSlots || OnlyInBuilds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RuneTileViewModel? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage), nameof(UsageTitle))]
    private IReadOnlyList<RuneUsage> _usageRows = [];

    public bool HasUsage => UsageRows.Count > 0;
    public string UsageTitle => UsageRows.Count == 0 ? "IN YOUR BUILDS" : $"IN YOUR BUILDS · {UsageRows.Count}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormulas))]
    private IReadOnlyList<RuneFormulaRow> _formulas = [];

    public bool HasFormulas => Formulas.Count > 0;

    [ObservableProperty]
    private string _formulaNote = "";

    partial void OnSearchChanged(string value) => Refresh();
    partial void OnSelectedPathChanged(string value) => Refresh();
    partial void OnSelectedSlotChanged(string value) => Refresh();
    partial void OnOnlyInBuildsChanged(bool value) => Refresh();

    partial void OnSelectedChanged(RuneTileViewModel? oldValue, RuneTileViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        ShowDetails();
    }

    public async Task OnNavigatedToAsync()
    {
        var builds = await _shell.Repository.GetAllBuildsAsync();
        var game = _shell.Game;
        var usage = new Dictionary<string, List<RuneUsage>>();
        void Add(string key, RuneUsage use)
        {
            if (!usage.TryGetValue(key, out var list)) usage[key] = list = [];
            if (!list.Any(u => u.BuildId == use.BuildId)) list.Add(use);
        }
        foreach (var b in builds.OrderBy(b => game.Champion(b.ChampionId)?.Name ?? b.ChampionId, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.SortOrder))
        {
            if (game.Champion(b.ChampionId) is not { } champion) continue;
            var icon = AssetUrls.ChampionSquare(game.Version, champion.ImageFile);
            foreach (var id in b.Runes.PrimaryPerks.Where(id => id != 0))
                Add(RuneTileViewModel.RuneKey(id), new RuneUsage(champion.Id, champion.Name, icon, b.Id, b.Name, "Primary"));
            foreach (var id in b.Runes.SubPerks.Where(id => id != 0))
                Add(RuneTileViewModel.RuneKey(id), new RuneUsage(champion.Id, champion.Name, icon, b.Id, b.Name, "Secondary"));
            for (var row = 0; row < b.Runes.Shards.Count; row++)
                if (b.Runes.Shards[row] != 0)
                    Add(RuneTileViewModel.ShardKey(b.Runes.Shards[row], row), new RuneUsage(champion.Id, champion.Name, icon, b.Id, b.Name, "Stat shard"));
        }
        _usage = usage;
        foreach (var t in _all) t.BuildCount = usage.TryGetValue(t.Key, out var u) ? u.Count : 0;
        Refresh();
        ShowDetails();
        if (_numbersState is NumbersState.NotLoaded or NumbersState.Failed)
        {
            _numbersState = NumbersState.Loading;
            ShowDetails();
            _numbers = await _shell.GameData.GetRuneNumbersAsync(CancellationToken.None);
            _numbersState = _numbers is null ? NumbersState.Failed : NumbersState.Loaded;
            ShowDetails();
        }
    }

    /// <summary>Selects a rune (from search); clears filters that would hide it.</summary>
    public void Show(int runeId)
    {
        var tile = _all.FirstOrDefault(t => t.Key == RuneTileViewModel.RuneKey(runeId));
        if (tile is null) return;
        if (!Sections.Any(s => s.Rows.Any(r => r.Tiles.Contains(tile)))) ClearFilters();
        Selected = tile;
    }

    [RelayCommand]
    private void Select(RuneTileViewModel tile) => Selected = tile;

    [RelayCommand]
    private void SelectFirst()
    {
        if (Sections.FirstOrDefault()?.Rows.FirstOrDefault()?.Tiles.FirstOrDefault() is { } first) Selected = first;
    }

    [RelayCommand]
    private void OpenUsage(RuneUsage usage) => _shell.OpenChampion(usage.ChampionId, ChampionTab.Builds, buildId: usage.BuildId);

    [RelayCommand]
    private void ClearFilters()
    {
        _restoring = true;
        Search = "";
        SelectedPath = AllPaths;
        SelectedSlot = AllSlots;
        OnlyInBuilds = false;
        _restoring = false;
        Refresh();
    }

    private bool SlotMatches(RuneTileViewModel t) => SelectedSlot switch
    {
        Keystones => t.Rune is not null && t.Row == 0,
        "Slot 1" => t.Rune is not null && t.Row == 1,
        "Slot 2" => t.Rune is not null && t.Row == 2,
        "Slot 3" => t.Rune is not null && t.Row == 3,
        _ => true,
    };

    private void Refresh()
    {
        if (_restoring) return;
        var query = ChampionInfo.Normalize(Search.Trim());
        bool Matches(RuneTileViewModel t) =>
            (query.Length == 0 || ChampionInfo.Normalize(t.Name).Contains(query, StringComparison.Ordinal)
                               || ChampionInfo.Normalize(t.Rune?.ShortDesc ?? t.Shard?.ShortDesc ?? "").Contains(query, StringComparison.Ordinal)) &&
            (SelectedPath == AllPaths || t.PathName == SelectedPath) &&
            (SelectedSlot == AllSlots || SlotMatches(t)) &&
            (!OnlyInBuilds || t.BuildCount > 0);

        var sections = new List<RuneSection>();
        foreach (var group in _layout.Where(x => Matches(x.Tile)).GroupBy(x => x.Tree?.Name ?? StatShards))
        {
            var tree = group.First().Tree;
            var rows = group.GroupBy(x => x.RowLabel).Select(r => new RuneRowGroup(r.Key, r.Select(x => x.Tile).ToList())).ToList();
            sections.Add(new RuneSection(group.Key, tree is null ? null : AssetUrls.Rune(tree.IconPath), tree?.Tagline ?? "Small stat bonuses: one from each row.", tree?.Color, rows));
        }
        Sections = sections;
        var count = sections.Sum(s => s.Rows.Sum(r => r.Tiles.Count));
        CountText = $"{count} rune{(count == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(HasActiveFilters));
        _shell.Preferences.Set(PrefsKey, new RuneFilterPrefs { Path = SelectedPath, Slot = SelectedSlot, OnlyInBuilds = OnlyInBuilds });
    }

    private void ShowDetails()
    {
        if (Selected is not { } tile)
        {
            UsageRows = [];
            Formulas = [];
            FormulaNote = "";
            return;
        }
        UsageRows = _usage.TryGetValue(tile.Key, out var u) ? u : [];
        if (tile.Rune is { } rune)
        {
            var values = _numbers?[rune.Id] ?? [];
            Formulas = values.Select(v => new RuneFormulaRow(v.Label, v.Text)).ToList();
            FormulaNote = values.Count > 0
                ? $"From the game files (patch {_numbers!.Patch}, via Community Dragon). The Abilities tab's calculator works these out for a champion, level and items."
                : _numbersState switch
                {
                    NumbersState.NotLoaded or NumbersState.Loading => "Loading numbers from the game files…",
                    NumbersState.Failed => "Rune numbers couldn't be downloaded from the game files. Check your connection and open the page again.",
                    _ => $"The game files have no formula for {rune.Name}; its description above is all there is.",
                };
        }
        else
        {
            Formulas = [];
            FormulaNote = "";
        }
    }

    public sealed class RuneFilterPrefs
    {
        public string Path { get; set; } = AllPaths;
        public string Slot { get; set; } = AllSlots;
        public bool OnlyInBuilds { get; set; }
    }
}

/// <summary>A rune or stat shard tile on the Runes page.</summary>
public sealed partial class RuneTileViewModel(string key, string name, string iconUrl, bool isKeystone, string pathName, int row) : ObservableObject
{
    public static string RuneKey(int id) => "r" + id;
    /// <summary>Stat shards repeat across rows (Adaptive Force is in two), so the row is part of the key.</summary>
    public static string ShardKey(int id, int row) => $"s{id}:{row}";

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string IconUrl { get; } = iconUrl;
    public bool IsKeystone { get; } = isKeystone;
    public string PathName { get; } = pathName;
    public int Row { get; } = row;
    public double IconSize => IsKeystone ? 50 : Shard is not null ? 28 : 38;
    public RuneInfo? Rune { get; init; }
    public RuneTreeInfo? Tree { get; init; }
    public StatShardInfo? Shard { get; init; }
    public string? ShardRow { get; init; }

    public string Description => Rune?.LongDesc ?? Shard?.ShortDesc ?? "";
    public string KindText => Rune is null ? $"STAT SHARD · {ShardRow?.ToUpperInvariant()}" : IsKeystone ? $"{PathName.ToUpperInvariant()} · KEYSTONE" : $"{PathName.ToUpperInvariant()} · SLOT {Row}";
    public string Tip => Rune is not null ? $"{Name} — {Kind}" : $"{Name} ({ShardRow})";
    private string Kind => IsKeystone ? "keystone" : $"slot {Row}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuilds))]
    private int _buildCount;

    public bool HasBuilds => BuildCount > 0;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record RuneRowGroup(string Label, IReadOnlyList<RuneTileViewModel> Tiles);

/// <param name="Color">The path's colour ("#C8AA6E"); null for stat shards.</param>
public sealed record RuneSection(string Name, string? IconUrl, string Tagline, string? Color, IReadOnlyList<RuneRowGroup> Rows)
{
    public bool HasIcon => IconUrl is not null;
    public Avalonia.Media.IBrush Accent => Palette.Hex(Color is { Length: > 0 } c ? c : Palette.Muted);
}

public sealed record RuneFormulaRow(string Label, string Text);

/// <summary>A build that uses the selected rune.</summary>
public sealed record RuneUsage(string ChampionId, string ChampionName, string IconUrl, int BuildId, string BuildName, string Where)
{
    public string Text => $"{ChampionName} · {BuildName}";
}
