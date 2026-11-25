using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Pick the enemy champions; see which of your champions are good (or bad) into them.</summary>
public sealed partial class CounterPicksViewModel : ObservableObject, IPage, IDropTarget
{
    private readonly IShell _shell;
    private IReadOnlyList<RosterGroup> _groups = [];
    private IReadOnlyList<Matchup> _matchups = [];

    public CounterPicksViewModel(IShell shell)
    {
        _shell = shell;
        Picker = new ChampionBrowserViewModel(shell.Game);
        for (var i = 0; i < 5; i++) Enemies.Add(new EnemySlotViewModel(this, i, shell.Game.Version));
    }

    public string Title => "Counter Picks";
    public ChampionBrowserViewModel Picker { get; }
    public ObservableCollection<EnemySlotViewModel> Enemies { get; } = [];
    public ObservableCollection<ScopeOption> Scopes { get; } = [];
    public ObservableCollection<CounterResultViewModel> Strong { get; } = [];
    public ObservableCollection<CounterResultViewModel> Risky { get; } = [];
    public ObservableCollection<CounterResultViewModel> Neutral { get; } = [];

    [ObservableProperty]
    private ScopeOption? _selectedScope;

    [ObservableProperty]
    private bool _includeOutsideRoster = true;

    [ObservableProperty]
    private bool _showNeutral;

    [ObservableProperty]
    private bool _hasEnemies;

    [ObservableProperty]
    private string _resultSummary = "";

    [ObservableProperty]
    private bool _hasMatchupData;

    public bool HasStrong => Strong.Count > 0;
    public bool HasRisky => Risky.Count > 0;
    public bool HasNeutral => Neutral.Count > 0;

    partial void OnSelectedScopeChanged(ScopeOption? value) => Recompute();
    partial void OnIncludeOutsideRosterChanged(bool value) => Recompute();

    public async Task OnNavigatedToAsync()
    {
        _groups = await _shell.Repository.GetGroupsAsync();
        _matchups = await _shell.Repository.GetMatchupsAsync();
        HasMatchupData = _matchups.Count > 0;
        var previous = SelectedScope?.GroupId;
        Scopes.Clear();
        Scopes.Add(new ScopeOption(null, "All my groups", Palette.Gold, null));
        foreach (var g in _groups) Scopes.Add(new ScopeOption(g.Id, g.Name, g.Color ?? Palette.RoleHex(g.Role), g.Role));
        SelectedScope = Scopes.FirstOrDefault(s => s.GroupId == previous) ?? Scopes[0];
        Recompute();
    }

    [RelayCommand]
    private void AddEnemy(ChampionTileViewModel tile) => SetEnemy(tile.Champion, null);

    [RelayCommand]
    private void ClearEnemies()
    {
        foreach (var e in Enemies) e.Champion = null;
        Recompute();
    }

    [RelayCommand]
    private void OpenChampion(CounterResultViewModel result) => _shell.OpenChampion(result.Champion.Id, ChampionTab.Matchups);

    internal void SetEnemy(ChampionInfo champion, EnemySlotViewModel? slot)
    {
        var existing = Enemies.FirstOrDefault(e => e.Champion?.Id == champion.Id);
        if (existing is not null && slot is null) return;
        if (existing is not null) existing.Champion = null;
        slot ??= Enemies.FirstOrDefault(e => e.Champion is null);
        if (slot is null)
        {
            _shell.ShowToast("All five enemy slots are filled — remove one first.", ToastKind.Warning);
            return;
        }
        slot.Champion = champion;
        Recompute();
    }

    /// <summary>Roster placements in the selected scope, each with its group's role (role-specific matchups apply).</summary>
    private List<ScopedEntry> ScopedEntries()
    {
        var scopeGroups = SelectedScope?.GroupId is { } gid ? _groups.Where(g => g.Id == gid) : _groups;
        return scopeGroups.SelectMany(g => g.Entries.Select(e => new ScopedEntry(e, g.Role))).ToList();
    }

    internal void Recompute()
    {
        var enemyIds = Enemies.Where(e => e.Champion is not null).Select(e => e.Champion!.Id).ToList();
        HasEnemies = enemyIds.Count > 0;
        foreach (var t in Picker.All) t.IsSelected = enemyIds.Contains(t.Id);
        Strong.Clear();
        Risky.Clear();
        Neutral.Clear();
        var entries = ScopedEntries();
        var groups = _groups.ToDictionary(g => g.Id);
        if (enemyIds.Count > 0)
        {
            var scopeRole = SelectedScope?.GroupId is { } gid && groups.TryGetValue(gid, out var sg) ? sg.Role : null;
            var ranked = CounterPickScorer.Rank(enemyIds, _matchups, entries, IncludeOutsideRoster, scopeRole);
            foreach (var c in ranked)
            {
                if (_shell.Game.Champion(c.ChampionId) is not { } champ) continue;
                var group = c.Entry is { } e && groups.TryGetValue(e.GroupId, out var g) ? g : null;
                var vm = new CounterResultViewModel(_shell.Game, champ, c, group?.Name);
                if (c.Score > 0) Strong.Add(vm);
                else if (c.Score < 0 || c.Unfavorable > 0) Risky.Add(vm);
                else if (c.InRoster) Neutral.Add(vm);
            }
        }
        ResultSummary = !HasEnemies ? "" :
            $"{Strong.Count} strong pick{(Strong.Count == 1 ? "" : "s")} · {Risky.Count} risky · {Neutral.Count} without matchup notes";
        OnPropertyChanged(nameof(HasStrong));
        OnPropertyChanged(nameof(HasRisky));
        OnPropertyChanged(nameof(HasNeutral));

        // Ban suggestions don't depend on the enemy picks (bans come first), only on the scope.
        Bans.Clear();
        foreach (var s in BanSuggester.Suggest(_matchups, entries))
            if (_shell.Game.Champion(s.OpponentId) is { } champ)
                Bans.Add(new BanSuggestionViewModel(_shell.Game, champ, s, groups, enemyIds.Contains(champ.Id, StringComparer.OrdinalIgnoreCase)));
        OnPropertyChanged(nameof(HasBans));
    }

    public ObservableCollection<BanSuggestionViewModel> Bans { get; } = [];
    public bool HasBans => Bans.Count > 0;

    [RelayCommand]
    private void OpenBan(BanSuggestionViewModel ban) => _shell.OpenChampion(ban.Champion.Id, ChampionTab.Matchups);

    public bool CanAccept(IDragPayload payload) => payload is ChampionDragData;
    public string? Describe(IDragPayload payload, bool copy) => payload is ChampionDragData d ? $"Add {d.Champion.Name} to enemy team" : null;

    public void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy)
    {
        if (payload is ChampionDragData d) SetEnemy(d.Champion, relativeTo as EnemySlotViewModel);
    }
}

public sealed partial class EnemySlotViewModel(CounterPicksViewModel owner, int index, string version) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconUrl), nameof(Name), nameof(HasChampion))]
    private ChampionInfo? _champion;

    public string? IconUrl => Champion is { } c ? AssetUrls.ChampionSquare(version, c.ImageFile) : null;
    public string Name => Champion?.Name ?? $"Enemy {Index + 1}";
    public bool HasChampion => Champion is not null;

    [RelayCommand]
    private void Clear()
    {
        Champion = null;
        owner.Recompute();
    }
}

public sealed record ScopeOption(int? GroupId, string Label, string ColorHex, Position? Role)
{
    public IBrush Accent => Palette.Hex(ColorHex);
    public string? RoleIconUrl => Role is { } r ? AssetUrls.PositionIcon(r) : null;
    public bool HasRoleIcon => Role is not null;
    public override string ToString() => Label;
}

public sealed class CounterResultViewModel
{
    public CounterResultViewModel(GameDataSnapshot game, ChampionInfo champion, CounterCandidate candidate, string? groupName)
    {
        Champion = champion;
        Candidate = candidate;
        IconUrl = AssetUrls.ChampionSquare(game.Version, champion.ImageFile);
        GroupName = groupName;
        Reasons = candidate.Reasons
            .Select(r => game.Champion(r.EnemyId) is { } enemy ? new ReasonViewModel(r, enemy, AssetUrls.ChampionSquare(game.Version, enemy.ImageFile)) : null)
            .Where(r => r is not null).Cast<ReasonViewModel>()
            .OrderByDescending(r => r.Reason.Favorable).ToList();
    }

    public ChampionInfo Champion { get; }
    public CounterCandidate Candidate { get; }
    public string Name => Champion.Name;
    public string IconUrl { get; }
    public string? GroupName { get; }
    public bool InRoster => Candidate.InRoster;
    public string ScoreText => Candidate.Score > 0 ? $"+{Candidate.Score}" : Candidate.Score.ToString();
    public IBrush ScoreBrush => Palette.Hex(Candidate.Score > 0 ? Palette.Good : Candidate.Score < 0 ? Palette.Bad : Palette.Muted);
    public IBrush ScoreSoft => Palette.Soft(Candidate.Score > 0 ? Palette.Good : Candidate.Score < 0 ? Palette.Bad : Palette.Muted, 0x24);
    public int Proficiency => Candidate.Entry?.Proficiency ?? 0;
    public bool HasEntry => Candidate.Entry is not null;
    public string PriorityLabel => Candidate.Entry?.Priority.ToString().ToUpperInvariant() ?? "NOT IN ROSTER";
    public IBrush PriorityBrush => Candidate.Entry is { } e ? Palette.Hex(Palette.PriorityHex(e.Priority)) : Palette.Hex(Palette.Muted);
    public string PlacementText => GroupName is null ? "Not in your roster" : $"{GroupName}";
    public string? RoleIconUrl => Candidate.Role is { } r ? AssetUrls.PositionIcon(r) : null;
    public bool HasRole => Candidate.Role is not null;
    public IReadOnlyList<ReasonViewModel> Reasons { get; }
    public bool HasReasons => Reasons.Count > 0;
}

public sealed class ReasonViewModel(CounterReason reason, ChampionInfo enemy, string enemyIconUrl)
{
    public CounterReason Reason { get; } = reason;
    public string EnemyIconUrl { get; } = enemyIconUrl;
    public string Text => (Reason.Favorable ? "Good vs " : "Weak vs ") + enemy.Name;
    public string Note => Reason.Note;
    public bool HasNote => Reason.Note.Length > 0;
    public string Tooltip => Text + (Reason.Role is { } r ? $" — {r.DisplayName()} matchup" : "")
                             + (Reason.Inferred ? " (from that champion's matchup notes)" : "") + (HasNote ? "\n" + Note : "");
    public IBrush Accent => Palette.Hex(Reason.Favorable ? Palette.Good : Palette.Bad);
    public IBrush AccentSoft => Palette.Soft(Reason.Favorable ? Palette.Good : Palette.Bad, 0x22);
    public bool Inferred => Reason.Inferred;
}

/// <summary>An opponent worth banning: it counters several of your (important) champions.</summary>
public sealed class BanSuggestionViewModel
{
    public BanSuggestionViewModel(GameDataSnapshot game, ChampionInfo champion, BanSuggestion suggestion, IReadOnlyDictionary<int, RosterGroup> groups, bool alreadyPicked)
    {
        Champion = champion;
        Suggestion = suggestion;
        IconUrl = AssetUrls.ChampionSquare(game.Version, champion.ImageFile);
        AlreadyPicked = alreadyPicked;
        Threats = suggestion.Threatened.Select(t =>
        {
            var c = game.Champion(t.Scoped.Entry.ChampionId);
            var group = groups.TryGetValue(t.Scoped.Entry.GroupId, out var g) ? g.Name : "";
            return new BanThreatViewModel(c?.Name ?? t.Scoped.Entry.ChampionId, c is null ? null : AssetUrls.ChampionSquare(game.Version, c.ImageFile),
                group, t.Scoped.Entry.Priority, t.Note, t.Inferred);
        }).ToList();
    }

    public ChampionInfo Champion { get; }
    public BanSuggestion Suggestion { get; }
    public string Name => Champion.Name;
    public string IconUrl { get; }
    public bool AlreadyPicked { get; }
    public IReadOnlyList<BanThreatViewModel> Threats { get; }
    public IReadOnlyList<BanThreatViewModel> TopThreats => Threats.Take(4).ToList();
    public string CountText => Threats.Count == 1 ? "Counters 1 of yours" : $"Counters {Threats.Count} of yours";
    public string AnswersText => Suggestion.Answers switch
    {
        0 => "No answers recorded",
        1 => "1 of your champions beats it",
        var n => $"{n} of your champions beat it",
    };
}

public sealed record BanThreatViewModel(string Name, string? IconUrl, string GroupName, ChampionPriority Priority, string Note, bool Inferred)
{
    public string Line => $"{Name} ({GroupName}, {Priority})" + (Note.Length > 0 ? $": {Note}" : "") + (Inferred ? " — from its matchup notes" : "");
    public IBrush PriorityBrush => Palette.Hex(Palette.PriorityHex(Priority));
}
