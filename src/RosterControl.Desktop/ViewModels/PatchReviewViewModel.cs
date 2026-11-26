using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// Builds affected by patches since they were saved (items changed or removed, runes removed), kept up to date by the
/// shell; drives the banner on the Rosters page and the Patch review page.
/// </summary>
public sealed partial class PatchReviewState(Action open) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Count), nameof(HasEntries), nameof(ShowBanner), nameof(BannerText))]
    private IReadOnlyList<ReviewEntry> _entries = [];

    /// <summary>"Later" on the banner: hidden until the next start (or the next new patch).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private bool _hiddenThisSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerText))]
    private string _patch = "";

    public int Count => Entries.Count;
    public bool HasEntries => Count > 0;
    public bool ShowBanner => Count > 0 && !HiddenThisSession;
    public string BannerText => $"{Count} build{(Count == 1 ? " needs" : "s need")} a look after patch changes (now {Patch})";

    [RelayCommand]
    private void Open() => open();

    [RelayCommand]
    private void Hide() => HiddenThisSession = true;

    /// <summary>Finds every build with real changes since its patch. Old patches' item data is downloaded if needed.</summary>
    public static async Task<IReadOnlyList<ReviewEntry>> ScanAsync(IShell shell, CancellationToken ct)
    {
        var game = shell.Game;
        var builds = (await shell.Repository.GetAllBuildsAsync(ct))
            .Where(b => !string.IsNullOrEmpty(b.GameVersion) && b.GameVersion != game.Version && game.Champion(b.ChampionId) is not null)
            .ToList();
        var baselines = new Dictionary<string, IReadOnlyDictionary<string, ItemInfo>?>(StringComparer.Ordinal);
        foreach (var version in builds.Select(b => b.GameVersion!).Distinct())
            baselines[version] = await shell.GameData.GetItemsForVersionAsync(version, ct);
        return builds
            .Select(b => (Build: b, Changes: PatchReview.Changes(b, game, baselines[b.GameVersion!])))
            .Where(x => x.Changes.Count > 0)
            .Select(x => new ReviewEntry(game, x.Build, x.Changes, baselines[x.Build.GameVersion!] is not null))
            .OrderBy(e => e.ChampionName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Build.SortOrder)
            .ToList();
    }
}

/// <summary>One build to review, with what changed.</summary>
public sealed class ReviewEntry(GameDataSnapshot game, Build build, IReadOnlyList<BuildChange> changes, bool comparedItems)
{
    private readonly ChampionInfo _champion = game.Champion(build.ChampionId)!;

    public Build Build { get; } = build;
    public string ChampionName => _champion.Name;
    public string PortraitUrl { get; } = AssetUrls.ChampionSquare(game.Version, game.Champion(build.ChampionId)!.ImageFile);
    public string BuildName => Build.Name;
    public string SavedOnText => $"Saved on patch {Build.GameVersion}" + (comparedItems ? "" : " · couldn't download that patch's items, so only removals are listed");
    public IReadOnlyList<ReviewChangeLine> Changes { get; } = changes.Select(c => new ReviewChangeLine(game, build, c)).ToList();
}

public sealed class ReviewChangeLine(GameDataSnapshot game, Build build, BuildChange change)
{
    public string Text => change.Text;
    public bool IsRemoval => change.Kind is BuildChangeKind.ItemRemoved or BuildChangeKind.RuneRemoved;
    public string Icon => change.Kind == BuildChangeKind.ItemChanged ? "triangle-alert" : "circle-x";

    /// <summary>The item's icon (from the build's patch when it no longer exists).</summary>
    public string? ItemIconUrl { get; } = change.ItemId is { } id
        ? AssetUrls.Item(game.Item(id) is null ? build.GameVersion ?? game.Version : game.Version, id)
        : null;

    public bool HasItemIcon => ItemIconUrl is not null;
}

/// <summary>The Patch review page: every affected build, with Open and Mark reviewed.</summary>
public sealed partial class PatchReviewViewModel(IShell shell) : ObservableObject, IPage
{
    public string Title => "Patch review";
    public PatchReviewState State => shell.Review;
    public string EmptyText => $"Every build is up to date with patch {shell.Game.Version}.";

    [ObservableProperty]
    private bool _isScanning;

    public async Task OnNavigatedToAsync()
    {
        IsScanning = true;
        try
        {
            await shell.RefreshPatchReviewAsync();
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void OpenBuild(ReviewEntry entry)
    {
        shell.Preferences.Set("build.editorTab", 1); // the Items tab, where changes are marked
        shell.OpenChampion(entry.Build.ChampionId, ChampionTab.Builds, buildId: entry.Build.Id);
    }

    [RelayCommand]
    private Task MarkReviewedAsync(ReviewEntry entry) => MarkAsync([entry]);

    [RelayCommand]
    private Task MarkAllReviewedAsync() => MarkAsync(State.Entries.ToList());

    private async Task MarkAsync(IReadOnlyList<ReviewEntry> entries)
    {
        if (entries.Count == 0) return;
        var previous = entries.ToDictionary(e => e.Build.Id, e => e.Build.GameVersion);
        await shell.Repository.SetBuildPatchesAsync(entries.ToDictionary(e => e.Build.Id, _ => (string?)shell.Game.Version));
        // By id: a rescan may have replaced the entry objects since this one was shown.
        State.Entries = State.Entries.Where(e => !previous.ContainsKey(e.Build.Id)).ToList();
        shell.MarkDirty();
        shell.ShowToast(entries.Count == 1 ? $"Marked \"{entries[0].BuildName}\" as reviewed" : $"Marked {entries.Count} builds as reviewed",
            ToastKind.Success, "Undo", async () =>
            {
                await shell.Repository.SetBuildPatchesAsync(previous);
                await shell.RefreshPatchReviewAsync();
                shell.MarkDirty();
            });
    }
}
