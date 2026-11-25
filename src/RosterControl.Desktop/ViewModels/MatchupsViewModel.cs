using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// Good against / weak against lists for one champion, with a champion picker to drag from. Matchups can apply to all
/// roles or to one role (the same opponent can need different notes top vs support); choose the role you're recording
/// for with the role bar. In a role view, a matchup recorded for that role replaces the all-roles one.
/// </summary>
public sealed partial class MatchupsViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly ChampionInfo _champion;
    private readonly List<MatchupItemViewModel> _items = [];

    public MatchupsViewModel(IShell shell, ChampionInfo champion)
    {
        _shell = shell;
        _champion = champion;
        Good = new MatchupColumnViewModel(this, MatchupKind.GoodAgainst);
        Weak = new MatchupColumnViewModel(this, MatchupKind.WeakAgainst);
        var game = shell.Game;
        Picker = new ChampionBrowserViewModel(game, game.Champions.Where(c => c.Id != champion.Id)
            .Select(c => new ChampionTileViewModel(c, game.Version, game.NewChampionIds.Contains(c.Id))).ToList());
        RoleOptions = new ObservableCollection<FilterOption>(
            new[] { new FilterOption("All roles", null, iconKind: "layout-grid") { IsSelected = true } }
                .Concat(PositionInfo.All.Select(p => new FilterOption(p.DisplayName(), p, AssetUrls.PositionIcon(p)))));
    }

    public IShell Shell => _shell;
    public ChampionInfo Champion => _champion;
    public MatchupColumnViewModel Good { get; }
    public MatchupColumnViewModel Weak { get; }
    public ChampionBrowserViewModel Picker { get; }
    public ObservableCollection<InverseMatchupViewModel> Inverse { get; } = [];
    public ObservableCollection<FilterOption> RoleOptions { get; }
    public static IReadOnlyList<RoleChoice> RoleChoices { get; } =
        new[] { new RoleChoice(null, "All roles") }.Concat(PositionInfo.All.Select(p => new RoleChoice(p, p.DisplayName()))).ToList();

    [ObservableProperty]
    private bool _hasInverse;

    /// <summary>Role being viewed and recorded for (null = all roles).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleHint))]
    private Position? _selectedRole;

    public string RoleHint => SelectedRole is { } r
        ? $"Showing {Champion.Name} in {r.DisplayName()}: matchups recorded for {r.DisplayName()} plus the ones for all roles. New matchups are recorded for {r.DisplayName()} only."
        : "Showing every matchup. New matchups apply to all roles — pick a role to record role-specific notes.";

    partial void OnSelectedRoleChanged(Position? value)
    {
        foreach (var o in RoleOptions) o.IsSelected = Equals(o.Value as Position?, value);
        Good.NotifyHint();
        Weak.NotifyHint();
        Refresh();
    }

    [RelayCommand]
    private void SelectRole(FilterOption option) => SelectedRole = option.Value as Position?;

    public async Task LoadAsync()
    {
        var all = await _shell.Repository.GetMatchupsAsync();
        _items.Clear();
        foreach (var m in all.Where(m => string.Equals(m.ChampionId, _champion.Id, StringComparison.OrdinalIgnoreCase)))
            if (_shell.Game.Champion(m.OpponentId) is { } opp) _items.Add(new MatchupItemViewModel(this, m, opp));

        // Matchups recorded from the other champion's side ("Darius is weak against <this champion>").
        Inverse.Clear();
        foreach (var m in all.Where(m => string.Equals(m.OpponentId, _champion.Id, StringComparison.OrdinalIgnoreCase)))
            if (_shell.Game.Champion(m.ChampionId) is { } other) Inverse.Add(new InverseMatchupViewModel(_shell, m, other));
        HasInverse = Inverse.Count > 0;
        Refresh();
    }

    /// <summary>Rebuilds both columns for the selected role.</summary>
    internal void Refresh()
    {
        var role = SelectedRole;
        IEnumerable<MatchupItemViewModel> visible = _items;
        if (role is { } r)
        {
            var specific = _items.Where(i => i.Role == r).Select(i => i.OpponentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            visible = _items.Where(i => i.Role == r || (i.Role is null && !specific.Contains(i.OpponentId)));
        }
        var ordered = visible.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList();
        foreach (var i in ordered) i.ShowAllRolesTag = role is not null && i.Role is null;
        Replace(Good.Items, ordered.Where(i => i.Kind == MatchupKind.GoodAgainst));
        Replace(Weak.Items, ordered.Where(i => i.Kind == MatchupKind.WeakAgainst));

        var used = ordered.Select(i => i.OpponentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Picker.All) t.IsSelected = used.Contains(t.Id);
        Good.Notify();
        Weak.Notify();
    }

    private static void Replace(ObservableCollection<MatchupItemViewModel> target, IEnumerable<MatchupItemViewModel> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    internal MatchupColumnViewModel Column(MatchupKind kind) => kind == MatchupKind.GoodAgainst ? Good : Weak;

    /// <summary>Records a matchup for the selected role (or keeps the role of the matchup being moved).</summary>
    internal async Task SetAsync(ChampionInfo opponent, MatchupKind kind, string? note, int? index, Position? role)
    {
        var existing = _items.FirstOrDefault(i => string.Equals(i.OpponentId, opponent.Id, StringComparison.OrdinalIgnoreCase) && i.Role == role);
        var model = await _shell.Repository.SetMatchupAsync(_champion.Id, opponent.Id, kind, note ?? existing?.Note, role);
        if (existing is not null) _items.Remove(existing);
        var vm = new MatchupItemViewModel(this, model, opponent);
        _items.Add(vm);
        Refresh();
        if (index is not null)
        {
            var column = Column(kind);
            var current = column.Items.IndexOf(vm);
            var target = Math.Clamp(index.Value, 0, column.Items.Count - 1);
            if (current >= 0 && current != target) column.Items.Move(current, target);
            await SaveOrderAsync(column);
        }
        _shell.MarkDirty();
    }

    internal async Task SaveOrderAsync(MatchupColumnViewModel column)
    {
        var ids = column.Items.Select(i => i.Id).ToList();
        for (var n = 0; n < column.Items.Count; n++) column.Items[n].SortOrder = n;
        await _shell.Repository.ReorderMatchupsAsync(ids);
    }

    internal async Task RemoveAsync(MatchupItemViewModel item)
    {
        await _shell.Repository.DeleteMatchupAsync(item.Id);
        _items.Remove(item);
        Refresh();
        _shell.MarkDirty();
    }

    internal async Task ChangeRoleAsync(MatchupItemViewModel item, Position? role)
    {
        if (item.Role == role) return;
        if (!await _shell.Repository.SetMatchupRoleAsync(item.Id, role))
        {
            _shell.ShowToast($"There is already a {(role is { } r ? r.DisplayName() : "all-roles")} matchup vs {item.Name}.", ToastKind.Warning);
            return;
        }
        item.Role = role;
        Refresh();
        _shell.MarkDirty();
    }

    [RelayCommand]
    private Task AddGood(ChampionTileViewModel tile) => SetAsync(tile.Champion, MatchupKind.GoodAgainst, null, null, SelectedRole);

    [RelayCommand]
    private Task AddWeak(ChampionTileViewModel tile) => SetAsync(tile.Champion, MatchupKind.WeakAgainst, null, null, SelectedRole);

    [RelayCommand]
    private void OpenChampion(ChampionTileViewModel tile) => _shell.OpenChampion(tile.Id, ChampionTab.Matchups);
}

public sealed partial class MatchupColumnViewModel : ObservableObject, IDropTarget
{
    private readonly MatchupsViewModel _owner;

    public MatchupColumnViewModel(MatchupsViewModel owner, MatchupKind kind)
    {
        _owner = owner;
        Kind = kind;
        Title = kind == MatchupKind.GoodAgainst ? "GOOD AGAINST" : "WEAK AGAINST";
        Accent = Palette.Hex(kind == MatchupKind.GoodAgainst ? Palette.Good : Palette.Bad);
        AccentSoft = Palette.Soft(kind == MatchupKind.GoodAgainst ? Palette.Good : Palette.Bad, 0x22);
        IconKind = kind == MatchupKind.GoodAgainst ? "thumbs-up" : "thumbs-down";
    }

    public MatchupKind Kind { get; }
    public string Title { get; }
    public IBrush Accent { get; }
    public IBrush AccentSoft { get; }
    public string IconKind { get; }
    public ObservableCollection<MatchupItemViewModel> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public string CountText => Items.Count.ToString();

    public string Hint
    {
        get
        {
            var name = _owner.Champion.Name;
            var where = _owner.SelectedRole is { } r ? $" in {r.DisplayName()}" : "";
            return Kind == MatchupKind.GoodAgainst
                ? $"Champions {name} beats{where}. Drag champions here."
                : $"Champions that give {name} trouble{where}. Drag champions here.";
        }
    }

    internal void Notify()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    internal void NotifyHint() => OnPropertyChanged(nameof(Hint));

    public bool CanAccept(IDragPayload payload) => payload is ChampionDragData d && d.Champion.Id != _owner.Champion.Id;

    public string? Describe(IDragPayload payload, bool copy)
    {
        if (payload is not ChampionDragData d) return null;
        if (d.Origin is MatchupItemViewModel m && m.Column == this) return "Reorder";
        var role = d.Origin is MatchupItemViewModel moved ? moved.Role : _owner.SelectedRole;
        var where = role is { } r ? $" ({r.DisplayName()})" : "";
        return Kind == MatchupKind.GoodAgainst
            ? $"{_owner.Champion.Name} is good against {d.Champion.Name}{where}"
            : $"{_owner.Champion.Name} is weak against {d.Champion.Name}{where}";
    }

    public void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy)
    {
        if (payload is not ChampionDragData d) return;
        var relative = relativeTo as MatchupItemViewModel;
        int? index = relative is not null && Items.Contains(relative) ? Items.IndexOf(relative) + (after ? 1 : 0) : null;
        if (d.Origin is MatchupItemViewModel m && m.Column == this)
        {
            var old = Items.IndexOf(m);
            var target = index ?? Items.Count;
            if (target > old) target--;
            if (old >= 0 && target != old)
            {
                Items.Move(old, target);
                _ = _owner.SaveOrderAsync(this);
            }
            return;
        }
        // Moving a card keeps its role; dragging a new champion records it for the role being viewed.
        var role = d.Origin is MatchupItemViewModel moved ? moved.Role : _owner.SelectedRole;
        _ = _owner.SetAsync(d.Champion, Kind, null, index, role);
    }
}

public sealed partial class MatchupItemViewModel : ObservableObject
{
    private readonly MatchupsViewModel _owner;
    private readonly bool _ready;

    public MatchupItemViewModel(MatchupsViewModel owner, Matchup model, ChampionInfo opponent)
    {
        _owner = owner;
        Id = model.Id;
        Kind = model.Kind;
        SortOrder = model.SortOrder;
        _role = model.Role;
        Opponent = opponent;
        IconUrl = AssetUrls.ChampionSquare(owner.Shell.Game.Version, opponent.ImageFile);
        DragData = new ChampionDragData(opponent, IconUrl, this);
        _note = model.Note;
        _ready = true;
    }

    public int Id { get; }
    public MatchupKind Kind { get; }
    public int SortOrder { get; set; }
    public ChampionInfo Opponent { get; }
    public string OpponentId => Opponent.Id;
    public string Name => Opponent.Name;
    public string IconUrl { get; }
    public ChampionDragData DragData { get; }
    public MatchupColumnViewModel Column => _owner.Column(Kind);
    public string FlipText => Kind == MatchupKind.GoodAgainst ? "Move to Weak against" : "Move to Good against";
    public IReadOnlyList<RoleChoice> RoleChoices => MatchupsViewModel.RoleChoices;

    [ObservableProperty]
    private string _note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRole), nameof(RoleIconUrl), nameof(RoleText))]
    private Position? _role;

    /// <summary>In a role view, marks matchups that apply to every role.</summary>
    [ObservableProperty]
    private bool _showAllRolesTag;

    public bool HasRole => Role is not null;
    public string? RoleIconUrl => Role is { } r ? AssetUrls.PositionIcon(r) : null;
    public string RoleText => Role is { } r ? $"Only when playing {r.DisplayName()}" : "All roles";

    partial void OnNoteChanged(string value)
    {
        if (!_ready) return;
        var shell = _owner.Shell;
        var id = Id;
        shell.Saves.Schedule(("matchup", id), () => shell.Repository.UpdateMatchupNoteAsync(id, value));
    }

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);

    /// <summary>Good ⇄ weak. Updates the same record (same opponent and role).</summary>
    [RelayCommand]
    private Task FlipAsync() => _owner.SetAsync(Opponent, Kind == MatchupKind.GoodAgainst ? MatchupKind.WeakAgainst : MatchupKind.GoodAgainst, Note, null, Role);

    [RelayCommand]
    private Task SetRoleAsync(RoleChoice choice) => _owner.ChangeRoleAsync(this, choice.Role);

    [RelayCommand]
    private void Open() => _owner.Shell.OpenChampion(OpponentId, ChampionTab.Matchups);
}

/// <summary>A matchup recorded on another champion that mentions this one.</summary>
public sealed class InverseMatchupViewModel
{
    private readonly IShell _shell;

    public InverseMatchupViewModel(IShell shell, Matchup model, ChampionInfo other)
    {
        _shell = shell;
        Other = other;
        IconUrl = AssetUrls.ChampionSquare(shell.Game.Version, other.ImageFile);
        var where = model.Role is { } r ? $" ({r.DisplayName()})" : "";
        Text = model.Kind == MatchupKind.GoodAgainst ? $"{other.Name} is good against this champion{where}" : $"{other.Name} is weak against this champion{where}";
        Note = model.Note;
        Accent = Palette.Hex(model.Kind == MatchupKind.GoodAgainst ? Palette.Bad : Palette.Good);
        OpenCommand = new RelayCommand(() => _shell.OpenChampion(other.Id, ChampionTab.Matchups));
    }

    public ChampionInfo Other { get; }
    public string IconUrl { get; }
    public string Text { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
    public IBrush Accent { get; }
    public IRelayCommand OpenCommand { get; }
}
