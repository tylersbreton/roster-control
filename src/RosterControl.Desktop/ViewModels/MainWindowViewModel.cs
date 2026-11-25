using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RosterControl.Core.Abstractions;
using RosterControl.Core.GameData;
using RosterControl.Data;
using RosterControl.Desktop.Services;
using RosterControl.Integrations;

namespace RosterControl.Desktop.ViewModels;

public enum Section
{
    Rosters,
    Champions,
    Counters,
    Settings,
    Champion,
}

/// <summary>Application shell: loading, navigation, dialogs and toasts.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IShell
{
    private readonly IGameDataService _gameData;
    private readonly RosterRepository _repository;
    private readonly SaveCoordinator _saves;
    private readonly BackupService _backups;
    private readonly PreferencesStore _preferences;
    private readonly IAppPaths _paths;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Stack<(Section Section, object Page)> _history = new();
    private GameDataSnapshot? _game;
    private Func<TopLevel?>? _topLevel;

    public MainWindowViewModel(IGameDataService gameData, RosterRepository repository, SaveCoordinator saves, BackupService backups,
        PreferencesStore preferences, IAppPaths paths, AppUpdateService updates, ILogger<MainWindowViewModel> logger)
    {
        _gameData = gameData;
        _repository = repository;
        _saves = saves;
        _backups = backups;
        _preferences = preferences;
        _paths = paths;
        _updates = updates;
        _logger = logger;
        _saves.SavingChanged += (_, saving) => Dispatcher.UIThread.Post(() => IsSaving = saving);
        _updates.Changed += (_, _) => Dispatcher.UIThread.Post(OnUpdateStateChanged);
        _sidebarCollapsed = preferences.Get("ui.sidebarCollapsed", false);
    }

    // ── App updates (GitHub releases) ──────────────────────────────────────────────────────────────

    private readonly AppUpdateService _updates;
    private DispatcherTimer? _updateTimer;

    public AppUpdateService Updates => _updates;

    /// <summary>A downloaded app update is ready; applied when the user clicks "Restart to update".</summary>
    [ObservableProperty]
    private bool _isAppUpdateReady;

    [ObservableProperty]
    private string _appUpdateText = "";

    private void OnUpdateStateChanged()
    {
        var ready = _updates.State == AppUpdateState.ReadyToRestart;
        if (ready && !IsAppUpdateReady) ShowToast($"Roster Control {_updates.AvailableVersion} is ready — click “Restart to update” when convenient.", ToastKind.Success);
        IsAppUpdateReady = ready;
        AppUpdateText = ready ? $"Version {_updates.AvailableVersion} ready" : "";
        Settings?.RefreshUpdateStatus();
    }

    private void StartUpdateChecks()
    {
        if (!_updates.IsSupported || _updateTimer is not null) return;
        _updateTimer = new DispatcherTimer(AppUpdateService.CheckInterval, DispatcherPriority.Background, (_, _) => _ = _updates.CheckAndDownloadAsync());
        _updateTimer.Start();
        // First check shortly after startup so it doesn't compete with loading game data.
        DispatcherTimer.RunOnce(() => _ = _updates.CheckAndDownloadAsync(), TimeSpan.FromSeconds(10));
    }

    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (Dialog is not null) return;
        await _saves.FlushAsync();
        await _preferences.FlushAsync(CancellationToken.None);
        _updates.RestartToUpdate();
    }

    // ── IShell ─────────────────────────────────────────────────────────────────────────────────────

    public GameDataSnapshot Game => _game ?? throw new InvalidOperationException("Game data not loaded.");
    public RosterRepository Repository => _repository;
    public SaveCoordinator Saves => _saves;
    public IGameDataService GameData => _gameData;
    public PreferencesStore Preferences => _preferences;

    public void MarkDirty()
    {
        // Pages refresh their derived data (counts, memberships) every time they are shown.
    }

    // ── State ──────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _loadingText = "Starting…";

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRostersSection), nameof(IsChampionsSection), nameof(IsCountersSection), nameof(IsSettingsSection))]
    private Section _currentSection;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private ConfirmDialogViewModel? _dialog;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _sidebarCollapsed;

    [ObservableProperty]
    private string _pageTitle = "";

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public bool HasError => LoadError is not null;
    public double SidebarWidth => SidebarCollapsed ? 64 : 212;
    public bool IsRostersSection => CurrentSection == Section.Rosters;
    public bool IsChampionsSection => CurrentSection is Section.Champions;
    public bool IsCountersSection => CurrentSection == Section.Counters;
    public bool IsSettingsSection => CurrentSection == Section.Settings;
    public string PatchText => _game is null ? "" : $"Patch data {_game.Version}";

    /// <summary>A newer patch published while the app is running (offered as an "Update" button, never forced mid-edit).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewPatch), nameof(NewPatchText))]
    private string? _newPatchVersion;

    public bool HasNewPatch => NewPatchVersion is not null;
    public string NewPatchText => NewPatchVersion is null ? "" : $"Patch {NewPatchVersion} is out";

    /// <summary>How often to look for a new patch while running (more often when the last load was offline).</summary>
    private static readonly TimeSpan PatchCheckInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan OfflinePatchCheckInterval = TimeSpan.FromMinutes(10);
    private DispatcherTimer? _patchTimer;

    private void StartPatchWatcher()
    {
        _patchTimer?.Stop();
        var offline = _gameData.Status?.Source == GameDataSource.Cache;
        _patchTimer = new DispatcherTimer(offline ? OfflinePatchCheckInterval : PatchCheckInterval, DispatcherPriority.Background, async (_, _) => await CheckForNewPatchAsync());
        _patchTimer.Start();
    }

    public async Task CheckForNewPatchAsync()
    {
        if (_game is null || IsLoading) return;
        var newer = await _gameData.CheckForNewPatchAsync(CancellationToken.None);
        if (newer is null || newer == NewPatchVersion) return;
        _logger.LogInformation("New patch {Version} available", newer);
        NewPatchVersion = newer;
        ShowToast($"Patch {newer} is out — click “Update” in the title bar to load the new champions, items and runes.", ToastKind.Info);
    }

    [RelayCommand]
    private Task UpdatePatchAsync() => ReloadGameDataAsync(force: false);

    public RostersViewModel? Rosters { get; private set; }
    public LibraryViewModel? Library { get; private set; }
    public CounterPicksViewModel? Counters { get; private set; }
    public SettingsViewModel? Settings { get; private set; }

    partial void OnLoadErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnSidebarCollapsedChanged(bool value) => _preferences.Set("ui.sidebarCollapsed", value);

    public void AttachTopLevel(Func<TopLevel?> topLevel) => _topLevel = topLevel;

    // ── Startup ────────────────────────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            LoadingText = "Opening your rosters…";
            await _repository.InitializeAsync();
            var progress = new Progress<string>(t => LoadingText = t);
            _game = await _gameData.LoadAsync(false, progress, CancellationToken.None);
            OnPropertyChanged(nameof(PatchText));
            CreatePages();
            IsLoading = false;
            await NavigateAsync(_preferences.Get("ui.section", Section.Rosters) is var s && s is Section.Champion ? Section.Rosters : s);
            _ = Task.Run(() => _backups.RunAutomaticBackupAsync(_game.Version));
            foreach (var warning in _gameData.Status?.Warnings ?? []) ShowToast(warning, ToastKind.Warning);
            if (_game.NewChampionIds.Count > 0)
                ShowToast($"New this patch: {string.Join(", ", _game.NewChampionIds.Select(id => _game.Champion(id)?.Name ?? id))}", ToastKind.Success);
            StartPatchWatcher();
            StartUpdateChecks();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup failed");
            LoadError = ex is GameDataUnavailableException ? ex.Message : "Something went wrong while starting: " + ex.Message;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => InitializeAsync();

    private void CreatePages()
    {
        Rosters = new RostersViewModel(this);
        Library = new LibraryViewModel(this);
        Counters = new CounterPicksViewModel(this);
        Settings = new SettingsViewModel(this, _paths, _backups);
        _history.Clear();
        CanGoBack = false;
    }

    /// <summary>Re-downloads game data (Settings → Refresh) and rebuilds every page.</summary>
    public async Task ReloadGameDataAsync(bool force)
    {
        await _saves.FlushAsync();
        var section = CurrentSection == Section.Champion ? Section.Rosters : CurrentSection;
        IsLoading = true;
        try
        {
            var progress = new Progress<string>(t => LoadingText = t);
            // Every reload re-checks the patch list; force also re-downloads Community Dragon / Meraki documents.
            _game = await _gameData.LoadAsync(force, progress, CancellationToken.None);
            OnPropertyChanged(nameof(PatchText));
            NewPatchVersion = null;
            CreatePages();
            IsLoading = false;
            await NavigateAsync(section);
            StartPatchWatcher();
            ShowToast($"Game data is up to date (patch {_game.Version})", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh failed");
            IsLoading = false;
            ShowToast(ex is GameDataUnavailableException ? ex.Message : "Refresh failed: " + ex.Message, ToastKind.Error);
        }
    }

    /// <summary>After an import: rebuild pages from the database.</summary>
    public async Task ResetPagesAsync()
    {
        CreatePages();
        await NavigateAsync(Section.Settings);
    }

    // ── Navigation ─────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task Navigate(Section section) => NavigateAsync(section);

    public async Task NavigateAsync(Section section, bool pushHistory = false)
    {
        if (_game is null) return;
        await _saves.FlushAsync();
        object? page = section switch
        {
            Section.Rosters => Rosters,
            Section.Champions => Library,
            Section.Counters => Counters,
            Section.Settings => Settings,
            _ => CurrentPage,
        };
        if (page is null) return;
        if (!pushHistory)
        {
            _history.Clear();
            CanGoBack = false;
        }
        CurrentSection = section;
        CurrentPage = page;
        PageTitle = (page as IPage)?.Title ?? "";
        if (section != Section.Champion) _preferences.Set("ui.section", section);
        if (page is IPage p) await p.OnNavigatedToAsync();
    }

    public void OpenChampion(string championId, ChampionTab tab = ChampionTab.Builds) => _ = OpenChampionAsync(championId, tab);

    private async Task OpenChampionAsync(string championId, ChampionTab tab)
    {
        if (_game?.Champion(championId) is not { } champion) return;
        await _saves.FlushAsync();
        if (CurrentPage is not null) _history.Push((CurrentSection, CurrentPage));
        CanGoBack = _history.Count > 0;
        var page = new ChampionDetailViewModel(this, champion, tab);
        CurrentSection = Section.Champion;
        CurrentPage = page;
        PageTitle = champion.Name;
        await page.OnNavigatedToAsync();
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (_history.Count == 0) return;
        await _saves.FlushAsync();
        var (section, page) = _history.Pop();
        CanGoBack = _history.Count > 0;
        CurrentSection = section;
        CurrentPage = page;
        PageTitle = (page as IPage)?.Title ?? "";
        if (page is IPage p) await p.OnNavigatedToAsync();
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    // ── Dialogs & toasts ───────────────────────────────────────────────────────────────────────────

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = true)
    {
        var dialog = new ConfirmDialogViewModel(title, message, confirmText, danger);
        Dialog = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            if (ReferenceEquals(Dialog, dialog)) Dialog = null;
        }
    }

    public void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastViewModel(message, kind);
            Toasts.Add(toast);
            while (Toasts.Count > 3) Toasts.RemoveAt(0);
            DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromSeconds(kind is ToastKind.Error or ToastKind.Warning ? 6 : 3.2));
        });
    }

    [RelayCommand]
    private void DismissToast(ToastViewModel toast) => Toasts.Remove(toast);

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        if (_topLevel?.Invoke()?.StorageProvider is not { } storage) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Roster Control backup") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(_paths.BackupDirectory),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title)
    {
        if (_topLevel?.Invoke()?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Roster Control backup") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(_paths.BackupDirectory),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
