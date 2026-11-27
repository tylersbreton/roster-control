using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// The dialog for sharing a roster as a <c>.rcroster</c> file (choose what to include, then save) or adding one someone
/// shared (preview, then a new roster or merged into the current one).
/// </summary>
public sealed partial class RosterShareViewModel : ObservableObject
{
    private readonly RostersViewModel _owner;
    private readonly IShell _shell;
    private readonly SharedRosterFile? _file;

    private RosterShareViewModel(RostersViewModel owner, IShell shell, SharedRosterFile? file)
    {
        _owner = owner;
        _shell = shell;
        _file = file;
    }

    public bool IsExport => _file is null;
    public bool IsImport => _file is not null;
    public string Title { get; private init; } = "";
    public string Summary { get; private init; } = "";
    public string Warning { get; private init; } = "";
    public bool HasWarning => Warning.Length > 0;

    // ── Export ────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _includeRatings = true;

    [ObservableProperty]
    private bool _includeBuilds = true;

    public string BuildsOptionText { get; private init; } = "";
    public bool CanIncludeBuilds { get; private init; }

    internal static async Task<RosterShareViewModel> ForExportAsync(RostersViewModel owner, IShell shell)
    {
        var champions = owner.AllEntries.Select(e => e.ChampionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builds = (await shell.Repository.GetAllBuildsAsync()).Count(b => champions.Contains(b.ChampionId));
        return new RosterShareViewModel(owner, shell, null)
        {
            Title = $"Share \"{owner.RosterName}\" as a file",
            Summary = $"{owner.Groups.Count} group{S(owner.Groups.Count)} · {champions.Count} champion{S(champions.Count)}. Friends add it with Import roster from file. Notes are never included.",
            BuildsOptionText = builds == 0 ? "Builds (none for these champions yet)" : $"Builds of these champions ({builds})",
            CanIncludeBuilds = builds > 0,
            IncludeBuilds = builds > 0,
        };
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var safeName = string.Concat(_owner.RosterName.Split(Path.GetInvalidFileNameChars())).Trim();
        var path = await _shell.PickSaveFileAsync("Share roster", $"{(safeName.Length == 0 ? "Roster" : safeName)}.{SharedRosterFile.Extension}",
            SharedRosterFile.Extension, "Roster Control roster");
        if (path is not null) await SaveToAsync(path);
    }

    /// <summary>Writes the roster file to <paramref name="path"/> with the chosen options.</summary>
    internal async Task SaveToAsync(string path)
    {
        await _shell.Saves.FlushAsync();
        var groups = await _shell.Repository.GetGroupsAsync(_owner.ActiveRosterId);
        var builds = IncludeBuilds && CanIncludeBuilds ? await _shell.Repository.GetAllBuildsAsync() : null;
        var version = typeof(RosterShareViewModel).Assembly.GetName().Version?.ToString(3);
        var file = SharedRosterFile.Create(_owner.RosterName, groups, IncludeRatings, builds, version, _shell.Game.Version);
        try
        {
            await File.WriteAllTextAsync(path, file.Serialize());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast("Couldn't save the file: " + ex.Message, ToastKind.Error);
            return;
        }
        _owner.ShareDialog = null;
        var what = file.Builds.Count > 0 ? $" with {file.Builds.Count} build{S(file.Builds.Count)}" : "";
        _shell.ShowToast($"Saved {Path.GetFileName(path)}{what}. Send it to anyone with Roster Control.", ToastKind.Success, "Show", () =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return Task.CompletedTask;
        });
    }

    // ── Import ────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Merge))]
    private bool _asNewRoster = true;

    public bool Merge
    {
        get => !AsNewRoster;
        set => AsNewRoster = !value;
    }

    [ObservableProperty]
    private string _newName = "";

    /// <param name="mode">"new" or "merge".</param>
    [RelayCommand]
    private void ChooseMode(string mode) => AsNewRoster = mode != "merge";

    public string MergeText { get; private init; } = "";
    public bool HasBuildsToImport { get; private init; }
    public string ImportBuildsText { get; private init; } = "";

    internal static RosterShareViewModel ForImport(RostersViewModel owner, IShell shell, SharedRosterFile file)
    {
        var game = shell.Game;
        var champions = file.Groups.SelectMany(g => g.Entries).Select(e => e.ChampionId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unknown = champions.Where(id => game.Champion(id) is null).ToList();
        var warnings = new List<string>();
        if (unknown.Count > 0) warnings.Add($"Not in patch {game.Version} (skipped): {string.Join(", ", unknown)}.");
        if (file.IsFromNewerVersion) warnings.Add("Made by a newer version of Roster Control: anything this version doesn't know is left out. Update to import everything.");
        var name = file.Name;
        for (var n = 2; owner.Rosters.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{file.Name} {n}";
        var ratings = file.IncludesRatings ? "with proficiency and priority" : "without ratings";
        return new RosterShareViewModel(owner, shell, file)
        {
            Title = $"Add the roster \"{file.Name}\"",
            Summary = $"{file.Groups.Count} group{S(file.Groups.Count)} · {champions.Count} champion{S(champions.Count)}, {ratings}" +
                      (file.GameVersion is { } v ? $" · shared on patch {v}" : "") + ".",
            Warning = string.Join(" ", warnings),
            NewName = name,
            MergeText = $"Merge into \"{owner.RosterName}\": groups with the same name or role get the new champions; other groups are added",
            HasBuildsToImport = file.Builds.Count > 0,
            ImportBuildsText = $"Add their builds ({file.Builds.Count}) next to yours; identical ones are skipped and nothing is replaced",
            IncludeBuilds = file.Builds.Count > 0,
        };
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_file is null) return;
        await _shell.Saves.FlushAsync();
        var game = _shell.Game;
        RosterImportResultView result;
        try
        {
            var r = await _shell.Repository.ImportSharedRosterAsync(_file, AsNewRoster ? null : _owner.ActiveRosterId, NewName, IncludeBuilds && HasBuildsToImport,
                id => game.Champion(id)?.Id);
            result = new RosterImportResultView(r.RosterId, r.PlacementsAdded, r.GroupsAdded, r.BuildsAdded, r.BuildsAlreadyThere);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Roster import failed");
            _shell.ShowToast("Couldn't add the roster: " + ex.Message, ToastKind.Error);
            return;
        }
        _owner.ShareDialog = null;
        _shell.MarkDirty();
        if (AsNewRoster) await _shell.SwitchRosterAsync(result.RosterId);
        else await _owner.ReloadAsync();
        var builds = result.Builds > 0 ? $", {result.Builds} build{S(result.Builds)}" : "";
        var same = result.SameBuilds > 0 ? $" ({result.SameBuilds} identical build{S(result.SameBuilds)} skipped)" : "";
        _shell.ShowToast(AsNewRoster
            ? $"Added the roster \"{NewName.Trim()}\": {result.Placements} champion{S(result.Placements)}{builds}{same}."
            : $"Merged: {result.Placements} new placement{S(result.Placements)}, {result.Groups} new group{S(result.Groups)}{builds}{same}.", ToastKind.Success);
    }

    private sealed record RosterImportResultView(int RosterId, int Placements, int Groups, int Builds, int SameBuilds);

    [RelayCommand]
    private void Cancel() => _owner.ShareDialog = null;

    private static string S(int n) => n == 1 ? "" : "s";
}
