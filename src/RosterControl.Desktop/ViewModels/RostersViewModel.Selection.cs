using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Selecting several cards (Ctrl/Shift-click) and acting on the selection.</summary>
public sealed partial class RostersViewModel
{
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
}
