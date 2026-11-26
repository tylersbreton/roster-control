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

    // ── Rosters (Solo/Duo, Flex, ARAM…) ────────────────────────────────────────────────────────────

    public ObservableCollection<RosterChoice> Rosters { get; } = [];
    public int ActiveRosterId { get; private set; }

    [ObservableProperty]
    private string _rosterName = "";

    [ObservableProperty]
    private string _newRosterName = "";

    [ObservableProperty]
    private string _renameText = "";

    public bool CanDeleteRoster => Rosters.Count > 1;
    public string RosterHeading => RosterName.ToUpperInvariant();

    partial void OnRosterNameChanged(string value) => OnPropertyChanged(nameof(RosterHeading));

    private async Task LoadRostersAsync()
    {
        var repo = _shell.Repository;
        var rosters = await repo.GetRostersAsync();
        ActiveRosterId = await repo.GetActiveRosterIdAsync();
        Rosters.Clear();
        foreach (var r in rosters)
        {
            var champions = (await repo.GetGroupsAsync(r.Id)).SelectMany(g => g.Entries).Select(e => e.ChampionId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Rosters.Add(new RosterChoice(r.Id, r.Name, champions, r.Id == ActiveRosterId));
        }
        RosterName = Rosters.FirstOrDefault(r => r.IsActive)?.Name ?? "";
        RenameText = RosterName;
        OnPropertyChanged(nameof(CanDeleteRoster));
    }

    [RelayCommand]
    private Task SwitchRosterAsync(RosterChoice roster) => roster.IsActive ? Task.CompletedTask : _shell.SwitchRosterAsync(roster.Id);

    /// <param name="template">"Roles", "Empty" or "Copy".</param>
    [RelayCommand]
    private async Task NewRosterAsync(string template)
    {
        var kind = Enum.TryParse<RosterTemplate>(template, out var t) ? t : RosterTemplate.Roles;
        var name = (NewRosterName ?? "").Trim();
        if (name.Length == 0) name = kind == RosterTemplate.Copy ? $"{RosterName} (copy)" : "New roster";
        for (var n = 2; Rosters.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{name.TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9')} {n}";
        var roster = await _shell.Repository.AddRosterAsync(name, kind, ActiveRosterId);
        NewRosterName = "";
        await _shell.SwitchRosterAsync(roster.Id);
        _shell.ShowToast($"Created roster \"{roster.Name}\"", ToastKind.Success);
    }

    [RelayCommand]
    private async Task RenameRosterAsync()
    {
        var name = (RenameText ?? "").Trim();
        if (name.Length == 0 || name == RosterName) return;
        await _shell.Repository.RenameRosterAsync(ActiveRosterId, name);
        await LoadRostersAsync();
    }

    [RelayCommand]
    private async Task DeleteRosterAsync()
    {
        if (!CanDeleteRoster) return;
        var count = Groups.Sum(g => g.Entries.Count);
        var ok = await _shell.ConfirmAsync($"Delete the roster \"{RosterName}\"?",
            $"Its {Groups.Count} group{(Groups.Count == 1 ? "" : "s")} and {count} placement{(count == 1 ? "" : "s")} are removed. Builds, matchups and notes are shared by all rosters and are kept.",
            "Delete roster");
        if (!ok) return;
        var name = RosterName;
        await _shell.Repository.DeleteRosterAsync(ActiveRosterId);
        var next = Rosters.First(r => r.Id != ActiveRosterId);
        await _shell.SwitchRosterAsync(next.Id);
        _shell.ShowToast($"Deleted roster \"{name}\"");
    }

    // ── Multi-select (Ctrl/Shift-click) ────────────────────────────────────────────────────────────

    private RosterEntryViewModel? _selectionAnchor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText), nameof(ShowTips))]
    private int _selectionCount;

    public bool HasSelection => SelectionCount > 0;
    public string SelectionText => $"{SelectionCount} selected";

    /// <summary>All cards in board order (groups left to right, cards top to bottom).</summary>
    public IEnumerable<RosterEntryViewModel> AllEntries => Groups.SelectMany(g => g.Entries);
    public IReadOnlyList<RosterEntryViewModel> SelectedEntries => AllEntries.Where(e => e.IsSelected).ToList();

    internal void UpdateSelection() => SelectionCount = AllEntries.Count(e => e.IsSelected);

    /// <summary>Click opens the champion; Ctrl+click toggles a card; Shift+click selects a range.</summary>
    [RelayCommand]
    private void CardClick(RosterEntryViewModel entry)
    {
        var mods = Dnd.LastClickModifiers;
        if (mods.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            var all = AllEntries.ToList();
            var from = _selectionAnchor is not null ? all.IndexOf(_selectionAnchor) : -1;
            var to = all.IndexOf(entry);
            if (from < 0) from = to;
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) all[i].IsSelected = true;
            UpdateSelection();
            return;
        }
        if (mods.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            entry.IsSelected = !entry.IsSelected;
            _selectionAnchor = entry;
            UpdateSelection();
            return;
        }
        ClearSelection();
        entry.OpenCommand.Execute(null);
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var e in AllEntries) e.IsSelected = false;
        _selectionAnchor = null;
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var e in AllEntries) e.IsSelected = true;
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectGroup(RosterGroupViewModel group)
    {
        foreach (var e in group.Entries) e.IsSelected = true;
        UpdateSelection();
    }

    [RelayCommand]
    private Task MoveSelectionAsync(RosterGroupViewModel target) => TransferSelectionAsync(target, copy: false);

    [RelayCommand]
    private Task CopySelectionAsync(RosterGroupViewModel target) => TransferSelectionAsync(target, copy: true);

    /// <summary>Moves or copies every selected card to <paramref name="target"/> (cards already there are skipped).</summary>
    internal async Task TransferSelectionAsync(RosterGroupViewModel target, bool copy, int? index = null)
    {
        var items = SelectedEntries.Where(e => !ReferenceEquals(e.Group, target)).ToList();
        var done = 0;
        var skipped = 0;
        foreach (var entry in items)
        {
            if (target.Entries.Any(e => string.Equals(e.ChampionId, entry.ChampionId, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            await target.DropAsync(entry.DragData, null, after: false, copy, quiet: true, index: index is { } i ? i + done : null);
            done++;
        }
        ClearSelection();
        var verb = copy ? "Copied" : "Moved";
        var message = $"{verb} {done} champion{(done == 1 ? "" : "s")} to {target.Name}";
        if (skipped > 0) message += $" ({skipped} already there)";
        _shell.ShowToast(message, done > 0 ? ToastKind.Success : ToastKind.Info);
    }

    [RelayCommand]
    private void RateSelection(string value)
    {
        if (!int.TryParse(value, out var n)) return;
        foreach (var e in SelectedEntries) e.Proficiency = Math.Clamp(n, 0, 5);
    }

    [RelayCommand]
    private void PrioritizeSelection(ChampionPriority priority)
    {
        foreach (var e in SelectedEntries) e.Priority = priority;
    }

    [RelayCommand]
    private async Task RemoveSelectionAsync()
    {
        var items = SelectedEntries.Select(e => (Entry: e, Group: e.Group, Index: e.Group.Entries.IndexOf(e))).ToList();
        if (items.Count == 0) return;
        foreach (var (entry, group, _) in items) await group.RemoveAsync(entry, toast: false);
        ClearSelection();
        _shell.ShowToast($"Removed {items.Count} champion{(items.Count == 1 ? "" : "s")}", ToastKind.Info, "Undo", async () =>
        {
            foreach (var (entry, group, _) in items)
                if (Groups.Contains(group))
                    await _shell.Repository.AddEntryAsync(group.Id, entry.ChampionId, entry.Proficiency, entry.Priority);
            // Put them back where they were, then reload.
            foreach (var group in items.Select(i => i.Group).Distinct().Where(Groups.Contains))
            {
                var stored = (await _shell.Repository.GetGroupsAsync()).FirstOrDefault(g => g.Id == group.Id);
                if (stored is null) continue;
                var order = group.Entries.Select(e => e.ChampionId).ToList();
                foreach (var (entry, g, index) in items.Where(i => ReferenceEquals(i.Group, group)).OrderBy(i => i.Index))
                    order.Insert(Math.Clamp(index, 0, order.Count), entry.ChampionId);
                var ids = order.Select(id => stored.Entries.FirstOrDefault(e => e.ChampionId == id)?.Id).OfType<int>().ToList();
                await _shell.Repository.SetGroupOrderAsync(group.Id, ids);
            }
            await ReloadAsync();
            _shell.MarkDirty();
        });
    }

    partial void OnHideAssignedChanged(bool value)
    {
        _shell.Preferences.Set("rosters.hideAssigned", value);
        Pool.Refilter();
    }

    partial void OnCompactChanged(bool value) => _shell.Preferences.Set("rosters.compact", value);

    [RelayCommand]
    private void DismissTips()
    {
        TipsDismissed = true;
        _shell.Preferences.Set("rosters.tipsDismissed", true);
    }

    [RelayCommand]
    private void ToggleCompact() => Compact = !Compact;

    /// <summary>Enter in the pool search: open the best match.</summary>
    [RelayCommand]
    private void OpenFirst()
    {
        if (Pool.FirstTile is { } tile) _shell.OpenChampion(tile.Id, null, Pool.Sequence("Champion pool"));
    }

    public bool HandleKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers, bool typing)
    {
        if (typing) return false;
        if (modifiers == Avalonia.Input.KeyModifiers.Control && key == Avalonia.Input.Key.N)
        {
            AddGroupCommand.Execute(null);
            return true;
        }
        if (modifiers == Avalonia.Input.KeyModifiers.Control && key == Avalonia.Input.Key.A)
        {
            SelectAll();
            return true;
        }
        if (key == Avalonia.Input.Key.Escape && HasSelection)
        {
            ClearSelection();
            return true;
        }
        if (modifiers == Avalonia.Input.KeyModifiers.None && HasSelection)
        {
            var n = key switch
            {
                >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D5 => key - Avalonia.Input.Key.D0,
                >= Avalonia.Input.Key.NumPad0 and <= Avalonia.Input.Key.NumPad5 => key - Avalonia.Input.Key.NumPad0,
                _ => -1,
            };
            if (n >= 0)
            {
                RateSelection(n.ToString());
                return true;
            }
            if (key is Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back)
            {
                _ = RemoveSelectionAsync();
                return true;
            }
        }
        if (modifiers != Avalonia.Input.KeyModifiers.None || HoveredEntry is not { } entry) return false;
        var digit = key switch
        {
            >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D5 => key - Avalonia.Input.Key.D0,
            >= Avalonia.Input.Key.NumPad0 and <= Avalonia.Input.Key.NumPad5 => key - Avalonia.Input.Key.NumPad0,
            _ => -1,
        };
        if (digit >= 0)
        {
            entry.Proficiency = digit;
            return true;
        }
        if (key is Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back)
        {
            HoveredEntry = null;
            _ = entry.Group.RemoveAsync(entry);
            return true;
        }
        return false;
    }

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

public sealed partial class RosterGroupViewModel : ObservableObject, IDropTarget
{
    private readonly RostersViewModel _owner;

    public RosterGroupViewModel(RostersViewModel owner, RosterGroup model)
    {
        _owner = owner;
        Id = model.Id;
        _name = model.Name;
        _editName = model.Name;
        _role = model.Role;
        _colorHex = model.Color ?? Palette.RoleHex(model.Role);
        foreach (var e in model.Entries)
            if (owner.Game.Champion(e.ChampionId) is { } champion)
                Entries.Add(new RosterEntryViewModel(this, e, champion, owner.Game.Version));
    }

    public int Id { get; }
    public RostersViewModel Owner => _owner;
    public ObservableCollection<RosterEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _editName;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleIconUrl), nameof(HasRoleIcon))]
    private Position? _role;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Accent), nameof(AccentSoft))]
    private string _colorHex;

    public string? RoleIconUrl => Role is { } r ? AssetUrls.PositionIcon(r) : null;
    public bool HasRoleIcon => Role is not null;
    public IBrush Accent => Palette.Hex(ColorHex);
    public IBrush AccentSoft => Palette.Soft(ColorHex, 0x30);
    public string CountText => Entries.Count.ToString();
    public bool IsEmpty => Entries.Count == 0;
    public IReadOnlyList<string> ColorChoices => Palette.GroupColors;
    public IReadOnlyList<Position> RoleChoices => PositionInfo.All;

    internal void NotifyCount()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void BeginRename()
    {
        EditName = Name;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsEditing) return;
        IsEditing = false;
        var name = (EditName ?? "").Trim();
        if (name.Length == 0 || name == Name) return;
        Name = name;
        await SaveGroupAsync();
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsEditing = false;
        EditName = Name;
    }

    [RelayCommand]
    private async Task SetRoleAsync(object? role)
    {
        Role = role as Position?;
        if (Role is { } r) ColorHex = Palette.RoleHex(r);
        await SaveGroupAsync();
    }

    [RelayCommand]
    private async Task SetColorAsync(string hex)
    {
        ColorHex = hex;
        await SaveGroupAsync();
    }

    private Task SaveGroupAsync() => _owner.Shell.Repository.UpdateGroupAsync(Id, Name, Role, ColorHex);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteGroupAsync(this);

    [RelayCommand]
    private Task MoveLeftAsync() => _owner.MoveGroupAsync(this, -1);

    [RelayCommand]
    private Task MoveRightAsync() => _owner.MoveGroupAsync(this, +1);

    [RelayCommand]
    private Task SortByPriorityAsync() => SortAsync(Entries.OrderBy(e => e.Priority).ThenByDescending(e => e.Proficiency).ThenBy(e => e.Name));

    [RelayCommand]
    private Task SortByProficiencyAsync() => SortAsync(Entries.OrderByDescending(e => e.Proficiency).ThenBy(e => e.Priority).ThenBy(e => e.Name));

    [RelayCommand]
    private Task SortByNameAsync() => SortAsync(Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));

    private async Task SortAsync(IEnumerable<RosterEntryViewModel> order)
    {
        var sorted = order.ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Entries.IndexOf(sorted[i]);
            if (current != i) Entries.Move(current, i);
        }
        await SaveOrderAsync();
    }

    internal Task SaveOrderAsync() => _owner.Shell.Repository.SetGroupOrderAsync(Id, Entries.Select(e => e.Id).ToList());

    internal async Task RemoveAsync(RosterEntryViewModel entry, bool toast = true)
    {
        var index = Entries.IndexOf(entry);
        if (index < 0) return;
        Entries.Remove(entry);
        await _owner.Shell.Repository.DeleteEntryAsync(entry.Id);
        _owner.OnRosterChanged();
        if (!toast) return;
        var (champion, proficiency, priority) = (entry.Champion, entry.Proficiency, entry.Priority);
        _owner.Shell.ShowToast($"Removed {entry.Name} from {Name}", ToastKind.Info, "Undo", async () =>
        {
            if (!_owner.Groups.Contains(this) || Entries.Any(e => e.ChampionId == champion.Id)) return;
            var restored = await _owner.Shell.Repository.AddEntryAsync(Id, champion.Id, proficiency, priority);
            Entries.Insert(Math.Clamp(index, 0, Entries.Count), new RosterEntryViewModel(this, restored, champion, _owner.Game.Version)
            {
                BuildCount = entry.BuildCount,
                MatchupCount = entry.MatchupCount,
            });
            await SaveOrderAsync();
            _owner.OnRosterChanged();
        });
    }

    public ChampionSequence Sequence => new(Entries.Select(e => e.ChampionId).ToList(), Name, Role);

    // ── Drop target ────────────────────────────────────────────────────────────────────────────────

    public bool CanAccept(IDragPayload payload) => payload is ChampionDragData;

    public string? Describe(IDragPayload payload, bool copy)
    {
        if (payload is not ChampionDragData data) return null;
        if (data.Origin is RosterEntryViewModel { IsSelected: true } && _owner.SelectionCount > 1)
            return $"{(copy ? "Copy" : "Move")} {_owner.SelectionCount} to {Name}";
        if (data.Origin is RosterEntryViewModel source)
        {
            if (ReferenceEquals(source.Group, this)) return "Reorder";
            if (Contains(data.Champion.Id)) return copy ? $"Already in {Name}" : $"Merge into {Name}";
            return copy ? $"Copy to {Name}" : $"Move to {Name}";
        }
        return Contains(data.Champion.Id) ? $"Already in {Name}" : $"Add to {Name}";
    }

    public void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy)
    {
        if (payload is not ChampionDragData data) return;
        if (data.Origin is RosterEntryViewModel { IsSelected: true } && _owner.SelectionCount > 1)
        {
            var relative = relativeTo as RosterEntryViewModel;
            int? index = relative is not null && Entries.Contains(relative) ? Entries.IndexOf(relative) + (after ? 1 : 0) : null;
            _ = _owner.TransferSelectionAsync(this, copy, index);
            return;
        }
        _ = DropAsync(data, relativeTo as RosterEntryViewModel, after, copy);
    }

    private bool Contains(string championId) => Entries.Any(e => string.Equals(e.ChampionId, championId, StringComparison.OrdinalIgnoreCase));

    /// <param name="quiet">No toasts (bulk moves report once).</param>
    /// <param name="index">Insert position; null = from <paramref name="relative"/>, or the end.</param>
    internal async Task DropAsync(ChampionDragData data, RosterEntryViewModel? relative, bool after, bool copy, bool quiet = false, int? index = null)
    {
        var shell = _owner.Shell;
        var position = index is { } i ? Math.Clamp(i, 0, Entries.Count)
            : relative is not null && Entries.Contains(relative) ? Entries.IndexOf(relative) + (after ? 1 : 0) : Entries.Count;
        index = position;
        try
        {
            if (data.Origin is RosterEntryViewModel source)
            {
                if (ReferenceEquals(source.Group, this))
                {
                    var old = Entries.IndexOf(source);
                    var to = index.Value;
                    if (to > old) to--;
                    if (to == old || old < 0) return;
                    Entries.Move(old, Math.Clamp(to, 0, Entries.Count - 1));
                    await SaveOrderAsync();
                    return;
                }
                if (Contains(data.Champion.Id))
                {
                    if (copy)
                    {
                        shell.ShowToast($"{data.Champion.Name} is already in {Name}", ToastKind.Warning);
                        return;
                    }
                    await source.Group.RemoveAsync(source, toast: false);
                    shell.ShowToast($"{data.Champion.Name} was already in {Name}; removed from {source.Group.Name}");
                    return;
                }
                if (copy)
                {
                    var entry = await shell.Repository.AddEntryAsync(Id, data.Champion.Id, source.Proficiency, source.Priority);
                    Entries.Insert(index.Value, new RosterEntryViewModel(this, entry, data.Champion, _owner.Game.Version) { BuildCount = source.BuildCount, MatchupCount = source.MatchupCount });
                    await SaveOrderAsync();
                    if (!quiet) shell.ShowToast($"Copied {data.Champion.Name} to {Name}", ToastKind.Success);
                }
                else
                {
                    var from = source.Group;
                    from.Entries.Remove(source);
                    source.Group = this;
                    source.IsSelected = false;
                    Entries.Insert(Math.Clamp(index.Value, 0, Entries.Count), source);
                    await SaveOrderAsync();
                    await from.SaveOrderAsync();
                }
            }
            else
            {
                if (Contains(data.Champion.Id))
                {
                    shell.ShowToast($"{data.Champion.Name} is already in {Name}", ToastKind.Warning);
                    return;
                }
                var priority = Entries.Count == 0 ? ChampionPriority.Main : ChampionPriority.Secondary;
                var entry = await shell.Repository.AddEntryAsync(Id, data.Champion.Id, 0, priority);
                var tile = _owner.Pool.Find(data.Champion.Id);
                Entries.Insert(index.Value, new RosterEntryViewModel(this, entry, data.Champion, _owner.Game.Version)
                {
                    BuildCount = tile?.BuildCount ?? 0,
                    MatchupCount = tile?.MatchupCount ?? 0,
                });
                await SaveOrderAsync();
            }
        }
        finally
        {
            _owner.OnRosterChanged();
        }
    }
}

/// <summary>Priority icons and colours (legend on the Rosters page; badges on cards).</summary>
public sealed record PriorityLegend(ChampionPriority Priority)
{
    public static IReadOnlyList<PriorityLegend> Items { get; } = PriorityInfo.All.Select(p => new PriorityLegend(p)).ToList();

    public static string IconFor(ChampionPriority p) => p switch
    {
        ChampionPriority.Main => "crown",
        ChampionPriority.Secondary => "shield-half",
        ChampionPriority.Pocket => "crosshair",
        _ => "book-open",
    };

    public string Label => Priority.ToString();
    public string Description => $"{Priority}: {Priority.Description()}";
    public string Icon => IconFor(Priority);
    public IBrush Brush => Palette.Hex(Palette.PriorityHex(Priority));
}

public sealed partial class RosterEntryViewModel : ObservableObject
{
    private bool _loading = true;

    public RosterEntryViewModel(RosterGroupViewModel group, RosterEntry model, ChampionInfo champion, string version)
    {
        _group = group;
        Id = model.Id;
        Champion = champion;
        IconUrl = AssetUrls.ChampionSquare(version, champion.ImageFile);
        DragData = new ChampionDragData(champion, IconUrl, this);
        _proficiency = model.Proficiency;
        _priority = model.Priority;
        _loading = false;
    }

    public int Id { get; }
    public ChampionInfo Champion { get; }
    public string ChampionId => Champion.Id;
    public string Name => Champion.Name;
    public string IconUrl { get; }
    public ChampionDragData DragData { get; }
    public static IReadOnlyList<ChampionPriority> PriorityChoices => PriorityInfo.All;

    [ObservableProperty]
    private RosterGroupViewModel _group;

    /// <summary>Part of the multi-selection (Ctrl/Shift-click).</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _proficiency;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityLabel), nameof(PriorityBrush), nameof(PrioritySoftBrush), nameof(PriorityTooltip), nameof(PriorityIcon))]
    private ChampionPriority _priority;

    public string PriorityIcon => PriorityLegend.IconFor(Priority);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuilds))]
    private int _buildCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatchups))]
    private int _matchupCount;

    public bool HasBuilds => BuildCount > 0;
    public bool HasMatchups => MatchupCount > 0;
    public string PriorityLabel => Priority.ToString().ToUpperInvariant();
    public string PriorityTooltip => $"{Priority}: {Priority.Description()}";
    public IBrush PriorityBrush => Palette.Hex(Palette.PriorityHex(Priority));
    public IBrush PrioritySoftBrush => Palette.Soft(Palette.PriorityHex(Priority), 0x2A);

    partial void OnProficiencyChanged(int value) => Save();
    partial void OnPriorityChanged(ChampionPriority value) => Save();

    private void Save()
    {
        if (_loading) return;
        var shell = Group.Owner.Shell;
        shell.Saves.Schedule(this, () => shell.Repository.UpdateEntryAsync(Id, Proficiency, Priority));
        shell.MarkDirty();
    }

    [RelayCommand]
    private void SetPriority(ChampionPriority priority) => Priority = priority;

    [RelayCommand]
    private void SetProficiency(int value) => Proficiency = value;

    [RelayCommand]
    private Task RemoveAsync() => Group.RemoveAsync(this);

    [RelayCommand]
    private void Open() => Group.Owner.Shell.OpenChampion(ChampionId, null, Group.Sequence);

    [RelayCommand]
    private void OpenMatchups() => Group.Owner.Shell.OpenChampion(ChampionId, ChampionTab.Matchups, Group.Sequence);

    // ── Hover preview: the build that opens first for this group's role ────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewLoading))]
    private BuildPreview? _preview;

    public bool IsPreviewLoading => Preview is null;
    private bool _previewRequested;

    internal void InvalidatePreview() => _previewRequested = false;

    /// <summary>Loads (or refreshes) the preview; called when the pointer enters the card.</summary>
    public async Task LoadPreviewAsync()
    {
        if (_previewRequested) return;
        _previewRequested = true;
        var shell = Group.Owner.Shell;
        var builds = await shell.Repository.GetBuildsAsync(ChampionId);
        Preview = BuildPreview.Create(shell.Game, Champion, Core.Rules.BuildSelection.PickDefault(builds, Group.Role), builds.Count, Group.Role);
    }
}

/// <summary>A roster in the switcher (with how many champions it holds).</summary>
public sealed record RosterChoice(int Id, string Name, int ChampionCount, bool IsActive)
{
    public string Detail => $"{ChampionCount} champion{(ChampionCount == 1 ? "" : "s")}";
}

/// <summary>What a roster card's tooltip shows: the build that opens first for the group's role.</summary>
public sealed record BuildPreview(
    string ChampionName,
    bool HasBuild,
    string BuildName,
    bool IsDefault,
    string DefaultText,
    string? KeystoneUrl,
    string? SecondaryTreeUrl,
    string RuneText,
    IReadOnlyList<CompareIcon> Items,
    IReadOnlyList<CompareIcon> Spells,
    string Footer)
{
    public bool HasItems => Items.Count > 0;
    public bool HasSpells => Spells.Count > 0;

    public static BuildPreview Create(GameDataSnapshot game, ChampionInfo champion, Build? build, int buildCount, Position? role)
    {
        if (build is null)
            return new BuildPreview(champion.Name, false, "", false, "", null, null, "No builds yet", [], [], "Click to open and add a build");
        var runes = build.Runes;
        var keystone = game.Rune(runes.PrimaryPerks.ElementAtOrDefault(0));
        var primary = game.Tree(runes.PrimaryStyleId);
        var secondary = game.Tree(runes.SubStyleId);
        var runeText = string.Join(" · ", new[] { keystone?.Name, string.Join(" + ", new[] { primary?.Name, secondary?.Name }.Where(n => n is not null)) }
            .Where(t => !string.IsNullOrEmpty(t)));
        var core = Core.Rules.BuildSelection.CoreBlock(build);
        var items = (core?.Items ?? []).Take(6)
            .Select(id => new CompareIcon(AssetUrls.Item(game.Item(id) is null ? build.GameVersion ?? game.Version : game.Version, id), game.Item(id)?.Name ?? "Removed item"))
            .ToList();
        var spells = build.SummonerSpells.Select(game.Spell).OfType<SummonerSpellInfo>()
            .Select(s => new CompareIcon(AssetUrls.Spell(game.Version, s.ImageFile), s.Name)).ToList();
        var defaultText = build.IsDefault ? $"Default for {(build.Role is { } r ? r.DisplayName() : "any role")}" : "";
        var footer = buildCount > 1 ? $"Click to open · {buildCount} builds" : "Click to open";
        return new BuildPreview(champion.Name, true, build.Name, build.IsDefault, defaultText,
            keystone is not null ? AssetUrls.Rune(keystone.IconPath) : primary is not null ? AssetUrls.Rune(primary.IconPath) : null,
            secondary is not null ? AssetUrls.Rune(secondary.IconPath) : null,
            runeText.Length > 0 ? runeText : "No runes chosen", items, spells, footer);
    }
}
