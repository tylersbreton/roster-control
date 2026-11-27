using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

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
