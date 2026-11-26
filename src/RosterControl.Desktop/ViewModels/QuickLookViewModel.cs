using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// The quick-look window: one champion's build at a glance (runes, items, spells, skill order), always on top, opened
/// with a global shortcut. You pick the champion yourself; nothing is read from the League client.
/// </summary>
public sealed partial class QuickLookViewModel : ObservableObject
{
    private const string PinnedKey = "quicklook.pinned";
    private const string LastChampionKey = "quicklook.champion";
    private const int MaxResults = 8;

    private readonly IShell _shell;
    private int _loadVersion;
    private Position? _role;

    public QuickLookViewModel(IShell shell)
    {
        _shell = shell;
        _isPinned = shell.Preferences.Get(PinnedKey, true);
    }

    private GameDataSnapshot Game => _shell.Game;

    /// <summary>Asks the window to focus the search box (e.g. after the global shortcut).</summary>
    public event EventHandler? FocusSearchRequested;

    public void RequestSearchFocus() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    // ── Window ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Always on top of other windows (including the game in borderless mode).</summary>
    [ObservableProperty]
    private bool _isPinned;

    partial void OnIsPinnedChanged(bool value) => _shell.Preferences.Set(PinnedKey, value);

    [ObservableProperty]
    private string _shortcutHint = "";

    // ── Search ─────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private IReadOnlyList<QuickLookResult> _results = [];

    [ObservableProperty]
    private string _search = "";

    public bool HasResults => Results.Count > 0;

    partial void OnSearchChanged(string value)
    {
        var query = value.Trim();
        if (query.Length == 0)
        {
            Results = [];
            return;
        }
        var normalized = ChampionInfo.Normalize(query);
        Results = Game.Champions.Where(c => c.Matches(query))
            .OrderBy(c => ChampionInfo.Normalize(c.Name).StartsWith(normalized, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .Select(c => new QuickLookResult(c.Id, c.Name, AssetUrls.ChampionSquare(Game.Version, c.ImageFile)))
            .ToList();
    }

    /// <summary>Enter in the search box: the best match.</summary>
    [RelayCommand]
    private Task PickFirstAsync() => Results.Count > 0 ? PickAsync(Results[0]) : Task.CompletedTask;

    [RelayCommand]
    private Task PickAsync(QuickLookResult result)
    {
        Search = "";
        return ShowChampionAsync(result.Id);
    }

    // ── Champion and builds ────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChampion), nameof(ChampionName), nameof(ChampionTitle), nameof(PortraitUrl), nameof(ShowIntro))]
    private ChampionInfo? _champion;

    public bool HasChampion => Champion is not null;
    public bool ShowIntro => Champion is null;
    public string ChampionName => Champion?.Name ?? "";
    public string ChampionTitle => Champion?.Title ?? "";
    public string? PortraitUrl => Champion is { } c ? AssetUrls.ChampionSquare(Game.Version, c.ImageFile) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuilds), nameof(HasNoBuilds), nameof(HasSeveralBuilds))]
    private IReadOnlyList<QuickLookBuildChoice> _builds = [];

    public bool HasBuilds => Builds.Count > 0;
    public bool HasNoBuilds => HasChampion && Builds.Count == 0;
    public bool HasSeveralBuilds => Builds.Count > 1;

    [ObservableProperty]
    private QuickLookBuildChoice? _selectedBuild;

    partial void OnSelectedBuildChanged(QuickLookBuildChoice? oldValue, QuickLookBuildChoice? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        ShowBuild(newValue?.Build);
    }

    [RelayCommand]
    private void SelectBuild(QuickLookBuildChoice choice) => SelectedBuild = choice;

    /// <summary>Shows a champion: the build <paramref name="buildId"/>, else the default for <paramref name="role"/>.</summary>
    public async Task ShowChampionAsync(string championId, Position? role = null, int? buildId = null)
    {
        if (Game.Champion(championId) is not { } champion) return;
        var version = ++_loadVersion;
        _role = role;
        var changed = !ReferenceEquals(Champion, champion);
        Champion = champion;
        _shell.Preferences.Set(LastChampionKey, champion.Id);
        var builds = await _shell.Repository.GetBuildsAsync(champion.Id);
        if (version != _loadVersion) return;
        Builds = builds.Select(b => new QuickLookBuildChoice(b)).ToList();
        SelectedBuild = (buildId is { } id ? Builds.FirstOrDefault(b => b.Build.Id == id) : null)
                        ?? BuildSelection.PickDefault(Builds, role, b => b.Build);
        if (changed) await LoadAbilitiesAsync(champion.Id, version);
    }

    /// <summary>Reloads the builds (they may have been edited in the main window), keeping the selected one.</summary>
    public Task RefreshAsync() =>
        Champion is { } c ? ShowChampionAsync(c.Id, _role, SelectedBuild?.Build.Id) : Task.CompletedTask;

    /// <summary>Shows the champion looked at last time, when the window opens empty.</summary>
    public Task RestoreLastAsync() =>
        Champion is null && _shell.Preferences.Get<string?>(LastChampionKey, null) is { } id ? ShowChampionAsync(id) : Task.CompletedTask;

    [RelayCommand]
    private void OpenInApp()
    {
        if (Champion is not { } c) return;
        _shell.OpenChampion(c.Id, ChampionTab.Builds, buildId: SelectedBuild?.Build.Id);
        _shell.BringToFront();
    }

    // ── The selected build ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string? _keystoneUrl;
    [ObservableProperty] private string _keystoneName = "";
    [ObservableProperty] private string _runeTrees = "";
    [ObservableProperty] private IReadOnlyList<CompareIcon> _primaryRunes = [];
    [ObservableProperty] private IReadOnlyList<CompareIcon> _secondaryRunes = [];
    [ObservableProperty] private IReadOnlyList<CompareIcon> _shards = [];
    [ObservableProperty] private IReadOnlyList<QuickLookBlock> _blocks = [];
    [ObservableProperty] private string _coreTotals = "";
    [ObservableProperty] private IReadOnlyList<CompareIcon> _spells = [];
    [ObservableProperty] private IReadOnlyList<QuickLookSkillCell> _skillCells = [];
    [ObservableProperty] private IReadOnlyList<QuickLookMaxStep> _maxOrder = [];

    public bool HasRunes => PrimaryRunes.Count > 0 || KeystoneUrl is not null;
    public bool HasBlocks => Blocks.Count > 0;
    public bool HasSpells => Spells.Count > 0;
    public bool HasSkillOrder => SkillCells.Any(c => c.HasSkill);
    public bool HasCoreTotals => CoreTotals.Length > 0;

    partial void OnPrimaryRunesChanged(IReadOnlyList<CompareIcon> value) => OnPropertyChanged(nameof(HasRunes));
    partial void OnBlocksChanged(IReadOnlyList<QuickLookBlock> value) => OnPropertyChanged(nameof(HasBlocks));
    partial void OnSpellsChanged(IReadOnlyList<CompareIcon> value) => OnPropertyChanged(nameof(HasSpells));
    partial void OnSkillCellsChanged(IReadOnlyList<QuickLookSkillCell> value) => OnPropertyChanged(nameof(HasSkillOrder));
    partial void OnCoreTotalsChanged(string value) => OnPropertyChanged(nameof(HasCoreTotals));

    private IReadOnlyList<AbilityDetailViewModel> _abilities = [];

    private void ShowBuild(Build? build)
    {
        if (build is null)
        {
            KeystoneUrl = null;
            KeystoneName = RuneTrees = CoreTotals = "";
            PrimaryRunes = SecondaryRunes = Shards = Spells = [];
            Blocks = [];
            SkillCells = [];
            MaxOrder = [];
            return;
        }
        var runes = build.Runes;
        CompareIcon Rune(int id) => Game.Rune(id) is { } r ? new CompareIcon(AssetUrls.Rune(r.IconPath), $"{r.Name}\n{r.ShortDesc}") : new CompareIcon(null, "—");
        var keystone = Game.Rune(runes.PrimaryPerks.ElementAtOrDefault(0));
        KeystoneUrl = keystone is not null ? AssetUrls.Rune(keystone.IconPath) : Game.Tree(runes.PrimaryStyleId) is { } tree ? AssetUrls.Rune(tree.IconPath) : null;
        KeystoneName = keystone?.Name ?? "No keystone";
        RuneTrees = string.Join(" + ", new[] { Game.Tree(runes.PrimaryStyleId)?.Name, Game.Tree(runes.SubStyleId)?.Name }.Where(n => n is not null));
        PrimaryRunes = runes.PrimaryPerks.Skip(1).Where(id => id != 0).Select(Rune).ToList();
        SecondaryRunes = runes.SubPerks.Where(id => id != 0).Select(Rune).ToList();
        Shards = runes.Shards.Where(id => id != 0).Select(id => Game.Shard(id) is { } s ? new CompareIcon(s.IconUrl, s.Name) : new CompareIcon(null, "—")).ToList();
        OnPropertyChanged(nameof(HasRunes));

        var core = BuildSelection.CoreBlock(build);
        Blocks = build.Items.Blocks.Where(b => b.Items.Count > 0).Select(b => new QuickLookBlock(b.Name, ReferenceEquals(b, core),
            b.Items.Select(id => new CompareIcon(AssetUrls.Item(Game.Item(id) is null ? build.GameVersion ?? Game.Version : Game.Version, id),
                Game.Item(id) is { } item ? $"{item.Name} · {item.TotalGold:N0} gold" : "Removed item")).ToList())).ToList();
        CoreTotals = ItemStats.Core(build, Game)?.Summary(6) ?? "";
        Spells = build.SummonerSpells.Select(Game.Spell).OfType<SummonerSpellInfo>()
            .Select(s => new CompareIcon(AssetUrls.Spell(Game.Version, s.ImageFile), $"{s.Name}\n{s.Description}")).ToList();
        ShowSkills(build);
    }

    private void ShowSkills(Build build)
    {
        SkillCells = Enumerable.Range(1, SkillOrderRules.MaxLevel)
            .Select(level => new QuickLookSkillCell(level, SkillOrderRules.At(build.SkillOrder, level) is var c && c != '-' ? c : null))
            .ToList();
        MaxOrder = SkillOrderRules.MaxOrder(build.SkillOrder)
            .Select(slot => new QuickLookMaxStep(slot, _abilities.FirstOrDefault(a => a.Slot == slot)))
            .ToList();
    }

    private async Task LoadAbilitiesAsync(string championId, int version)
    {
        _abilities = [];
        var abilities = await _shell.GameData.GetAbilitiesAsync(championId, CancellationToken.None);
        if (version != _loadVersion || abilities is null) return;
        _abilities = abilities.Abilities.Select(a => new AbilityDetailViewModel(a)).ToList();
        if (SelectedBuild?.Build is { } build) ShowSkills(build);
        var numbers = await _shell.GameData.GetAbilityNumbersAsync(championId, CancellationToken.None);
        if (version == _loadVersion) AbilitiesViewModel.Apply(_abilities, numbers);
    }
}

public sealed record QuickLookResult(string Id, string Name, string IconUrl);

public sealed partial class QuickLookBuildChoice(Build build) : ObservableObject
{
    public Build Build { get; } = build;
    public string Name => Build.Name;
    public bool IsDefault => Build.IsDefault;
    public string RoleText => Build.Role is { } r ? r.DisplayName() : "";
    public string? RoleIconUrl => Build.Role is { } r ? AssetUrls.PositionIcon(r) : null;
    public bool HasRole => Build.Role is not null;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record QuickLookBlock(string Name, bool IsCore, IReadOnlyList<CompareIcon> Items);

public sealed record QuickLookSkillCell(int Level, char? Slot)
{
    public bool HasSkill => Slot is not null;
    public string Text => Slot?.ToString() ?? "";
    public string Tip => Slot is { } s ? $"Level {Level}: {s}" : $"Level {Level}";
    public IBrush Accent => Palette.Hex(Slot switch { 'Q' => "#6FA8F5", 'W' => "#6FCB74", 'E' => "#E5C35C", 'R' => "#EE5A6F", _ => "#2A3546" });
}

/// <summary>One step of the max order ("Q › E › W"), with the ability for its icon and tooltip.</summary>
public sealed record QuickLookMaxStep(char Slot, AbilityDetailViewModel? Ability)
{
    public string Key => Slot.ToString();
    public string? IconUrl => Ability?.IconUrl;
    public bool HasAbility => Ability is not null;
    public IBrush Accent => Palette.Hex(Slot switch { 'Q' => "#6FA8F5", 'W' => "#6FCB74", 'E' => "#E5C35C", _ => "#EE5A6F" });
}
