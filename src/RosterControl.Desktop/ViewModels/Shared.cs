using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.Abstractions;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Data;
using RosterControl.Desktop.Controls;
using RosterControl.Desktop.Services;

namespace RosterControl.Desktop.ViewModels;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public enum ChampionTab
{
    Builds = 0,
    Matchups = 1,
    Notes = 2,
}

/// <summary>Application services and navigation available to every page.</summary>
public interface IShell
{
    GameDataSnapshot Game { get; }
    RosterRepository Repository { get; }
    SaveCoordinator Saves { get; }
    IGameDataService GameData { get; }
    PreferencesStore Preferences { get; }

    /// <param name="tab">Tab to show; null = the tab used last.</param>
    /// <param name="sequence">The list the champion was opened from, for previous/next navigation.</param>
    /// <param name="buildId">Build to select (e.g. one just added); null = the default build.</param>
    void OpenChampion(string championId, ChampionTab? tab = null, ChampionSequence? sequence = null, int? buildId = null);

    /// <summary>Previous/next within a sequence: replaces the current champion page.</summary>
    void ShowChampionInSequence(string championId, ChampionTab tab, ChampionSequence sequence);

    /// <param name="actionText">Optional button on the toast (e.g. "Undo"); Ctrl+Z runs the newest one.</param>
    void ShowToast(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Func<Task>? action = null);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = true);

    Task<bool> CopyTextAsync(string text);

    /// <summary>
    /// Reads a build share code from the clipboard and saves it. When it's for a different champion than
    /// <paramref name="targetChampionId"/>, asks first. Returns the saved build, or null (with a toast saying why).
    /// </summary>
    Task<Build?> ImportSharedBuildAsync(string? targetChampionId);
    Task NavigateAsync(Section section, bool pushHistory = false);
    /// <summary>Roster/builds/matchups changed: pages refresh their derived data when next shown.</summary>
    void MarkDirty();
}

/// <summary>Implemented by pages that refresh when navigated to.</summary>
public interface IPage
{
    string Title { get; }
    Task OnNavigatedToAsync();
}

/// <summary>Pages that react to keyboard shortcuts. <c>typing</c> is true while a text box has focus.</summary>
public interface IKeyboardPage
{
    bool HandleKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers, bool typing);
}

/// <summary>An ordered list of champions (a roster group, a filtered grid…) for previous/next on the champion page.</summary>
/// <param name="Role">The group's role, if any: the champion page opens that role's default build.</param>
public sealed record ChampionSequence(IReadOnlyList<string> ChampionIds, string Label, Position? Role = null);

/// <summary>Small helpers for remembering UI state in preferences.</summary>
public static class UiState
{
    public static T Load<T>(PreferencesStore prefs, string key, T fallback) => prefs.Get(key, fallback);
    public static void Save<T>(PreferencesStore prefs, string key, T value) => prefs.Set(key, value);
}

public sealed record ChampionDragData(ChampionInfo Champion, string ImageUrl, object? Origin) : IDragPayload
{
    public string Label => Champion.Name;
}

public sealed record ItemDragData(string ItemId, ItemInfo? Item, string ImageUrl, object? Origin) : IDragPayload
{
    public string Label => Item?.Name ?? $"Item {ItemId}";
}

/// <summary>Colours that depend on data (priorities, roles, matchup kinds). Static colours live in Styles/Tokens.axaml.</summary>
public static class Palette
{
    private static readonly Dictionary<string, IBrush> Cache = [];

    public static IBrush Hex(string hex)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(hex, out var b)) Cache[hex] = b = new SolidColorBrush(Color.Parse(hex)).ToImmutable();
            return b;
        }
    }

    public static IBrush Soft(string hex, byte alpha = 0x26)
    {
        var c = Color.Parse(hex);
        return Hex($"#{alpha:X2}{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    public const string Gold = "#C8AA6E";
    public const string GoldLight = "#F0E6D2";
    public const string Teal = "#0AC8B9";
    public const string Good = "#4CD98A";
    public const string Bad = "#EE5A6F";
    public const string Muted = "#7D7A70";

    public static string PriorityHex(ChampionPriority p) => p switch
    {
        ChampionPriority.Main => "#E8C26A",
        ChampionPriority.Secondary => "#0AC8B9",
        ChampionPriority.Pocket => "#B98CFF",
        _ => "#8C99AD",
    };

    public static string RoleHex(Position? role) => role switch
    {
        Position.Top => "#E0815A",
        Position.Jungle => "#6FCB74",
        Position.Middle => "#6FA8F5",
        Position.Bottom => "#E5C35C",
        Position.Support => "#5FD3C9",
        _ => Gold,
    };

    public static readonly IReadOnlyList<string> GroupColors =
        ["#E0815A", "#6FCB74", "#6FA8F5", "#E5C35C", "#5FD3C9", "#B98CFF", "#EE5A6F", "#C8AA6E", "#8C99AD"];
}

/// <summary>A champion portrait in a grid (pool, library, pickers).</summary>
public sealed partial class ChampionTileViewModel : ObservableObject
{
    public ChampionTileViewModel(ChampionInfo champion, string version, bool isNew = false)
    {
        Champion = champion;
        IsNew = isNew;
        IconUrl = AssetUrls.ChampionSquare(version, champion.ImageFile);
        DragData = new ChampionDragData(champion, IconUrl, null);
    }

    public ChampionInfo Champion { get; }
    /// <summary>Released in the current patch.</summary>
    public bool IsNew { get; }
    public string Name => Champion.Name;
    public string Id => Champion.Id;
    public string IconUrl { get; }
    public ChampionDragData DragData { get; }
    public string Tooltip => Champion.Name + " — " + Champion.Title + (Champion.Tags.Count > 0 ? "\n" + string.Join(" · ", Champion.Tags) : "")
                             + (Champion.Positions.Count > 0 ? "\n" + string.Join(", ", Champion.Positions.Select(p => p.DisplayName())) : "");

    /// <summary>Number of roster groups containing the champion.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInRoster))]
    private int _groupCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuilds))]
    private int _buildCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatchups))]
    private int _matchupCount;

    [ObservableProperty]
    private bool _isSelected;

    public bool IsInRoster => GroupCount > 0;
    public bool HasBuilds => BuildCount > 0;
    public bool HasMatchups => MatchupCount > 0;
}

public sealed partial class FilterOption : ObservableObject
{
    public FilterOption(string label, object? value, string? iconUrl = null, string? iconKind = null)
    {
        Label = label;
        Value = value;
        IconUrl = iconUrl;
        IconKind = iconKind;
    }

    public string Label { get; }
    public object? Value { get; }
    public string? IconUrl { get; }
    public string? IconKind { get; }
    public bool HasIconUrl => IconUrl is not null;
    public bool HasIconKind => IconKind is not null;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Searchable, filterable champion grid used by the pool, the library and the pickers.</summary>
public sealed partial class ChampionBrowserViewModel : ObservableObject
{
    private readonly IReadOnlyList<ChampionTileViewModel> _all;
    private PreferencesStore? _prefs;
    private string? _stateKey;
    private bool _restoring;

    public ChampionBrowserViewModel(GameDataSnapshot game, IReadOnlyList<ChampionTileViewModel>? tiles = null)
    {
        _all = tiles ?? game.Champions.Select(c => new ChampionTileViewModel(c, game.Version, game.NewChampionIds.Contains(c.Id))).ToList();
        PositionsAvailable = game.PositionsAvailable;
        RoleOptions = new ObservableCollection<FilterOption>(
            new[] { new FilterOption("All roles", null, iconKind: "layout-grid") { IsSelected = true } }
                .Concat(PositionInfo.All.Select(p => new FilterOption(p.DisplayName(), p, AssetUrls.PositionIcon(p)))));
        ClassOptions = ["All classes", "Assassin", "Fighter", "Mage", "Marksman", "Support", "Tank"];
        _selectedClass = ClassOptions[0];
        Refilter();
    }

    public IReadOnlyList<ChampionTileViewModel> All => _all;
    public ObservableCollection<FilterOption> RoleOptions { get; }
    public IReadOnlyList<string> ClassOptions { get; }
    public bool PositionsAvailable { get; }

    /// <summary>Additional filter supplied by the page (e.g. "only champions in my roster").</summary>
    public Func<ChampionTileViewModel, bool>? ExtraFilter { get; set; }

    /// <summary>Reports whether the page's own filters are active, and resets them (for "Clear filters").</summary>
    public Func<bool>? ExtraFiltersActive { get; set; }
    public Action? ResetExtraFilters { get; set; }

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private string _selectedClass;

    [ObservableProperty]
    private Position? _role;

    [ObservableProperty]
    private IReadOnlyList<ChampionTileViewModel> _tiles = [];

    public string CountText => Tiles.Count == _all.Count ? $"{_all.Count}" : $"{Tiles.Count} / {_all.Count}";
    public bool IsEmpty => Tiles.Count == 0;
    public bool HasActiveFilters => Search.Length > 0 || Role is not null || SelectedClass != ClassOptions[0] || (ExtraFiltersActive?.Invoke() ?? false);
    public ChampionTileViewModel? FirstTile => Tiles.Count > 0 ? Tiles[0] : null;

    partial void OnSearchChanged(string value) => Changed();
    partial void OnSelectedClassChanged(string value) => Changed();
    partial void OnRoleChanged(Position? value)
    {
        foreach (var o in RoleOptions) o.IsSelected = Equals(o.Value as Position?, value);
        Changed();
    }

    partial void OnTilesChanged(IReadOnlyList<ChampionTileViewModel> value)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(FirstTile));
    }

    private void Changed()
    {
        Refilter();
        if (_restoring || _prefs is null || _stateKey is null) return;
        _prefs.Set(_stateKey, new BrowserState(Search, Role, SelectedClass));
    }

    private sealed record BrowserState(string Search, Position? Role, string Class);

    /// <summary>Restores the last search and filters, and keeps them saved under <paramref name="key"/>.</summary>
    public void Remember(PreferencesStore prefs, string key)
    {
        _prefs = prefs;
        _stateKey = key;
        if (prefs.Get<BrowserState?>(key, null) is not { } state) return;
        _restoring = true;
        Search = state.Search ?? "";
        Role = state.Role;
        SelectedClass = ClassOptions.Contains(state.Class) ? state.Class : ClassOptions[0];
        _restoring = false;
        Refilter();
    }

    [RelayCommand]
    private void SelectRole(FilterOption option) => Role = option.Value as Position?;

    [RelayCommand]
    private void ClearSearch() => Search = "";

    [RelayCommand]
    private void ClearFilters()
    {
        _restoring = true;
        Search = "";
        Role = null;
        SelectedClass = ClassOptions[0];
        ResetExtraFilters?.Invoke();
        _restoring = false;
        Changed();
    }

    public ChampionTileViewModel? Find(string championId) => _all.FirstOrDefault(t => string.Equals(t.Id, championId, StringComparison.OrdinalIgnoreCase));

    public ChampionSequence Sequence(string label) => new(Tiles.Select(t => t.Id).ToList(), label);

    public void Refilter()
    {
        var cls = SelectedClass == ClassOptions[0] ? null : SelectedClass;
        // Best matches first when searching: names starting with the query, then the rest alphabetically.
        var query = ChampionInfo.Normalize(Search);
        Tiles = _all.Where(t =>
                t.Champion.Matches(Search)
                && (Role is null || t.Champion.Positions.Contains(Role.Value))
                && (cls is null || t.Champion.Tags.Contains(cls))
                && (ExtraFilter?.Invoke(t) ?? true))
            .OrderBy(t => query.Length > 0 && !ChampionInfo.Normalize(t.Name).StartsWith(query, StringComparison.Ordinal))
            .ToList();
    }
}

/// <summary>An in-window confirmation dialog.</summary>
public sealed partial class ConfirmDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new();

    public ConfirmDialogViewModel(string title, string message, string confirmText, bool danger)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsDanger = danger;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public bool IsDanger { get; }
    public Task<bool> Result => _result.Task;

    [RelayCommand]
    private void Confirm() => _result.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _result.TrySetResult(false);
}

public sealed partial class ToastViewModel : ObservableObject
{
    public ToastViewModel(string message, ToastKind kind, string? actionText = null, Func<Task>? action = null)
    {
        Message = message;
        Kind = kind;
        ActionText = actionText;
        Action = action;
    }

    public string Message { get; }
    public ToastKind Kind { get; }
    /// <summary>Optional action button (e.g. "Undo").</summary>
    public string? ActionText { get; }
    public Func<Task>? Action { get; }
    public bool HasAction => Action is not null;
    public string IconKind => Kind switch
    {
        ToastKind.Success => "circle-check",
        ToastKind.Warning => "triangle-alert",
        ToastKind.Error => "circle-x",
        _ => "info",
    };
    public IBrush Accent => Palette.Hex(Kind switch
    {
        ToastKind.Success => Palette.Good,
        ToastKind.Warning => "#E6B85C",
        ToastKind.Error => Palette.Bad,
        _ => Palette.Teal,
    });
}
