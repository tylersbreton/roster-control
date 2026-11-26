using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
    Compare,
    Review,
}

/// <summary>Application shell: loading, navigation, keyboard shortcuts, search palette, dialogs and toasts.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IShell
{
    private const string ChampionTabKey = "ui.championTab";
    private const string ScaleKey = "ui.scale";
    public static readonly IReadOnlyList<double> ScaleSteps = [0.8, 0.9, 1.0, 1.1, 1.25, 1.4, 1.5];

    private readonly IGameDataService _gameData;
    private readonly RosterRepository _repository;
    private readonly SaveCoordinator _saves;
    private readonly BackupService _backups;
    private readonly PreferencesStore _preferences;
    private readonly IAppPaths _paths;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Stack<(Section Section, object Page)> _history = new();
    private readonly List<string> _recentChampions = [];
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
        _saves.SavingChanged += (_, saving) => Dispatcher.UIThread.Post(() =>
        {
            IsSaving = saving;
            if (!saving) FlashSaved();
        });
        _repository.ActiveRosterId = preferences.Get<int?>(ActiveRosterKey, null);
        _repository.Written += (_, _) => Dispatcher.UIThread.Post(FlashSaved);
        // Both off by default (user's choice): animations on, and startup always opens Rosters.
        _reduceMotion = preferences.Get(ReduceMotionKey, false);
        _reopenLast = preferences.Get(ReopenLastKey, false);
        // Read before startup navigation writes it: any earlier session saved the page it was on.
        _returningUser = preferences.Get<Section?>("ui.section", null) is not null;
        _updates.Changed += (_, _) => Dispatcher.UIThread.Post(OnUpdateStateChanged);
        _sidebarCollapsed = preferences.Get("ui.sidebarCollapsed", false);
        _uiScale = Math.Clamp(preferences.Get(ScaleKey, 1.0), ScaleSteps[0], ScaleSteps[^1]);
        _recentChampions.AddRange(preferences.Get<List<string>>("ui.recentChampions", []).Take(6));
        Palette = new CommandPaletteViewModel(this);
        Review = new PatchReviewState(OpenPatchReview);
    }

    /// <summary>Raised when every page is rebuilt (patch update, import) so cached views can be dropped.</summary>
    public event EventHandler? PagesReset;

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
    public CommandPaletteViewModel Palette { get; }
    public IReadOnlyList<string> RecentChampions => _recentChampions;

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

    /// <summary>Interface zoom (separate from Windows display scaling).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleTransform), nameof(ScaleText))]
    private double _uiScale;

    public ITransform ScaleTransform => new ScaleTransform(UiScale, UiScale);
    public string ScaleText => $"{UiScale * 100:0}%";

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public bool HasError => LoadError is not null;
    public double SidebarWidth => SidebarCollapsed ? 64 : 212;
    public bool IsRostersSection => CurrentSection == Section.Rosters;
    public bool IsChampionsSection => CurrentSection is Section.Champions;
    public bool IsCountersSection => CurrentSection == Section.Counters;
    public bool IsSettingsSection => CurrentSection == Section.Settings;
    public string PatchText => _game is null ? "" : $"Patch data {_game.Version}";

    partial void OnUiScaleChanged(double value) => _preferences.Set(ScaleKey, value);

    [RelayCommand]
    private void ZoomIn() => UiScale = ScaleSteps.FirstOrDefault(s => s > UiScale + 0.001, ScaleSteps[^1]);

    [RelayCommand]
    private void ZoomOut() => UiScale = ScaleSteps.LastOrDefault(s => s < UiScale - 0.001, ScaleSteps[0]);

    [RelayCommand]
    private void ResetZoom() => UiScale = 1.0;

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
        Settings?.RefreshGameDataStatus();
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

    // ── Saved indicator ────────────────────────────────────────────────────────────────────────────

    private DispatcherTimer? _savedTimer;

    /// <summary>"Saved ✓" shows briefly after each change reaches the database.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSaved))]
    private bool _isSavedVisible;

    public bool ShowSaved => IsSavedVisible && !IsSaving;

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(ShowSaved));

    private void FlashSaved()
    {
        if (IsLoading) return;
        IsSavedVisible = true;
        _savedTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.8), DispatcherPriority.Background, (_, _) =>
        {
            IsSavedVisible = false;
            _savedTimer?.Stop();
        });
        _savedTimer.Stop();
        _savedTimer.Start();
    }

    // ── Display options ────────────────────────────────────────────────────────────────────────────

    private const string ReduceMotionKey = "ui.reduceMotion";
    private const string ReopenLastKey = "ui.reopenLast";
    private const string LastChampionKey = "ui.lastChampion";
    private const string ActiveRosterKey = "roster.active";
    private const string LastSeenVersionKey = "app.lastSeenVersion";
    private readonly bool _returningUser;

    /// <summary>No transitions and no background glow (off by default).</summary>
    [ObservableProperty]
    private bool _reduceMotion;

    partial void OnReduceMotionChanged(bool value) => _preferences.Set(ReduceMotionKey, value);

    /// <summary>Reopen the last page (including the champion that was open) at startup; off = always start on Rosters.</summary>
    [ObservableProperty]
    private bool _reopenLast;

    partial void OnReopenLastChanged(bool value) => _preferences.Set(ReopenLastKey, value);

    private sealed record LastChampion(string Id, ChampionTab Tab);

    // ── Rosters, compare, files ────────────────────────────────────────────────────────────────────

    public async Task SwitchRosterAsync(int rosterId)
    {
        await _saves.FlushAsync();
        _repository.ActiveRosterId = rosterId;
        _preferences.Set(ActiveRosterKey, rosterId);
        MarkDirty();
        if (Rosters is { } rosters)
        {
            rosters.ClearSelection();
            await rosters.ReloadAsync();
        }
        if (CurrentSection != Section.Rosters) await NavigateAsync(Section.Rosters);
    }

    // ── Patch review ───────────────────────────────────────────────────────────────────────────────

    public PatchReviewState Review { get; }
    private int _reviewScan;

    public async Task RefreshPatchReviewAsync()
    {
        if (_game is null) return;
        var scan = ++_reviewScan;
        try
        {
            await _saves.FlushAsync();
            var entries = await PatchReviewState.ScanAsync(this, CancellationToken.None);
            if (scan != _reviewScan) return; // a newer scan started meanwhile
            Review.Patch = _game.Version;
            Review.Entries = entries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patch review scan failed");
        }
    }

    public void OpenPatchReview() => _ = OpenPatchReviewAsync();

    private async Task OpenPatchReviewAsync()
    {
        if (_game is null) return;
        await _saves.FlushAsync();
        if (CurrentPage is not null && CurrentSection != Section.Review) _history.Push((CurrentSection, CurrentPage));
        CanGoBack = _history.Count > 0;
        var page = new PatchReviewViewModel(this);
        CurrentSection = Section.Review;
        CurrentPage = page;
        PageTitle = page.Title;
        await page.OnNavigatedToAsync();
    }

    /// <summary>Pages opened on top of a section (not restored at startup, not remembered).</summary>
    private static bool IsTransient(Section section) => section is Section.Champion or Section.Compare or Section.Review;

    public void OpenCompare(string? leftChampionId, string? rightChampionId) => _ = OpenCompareAsync(leftChampionId, rightChampionId);

    private async Task OpenCompareAsync(string? left, string? right)
    {
        if (_game is null) return;
        await _saves.FlushAsync();
        if (CurrentPage is not null) _history.Push((CurrentSection, CurrentPage));
        CanGoBack = _history.Count > 0;
        var page = new CompareViewModel(this, left, right);
        CurrentSection = Section.Compare;
        CurrentPage = page;
        PageTitle = page.Title;
        await page.OnNavigatedToAsync();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string typeName)
    {
        if (_topLevel?.Invoke()?.StorageProvider is not { } storage) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] }],
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures),
        });
        return file?.TryGetLocalPath();
    }

    // ── What's new ─────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private WhatsNewViewModel? _whatsNew;

    private static IReadOnlyList<Core.Rules.ChangelogEntry> LoadChangelog()
    {
        using var stream = typeof(MainWindowViewModel).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return [];
        using var reader = new StreamReader(stream);
        return Core.Rules.Changelog.Parse(reader.ReadToEnd());
    }

    /// <summary>After an update, shows what changed since the version last run (nothing on the first run).</summary>
    private void ShowWhatsNewAfterUpdate()
    {
        if (!Core.Rules.Changelog.TryParseVersion(_updates.CurrentVersion, out var current)) return;
        // Versions before 1.3 didn't record this: an existing install without it is coming from 1.2.
        var lastSeenText = _preferences.Get<string?>(LastSeenVersionKey, null) ?? (_returningUser ? "1.2.0" : null);
        _preferences.Set(LastSeenVersionKey, current.ToString(3));
        if (!Core.Rules.Changelog.TryParseVersion(lastSeenText, out var lastSeen) || lastSeen >= current) return;
        var entries = Core.Rules.Changelog.Between(LoadChangelog(), lastSeen, current);
        if (entries.Count > 0) WhatsNew = new WhatsNewViewModel(this, $"What's new in Roster Control {current.ToString(3)}", entries);
    }

    [RelayCommand]
    public void ShowWhatsNew()
    {
        var entries = LoadChangelog().Take(3).ToList();
        if (entries.Count > 0) WhatsNew = new WhatsNewViewModel(this, "What's new", entries);
    }

    // ── Startup ────────────────────────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            LoadingText = "Opening your rosters…";
            await _repository.InitializeAsync();
            if (_repository.DatabaseFromNewerVersion)
                ShowToast("This data was last used by a newer version of Roster Control. Update this copy (Settings → App updates) so nothing goes missing.", ToastKind.Warning);
            var progress = new Progress<string>(t => LoadingText = t);
            // Fast start: open with cached game data and check for a new patch in the background.
            var cached = await _gameData.LoadCachedAsync(progress, CancellationToken.None);
            _game = cached ?? await _gameData.LoadAsync(false, progress, CancellationToken.None);
            OnPropertyChanged(nameof(PatchText));
            CreatePages();
            IsLoading = false;
            await RestoreLastPageAsync();
            ShowWhatsNewAfterUpdate();
            _ = Task.Run(() => _backups.RunAutomaticBackupAsync(_game.Version));
            foreach (var warning in _gameData.Status?.Warnings ?? []) ShowToast(warning, ToastKind.Warning);
            if (_game.NewChampionIds.Count > 0)
                ShowToast($"New this patch: {string.Join(", ", _game.NewChampionIds.Select(id => _game.Champion(id)?.Name ?? id))}", ToastKind.Success);
            if (cached is not null) DispatcherTimer.RunOnce(() => _ = CheckForNewPatchAsync(), TimeSpan.FromSeconds(1.5));
            StartPatchWatcher();
            StartUpdateChecks();
            DispatcherTimer.RunOnce(() => _ = RefreshPatchReviewAsync(), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup failed");
            LoadError = ex is GameDataUnavailableException ? ex.Message : "Something went wrong while starting: " + ex.Message;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => InitializeAsync();

    private async Task RestoreLastPageAsync()
    {
        var section = ReopenLast ? _preferences.Get("ui.section", Section.Rosters) : Section.Rosters;
        if (section == Section.Champion && _preferences.Get<LastChampion?>(LastChampionKey, null) is { } last && _game?.Champion(last.Id) is not null)
        {
            await NavigateAsync(Section.Rosters);
            await OpenChampionAsync(last.Id, last.Tab, null, replace: false);
            return;
        }
        await NavigateAsync(IsTransient(section) ? Section.Rosters : section);
    }

    private void CreatePages()
    {
        Rosters = new RostersViewModel(this);
        Library = new LibraryViewModel(this);
        Counters = new CounterPicksViewModel(this);
        Settings = new SettingsViewModel(this, _paths, _backups);
        _history.Clear();
        CanGoBack = false;
        PagesReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-downloads game data (Settings → Refresh, or a new patch) and rebuilds every page.</summary>
    public async Task ReloadGameDataAsync(bool force)
    {
        await _saves.FlushAsync();
        var section = IsTransient(CurrentSection) ? Section.Rosters : CurrentSection;
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
            Review.HiddenThisSession = false;
            _ = RefreshPatchReviewAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh failed");
            IsLoading = false;
            ShowToast(ex is GameDataUnavailableException ? ex.Message : "Refresh failed: " + NetworkErrors.Describe(ex, "Data Dragon"), ToastKind.Error);
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
        if (!IsTransient(section)) _preferences.Set("ui.section", section);
        if (page is IPage p) await p.OnNavigatedToAsync();
    }

    public void OpenChampion(string championId, ChampionTab? tab = null, ChampionSequence? sequence = null, int? buildId = null) =>
        _ = OpenChampionAsync(championId, tab, sequence, replace: false, buildId);

    /// <summary>Previous/next within a sequence: replaces the current champion page (Back still returns to the list).</summary>
    public void ShowChampionInSequence(string championId, ChampionTab tab, ChampionSequence sequence) =>
        _ = OpenChampionAsync(championId, tab, sequence, replace: true);

    private async Task OpenChampionAsync(string championId, ChampionTab? tab, ChampionSequence? sequence, bool replace, int? buildId = null)
    {
        if (_game?.Champion(championId) is not { } champion) return;
        await _saves.FlushAsync();
        if (!replace || CurrentSection != Section.Champion)
        {
            if (CurrentPage is not null) _history.Push((CurrentSection, CurrentPage));
        }
        CanGoBack = _history.Count > 0;
        var page = new ChampionDetailViewModel(this, champion, tab ?? _preferences.Get(ChampionTabKey, ChampionTab.Builds), sequence, buildId);
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChampionDetailViewModel.SelectedTab)) return;
            _preferences.Set(ChampionTabKey, (ChampionTab)page.SelectedTab);
            if (ReferenceEquals(CurrentPage, page)) _preferences.Set(LastChampionKey, new LastChampion(page.Champion.Id, (ChampionTab)page.SelectedTab));
        };
        _preferences.Set("ui.section", Section.Champion);
        _preferences.Set(LastChampionKey, new LastChampion(champion.Id, (ChampionTab)page.SelectedTab));
        CurrentSection = Section.Champion;
        CurrentPage = page;
        PageTitle = champion.Name;
        _recentChampions.RemoveAll(id => string.Equals(id, champion.Id, StringComparison.OrdinalIgnoreCase));
        _recentChampions.Insert(0, champion.Id);
        if (_recentChampions.Count > 6) _recentChampions.RemoveRange(6, _recentChampions.Count - 6);
        _preferences.Set("ui.recentChampions", _recentChampions);
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

    [RelayCommand]
    private void OpenPalette()
    {
        if (!IsLoading) Palette.Open();
    }

    // ── Keyboard shortcuts ─────────────────────────────────────────────────────────────────────────

    /// <summary>Global shortcuts. <paramref name="typing"/> is true while a text box has focus.</summary>
    public bool HandleShortcut(Key key, KeyModifiers modifiers, bool typing)
    {
        if (IsLoading || Dialog is not null) return false;
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && key is Key.K or Key.P)
        {
            Palette.Open();
            return true;
        }
        if (Palette.IsOpen) return false;
        if (ctrl && !shift)
        {
            switch (key)
            {
                case Key.D1 or Key.NumPad1:
                    _ = NavigateAsync(Section.Rosters);
                    return true;
                case Key.D2 or Key.NumPad2:
                    _ = NavigateAsync(Section.Champions);
                    return true;
                case Key.D3 or Key.NumPad3:
                    _ = NavigateAsync(Section.Counters);
                    return true;
                case Key.D4 or Key.NumPad4:
                    _ = NavigateAsync(Section.Settings);
                    return true;
                case Key.OemPlus or Key.Add:
                    ZoomIn();
                    return true;
                case Key.OemMinus or Key.Subtract:
                    ZoomOut();
                    return true;
                case Key.D0 or Key.NumPad0:
                    ResetZoom();
                    return true;
                case Key.Z when !typing:
                    return UndoLast();
            }
        }
        if (modifiers == KeyModifiers.Alt && key == Key.Left)
        {
            GoBackCommand.Execute(null);
            return true;
        }
        return CurrentPage is IKeyboardPage page && page.HandleKey(key, modifiers, typing);
    }

    // ── Dialogs & toasts ───────────────────────────────────────────────────────────────────────────

    public async Task<bool> CopyTextAsync(string text)
    {
        if (_topLevel?.Invoke()?.Clipboard is not { } clipboard) return false;
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard write failed");
            return false;
        }
    }

    private async Task<string?> ReadTextAsync()
    {
        if (_topLevel?.Invoke()?.Clipboard is not { } clipboard) return null;
        try
        {
            return await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard read failed");
            return null;
        }
    }

    public async Task<Core.Model.Build?> ImportSharedBuildAsync(string? targetChampionId)
    {
        var text = await ReadTextAsync();
        if (!Core.Rules.BuildShareCode.TryDecode(text, out var build, out var error))
        {
            ShowToast(string.IsNullOrWhiteSpace(text) ? "Copy a build code (it starts with RC1:) first, then paste it here." : error!, ToastKind.Warning);
            return null;
        }
        if (_game?.Champion(build.ChampionId) is not { } champion)
        {
            ShowToast($"That build is for a champion that isn't in patch {_game?.Version} yet.", ToastKind.Warning);
            return null;
        }
        build.ChampionId = champion.Id;
        if (targetChampionId is not null && !string.Equals(targetChampionId, champion.Id, StringComparison.OrdinalIgnoreCase))
        {
            var here = _game.Champion(targetChampionId)?.Name ?? targetChampionId;
            if (!await ConfirmAsync($"Add to {champion.Name}?", $"This code is a {champion.Name} build, not a {here} build. It will be added to {champion.Name}'s builds.", $"Add to {champion.Name}", danger: false))
                return null;
        }
        build.GameVersion ??= _game.Version;
        build.Id = await _repository.SaveBuildAsync(build);
        MarkDirty();
        return build;
    }

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

    public void ShowToast(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Func<Task>? action = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastViewModel(message, kind, actionText, action);
            Toasts.Add(toast);
            while (Toasts.Count > 3) Toasts.RemoveAt(0);
            var seconds = action is not null ? 8 : kind is ToastKind.Error or ToastKind.Warning ? 6 : 3.2;
            DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromSeconds(seconds));
        });
    }

    [RelayCommand]
    private void DismissToast(ToastViewModel toast) => Toasts.Remove(toast);

    [RelayCommand]
    private async Task RunToastActionAsync(ToastViewModel toast)
    {
        Toasts.Remove(toast);
        if (toast.Action is null) return;
        try
        {
            await toast.Action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toast action failed");
            ShowToast("That couldn't be undone: " + ex.Message, ToastKind.Error);
        }
    }

    /// <summary>Ctrl+Z: runs the newest Undo toast (other toast actions, such as Open, are left alone).</summary>
    public bool UndoLast()
    {
        var toast = Toasts.LastOrDefault(t => t.HasAction && t.ActionText == "Undo");
        if (toast is null) return false;
        RunToastActionCommand.Execute(toast);
        return true;
    }

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

/// <summary>The "What's new" dialog (after an update, or from Settings → About).</summary>
public sealed partial class WhatsNewViewModel(MainWindowViewModel owner, string title, IReadOnlyList<Core.Rules.ChangelogEntry> entries) : ObservableObject
{
    public string Title { get; } = title;
    public IReadOnlyList<WhatsNewEntry> Entries { get; } = entries.Select(e => new WhatsNewEntry(e.Version.ToString(3), e.Body)).ToList();

    [RelayCommand]
    private void Close() => owner.WhatsNew = null;
}

public sealed record WhatsNewEntry(string Version, string Body)
{
    public string Heading => "Version " + Version;
}

