using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>One champion: splash header, roster placements, builds, matchups, notes and tags.</summary>
public sealed partial class ChampionDetailViewModel : ObservableObject, IPage
{
    private readonly IShell _shell;
    private bool _loaded;
    private bool _profileLoading;

    public ChampionDetailViewModel(IShell shell, ChampionInfo champion, ChampionTab tab)
    {
        _shell = shell;
        Champion = champion;
        _selectedTab = (int)tab;
        PortraitUrl = AssetUrls.ChampionSquare(shell.Game.Version, champion.ImageFile);
        SplashUrl = AssetUrls.ChampionCentered(champion.Id);
        SplashFallbackUrl = AssetUrls.ChampionSplash(champion.Id);
        Matchups = new MatchupsViewModel(shell, champion);
    }

    public string Title => Champion.Name;
    public ChampionInfo Champion { get; }
    public string Name => Champion.Name.ToUpperInvariant();
    public string ChampionTitle => Champion.Title;
    public IReadOnlyList<string> Tags => Champion.Tags;
    public IReadOnlyList<RoleChoice> Positions => Champion.Positions.Select(p => new RoleChoice(p, p.DisplayName())).ToList();
    public bool HasPositions => Champion.Positions.Count > 0;
    public string PortraitUrl { get; }
    public string SplashUrl { get; }
    public string SplashFallbackUrl { get; }
    public MatchupsViewModel Matchups { get; }

    public ObservableCollection<PlacementViewModel> Placements { get; } = [];
    public ObservableCollection<GroupChoice> AvailableGroups { get; } = [];
    public ObservableCollection<BuildViewModel> Builds { get; } = [];
    public ObservableCollection<string> UserTags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuildsTab), nameof(IsMatchupsTab), nameof(IsNotesTab))]
    private int _selectedTab;

    public bool IsBuildsTab { get => SelectedTab == 0; set { if (value) SelectedTab = 0; } }
    public bool IsMatchupsTab { get => SelectedTab == 1; set { if (value) SelectedTab = 1; } }
    public bool IsNotesTab { get => SelectedTab == 2; set { if (value) SelectedTab = 2; } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedBuild))]
    private BuildViewModel? _selectedBuild;

    public bool HasSelectedBuild => SelectedBuild is not null;

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private string _newTag = "";

    [ObservableProperty]
    private bool _hasPlacements;

    [ObservableProperty]
    private bool _canAddToGroup;

    public bool HasBuilds => Builds.Count > 0;
    public string BuildsTabText => Builds.Count == 0 ? "BUILDS" : $"BUILDS  {Builds.Count}";

    partial void OnSelectedBuildChanged(BuildViewModel? oldValue, BuildViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            if (oldValue is not null) newValue.EditorTab = oldValue.EditorTab; // keep the same editor tab when switching builds
        }
    }

    partial void OnNotesChanged(string value) => SaveProfile();

    public async Task OnNavigatedToAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadPlacementsAsync();

        _profileLoading = true;
        var profile = await _shell.Repository.GetProfileAsync(Champion.Id);
        Notes = profile.Notes;
        UserTags.Clear();
        foreach (var t in profile.Tags) UserTags.Add(t);
        _profileLoading = false;

        var builds = await _shell.Repository.GetBuildsAsync(Champion.Id);
        Builds.Clear();
        foreach (var b in builds) Builds.Add(new BuildViewModel(_shell, b));
        SelectedBuild = Builds.FirstOrDefault();
        NotifyBuilds();

        await Matchups.LoadAsync();
    }

    private async Task LoadPlacementsAsync()
    {
        var groups = await _shell.Repository.GetGroupsAsync();
        Placements.Clear();
        AvailableGroups.Clear();
        foreach (var g in groups)
        {
            var entry = g.Entries.FirstOrDefault(e => string.Equals(e.ChampionId, Champion.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) Placements.Add(new PlacementViewModel(this, g, entry));
            else AvailableGroups.Add(new GroupChoice(g.Id, g.Name, g.Color ?? Palette.RoleHex(g.Role), g.Role));
        }
        HasPlacements = Placements.Count > 0;
        CanAddToGroup = AvailableGroups.Count > 0;
    }

    internal IShell Shell => _shell;

    [RelayCommand]
    private async Task AddToGroupAsync(GroupChoice group)
    {
        var groups = await _shell.Repository.GetGroupsAsync();
        var count = groups.FirstOrDefault(g => g.Id == group.Id)?.Entries.Count ?? 0;
        await _shell.Repository.AddEntryAsync(group.Id, Champion.Id, 0, count == 0 ? ChampionPriority.Main : ChampionPriority.Secondary);
        await LoadPlacementsAsync();
        _shell.MarkDirty();
        _shell.ShowToast($"Added {Champion.Name} to {group.Name}", ToastKind.Success);
    }

    internal async Task RemovePlacementAsync(PlacementViewModel placement)
    {
        await _shell.Repository.DeleteEntryAsync(placement.EntryId);
        await LoadPlacementsAsync();
        _shell.MarkDirty();
        _shell.ShowToast($"Removed {Champion.Name} from {placement.GroupName}");
    }

    // ── Builds ─────────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddBuildAsync()
    {
        var role = Placements.Count == 1 ? Placements[0].Role : null;
        var build = new Build
        {
            ChampionId = Champion.Id,
            Name = Builds.Count == 0 ? "Standard" : $"Build {Builds.Count + 1}",
            Role = role,
            GameVersion = _shell.Game.Version,
            Items = ItemSetData.CreateDefault(),
        };
        build.Id = await _shell.Repository.SaveBuildAsync(build);
        var vm = new BuildViewModel(_shell, build);
        Builds.Add(vm);
        SelectedBuild = vm;
        NotifyBuilds();
        _shell.MarkDirty();
    }

    [RelayCommand]
    private async Task DuplicateBuildAsync(BuildViewModel build)
    {
        await _shell.Saves.FlushAsync();
        var copy = build.Model.CloneAsNew(build.Name + " (copy)");
        copy.Id = await _shell.Repository.SaveBuildAsync(copy);
        var vm = new BuildViewModel(_shell, copy);
        Builds.Insert(Builds.IndexOf(build) + 1, vm);
        await _shell.Repository.ReorderBuildsAsync(Builds.Select(b => b.Model.Id).ToList());
        SelectedBuild = vm;
        NotifyBuilds();
        _shell.MarkDirty();
    }

    [RelayCommand]
    private async Task DeleteBuildAsync(BuildViewModel build)
    {
        if (!await _shell.ConfirmAsync($"Delete \"{build.Name}\"?", "The rune page, item set, spells, skill order and notes of this build will be deleted.", "Delete build"))
            return;
        await _shell.Saves.FlushAsync();
        var index = Builds.IndexOf(build);
        await _shell.Repository.DeleteBuildAsync(build.Model.Id);
        Builds.Remove(build);
        SelectedBuild = Builds.ElementAtOrDefault(Math.Max(0, index - 1));
        NotifyBuilds();
        _shell.MarkDirty();
    }

    [RelayCommand]
    private async Task MoveBuildUpAsync(BuildViewModel build)
    {
        var i = Builds.IndexOf(build);
        if (i <= 0) return;
        Builds.Move(i, i - 1);
        await _shell.Repository.ReorderBuildsAsync(Builds.Select(b => b.Model.Id).ToList());
    }

    [RelayCommand]
    private async Task MoveBuildDownAsync(BuildViewModel build)
    {
        var i = Builds.IndexOf(build);
        if (i < 0 || i >= Builds.Count - 1) return;
        Builds.Move(i, i + 1);
        await _shell.Repository.ReorderBuildsAsync(Builds.Select(b => b.Model.Id).ToList());
    }

    [RelayCommand]
    private void SelectBuild(BuildViewModel build) => SelectedBuild = build;

    private void NotifyBuilds()
    {
        OnPropertyChanged(nameof(HasBuilds));
        OnPropertyChanged(nameof(BuildsTabText));
    }

    // ── Notes & tags ───────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void AddTag()
    {
        var tag = (NewTag ?? "").Trim();
        NewTag = "";
        if (tag.Length == 0 || UserTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
        UserTags.Add(tag.Length > 32 ? tag[..32] : tag);
        SaveProfile();
    }

    [RelayCommand]
    private void RemoveTag(string tag)
    {
        UserTags.Remove(tag);
        SaveProfile();
    }

    private void SaveProfile()
    {
        if (_profileLoading) return;
        var notes = Notes;
        var tags = UserTags.ToList();
        _shell.Saves.Schedule(("profile", Champion.Id), () => _shell.Repository.SaveProfileAsync(Champion.Id, notes, tags));
        _shell.MarkDirty();
    }
}

public sealed record GroupChoice(int Id, string Name, string ColorHex, Position? Role)
{
    public IBrush Accent => Palette.Hex(ColorHex);
}

public sealed partial class PlacementViewModel : ObservableObject
{
    private readonly ChampionDetailViewModel _owner;
    private readonly bool _ready;

    public PlacementViewModel(ChampionDetailViewModel owner, RosterGroup group, RosterEntry entry)
    {
        _owner = owner;
        EntryId = entry.Id;
        GroupName = group.Name;
        Role = group.Role;
        Accent = Palette.Hex(group.Color ?? Palette.RoleHex(group.Role));
        RoleIconUrl = group.Role is { } r ? AssetUrls.PositionIcon(r) : null;
        _proficiency = entry.Proficiency;
        _priority = entry.Priority;
        _ready = true;
    }

    public int EntryId { get; }
    public string GroupName { get; }
    public Position? Role { get; }
    public IBrush Accent { get; }
    public string? RoleIconUrl { get; }
    public bool HasRoleIcon => RoleIconUrl is not null;
    public static IReadOnlyList<ChampionPriority> PriorityChoices => PriorityInfo.All;

    [ObservableProperty]
    private int _proficiency;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityBrush), nameof(PrioritySoftBrush), nameof(PriorityLabel))]
    private ChampionPriority _priority;

    public string PriorityLabel => Priority.ToString().ToUpperInvariant();
    public IBrush PriorityBrush => Palette.Hex(Palette.PriorityHex(Priority));
    public IBrush PrioritySoftBrush => Palette.Soft(Palette.PriorityHex(Priority), 0x2A);

    partial void OnProficiencyChanged(int value) => Save();
    partial void OnPriorityChanged(ChampionPriority value) => Save();

    private void Save()
    {
        if (!_ready) return;
        var shell = _owner.Shell;
        shell.Saves.Schedule(("entry", EntryId), () => shell.Repository.UpdateEntryAsync(EntryId, Proficiency, Priority));
        shell.MarkDirty();
    }

    [RelayCommand]
    private void SetPriority(ChampionPriority priority) => Priority = priority;

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemovePlacementAsync(this);
}

