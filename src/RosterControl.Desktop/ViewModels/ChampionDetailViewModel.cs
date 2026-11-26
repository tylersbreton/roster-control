using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>One champion: splash header, roster placements, builds, matchups, notes and tags.</summary>
public sealed partial class ChampionDetailViewModel : ObservableObject, IPage, IKeyboardPage
{
    private readonly IShell _shell;
    private bool _loaded;
    private bool _profileLoading;

    private int? _initialBuildId;

    public ChampionDetailViewModel(IShell shell, ChampionInfo champion, ChampionTab tab, ChampionSequence? sequence = null, int? initialBuildId = null)
    {
        _shell = shell;
        _initialBuildId = initialBuildId;
        BuildDrop = new BuildListDropTarget(this);
        Champion = champion;
        _selectedTab = (int)tab;
        Sequence = sequence;
        SequenceIndex = sequence?.ChampionIds.ToList().FindIndex(id => string.Equals(id, champion.Id, StringComparison.OrdinalIgnoreCase)) ?? -1;
        PortraitUrl = AssetUrls.ChampionSquare(shell.Game.Version, champion.ImageFile);
        SplashUrl = AssetUrls.ChampionCentered(champion.Id);
        SplashFallbackUrl = AssetUrls.ChampionSplash(champion.Id);
        Matchups = new MatchupsViewModel(shell, champion);
        Abilities = new AbilitiesViewModel(shell, champion.Id);
        if (tab == ChampionTab.Abilities) _ = Abilities.EnsureLoadedAsync();
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
    public AbilitiesViewModel Abilities { get; }

    // ── Previous / next within the list the champion was opened from ─────────────────────────────

    public ChampionSequence? Sequence { get; }
    public int SequenceIndex { get; }
    public bool HasSequence => Sequence is { ChampionIds.Count: > 1 } && SequenceIndex >= 0;
    public string SequenceText => HasSequence ? $"{SequenceIndex + 1} / {Sequence!.ChampionIds.Count} · {Sequence.Label}" : "";
    public string? PreviousName => HasSequence ? _shell.Game.Champion(Sequence!.ChampionIds[(SequenceIndex - 1 + Sequence.ChampionIds.Count) % Sequence.ChampionIds.Count])?.Name : null;
    public string? NextName => HasSequence ? _shell.Game.Champion(Sequence!.ChampionIds[(SequenceIndex + 1) % Sequence.ChampionIds.Count])?.Name : null;

    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Next() => Step(+1);

    private void Step(int delta)
    {
        if (!HasSequence) return;
        var ids = Sequence!.ChampionIds;
        var id = ids[(SequenceIndex + delta + ids.Count) % ids.Count];
        _shell.ShowChampionInSequence(id, (ChampionTab)SelectedTab, Sequence);
    }

    public bool HandleKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers, bool typing)
    {
        var ctrl = modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        var shift = modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        if (ctrl && key == Avalonia.Input.Key.Tab)
        {
            SelectedTab = (SelectedTab + (shift ? TabCount - 1 : 1)) % TabCount;
            return true;
        }
        if (ctrl && !shift && key == Avalonia.Input.Key.N)
        {
            SelectedTab = (int)ChampionTab.Builds;
            AddBuildCommand.Execute(null);
            return true;
        }
        if (!typing && ctrl && key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right && HasSequence)
        {
            Step(key == Avalonia.Input.Key.Left ? -1 : 1);
            return true;
        }
        return false;
    }

    // ── Copy a build to another champion ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _copyQuery = "";

    [ObservableProperty]
    private IReadOnlyList<CopyTarget> _copyTargets = [];

    partial void OnCopyQueryChanged(string value)
    {
        var query = ChampionInfo.Normalize(value);
        CopyTargets = query.Length == 0
            ? []
            : _shell.Game.Champions.Where(c => c.Id != Champion.Id && c.Matches(value))
                .OrderBy(c => !ChampionInfo.Normalize(c.Name).StartsWith(query, StringComparison.Ordinal))
                .Take(8)
                .Select(c => new CopyTarget(c, AssetUrls.ChampionSquare(_shell.Game.Version, c.ImageFile)))
                .ToList();
    }

    /// <summary>Copies the selected build (runes, items, spells, notes; not the skill order) to another champion.</summary>
    [RelayCommand]
    private async Task CopyBuildToAsync(CopyTarget target)
    {
        if (SelectedBuild is not { } build) return;
        await _shell.Saves.FlushAsync();
        var copy = build.Model.CloneAsNew(build.Name);
        copy.ChampionId = target.Champion.Id;
        copy.SkillOrder = ""; // abilities differ between champions
        await _shell.Repository.SaveBuildAsync(copy);
        CopyQuery = "";
        _shell.MarkDirty();
        _shell.ShowToast($"Copied \"{build.Name}\" to {target.Champion.Name}", ToastKind.Success, "Open",
            () => { _shell.OpenChampion(target.Champion.Id, ChampionTab.Builds); return Task.CompletedTask; });
    }

    public ObservableCollection<PlacementViewModel> Placements { get; } = [];
    public ObservableCollection<GroupChoice> AvailableGroups { get; } = [];
    public ObservableCollection<BuildViewModel> Builds { get; } = [];
    public ObservableCollection<string> UserTags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuildsTab), nameof(IsMatchupsTab), nameof(IsNotesTab), nameof(IsAbilitiesTab))]
    private int _selectedTab;

    private const int TabCount = 4;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == (int)ChampionTab.Abilities) _ = Abilities.EnsureLoadedAsync();
    }

    public bool IsBuildsTab { get => SelectedTab == 0; set { if (value) SelectedTab = 0; } }
    public bool IsMatchupsTab { get => SelectedTab == 1; set { if (value) SelectedTab = 1; } }
    public bool IsNotesTab { get => SelectedTab == 2; set { if (value) SelectedTab = 2; } }
    public bool IsAbilitiesTab { get => SelectedTab == 3; set { if (value) SelectedTab = 3; } }

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
        foreach (var b in builds) Builds.Add(Track(new BuildViewModel(_shell, b)));
        SelectedBuild = (_initialBuildId is { } id ? Builds.FirstOrDefault(b => b.Model.Id == id) : null) ?? PickInitialBuild();
        _initialBuildId = null;
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
        _shell.ShowToast($"Removed {Champion.Name} from {placement.GroupName}", ToastKind.Info, "Undo", async () =>
        {
            await _shell.Repository.AddEntryAsync(placement.GroupId, Champion.Id, placement.Proficiency, placement.Priority);
            await LoadPlacementsAsync();
            _shell.MarkDirty();
        });
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
        var vm = Track(new BuildViewModel(_shell, build));
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
        var vm = Track(new BuildViewModel(_shell, copy));
        Builds.Insert(Builds.IndexOf(build) + 1, vm);
        await _shell.Repository.ReorderBuildsAsync(Builds.Select(b => b.Model.Id).ToList());
        SelectedBuild = vm;
        NotifyBuilds();
        _shell.MarkDirty();
    }

    [RelayCommand]
    private async Task DeleteBuildAsync(BuildViewModel build)
    {
        // No confirmation: the toast offers Undo (also Ctrl+Z).
        await _shell.Saves.FlushAsync();
        var index = Builds.IndexOf(build);
        await _shell.Repository.DeleteBuildAsync(build.Model.Id);
        Builds.Remove(build);
        SelectedBuild = Builds.ElementAtOrDefault(Math.Max(0, index - 1));
        NotifyBuilds();
        _shell.MarkDirty();
        var snapshot = build.Model.CloneAsNew(build.Name);
        snapshot.GameVersion = build.Model.GameVersion;
        snapshot.IsDefault = build.Model.IsDefault;
        _shell.ShowToast($"Deleted build \"{build.Name}\"", ToastKind.Info, "Undo", async () =>
        {
            snapshot.Id = await _shell.Repository.SaveBuildAsync(snapshot);
            var vm = Track(new BuildViewModel(_shell, snapshot));
            Builds.Insert(Math.Clamp(index, 0, Builds.Count), vm);
            await _shell.Repository.ReorderBuildsAsync(Builds.Select(b => b.Model.Id).ToList());
            SelectedBuild = vm;
            NotifyBuilds();
            _shell.MarkDirty();
        });
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

    [RelayCommand]
    private void Compare() => _shell.OpenCompare(Champion.Id, null);

    [RelayCommand]
    private void QuickLook() => _shell.OpenQuickLook(Champion.Id, buildId: SelectedBuild?.Model.Id);

    /// <summary>Selects a build by id (e.g. from a notes search result).</summary>
    public void SelectBuildById(int buildId)
    {
        if (!_loaded) _initialBuildId = buildId;
        else if (Builds.FirstOrDefault(b => b.Model.Id == buildId) is { } build) SelectedBuild = build;
    }

    // ── Default build per role ─────────────────────────────────────────────────────────────────────

    /// <summary>The role the champion was opened for: the group it was opened from, or its only placement.</summary>
    private Position? ContextRole => Sequence?.Role ?? (Placements.Count == 1 ? Placements[0].Role : null);

    /// <summary>Default for the context role, then the any-role default, then the first build for that role, then the first.</summary>
    internal BuildViewModel? PickInitialBuild() => Core.Rules.BuildSelection.PickDefault(Builds, ContextRole, b => b.Model);

    [RelayCommand]
    private async Task ToggleDefaultAsync(BuildViewModel build)
    {
        var make = !build.IsDefault;
        await _shell.Saves.FlushAsync();
        if (make)
            foreach (var other in Builds.Where(b => !ReferenceEquals(b, build) && b.IsDefault && b.Role == build.Role))
                other.SetDefault(false);
        build.SetDefault(make);
        await _shell.Repository.SetDefaultBuildAsync(build.Model.Id, make);
        _shell.MarkDirty();
    }

    /// <summary>A default build moved to another role takes over as that role's default.</summary>
    private BuildViewModel Track(BuildViewModel build)
    {
        build.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(BuildViewModel.Role) || !build.IsDefault) return;
            foreach (var other in Builds.Where(b => !ReferenceEquals(b, build) && b.IsDefault && b.Role == build.Role))
                other.SetDefault(false);
            await _shell.Saves.FlushAsync();
            await _shell.Repository.SetDefaultBuildAsync(build.Model.Id, true);
        };
        return build;
    }

    // ── Reorder by dragging ────────────────────────────────────────────────────────────────────────

    public BuildListDropTarget BuildDrop { get; }

    internal async Task MoveBuildAsync(BuildViewModel build, BuildViewModel? relativeTo, bool after)
    {
        var from = Builds.IndexOf(build);
        if (from < 0) return;
        int to;
        if (relativeTo is null) to = Builds.Count - 1; // dropped below the list: move to the end
        else
        {
            var insertAt = Builds.IndexOf(relativeTo) + (after ? 1 : 0);
            to = insertAt > from ? insertAt - 1 : insertAt; // removing the build shifts the later ones up
        }
        to = Math.Clamp(to, 0, Builds.Count - 1);
        if (to == from) return;
        Builds.Move(from, to);
        await _shell.Repository.ReorderBuildsAsync(Builds.Select(b => b.Model.Id).ToList());
    }

    // ── Share codes ────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CopyShareCodeAsync(BuildViewModel? build)
    {
        build ??= SelectedBuild;
        if (build is null) return;
        await _shell.Saves.FlushAsync();
        var code = Core.Rules.BuildShareCode.Encode(build.Model);
        var text = $"{Champion.Name} · {build.Name} — Roster Control build: {code}";
        if (await _shell.CopyTextAsync(text))
            _shell.ShowToast($"Build code for \"{build.Name}\" copied. Share it anywhere; others add it with Paste build code.", ToastKind.Success);
        else
            _shell.ShowToast("Couldn't access the clipboard.", ToastKind.Error);
    }

    [RelayCommand]
    private async Task PasteShareCodeAsync()
    {
        var build = await _shell.ImportSharedBuildAsync(Champion.Id);
        if (build is null) return;
        if (string.Equals(build.ChampionId, Champion.Id, StringComparison.OrdinalIgnoreCase))
        {
            var vm = Track(new BuildViewModel(_shell, build));
            Builds.Add(vm);
            SelectedBuild = vm;
            SelectedTab = (int)ChampionTab.Builds;
            NotifyBuilds();
            _shell.ShowToast($"Added build \"{build.Name}\"", ToastKind.Success);
        }
        else
        {
            var name = _shell.Game.Champion(build.ChampionId)?.Name ?? build.ChampionId;
            _shell.ShowToast($"Added \"{build.Name}\" to {name}", ToastKind.Success, "Open",
                () => { _shell.OpenChampion(build.ChampionId, ChampionTab.Builds, buildId: build.Id); return Task.CompletedTask; });
        }
    }

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

/// <summary>The build list: accepts this champion's builds dragged to a new position.</summary>
public sealed class BuildListDropTarget(ChampionDetailViewModel owner) : IDropTarget
{
    public bool CanAccept(IDragPayload payload) => payload is BuildDragData d && owner.Builds.Contains(d.Build);
    public string? Describe(IDragPayload payload, bool copy) => "Move here";

    public void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy)
    {
        if (payload is BuildDragData d) _ = owner.MoveBuildAsync(d.Build, relativeTo as BuildViewModel, after);
    }
}

public sealed record CopyTarget(ChampionInfo Champion, string IconUrl)
{
    public string Name => Champion.Name;
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
        GroupId = group.Id;
        GroupName = group.Name;
        Role = group.Role;
        Accent = Palette.Hex(group.Color ?? Palette.RoleHex(group.Role));
        RoleIconUrl = group.Role is { } r ? AssetUrls.PositionIcon(r) : null;
        _proficiency = entry.Proficiency;
        _priority = entry.Priority;
        _ready = true;
    }

    public int EntryId { get; }
    public int GroupId { get; }
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

