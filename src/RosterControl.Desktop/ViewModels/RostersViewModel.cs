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
    public bool ShowTips => !TipsDismissed && IsEmpty;

    /// <summary>The roster card under the mouse (keyboard shortcuts 0–5 and Delete act on it).</summary>
    public RosterEntryViewModel? HoveredEntry { get; set; }

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
        var groups = await _shell.Repository.GetGroupsAsync();
        Groups.Clear();
        foreach (var g in groups) Groups.Add(new RosterGroupViewModel(this, g));
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

    public string? Describe(IDragPayload payload, bool copy) =>
        payload is ChampionDragData { Origin: RosterEntryViewModel e } ? $"Remove from {e.Group.Name}" : null;

    public void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy)
    {
        if (payload is ChampionDragData { Origin: RosterEntryViewModel e }) _ = e.Group.RemoveAsync(e);
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
        if (payload is ChampionDragData data) _ = DropAsync(data, relativeTo as RosterEntryViewModel, after, copy);
    }

    private bool Contains(string championId) => Entries.Any(e => string.Equals(e.ChampionId, championId, StringComparison.OrdinalIgnoreCase));

    private async Task DropAsync(ChampionDragData data, RosterEntryViewModel? relative, bool after, bool copy)
    {
        var shell = _owner.Shell;
        var index = relative is not null && Entries.Contains(relative) ? Entries.IndexOf(relative) + (after ? 1 : 0) : Entries.Count;
        try
        {
            if (data.Origin is RosterEntryViewModel source)
            {
                if (ReferenceEquals(source.Group, this))
                {
                    var old = Entries.IndexOf(source);
                    if (index > old) index--;
                    if (index == old || old < 0) return;
                    Entries.Move(old, index);
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
                    Entries.Insert(index, new RosterEntryViewModel(this, entry, data.Champion, _owner.Game.Version) { BuildCount = source.BuildCount, MatchupCount = source.MatchupCount });
                    await SaveOrderAsync();
                    shell.ShowToast($"Copied {data.Champion.Name} to {Name}", ToastKind.Success);
                }
                else
                {
                    var from = source.Group;
                    from.Entries.Remove(source);
                    source.Group = this;
                    Entries.Insert(index, source);
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
                Entries.Insert(index, new RosterEntryViewModel(this, entry, data.Champion, _owner.Game.Version)
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
}
