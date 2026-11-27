using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>The main page: champion pool on the left, roster groups as columns. Drag champions between them.</summary>
public sealed partial class RostersViewModel : ObservableObject, IPage, IDropTarget, IKeyboardPage
{
    private readonly IShell _shell;
    private bool _loaded;

    public RostersViewModel(IShell shell)
    {
        _shell = shell;
        Pool = new ChampionBrowserViewModel(shell.Game);
        Pool.ExtraFilter = t => !HideAssigned || !t.IsInRoster;
        Pool.ExtraFiltersActive = () => HideAssigned;
        Pool.ResetExtraFilters = () => HideAssigned = false;
        _hideAssigned = shell.Preferences.Get("rosters.hideAssigned", false);
        _compact = shell.Preferences.Get("rosters.compact", false);
        _tipsDismissed = shell.Preferences.Get("rosters.tipsDismissed", false);
        Pool.Remember(shell.Preferences, "rosters.pool");
    }

    public string Title => "Rosters";
    public IShell Shell => _shell;
    public GameDataSnapshot Game => _shell.Game;
    public ChampionBrowserViewModel Pool { get; }
    public ObservableCollection<RosterGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private bool _hideAssigned;

    /// <summary>Width of each group column; the view divides the available width between the groups.</summary>
    [ObservableProperty]
    private double _columnWidth = 240;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTips))]
    private bool _isEmpty;

    /// <summary>Denser cards that fit more champions per column.</summary>
    [ObservableProperty]
    private bool _compact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTips))]
    private bool _tipsDismissed;

    /// <summary>First-run guidance, shown until dismissed or until the first champion is added.</summary>
    public bool ShowTips => !TipsDismissed && IsEmpty && !HasSelection;

    /// <summary>The roster card under the mouse (keyboard shortcuts 0–5 and Delete act on it).</summary>
    public RosterEntryViewModel? HoveredEntry { get; set; }

    // ── Right-click actions (same behaviour as dragging) ──────────────────────────────────────────

    public void AddToGroup(ChampionInfo champion, RosterGroupViewModel group) =>
        group.Drop(new ChampionDragData(champion, AssetUrls.ChampionSquare(Game.Version, champion.ImageFile), null), null, false, copy: false);

    public void MoveOrCopy(RosterEntryViewModel entry, RosterGroupViewModel group, bool copy) =>
        group.Drop(entry.DragData, null, false, copy);

    public async Task OnNavigatedToAsync()
    {
        _ = _shell.RefreshPatchReviewAsync();
        if (_loaded)
        {
            await RefreshCountsAsync();
            return;
        }
        _loaded = true;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        await LoadRostersAsync();
        var groups = await _shell.Repository.GetGroupsAsync();
        Groups.Clear();
        foreach (var g in groups) Groups.Add(new RosterGroupViewModel(this, g));
        _selectionAnchor = null;
        UpdateSelection();
        await RefreshCountsAsync();
    }

    private async Task RefreshCountsAsync()
    {
        var counts = await _shell.Repository.GetChampionCountsAsync();
        foreach (var g in Groups)
            foreach (var e in g.Entries)
            {
                counts.TryGetValue(e.ChampionId, out var c);
                e.BuildCount = c.Builds;
                e.MatchupCount = c.Matchups;
                e.InvalidatePreview(); // builds may have changed while away
            }
        foreach (var t in Pool.All)
        {
            counts.TryGetValue(t.Id, out var c);
            t.BuildCount = c.Builds;
            t.MatchupCount = c.Matchups;
        }
        UpdateMembership();
    }

    /// <summary>Recomputes which champions are in which groups (pool badges, summary).</summary>
    internal void UpdateMembership()
    {
        var membership = Groups.SelectMany(g => g.Entries).GroupBy(e => e.ChampionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var t in Pool.All) t.GroupCount = membership.TryGetValue(t.Id, out var n) ? n : 0;
        var champions = membership.Count;
        Summary = $"{champions} champion{(champions == 1 ? "" : "s")} across {Groups.Count} group{(Groups.Count == 1 ? "" : "s")}";
        if (Rosters.FirstOrDefault(r => r.IsActive) is { } active && active.ChampionCount != champions)
            Rosters[Rosters.IndexOf(active)] = active with { ChampionCount = champions };
        IsEmpty = champions == 0;
        foreach (var g in Groups) g.NotifyCount();
        if (HideAssigned) Pool.Refilter();
    }

    internal void OnRosterChanged()
    {
        UpdateMembership();
        _shell.MarkDirty();
    }

    [RelayCommand]
    private void OpenChampion(object? item)
    {
        switch (item)
        {
            case ChampionTileViewModel t:
                _shell.OpenChampion(t.Id, null, Pool.Sequence("Champion pool"));
                break;
            case RosterEntryViewModel e:
                e.OpenCommand.Execute(null);
                break;
        }
    }

    [RelayCommand]
    private async Task AddGroupAsync()
    {
        var used = Groups.Select(g => g.ColorHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = Palette.GroupColors.FirstOrDefault(c => !used.Contains(c)) ?? Palette.GroupColors[Groups.Count % Palette.GroupColors.Count];
        var name = "New group";
        for (var n = 2; Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"New group {n}";
        var group = await _shell.Repository.AddGroupAsync(name, null, color);
        var vm = new RosterGroupViewModel(this, group);
        Groups.Add(vm);
        vm.BeginRenameCommand.Execute(null);
        UpdateMembership();
    }

    internal async Task MoveGroupAsync(RosterGroupViewModel group, int delta)
    {
        var index = Groups.IndexOf(group);
        var target = Math.Clamp(index + delta, 0, Groups.Count - 1);
        if (index < 0 || target == index) return;
        Groups.Move(index, target);
        await _shell.Repository.ReorderGroupsAsync(Groups.Select(g => g.Id).ToList());
    }

    internal async Task DeleteGroupAsync(RosterGroupViewModel group)
    {
        var count = group.Entries.Count;
        var ok = await _shell.ConfirmAsync($"Delete \"{group.Name}\"?",
            count == 0
                ? "The group is empty."
                : $"The group's {count} champion placement{(count == 1 ? "" : "s")} will be removed. Builds, matchups and notes are kept.",
            "Delete group");
        if (!ok) return;
        var index = Groups.IndexOf(group);
        var snapshot = (group.Name, group.Role, group.ColorHex,
            Entries: group.Entries.Select(e => (e.ChampionId, e.Proficiency, e.Priority)).ToList());
        await _shell.Repository.DeleteGroupAsync(group.Id);
        Groups.Remove(group);
        OnRosterChanged();
        _shell.ShowToast($"Deleted \"{group.Name}\"", ToastKind.Info, "Undo", async () =>
        {
            var restored = await _shell.Repository.AddGroupAsync(snapshot.Name, snapshot.Role, snapshot.ColorHex);
            foreach (var (championId, proficiency, priority) in snapshot.Entries)
                await _shell.Repository.AddEntryAsync(restored.Id, championId, proficiency, priority);
            var ids = Groups.Select(g => g.Id).ToList();
            ids.Insert(Math.Clamp(index, 0, ids.Count), restored.Id);
            await _shell.Repository.ReorderGroupsAsync(ids);
            await ReloadAsync();
            _shell.ShowToast($"Restored \"{snapshot.Name}\"", ToastKind.Success);
        });
    }

    // ── The pool is a drop target: dropping a roster card here removes it from its group ──────────

    public bool CanAccept(IDragPayload payload) => payload is ChampionDragData { Origin: RosterEntryViewModel };

    public string? Describe(IDragPayload payload, bool copy) => payload switch
    {
        ChampionDragData { Origin: RosterEntryViewModel { IsSelected: true } } when SelectionCount > 1 => $"Remove {SelectionCount} from the roster",
        ChampionDragData { Origin: RosterEntryViewModel e } => $"Remove from {e.Group.Name}",
        _ => null,
    };

    public void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy)
    {
        if (payload is ChampionDragData { Origin: RosterEntryViewModel { IsSelected: true } } && SelectionCount > 1) _ = RemoveSelectionAsync();
        else if (payload is ChampionDragData { Origin: RosterEntryViewModel e }) _ = e.Group.RemoveAsync(e);
    }
}
