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
        Browser.ExtraFiltersActive = () => OnlyRoster || OnlyWithBuilds || SelectedTag != "Any tag";
        Browser.ResetExtraFilters = () =>
        {
            OnlyRoster = false;
            OnlyWithBuilds = false;
            SelectedTag = "Any tag";
        };
        _onlyRoster = shell.Preferences.Get("library.onlyRoster", false);
        _onlyWithBuilds = shell.Preferences.Get("library.onlyWithBuilds", false);
        _selectedTag = shell.Preferences.Get("library.tag", "Any tag");
        Browser.Remember(shell.Preferences, "library.browser");
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

    partial void OnOnlyRosterChanged(bool value)
    {
        _shell.Preferences.Set("library.onlyRoster", value);
        Browser.Refilter();
    }

    partial void OnOnlyWithBuildsChanged(bool value)
    {
        _shell.Preferences.Set("library.onlyWithBuilds", value);
        Browser.Refilter();
    }

    partial void OnSelectedTagChanged(string value)
    {
        if (value is not null) _shell.Preferences.Set("library.tag", value);
        Browser.Refilter();
    }

    /// <summary>Enter in the search box: open the best match.</summary>
    [RelayCommand]
    private void OpenFirst()
    {
        if (Browser.FirstTile is { } tile) Open(tile);
    }
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
    private void Open(ChampionTileViewModel tile) => _shell.OpenChampion(tile.Id, null, Browser.Sequence("Champions"));
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
    public const string SponsorUrl = "https://github.com/sponsors/tylersbreton";
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
                Services.AppUpdateState.Failed => $"Couldn't check for updates{checkedAt}: {u.LastError} It will try again later.",
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
    private void OpenReleases() => OpenUrl(ReleasesUrl);

    [RelayCommand]
    private void OpenSponsor() => OpenUrl(SponsorUrl);

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open {Url}", url);
        }
    }

    public void RefreshGameDataStatus()
    {
        var status = _main.GameData.Status;
        GameVersion = status?.Version ?? "—";
        var check = _main.GameData.LastPatchCheck is { } t ? $"Patch list checked online at {t:t}." : "Not checked online yet this session.";
        GameDataStatus = status is null ? "" : status.Source switch
        {
            GameDataSource.Online => $"Up to date. {check}",
            GameDataSource.StartupCache => $"Opened from the local cache for a fast start. {check}",
            _ => $"Offline: using the local cache. {check}",
        };
        Warnings = status?.Warnings ?? [];
    }

    // ── Display ────────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<ScaleOption> ScaleOptions { get; } = MainWindowViewModel.ScaleSteps.Select(s => new ScaleOption(s)).ToList();

    public ScaleOption SelectedScale
    {
        get => ScaleOptions.OrderBy(o => Math.Abs(o.Scale - _main.UiScale)).First();
        set
        {
            if (value is null) return;
            _main.UiScale = value.Scale;
            OnPropertyChanged();
        }
    }

    // ── Cache ──────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _cacheText = "Calculating…";

    private async Task RefreshCacheSizeAsync()
    {
        var cache = _paths.CacheDirectory;
        var (images, data) = await Task.Run(() => (FolderSize(Path.Combine(cache, "images")), FolderSize(Path.Combine(cache, "data"))));
        CacheText = $"Images {FormatSize(images)} · Game data {FormatSize(data)} (old patches are removed automatically)";
    }

    private static long FolderSize(string path)
    {
        try
        {
            return Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    [RelayCommand]
    private async Task ClearImageCacheAsync()
    {
        if (!await _main.ConfirmAsync("Clear cached images?", "Champion, item and rune images will be downloaded again as you use the app. Your rosters and builds are not affected.", "Clear images", danger: false))
            return;
        var folder = Path.Combine(_paths.CacheDirectory, "images");
        await Task.Run(() =>
        {
            if (!Directory.Exists(folder)) return;
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Serilog.Log.Debug("Could not delete {Dir}: {Message}", dir, ex.Message); }
            }
        });
        await RefreshCacheSizeAsync();
        _main.ShowToast("Image cache cleared", ToastKind.Success);
    }

    public Task OnNavigatedToAsync()
    {
        RefreshUpdateStatus();
        RefreshGameDataStatus();
        OnPropertyChanged(nameof(SelectedScale));
        _ = RefreshCacheSizeAsync();
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

public sealed record ScaleOption(double Scale)
{
    public string Label => Scale == 1.0 ? "100% (default)" : $"{Scale * 100:0}%";
    public override string ToString() => Label;
}
