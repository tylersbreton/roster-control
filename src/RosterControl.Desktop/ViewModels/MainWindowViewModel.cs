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
using RosterControl.Core.Model;
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
    // New sections go last: the section is saved by number.
    Items,
    Runes,
    Stats,
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
        _quickLookHotkeyEnabled = preferences.Get(QuickLookHotkeyEnabledKey, true);
        _quickLookHotkey = GlobalHotkey.TryParse(preferences.Get(QuickLookHotkeyKey, DefaultQuickLookHotkey), out var savedHotkey)
            ? GlobalHotkey.Format(savedHotkey)
            : DefaultQuickLookHotkey;
        // Read before startup navigation writes it: any earlier session saved the page it was on.
        _returningUser = preferences.Get<Section?>("ui.section", null) is not null;
        _updates.Changed += (_, _) => Dispatcher.UIThread.Post(OnUpdateStateChanged);
        _sidebarCollapsed = preferences.Get("ui.sidebarCollapsed", false);
        _uiScale = Math.Clamp(preferences.Get(ScaleKey, 1.0), ScaleSteps[0], ScaleSteps[^1]);
        _recentChampions.AddRange(preferences.Get<List<string>>("ui.recentChampions", []).Take(6));
        LoadTheme();
        Palette = new CommandPaletteViewModel(this);
        Review = new PatchReviewState(OpenPatchReview);
    }

    /// <summary>Raised when every page is rebuilt (patch update, import) so cached views can be dropped.</summary>
    public event EventHandler? PagesReset;

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
    [NotifyPropertyChangedFor(nameof(IsRostersSection), nameof(IsChampionsSection), nameof(IsItemsSection), nameof(IsRunesSection), nameof(IsStatsSection), nameof(IsCountersSection), nameof(IsSettingsSection))]
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
    public bool IsItemsSection => CurrentSection == Section.Items;
    public bool IsRunesSection => CurrentSection == Section.Runes;
    public bool IsStatsSection => CurrentSection == Section.Stats;
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
    public ItemsViewModel? Items { get; private set; }
    public RunesViewModel? Runes { get; private set; }
    public ChampionStatsViewModel? Stats { get; private set; }
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
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(extension == "png" ? WellKnownFolder.Pictures : WellKnownFolder.Documents),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title, string extension, string typeName)
    {
        if (_topLevel?.Invoke()?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] }],
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
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
        Items = new ItemsViewModel(this);
        Runes = new RunesViewModel(this);
        Stats = new ChampionStatsViewModel(this);
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
            Section.Items => Items,
            Section.Runes => Runes,
            Section.Stats => Stats,
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

    /// <summary>Opens the Runes page with <paramref name="runeId"/> selected (from search).</summary>
    public async Task OpenRuneAsync(int runeId)
    {
        await NavigateAsync(Section.Runes);
        Runes?.Show(runeId);
    }

    /// <summary>Opens the Items page with <paramref name="itemId"/> selected (from search).</summary>
    public async Task OpenItemAsync(string itemId)
    {
        await NavigateAsync(Section.Items);
        Items?.Show(itemId);
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
                case Key.D5 or Key.NumPad5:
                    _ = NavigateAsync(Section.Items);
                    return true;
                case Key.D6 or Key.NumPad6:
                    _ = NavigateAsync(Section.Runes);
                    return true;
                case Key.D7 or Key.NumPad7:
                    _ = NavigateAsync(Section.Stats);
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
}
