using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.Abstractions;
using RosterControl.Data;

namespace RosterControl.Desktop.ViewModels;

/// <summary>All champions, with what you have recorded for each (roster, builds, matchups, tags).</summary>
public sealed partial class LibraryViewModel : ObservableObject, IPage
{
    private readonly IShell _shell;
    private Dictionary<string, List<string>> _tags = new(StringComparer.OrdinalIgnoreCase);

    public LibraryViewModel(IShell shell)
    {
        _shell = shell;
        Browser = new ChampionBrowserViewModel(shell.Game);
        Browser.ExtraFilter = Filter;
    }

    public string Title => "Champions";
    public ChampionBrowserViewModel Browser { get; }

    [ObservableProperty]
    private bool _onlyRoster;

    [ObservableProperty]
    private bool _onlyWithBuilds;

    [ObservableProperty]
    private IReadOnlyList<string> _tagOptions = ["Any tag"];

    [ObservableProperty]
    private string _selectedTag = "Any tag";

    public bool HasTags => TagOptions.Count > 1;

    partial void OnOnlyRosterChanged(bool value) => Browser.Refilter();
    partial void OnOnlyWithBuildsChanged(bool value) => Browser.Refilter();
    partial void OnSelectedTagChanged(string value) => Browser.Refilter();
    partial void OnTagOptionsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasTags));

    private bool Filter(ChampionTileViewModel t) =>
        (!OnlyRoster || t.IsInRoster)
        && (!OnlyWithBuilds || t.HasBuilds)
        && (SelectedTag == "Any tag" || (_tags.TryGetValue(t.Id, out var tags) && tags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase)));

    public async Task OnNavigatedToAsync()
    {
        var counts = await _shell.Repository.GetChampionCountsAsync();
        var groups = await _shell.Repository.GetGroupsAsync();
        var membership = groups.SelectMany(g => g.Entries).GroupBy(e => e.ChampionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var t in Browser.All)
        {
            counts.TryGetValue(t.Id, out var c);
            t.BuildCount = c.Builds;
            t.MatchupCount = c.Matchups;
            t.GroupCount = membership.TryGetValue(t.Id, out var n) ? n : 0;
        }
        var profiles = await _shell.Repository.GetProfilesAsync();
        _tags = profiles.ToDictionary(p => p.ChampionId, p => p.Tags, StringComparer.OrdinalIgnoreCase);
        var selected = SelectedTag;
        TagOptions = new[] { "Any tag" }.Concat(profiles.SelectMany(p => p.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToList();
        SelectedTag = TagOptions.Contains(selected) ? selected : "Any tag";
        Browser.Refilter();
    }

    [RelayCommand]
    private void Open(ChampionTileViewModel tile) => _shell.OpenChampion(tile.Id);
}

public sealed partial class SettingsViewModel : ObservableObject, IPage
{
    private readonly MainWindowViewModel _main;
    private readonly IAppPaths _paths;
    private readonly BackupService _backups;

    public SettingsViewModel(MainWindowViewModel main, IAppPaths paths, BackupService backups)
    {
        _main = main;
        _paths = paths;
        _backups = backups;
    }

    public string Title => "Settings";
    public string DataFolder => _paths.Root;
    public string StorageText => _paths.Mode switch
    {
        StorageMode.Portable => "Portable: data is stored in the data folder of this copy of the app, so each copy has its own rosters. Updates never touch it.",
        StorageMode.Explicit => "Data is stored in the folder given with --data.",
        _ => "Data is stored in your user profile because the application folder is not writable.",
    };
    public string DatabasePath => _paths.DatabasePath;

    [ObservableProperty]
    private string _gameVersion = "";

    [ObservableProperty]
    private string _gameDataStatus = "";

    [ObservableProperty]
    private IReadOnlyList<string> _warnings = [];

    [ObservableProperty]
    private string _backupStatus = "";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasWarnings => Warnings.Count > 0;
    partial void OnWarningsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasWarnings));

    // ── App version & updates ──────────────────────────────────────────────────────────────────────

    public string AppVersion => _main.Updates.CurrentVersion;
    public string ReleasesUrl => Services.AppUpdateService.RepositoryUrl + "/releases";
    public bool UpdatesSupported => _main.Updates.IsSupported;
    public bool IsUpdateReady => _main.Updates.State == Services.AppUpdateState.ReadyToRestart;
    public bool CanCheckForUpdates => UpdatesSupported && _main.Updates.State is not (Services.AppUpdateState.Checking or Services.AppUpdateState.Downloading or Services.AppUpdateState.ReadyToRestart);

    public string UpdateStatus
    {
        get
        {
            var u = _main.Updates;
            var checkedAt = u.LastChecked is { } t ? $" (checked {t:t})" : "";
            return u.State switch
            {
                Services.AppUpdateState.NotSupported => "Development build — updates are only available in releases downloaded from GitHub.",
                Services.AppUpdateState.Checking => "Checking GitHub for a new version…",
                Services.AppUpdateState.Downloading => $"Downloading version {u.AvailableVersion}… {u.DownloadProgress}%",
                Services.AppUpdateState.ReadyToRestart => $"Version {u.AvailableVersion} is downloaded and ready. Restart to update — your data is kept.",
                Services.AppUpdateState.UpToDate => "You have the latest version" + checkedAt + ".",
                Services.AppUpdateState.Failed => "Couldn't check for updates" + checkedAt + ". It will try again later.",
                _ => "Checks for updates at startup and every 4 hours.",
            };
        }
    }

    public void RefreshUpdateStatus()
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(CanCheckForUpdates));
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => _main.Updates.CheckAndDownloadAsync();

    [RelayCommand]
    private void OpenReleases()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ReleasesUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open {Url}", ReleasesUrl);
        }
    }

    public Task OnNavigatedToAsync()
    {
        RefreshUpdateStatus();
        var status = _main.GameData.Status;
        GameVersion = status?.Version ?? "—";
        GameDataStatus = status is null ? "" : $"{(status.Source == GameDataSource.Online ? "Checked online" : "Loaded from cache (offline)")} at {status.LoadedAt:t}";
        Warnings = status?.Warnings ?? [];
        var latest = Directory.Exists(_paths.BackupDirectory)
            ? Directory.EnumerateFiles(_paths.BackupDirectory, "*.json").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
            : null;
        BackupStatus = latest is null ? "No backups yet." : $"Latest backup: {latest.Name} ({latest.LastWriteTime:g})";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_paths.Root);

    [RelayCommand]
    private void OpenBackupsFolder() => OpenFolder(_paths.BackupDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open {Path}", path);
        }
    }

    [RelayCommand]
    private async Task RefreshGameDataAsync()
    {
        await _main.ReloadGameDataAsync(force: true);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = await _main.PickSaveFileAsync("Export roster backup", $"roster-control-{DateTime.Now:yyyyMMdd}.json");
        if (path is null) return;
        IsBusy = true;
        try
        {
            await _main.Saves.FlushAsync();
            await _backups.ExportToFileAsync(path, _main.Game.Version);
            _main.ShowToast($"Exported to {Path.GetFileName(path)}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Export failed");
            _main.ShowToast("Export failed: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            await OnNavigatedToAsync();
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await _main.PickOpenFileAsync("Import roster backup");
        if (path is null) return;
        RosterBackup backup;
        try
        {
            await using var fs = File.OpenRead(path);
            backup = await BackupService.ReadAsync(fs);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _main.ShowToast(ex.Message, ToastKind.Error);
            return;
        }
        var champions = backup.Groups.Sum(g => g.Entries.Count);
        var ok = await _main.ConfirmAsync("Replace all data?",
            $"\"{Path.GetFileName(path)}\" contains {backup.Groups.Count} groups, {champions} roster placements, {backup.Builds.Count} builds and {backup.Matchups.Count} matchups (exported {backup.ExportedAt:g}).\n\nYour current data will be replaced. A safety backup is saved first.",
            "Replace and import");
        if (!ok) return;
        IsBusy = true;
        try
        {
            await _main.Saves.FlushAsync();
            await _backups.ImportAsync(backup, _main.Game.Version);
            await _main.ResetPagesAsync();
            _main.ShowToast("Backup imported", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Import failed");
            _main.ShowToast("Import failed: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            await OnNavigatedToAsync();
        }
    }
}
