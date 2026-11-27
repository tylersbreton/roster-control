using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// The Random pick page: roll a champion from one or more roster groups (from any roster), or from every champion in the
/// game. Equal chances by default; optionally weighted by priority and stars. A reel of portraits spins and slows down
/// before landing on the pick (skipped with Reduce motion).
/// </summary>
public sealed partial class RandomPickViewModel : ObservableObject, IPage, IKeyboardPage
{
    internal const string PrefsKey = "random.pick";
    /// <summary>Portraits on the reel; the pick lands in the middle one.</summary>
    public const int ReelLength = 9;
    public const int ReelCenter = ReelLength / 2;
    private const int MaxHistory = 12;

    private readonly IShell _shell;
    private readonly Dictionary<string, ChampionInfo> _champions;
    private IReadOnlyList<PickCandidate> _candidates = [];
    private bool _restoring;
    private int _rollId;

    public RandomPickViewModel(IShell shell)
    {
        _shell = shell;
        _champions = shell.Game.Champions.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        Reel = Enumerable.Range(0, ReelLength).Select(i => new ReelSlot(i == ReelCenter)).ToList();
        var prefs = shell.Preferences.Get(PrefsKey, new RandomPickPrefs());
        _restoring = true;
        _allChampions = prefs.AllChampions;
        _weighted = prefs.Weighted;
        _restoring = false;
        _savedGroupIds = prefs.GroupIds is { } ids ? ids.ToHashSet() : null;
        FillReel(_champions.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(ReelLength).Select(c => c.Id).ToList());
    }

    /// <summary>Group ids ticked last time; null = never chosen (then the active roster's groups are ticked).</summary>
    private HashSet<int>? _savedGroupIds;

    public string Title => "Random pick";

    /// <summary>Used by tests to make the rolls repeatable.</summary>
    internal Random Random { get; set; } = Random.Shared;

    /// <summary>Delay between reel steps (scaled to 0 by tests).</summary>
    internal double SpeedScale { get; set; } = 1;

    /// <summary>Raised for each reel step with its duration, so the view can slide the reel by one portrait.</summary>
    public event EventHandler<TimeSpan>? ReelStepped;

    /// <summary>Raised when the reel stops on the pick.</summary>
    public event EventHandler? Landed;

    public IReadOnlyList<ReelSlot> Reel { get; }

    public ObservableCollection<RosterPickSection> Rosters { get; } = [];
    public ObservableCollection<PickHistoryItem> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupMode))]
    private bool _allChampions;

    public bool IsGroupMode => !AllChampions;

    /// <summary>Weight by priority and stars (groups only).</summary>
    [ObservableProperty]
    private bool _weighted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRoll), nameof(RollText))]
    private bool _isRolling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ShowPlaceholder))]
    private PickResultViewModel? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRoll))]
    private int _candidateCount;

    [ObservableProperty]
    private string _candidateText = "";

    [ObservableProperty]
    private string _emptyText = "";

    public bool HasResult => Result is not null && !IsRolling;
    public bool ShowPlaceholder => Result is null && !IsRolling;
    public bool CanRoll => CandidateCount > 0 && !IsRolling;
    public bool HasHistory => History.Count > 0;
    public bool HasNoGroups => Rosters.Count == 0;
    public bool ShowRosterNames => Rosters.Count > 1;
    public bool ReduceMotion => _shell.ReduceMotion;
    public string RollText => IsRolling ? "Rolling…" : Result is null ? "Roll" : "Roll again";

    partial void OnAllChampionsChanged(bool value) => Update();
    partial void OnWeightedChanged(bool value) => Update();
    partial void OnIsRollingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    partial void OnCandidateCountChanged(int value) => RollCommand.NotifyCanExecuteChanged();

    public async Task OnNavigatedToAsync()
    {
        var rosters = await _shell.Repository.GetRostersAsync();
        var activeId = await _shell.Repository.GetActiveRosterIdAsync();
        var ticked = Rosters.SelectMany(r => r.Groups).Where(g => g.IsChecked).Select(g => g.Group.Id).ToHashSet();
        var first = Rosters.Count == 0;
        var sections = new List<RosterPickSection>();
        // The active roster first, then the others in their usual order.
        foreach (var roster in rosters.OrderBy(r => r.Id != activeId).ThenBy(r => r.SortOrder))
        {
            var groups = await _shell.Repository.GetGroupsAsync(roster.Id);
            if (groups.Count == 0) continue;
            var section = new RosterPickSection(roster, roster.Id == activeId, this);
            foreach (var g in groups.OrderBy(g => g.SortOrder))
            {
                var isChecked = first
                    ? _savedGroupIds?.Contains(g.Id) ?? roster.Id == activeId
                    : ticked.Contains(g.Id);
                section.Groups.Add(new PickGroupOption(g, g.Entries.Count(e => _champions.ContainsKey(e.ChampionId)), isChecked, this));
            }
            sections.Add(section);
        }
        _restoring = true;
        Rosters.Clear();
        foreach (var s in sections) Rosters.Add(s);
        _restoring = false;
        OnPropertyChanged(nameof(HasNoGroups));
        OnPropertyChanged(nameof(ShowRosterNames));
        Update();
    }

    public bool HandleKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers, bool typing)
    {
        if (typing || modifiers != Avalonia.Input.KeyModifiers.None || key != Avalonia.Input.Key.R) return false;
        if (CanRoll) _ = RollAsync();
        return true;
    }

    [RelayCommand]
    private void UseGroups() => AllChampions = false;

    [RelayCommand]
    private void UseAllChampions() => AllChampions = true;

    internal void GroupToggled()
    {
        if (!_restoring) Update();
    }

    internal void SetAll(RosterPickSection section, bool isChecked)
    {
        _restoring = true;
        foreach (var g in section.Groups) g.IsChecked = isChecked;
        _restoring = false;
        Update();
    }

    /// <summary>Recomputes the champions that can come up and saves the choices.</summary>
    private void Update()
    {
        if (_restoring) return;
        var known = _champions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tickedGroups = Rosters.SelectMany(r => r.Groups).Where(g => g.IsChecked).ToList();
        _candidates = AllChampions
            ? RandomPicker.FromAll(_champions.Keys)
            : RandomPicker.FromGroups(tickedGroups.Select(g => g.Group), Weighted, known);
        CandidateCount = _candidates.Count;
        CandidateText = CandidateCount == 1 ? "Picking from 1 champion" : $"Picking from {CandidateCount} champions";
        EmptyText = AllChampions ? ""
            : HasNoGroups ? "You have no roster groups yet. Add some on the Rosters page, or pick from all champions."
            : tickedGroups.Count == 0 ? "Tick one or more groups, or pick from all champions."
            : CandidateCount == 0 ? "The ticked groups have no champions yet."
            : "";
        if (Rosters.Count > 0) _savedGroupIds = Rosters.SelectMany(r => r.Groups).Where(g => g.IsChecked).Select(g => g.Group.Id).ToHashSet();
        _shell.Preferences.Set(PrefsKey, new RandomPickPrefs
        {
            AllChampions = AllChampions,
            Weighted = Weighted,
            GroupIds = _savedGroupIds?.Order().ToList(),
        });
        // Load the portraits now so the reel doesn't show blanks while it spins.
        _ = AssetImage.Images?.WarmAsync(_candidates.Select(c => IconUrl(c.ChampionId)).Where(u => u is not null).Cast<string>(), ReelSlot.DecodeWidth, TimeSpan.FromSeconds(20));
    }

    [RelayCommand(CanExecute = nameof(CanRoll))]
    private async Task RollAsync()
    {
        var candidates = _candidates;
        if (RandomPicker.Pick(candidates, Random) is not { } pick) return;
        var id = ++_rollId;
        IsRolling = true;
        RollCommand.NotifyCanExecuteChanged();
        try
        {
            var motion = !_shell.ReduceMotion && SpeedScale > 0;
            const int steps = 24;
            var sequence = ReelSequence(candidates.Select(c => c.ChampionId).ToList(), pick.ChampionId, steps - 1 + ReelCenter, steps - 1 + ReelLength, Random);
            if (motion)
            {
                for (var step = 0; step < steps; step++)
                {
                    FillReel(sequence.GetRange(step, ReelLength));
                    // Fast at first, slowing down towards the end (ease-out).
                    var t = (double)step / (steps - 1);
                    var duration = TimeSpan.FromMilliseconds((34 + 250 * Math.Pow(t, 2.6)) * SpeedScale);
                    ReelStepped?.Invoke(this, duration);
                    await Task.Delay(duration);
                    if (id != _rollId) return;
                }
            }
            else FillReel(sequence.GetRange(steps - 1, ReelLength));

            Result = BuildResult(pick, candidates);
            AddHistory(pick.ChampionId);
        }
        finally
        {
            if (id == _rollId)
            {
                IsRolling = false;
                RollCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(RollText));
            }
        }
        Landed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The champions the reel passes: shuffled rounds of the selection (so neighbours differ when there's more than one
    /// champion), with the pick at <paramref name="pickIndex"/>.
    /// </summary>
    internal static List<string> ReelSequence(IReadOnlyList<string> ids, string pick, int pickIndex, int length, Random random)
    {
        var sequence = new List<string>(length);
        while (sequence.Count < length)
        {
            var round = ids.ToArray();
            random.Shuffle(round);
            // Don't start a round with the champion that ended the last one.
            if (round.Length > 1 && sequence.Count > 0 && round[0] == sequence[^1]) (round[0], round[^1]) = (round[^1], round[0]);
            sequence.AddRange(round);
        }
        sequence.RemoveRange(length, sequence.Count - length);
        // Put the pick in place; move the champion it replaces to where the pick was (if the pick was nearby).
        var at = sequence.IndexOf(pick, Math.Max(0, pickIndex - ids.Count + 1));
        if (at >= 0 && at < length && Math.Abs(at - pickIndex) < ids.Count) (sequence[at], sequence[pickIndex]) = (sequence[pickIndex], pick);
        else sequence[pickIndex] = pick;
        return sequence;
    }

    private void FillReel(IReadOnlyList<string> championIds)
    {
        for (var i = 0; i < Reel.Count && i < championIds.Count; i++)
        {
            var champion = _champions.GetValueOrDefault(championIds[i]);
            Reel[i].Set(champion?.Name ?? championIds[i], IconUrl(championIds[i]));
        }
    }

    private string? IconUrl(string championId) =>
        _champions.TryGetValue(championId, out var c) ? AssetUrls.ChampionSquare(_shell.Game.Version, c.ImageFile) : null;

    private PickResultViewModel BuildResult(PickCandidate pick, IReadOnlyList<PickCandidate> candidates)
    {
        var champion = _champions[pick.ChampionId];
        var placements = pick.Placements
            .Select(p => new PickPlacementView(p.Group.Name, p.Group.Role is { } r ? AssetUrls.PositionIcon(r) : null, p.Entry.Priority, p.Entry.Proficiency))
            .ToList();
        var chance = RandomPicker.Chance(pick, candidates);
        var chanceText = chance >= 0.995 ? "the only champion in the selection"
            : $"{(chance >= 0.1 ? chance.ToString("P0") : chance.ToString("P1"))} chance{(Weighted && IsGroupMode ? " (weighted)" : "")}";
        // The role of the first ticked group it's in: the champion page opens that role's default build.
        var role = pick.Placements.Select(p => p.Group.Role).FirstOrDefault(r => r is not null);
        return new PickResultViewModel(champion, AssetUrls.ChampionCentered(champion.Id), AssetUrls.ChampionSplash(champion.Id),
            placements, chanceText, role, this);
    }

    private void AddHistory(string championId)
    {
        History.Insert(0, new PickHistoryItem(championId, _champions[championId].Name, IconUrl(championId), this));
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
        OnPropertyChanged(nameof(HasHistory));
    }

    internal void Open(string championId, Position? role) =>
        _shell.OpenChampion(championId, null, role is null ? null : new ChampionSequence([championId], "Random pick", role));

    internal void QuickLook(string championId, Position? role) => _shell.OpenQuickLook(championId, role);

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        OnPropertyChanged(nameof(HasHistory));
    }

    public sealed class RandomPickPrefs
    {
        public bool AllChampions { get; set; }
        public bool Weighted { get; set; }
        /// <summary>Ticked group ids; null = never chosen.</summary>
        public List<int>? GroupIds { get; set; }
    }
}

/// <summary>One portrait on the reel.</summary>
public sealed partial class ReelSlot(bool isCenter) : ObservableObject
{
    public const int DecodeWidth = 96;

    public bool IsCenter { get; } = isCenter;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string? _iconUrl;

    internal void Set(string name, string? iconUrl)
    {
        Name = name;
        IconUrl = iconUrl;
    }
}

/// <summary>A roster's groups in the "Pick from" list.</summary>
public sealed partial class RosterPickSection(Roster roster, bool isActive, RandomPickViewModel owner) : ObservableObject
{
    public Roster Roster { get; } = roster;
    public string Name => Roster.Name + (isActive ? " (active)" : "");
    public ObservableCollection<PickGroupOption> Groups { get; } = [];

    [RelayCommand]
    private void SelectAll() => owner.SetAll(this, true);

    [RelayCommand]
    private void SelectNone() => owner.SetAll(this, false);
}

/// <summary>A group checkbox.</summary>
public sealed partial class PickGroupOption(RosterGroup group, int count, bool isChecked, RandomPickViewModel owner) : ObservableObject
{
    public RosterGroup Group { get; } = group;
    public string Name => Group.Name;
    public string CountText => count.ToString();
    public string? RoleIconUrl => Group.Role is { } r ? AssetUrls.PositionIcon(r) : null;
    public bool HasRoleIcon => Group.Role is not null;
    public IBrush Accent => Palette.Hex(Group.Color ?? Palette.RoleHex(Group.Role));
    public string Tip => $"{Group.Name}: {count} champion{(count == 1 ? "" : "s")}";

    [ObservableProperty]
    private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => owner.GroupToggled();
}

public sealed record PickPlacementView(string GroupName, string? RoleIconUrl, ChampionPriority Priority, int Proficiency)
{
    public bool HasRoleIcon => RoleIconUrl is not null;
    public string PriorityLabel => Priority.ToString().ToUpperInvariant();
    public IBrush PriorityBrush => Palette.Hex(Palette.PriorityHex(Priority));
    public IBrush PrioritySoftBrush => Palette.Soft(Palette.PriorityHex(Priority), 0x2A);
}

/// <summary>The champion the reel landed on.</summary>
public sealed partial class PickResultViewModel(ChampionInfo champion, string splashUrl, string splashFallbackUrl,
    IReadOnlyList<PickPlacementView> placements, string chanceText, Position? role, RandomPickViewModel owner) : ObservableObject
{
    public ChampionInfo Champion { get; } = champion;
    public string Name => Champion.Name;
    public string ChampionTitle => Champion.Title;
    public string ClassText => string.Join(" · ", Champion.Tags) + (Champion.Stats is { } s ? (s.IsRanged ? " · Ranged" : " · Melee") : "");
    public string SplashUrl { get; } = splashUrl;
    public string SplashFallbackUrl { get; } = splashFallbackUrl;
    public IReadOnlyList<PickPlacementView> Placements { get; } = placements;
    public bool HasPlacements => Placements.Count > 0;
    public string ChanceText { get; } = chanceText;

    [RelayCommand]
    private void Open() => owner.Open(Champion.Id, role);

    [RelayCommand]
    private void QuickLook() => owner.QuickLook(Champion.Id, role);
}

/// <summary>An earlier pick (this session only).</summary>
public sealed partial class PickHistoryItem(string championId, string name, string? iconUrl, RandomPickViewModel owner)
{
    public string ChampionId { get; } = championId;
    public string Name { get; } = name;
    public string? IconUrl { get; } = iconUrl;
    public string Tip => $"{Name}: open the champion page";

    [RelayCommand]
    private void Open() => owner.Open(ChampionId, null);
}
