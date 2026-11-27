using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

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

    /// <summary>The always-on-top quick-look window, with this group's default build.</summary>
    [RelayCommand]
    private void QuickLook() => Group.Owner.Shell.OpenQuickLook(ChampionId, Group.Role);

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
    string Footer,
    string CoreText = "")
{
    public bool HasItems => Items.Count > 0;
    public bool HasCoreText => CoreText.Length > 0;
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
            runeText.Length > 0 ? runeText : "No runes chosen", items, spells, footer, Core.Rules.ItemStats.Core(build, game)?.Summary() ?? "");
    }
}
