using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// The Champion stats page: every champion's base stats at a level (1–18), sortable by any column, with your roster's
/// champions marked. The top and bottom tenth of each column (among the champions shown) are coloured.
/// </summary>
public sealed partial class ChampionStatsViewModel : ObservableObject, IPage
{
    private const string PrefsKey = "stats.table";
    public const string AnyRole = "Any role";
    public const string AnyClass = "Any class";
    public const string AnyRange = "Melee and ranged";
    public const string Melee = "Melee";
    public const string Ranged = "Ranged";

    private readonly IShell _shell;
    private readonly List<ChampionInfo> _champions;
    private HashSet<string> _roster = new(StringComparer.OrdinalIgnoreCase);
    private bool _restoring;

    public ChampionStatsViewModel(IShell shell)
    {
        _shell = shell;
        var game = shell.Game;
        _champions = game.Champions.Where(c => c.Stats is not null).ToList();
        ClassOptions = [AnyClass, .. _champions.SelectMany(c => c.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        RoleOptions = game.PositionsAvailable ? [AnyRole, .. PositionInfo.All.Select(p => p.DisplayName())] : [AnyRole];
        Columns = StatColumn.All.Select(c => new StatColumnHeader(c, SortBy)).ToList();

        _restoring = true;
        var prefs = shell.Preferences.Get(PrefsKey, new StatsPrefs());
        _level = Math.Clamp(prefs.Level, 1, ChampionMath.MaxLevel);
        _selectedRole = RoleOptions.Contains(prefs.Role) ? prefs.Role : AnyRole;
        _selectedClass = ClassOptions.Contains(prefs.Class) ? prefs.Class : AnyClass;
        _selectedRange = RangeOptions.Contains(prefs.Range) ? prefs.Range : AnyRange;
        _onlyRoster = prefs.OnlyRoster;
        _sortKey = prefs.Sort is { } key && (key == NameKey || StatColumn.All.Any(c => c.Key == key)) ? key : NameKey;
        _descending = prefs.Descending;
        _restoring = false;
        Refresh();
    }

    public string Title => "Champion stats";
    public const string NameKey = "name";

    public IReadOnlyList<StatColumnHeader> Columns { get; }
    public IReadOnlyList<string> RoleOptions { get; }
    public IReadOnlyList<string> ClassOptions { get; }
    public IReadOnlyList<string> RangeOptions { get; } = [AnyRange, Melee, Ranged];

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    private int _level;

    public string LevelText => $"Level {Level}";

    [ObservableProperty]
    private string _selectedRole;

    [ObservableProperty]
    private string _selectedClass;

    [ObservableProperty]
    private string _selectedRange;

    [ObservableProperty]
    private bool _onlyRoster;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    private string _sortKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    private bool _descending;

    public string NameSortGlyph => SortKey == NameKey ? (Descending ? " ▼" : " ▲") : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmpty))]
    private IReadOnlyList<ChampionStatRow> _rows = [];

    public string CountText => $"{Rows.Count} champion{(Rows.Count == 1 ? "" : "s")}";
    public bool IsEmpty => Rows.Count == 0;
    public bool HasActiveFilters => Search.Length > 0 || SelectedRole != AnyRole || SelectedClass != AnyClass || SelectedRange != AnyRange || OnlyRoster;

    partial void OnSearchChanged(string value) => Refresh();
    partial void OnLevelChanged(int value) => Refresh();
    partial void OnSelectedRoleChanged(string value) => Refresh();
    partial void OnSelectedClassChanged(string value) => Refresh();
    partial void OnSelectedRangeChanged(string value) => Refresh();
    partial void OnOnlyRosterChanged(bool value) => Refresh();

    public async Task OnNavigatedToAsync()
    {
        var groups = await _shell.Repository.GetGroupsAsync();
        _roster = groups.SelectMany(g => g.Entries).Select(e => e.ChampionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Refresh();
    }

    [RelayCommand]
    private void LevelDown() => Level = Math.Max(1, Level - 1);

    [RelayCommand]
    private void LevelUp() => Level = Math.Min(ChampionMath.MaxLevel, Level + 1);

    [RelayCommand]
    private void SortByName() => SortBy(NameKey);

    private void SortBy(string key)
    {
        _restoring = true;
        if (SortKey == key) Descending = !Descending;
        else
        {
            SortKey = key;
            // Big numbers first for stats, A–Z for names.
            Descending = key != NameKey;
        }
        _restoring = false;
        Refresh();
    }

    [RelayCommand]
    private void Open(ChampionStatRow row) => _shell.OpenChampion(row.Champion.Id, ChampionTab.Abilities);

    [RelayCommand]
    private void ClearFilters()
    {
        _restoring = true;
        Search = "";
        SelectedRole = AnyRole;
        SelectedClass = AnyClass;
        SelectedRange = AnyRange;
        OnlyRoster = false;
        _restoring = false;
        Refresh();
    }

    private void Refresh()
    {
        if (_restoring) return;
        var game = _shell.Game;
        var role = PositionInfo.All.FirstOrDefault(p => p.DisplayName() == SelectedRole);
        var visible = _champions.Where(c =>
            c.Matches(Search) &&
            (SelectedRole == AnyRole || c.Positions.Contains(role)) &&
            (SelectedClass == AnyClass || c.Tags.Contains(SelectedClass, StringComparer.OrdinalIgnoreCase)) &&
            (SelectedRange == AnyRange || (SelectedRange == Ranged) == c.Stats!.IsRanged) &&
            (!OnlyRoster || _roster.Contains(c.Id))).ToList();

        var blocks = visible.ToDictionary(c => c.Id, c => ChampionMath.Compute(c.Stats!, Level, []), StringComparer.OrdinalIgnoreCase);
        // The sorted values of each column, for colouring the top and bottom tenth (only with enough champions to compare).
        var sortedValues = StatColumn.All.ToDictionary(col => col.Key, col =>
            visible.Select(c => col.Value(c, blocks[c.Id])).OfType<double>().Order().ToArray());

        var rows = visible.Select(c => new ChampionStatRow(c, AssetUrls.ChampionSquare(game.Version, c.ImageFile), _roster.Contains(c.Id),
            StatColumn.All.Select(col => Cell(col, col.Value(c, blocks[c.Id]), sortedValues[col.Key], c)).ToList())).ToList();

        IEnumerable<ChampionStatRow> sorted;
        if (SortKey == NameKey)
            sorted = Descending ? rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        else
        {
            var index = StatColumn.All.ToList().FindIndex(c => c.Key == SortKey);
            // Champions without the stat (no mana) always go last.
            sorted = Descending
                ? rows.OrderBy(r => r.Cells[index].Value is null).ThenByDescending(r => r.Cells[index].Value).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Cells[index].Value is null).ThenBy(r => r.Cells[index].Value).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }
        Rows = sorted.ToList();
        foreach (var h in Columns) h.SetSort(SortKey == h.Column.Key, Descending);
        OnPropertyChanged(nameof(HasActiveFilters));
        _shell.Preferences.Set(PrefsKey, new StatsPrefs
        {
            Level = Level, Role = SelectedRole, Class = SelectedClass, Range = SelectedRange, OnlyRoster = OnlyRoster, Sort = SortKey, Descending = Descending,
        });
    }

    /// <summary>
    /// A cell; it's in the top tenth when the champions with at least this value are no more than a tenth of those shown
    /// (so a value shared by many champions, like 550 range, isn't highlighted), and likewise for the bottom tenth.
    /// </summary>
    internal static StatCell Cell(StatColumn column, double? value, double[] sorted, ChampionInfo champion)
    {
        if (value is not { } v) return new StatCell(null, "—", -1, column.MissingTip(champion));
        var tone = 0;
        if (sorted.Length >= 10)
        {
            var tenth = Math.Max(1, (int)Math.Floor(sorted.Length * 0.1));
            var atLeast = sorted.Length - LowerBound(sorted, v - 1e-9);
            var atMost = LowerBound(sorted, v + 1e-9);
            tone = atLeast <= tenth ? 1 : atMost <= tenth ? -1 : 0;
        }
        return new StatCell(v, column.Format(v), tone, "");
    }

    /// <summary>The number of values below <paramref name="value"/> in an ascending array.</summary>
    private static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public sealed class StatsPrefs
    {
        public int Level { get; set; } = 1;
        public string Role { get; set; } = AnyRole;
        public string Class { get; set; } = AnyClass;
        public string Range { get; set; } = AnyRange;
        public bool OnlyRoster { get; set; }
        public string? Sort { get; set; }
        public bool Descending { get; set; }
    }
}

/// <summary>A numeric column of the stats table.</summary>
public sealed record StatColumn(string Key, string Header, string Tip, Func<ChampionInfo, ChampionStatBlock, double?> Value, Func<double, string> Format)
{
    private static string Whole(double v) => ChampionMath.Number(v, 0);

    public string MissingTip(ChampionInfo champion) => Key switch
    {
        "mana" when champion.Stats is { } s => string.IsNullOrWhiteSpace(s.Resource) || s.Resource.Equals("None", StringComparison.OrdinalIgnoreCase) ? "No resource" : s.Resource,
        // Data Dragon reports 0 for every champion since 16.22; the growth comes from Meraki, which may not have a new champion yet.
        "adgrowth" => "Not published for this champion yet (AD below doesn't grow with level until it is)",
        _ => "",
    };

    public static IReadOnlyList<StatColumn> All { get; } =
    [
        new("health", "Health", "Health at this level", (_, s) => s.Health, Whole),
        new("mana", "Mana", "Mana at this level (champions with energy, fury or no resource show —)",
            (c, s) => c.Stats!.Resource.Equals("Mana", StringComparison.OrdinalIgnoreCase) ? s.Mana : null, Whole),
        new("ad", "AD", "Attack damage", (_, s) => s.AttackDamage, v => ChampionMath.Number(v, 1)),
        new("as", "Att. speed", "Attacks per second", (_, s) => s.AttackSpeed, v => ChampionMath.Number(v, 3)),
        new("armor", "Armor", "Armor", (_, s) => s.Armor, v => ChampionMath.Number(v, 1)),
        new("mr", "Magic res.", "Magic resist", (_, s) => s.MagicResist, v => ChampionMath.Number(v, 1)),
        new("ms", "Move spd", "Move speed", (_, s) => s.MoveSpeed, Whole),
        new("range", "Range", "Attack range", (_, s) => s.AttackRange, Whole),
        new("hpgrowth", "HP / level", "Health gained per level", (c, _) => c.Stats!.HealthPerLevel, Whole),
        new("adgrowth", "AD / level", "Attack damage gained per level", (c, _) => c.Stats!.AttackDamageGrowthUnknown ? null : c.Stats.AttackDamagePerLevel, v => ChampionMath.Number(v, 2)),
    ];
}

/// <summary>A column header: click to sort by it (again to flip the order).</summary>
public sealed partial class StatColumnHeader(StatColumn column, Action<string> sort) : ObservableObject
{
    public StatColumn Column { get; } = column;
    public string Tip => Column.Tip + " — click to sort";

    [ObservableProperty]
    private string _text = column.Header;

    [ObservableProperty]
    private bool _isSorted;

    internal void SetSort(bool sorted, bool descending)
    {
        IsSorted = sorted;
        Text = Column.Header + (sorted ? descending ? " ▼" : " ▲" : "");
    }

    [RelayCommand]
    private void Sort() => sort(Column.Key);
}

/// <param name="Tone">1 = in the top tenth of the column, −1 = bottom tenth (or no value), 0 = in between.</param>
public sealed record StatCell(double? Value, string Text, int Tone, string Tip)
{
    public bool IsHigh => Tone > 0;
    public bool IsLow => Tone < 0;
    public string? ToolTipText => Tip.Length > 0 ? Tip : null;
}

public sealed record ChampionStatRow(ChampionInfo Champion, string IconUrl, bool IsInRoster, IReadOnlyList<StatCell> Cells)
{
    public string Name => Champion.Name;
    public string ClassText => string.Join(" · ", Champion.Tags);
    public string RangeText => Champion.Stats!.IsRanged ? "Ranged" : "Melee";
    public string Tip => $"{Name}{(IsInRoster ? " (in your roster)" : "")} — open the Abilities tab";
}
