using CommunityToolkit.Mvvm.ComponentModel;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// The Compare page's power curve: both sides' builds on one chart from level 1 to 18, with the items each build counts in
/// the calculator and the levels they're finished at. Two champions compare stats; two builds of one champion can also
/// compare ability numbers.
/// </summary>
public sealed partial class CompareViewModel
{
    private const string CurvePrefsKey = "compare.curve";
    private static readonly IReadOnlyDictionary<char, int> DefaultMaxRanks = new Dictionary<char, int> { ['Q'] = 5, ['W'] = 5, ['E'] = 5, ['R'] = 3 };

    private string? _abilityChampion;
    private IReadOnlyList<CalcMetric> _abilityMetrics = [];
    private IReadOnlyDictionary<char, int> _maxRanks = DefaultMaxRanks;
    private bool _settingCurveMetrics;
    private int _curveVersion;

    [ObservableProperty]
    private IReadOnlyList<CalcMetric> _curveMetrics = CalcMetric.StatMetrics;

    [ObservableProperty]
    private CalcMetric? _curveMetric;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurve), nameof(LeftCurveName), nameof(RightCurveName))]
    private IReadOnlyList<ChartSeries> _curveSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartMarker> _curveMarkers = [];

    [ObservableProperty]
    private string _curveNote = "";

    public bool HasCurve => CurveSeries.Count > 0;
    public string LeftCurveName => CurveSeries.Count > 0 ? CurveSeries[0].Name : "";
    public string RightCurveName => CurveSeries.Count > 1 ? CurveSeries[1].Name : "";

    partial void OnCurveMetricChanged(CalcMetric? value)
    {
        if (_settingCurveMetrics || value is null) return;
        _shell.Preferences.Set(CurvePrefsKey, value.Key);
        DrawCurve();
    }

    /// <summary>Redraws after a champion or build changes; ability numbers are loaded when both sides are one champion.</summary>
    internal async Task UpdateCurveAsync()
    {
        var version = ++_curveVersion;
        var same = Left.Champion is { } a && Right.Champion is { } b && a.Id == b.Id ? a.Id : null;
        if (same != _abilityChampion)
        {
            _abilityChampion = same;
            _abilityMetrics = [];
            _maxRanks = DefaultMaxRanks;
            if (same is not null)
            {
                var abilities = await _shell.GameData.GetAbilitiesAsync(same, CancellationToken.None);
                var numbers = abilities is null ? null : await _shell.GameData.GetAbilityNumbersAsync(same, CancellationToken.None);
                if (version != _curveVersion) return;
                if (abilities is not null)
                {
                    _maxRanks = abilities.Abilities.Where(x => x.Slot != 'P').ToDictionary(x => x.Slot, x => Math.Max(1, x.MaxRank));
                    var list = new List<CalcMetric>();
                    foreach (var ability in abilities.Abilities)
                    {
                        var values = numbers?[ability.Slot] ?? [];
                        for (var i = 0; i < values.Count; i++)
                            if (values[i].Formula is not null) list.Add(CalcMetric.ForAbility(ability.Slot, i, values[i]));
                        if (CalcMetric.Cooldown(ability.Slot, ability.CooldownText) is { } cd) list.Add(cd);
                    }
                    _abilityMetrics = list;
                }
            }
        }
        var metrics = _abilityMetrics.Concat(CalcMetric.StatMetrics).ToList();
        _settingCurveMetrics = true;
        CurveMetrics = metrics;
        var saved = _shell.Preferences.Get<string?>(CurvePrefsKey, null);
        CurveMetric = metrics.FirstOrDefault(m => m.Key == CurveMetric?.Key) ?? metrics.FirstOrDefault(m => m.Key == saved) ?? CalcMetric.StatByKey("stat:health") ?? metrics[0];
        _settingCurveMetrics = false;
        DrawCurve();
    }

    /// <summary>A side picked another build: the champions are the same, so only the lines change.</summary>
    internal void DrawCurveForBuildChange()
    {
        if (!_settingCurveMetrics) DrawCurve();
    }

    private void DrawCurve()
    {
        if (Left.Champion is not { Stats: { } leftStats } left || Right.Champion is not { Stats: { } rightStats } right || CurveMetric is not { } metric)
        {
            CurveSeries = [];
            CurveMarkers = [];
            CurveNote = "";
            return;
        }
        var game = _shell.Game;
        var prefs = _shell.Preferences.Get(AbilityCalculatorViewModel.PrefsKey, new AbilityCalculatorViewModel.CalculatorPrefs());
        var markers = new List<ChartMarker>();
        ChartSeries Side(ChampionInfo champion, ChampionStats stats, Build? build, string colorKey)
        {
            var items = build is null ? [] : AbilityCalculatorViewModel.CountedItems(build, game, prefs);
            var timeline = AbilityCalculatorViewModel.TimelineFor(build, items, prefs);
            var shards = build?.Runes.Shards.Where(id => id != 0).Select(game.Shard).ToList() ?? [];
            var values = BuildCalculator.Curve(stats, timeline, shards, prefs.Shards, build?.SkillOrder, _maxRanks, metric);
            markers.AddRange(timeline.Select(t => new ChartMarker(t.Level, $"{t.Item.Name} ({champion.Name}{(build is null ? "" : " · " + build.Name)})", colorKey)));
            var name = left.Id == right.Id ? build?.Name ?? "No build" : champion.Name + (build is null ? " (no build: base stats)" : " · " + build.Name);
            return new ChartSeries(name, values, colorKey, metric.Format);
        }
        CurveSeries =
        [
            Side(left, leftStats, Left.SelectedBuild, "Brush.Gold"),
            Side(right, rightStats, Right.SelectedBuild, "Brush.Teal"),
        ];
        CurveMarkers = markers;
        CurveNote = "Each side counts the items ticked for its build in the calculator (or its core items), finished at the levels set on the Abilities tab. Listed stats only, no passives.";
    }
}
