using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Two champions (or two builds of one champion) side by side.</summary>
public sealed partial class CompareViewModel : ObservableObject, IPage
{
    private readonly IShell _shell;
    private readonly string? _leftId;
    private readonly string? _rightId;
    private bool _loaded;

    public CompareViewModel(IShell shell, string? leftChampionId, string? rightChampionId)
    {
        _shell = shell;
        _leftId = leftChampionId;
        _rightId = rightChampionId;
        Left = new CompareSideViewModel(shell, this);
        Right = new CompareSideViewModel(shell, this);
    }

    public string Title => "Compare";
    public CompareSideViewModel Left { get; }
    public CompareSideViewModel Right { get; }

    /// <summary>What each champion's matchups say about the other ("Ahri is good against Zed: …").</summary>
    [ObservableProperty]
    private IReadOnlyList<HeadToHeadLine> _headToHead = [];

    public bool HasHeadToHead => HeadToHead.Count > 0;
    public bool BothChosen => Left.HasChampion && Right.HasChampion;

    partial void OnHeadToHeadChanged(IReadOnlyList<HeadToHeadLine> value) => OnPropertyChanged(nameof(HasHeadToHead));

    public async Task OnNavigatedToAsync()
    {
        if (_loaded) return;
        _loaded = true;
        if (_leftId is not null) await Left.SetChampionAsync(_leftId);
        if (_rightId is not null) await Right.SetChampionAsync(_rightId);
        Refresh();
    }

    [RelayCommand]
    private async Task SwapAsync()
    {
        var (left, right) = (Left.Champion?.Id, Right.Champion?.Id);
        var (leftBuild, rightBuild) = (Left.SelectedBuild?.Id, Right.SelectedBuild?.Id);
        await Left.SetChampionAsync(right, rightBuild);
        await Right.SetChampionAsync(left, leftBuild);
        Refresh();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(BothChosen));
        if (Left.Champion is not { } a || Right.Champion is not { } b || a.Id == b.Id)
        {
            HeadToHead = [];
            return;
        }
        var lines = new List<HeadToHeadLine>();
        foreach (var m in Left.AllMatchups.Where(m => string.Equals(m.OpponentId, b.Id, StringComparison.OrdinalIgnoreCase)))
            lines.Add(new HeadToHeadLine(a.Name, b.Name, m.Kind, m.Role, NoteFormat.ToPlainText(m.Note).Replace('\n', ' ')));
        foreach (var m in Right.AllMatchups.Where(m => string.Equals(m.OpponentId, a.Id, StringComparison.OrdinalIgnoreCase)))
            lines.Add(new HeadToHeadLine(b.Name, a.Name, m.Kind, m.Role, NoteFormat.ToPlainText(m.Note).Replace('\n', ' ')));
        HeadToHead = lines;
    }
}

public sealed record HeadToHeadLine(string Champion, string Opponent, MatchupKind Kind, Position? Role, string Note)
{
    public bool IsGood => Kind == MatchupKind.GoodAgainst;
    public string Text => $"{Champion} is {(IsGood ? "good" : "weak")} against {Opponent}{(Role is { } r ? $" ({r.DisplayName()})" : "")}";
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public string Icon => IsGood ? "thumbs-up" : "thumbs-down";
    public IBrush Brush => Palette.Hex(IsGood ? "#4CC38A" : "#EE5A6F");
}

/// <summary>One side of the comparison: a champion picker, a build selector and the build's details.</summary>
public sealed partial class CompareSideViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly CompareViewModel _owner;

    public CompareSideViewModel(IShell shell, CompareViewModel owner)
    {
        _shell = shell;
        _owner = owner;
    }

    private GameDataSnapshot Game => _shell.Game;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChampion), nameof(Name), nameof(ChampionTitle), nameof(PortraitUrl))]
    private ChampionInfo? _champion;

    public bool HasChampion => Champion is not null;
    public string Name => Champion?.Name ?? "";
    public string ChampionTitle => Champion?.Title ?? "";
    public string? PortraitUrl => Champion is { } c ? AssetUrls.ChampionSquare(Game.Version, c.ImageFile) : null;

    // ── Picker ─────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private IReadOnlyList<CopyTarget> _results = [];

    partial void OnQueryChanged(string value)
    {
        var query = ChampionInfo.Normalize(value);
        Results = query.Length == 0
            ? []
            : Game.Champions.Where(c => c.Matches(value))
                .OrderBy(c => !ChampionInfo.Normalize(c.Name).StartsWith(query, StringComparison.Ordinal))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(c => new CopyTarget(c, AssetUrls.ChampionSquare(Game.Version, c.ImageFile)))
                .ToList();
    }

    [RelayCommand]
    private async Task PickAsync(CopyTarget target)
    {
        Query = "";
        await SetChampionAsync(target.Champion.Id);
        _owner.Refresh();
    }

    [RelayCommand]
    private void Open()
    {
        if (Champion is { } c) _shell.OpenChampion(c.Id, ChampionTab.Builds, buildId: SelectedBuild?.Id);
    }

    // ── Data ───────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<Build> _builds = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuild))]
    private Build? _selectedBuild;

    public bool HasBuild => SelectedBuild is not null;
    public bool HasNoBuilds => HasChampion && Builds.Count == 0;

    [ObservableProperty]
    private IReadOnlyList<ComparePlacement> _placements = [];

    [ObservableProperty]
    private IReadOnlyList<CompareIcon> _good = [];

    [ObservableProperty]
    private IReadOnlyList<CompareIcon> _weak = [];

    public IReadOnlyList<Matchup> AllMatchups { get; private set; } = [];

    // Build details
    [ObservableProperty] private IReadOnlyList<CompareIcon> _primaryRunes = [];
    [ObservableProperty] private IReadOnlyList<CompareIcon> _secondaryRunes = [];
    [ObservableProperty] private IReadOnlyList<CompareIcon> _shards = [];
    [ObservableProperty] private IReadOnlyList<CompareBlock> _blocks = [];
    [ObservableProperty] private IReadOnlyList<CompareIcon> _spells = [];
    [ObservableProperty] private string _skillOrder = "";
    [ObservableProperty] private string _runeTrees = "";
    [ObservableProperty] private string _coreTotals = "";

    public bool HasPlacements => Placements.Count > 0;
    public bool HasGood => Good.Count > 0;
    public bool HasWeak => Weak.Count > 0;
    public bool HasSkillOrder => SkillOrder.Length > 0;

    partial void OnPlacementsChanged(IReadOnlyList<ComparePlacement> value) => OnPropertyChanged(nameof(HasPlacements));
    partial void OnGoodChanged(IReadOnlyList<CompareIcon> value) => OnPropertyChanged(nameof(HasGood));
    partial void OnWeakChanged(IReadOnlyList<CompareIcon> value) => OnPropertyChanged(nameof(HasWeak));
    partial void OnSkillOrderChanged(string value) => OnPropertyChanged(nameof(HasSkillOrder));
    partial void OnSelectedBuildChanged(Build? value) => ShowBuild(value);

    internal async Task SetChampionAsync(string? championId, int? buildId = null)
    {
        Champion = championId is null ? null : Game.Champion(championId);
        if (Champion is not { } champion)
        {
            Builds = [];
            SelectedBuild = null;
            Placements = [];
            Good = Weak = [];
            AllMatchups = [];
            OnPropertyChanged(nameof(HasNoBuilds));
            return;
        }
        var repo = _shell.Repository;
        Builds = await repo.GetBuildsAsync(champion.Id);
        SelectedBuild = (buildId is { } id ? Builds.FirstOrDefault(b => b.Id == id) : null)
                        ?? Builds.FirstOrDefault(b => b.IsDefault) ?? Builds.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoBuilds));

        var groups = await repo.GetGroupsAsync();
        Placements = groups
            .SelectMany(g => g.Entries.Where(e => string.Equals(e.ChampionId, champion.Id, StringComparison.OrdinalIgnoreCase)).Select(e => (g, e)))
            .Select(x => new ComparePlacement(x.g.Name, Palette.Hex(x.g.Color ?? Palette.RoleHex(x.g.Role)), x.e.Proficiency, x.e.Priority))
            .ToList();

        AllMatchups = await repo.GetMatchupsAsync(champion.Id);
        CompareIcon Opponent(Matchup m) => Game.Champion(m.OpponentId) is { } o
            ? new CompareIcon(AssetUrls.ChampionSquare(Game.Version, o.ImageFile), o.Name + (m.Role is { } r ? $" ({r.DisplayName()})" : "") + (string.IsNullOrWhiteSpace(m.Note) ? "" : "\n" + NoteFormat.ToPlainText(m.Note)))
            : new CompareIcon(null, m.OpponentId);
        Good = AllMatchups.Where(m => m.Kind == MatchupKind.GoodAgainst).OrderBy(m => m.SortOrder).Select(Opponent).ToList();
        Weak = AllMatchups.Where(m => m.Kind == MatchupKind.WeakAgainst).OrderBy(m => m.SortOrder).Select(Opponent).ToList();
    }

    private void ShowBuild(Build? build)
    {
        if (build is null)
        {
            PrimaryRunes = SecondaryRunes = Shards = Spells = [];
            Blocks = [];
            SkillOrder = RuneTrees = CoreTotals = "";
            return;
        }
        var runes = build.Runes;
        CompareIcon Rune(int id) => Game.Rune(id) is { } r ? new CompareIcon(AssetUrls.Rune(r.IconPath), r.Name) : new CompareIcon(null, "—");
        PrimaryRunes = runes.PrimaryPerks.Where(id => id != 0).Select(Rune).ToList();
        SecondaryRunes = runes.SubPerks.Where(id => id != 0).Select(Rune).ToList();
        Shards = runes.Shards.Where(id => id != 0).Select(id => Game.Shard(id) is { } s ? new CompareIcon(s.IconUrl, s.Name) : new CompareIcon(null, "—")).ToList();
        RuneTrees = string.Join(" + ", new[] { Game.Tree(runes.PrimaryStyleId)?.Name, Game.Tree(runes.SubStyleId)?.Name }.Where(n => n is not null));
        Blocks = build.Items.Blocks.Where(b => b.Items.Count > 0).Select(b => new CompareBlock(b.Name,
            b.Items.Select(id => new CompareIcon(AssetUrls.Item(Game.Item(id) is null ? build.GameVersion ?? Game.Version : Game.Version, id), Game.Item(id)?.Name ?? $"Removed item {id}")).ToList())).ToList();
        Spells = build.SummonerSpells.Select(Game.Spell).OfType<SummonerSpellInfo>().Select(s => new CompareIcon(AssetUrls.Spell(Game.Version, s.ImageFile), s.Name)).ToList();
        CoreTotals = ItemStats.Core(build, Game) is { } totals ? "Core: " + totals.Summary(8) : "";
        var order = SkillOrderRules.MaxOrder(build.SkillOrder);
        SkillOrder = order.Count == 0 ? "" : "Max " + string.Join(" › ", order);
    }
}

public sealed record CompareIcon(string? Url, string Tip);

public sealed record CompareBlock(string Name, IReadOnlyList<CompareIcon> Items);

public sealed record ComparePlacement(string Group, IBrush Accent, int Proficiency, ChampionPriority Priority)
{
    public string PriorityLabel => Priority.ToString().ToUpperInvariant();
    public IBrush PriorityBrush => Palette.Hex(Palette.PriorityHex(Priority));
}
