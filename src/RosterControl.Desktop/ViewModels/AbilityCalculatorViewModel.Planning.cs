using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Rules;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// The calculator's what-ifs: the best item to buy next for a number you pick, swapping one counted item for another, and
/// the power curve (a number from level 1 to 18 as the build's items are finished).
/// </summary>
public sealed partial class AbilityCalculatorViewModel
{
    private const int MaxChoices = 8;
    private const int MaxSwapChoices = 12;

    private ChampionStats? _planStats;
    private IReadOnlyDictionary<char, int> _planRanks = new Dictionary<char, int>();
    private IReadOnlyDictionary<char, int> _planMaxRanks = new Dictionary<char, int>();
    private IReadOnlyList<StatShardInfo?> _planShards = [];
    private List<ItemInfo> _planItems = [];
    private ChampionStatBlock? _planBlock;
    private string _metricSignature = "";
    private bool _settingMetrics;

    /// <summary>Every number that can be measured: abilities, the build's runes with formulas, then stats.</summary>
    [ObservableProperty]
    private IReadOnlyList<CalcMetric> _metrics = [];

    /// <summary>What best-next and swap rank items by.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoicesHint))]
    private CalcMetric? _focusMetric;

    [ObservableProperty]
    private CalcMetric? _curveMetric;

    /// <summary>The counted item being swapped; null shows the best next item instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwapping), nameof(ChoicesTitle), nameof(ChoicesHint))]
    private CalculatorItem? _swapFrom;

    public bool IsSwapping => SwapFrom is not null;
    public string ChoicesTitle => SwapFrom is { } s ? $"INSTEAD OF {s.Name.ToUpperInvariant()}" : "BEST NEXT ITEM";

    public string ChoicesHint => FocusMetric is not { } m ? ""
        : SwapFrom is { } s ? $"Items that could replace {s.Name}, ranked by {m.Label} at level {Level}. Click one to see everything it changes."
        : $"Finished items you don't have yet, ranked by how much they add to {m.Label} at level {Level}. Click one to see everything it changes.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChoices))]
    private IReadOnlyList<ItemChoiceViewModel> _choices = [];

    public bool HasChoices => Choices.Count > 0;

    [ObservableProperty]
    private string _choicesEmptyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private ItemChoiceViewModel? _previewChoice;

    public bool HasPreview => PreviewChoice is not null;

    [ObservableProperty]
    private string _previewTitle = "";

    [ObservableProperty]
    private string _previewGold = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewRows))]
    private IReadOnlyList<MetricChangeRow> _previewRows = [];

    public bool HasPreviewRows => PreviewRows.Count > 0;

    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline))]
    private int _timelineCount;

    public bool HasTimeline => TimelineCount > 0;

    [ObservableProperty]
    private bool _timelineIsCustom;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _curveSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartMarker> _curveMarkers = [];

    [ObservableProperty]
    private string _curveSummary = "";

    partial void OnFocusMetricChanged(CalcMetric? value)
    {
        if (_settingMetrics || value is null) return;
        _prefs.Focus = value.Key;
        SavePrefs();
        PreviewChoice = null;
        UpdateChoices();
    }

    partial void OnCurveMetricChanged(CalcMetric? value)
    {
        if (_settingMetrics || value is null) return;
        _prefs.Curve = value.Key;
        SavePrefs();
        UpdateCurve();
    }

    partial void OnSwapFromChanged(CalculatorItem? oldValue, CalculatorItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSwapSource = false;
        if (newValue is not null) newValue.IsSwapSource = true;
        PreviewChoice = null;
        if (!_suspend) UpdateChoices();
    }

    partial void OnPreviewChoiceChanged(ItemChoiceViewModel? oldValue, ItemChoiceViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        UpdatePreview();
    }

    [RelayCommand]
    private void Swap(CalculatorItem item) => SwapFrom = SwapFrom == item ? null : item;

    [RelayCommand]
    private void EndSwap() => SwapFrom = null;

    [RelayCommand]
    private void PreviewItem(ItemChoiceViewModel choice) => PreviewChoice = PreviewChoice == choice ? null : choice;

    [RelayCommand]
    private void ClosePreview() => PreviewChoice = null;

    [RelayCommand]
    private void ResetTimeline()
    {
        if (SelectedChoice?.Build?.Model is { } build && _prefs.Timeline.Remove(build.Id)) SavePrefs();
        LoadTimeline();
        UpdateCurve();
    }

    private void UpdatePlanning(ChampionStats baseStats, ChampionStatBlock block, IReadOnlyDictionary<char, int> ranks, IReadOnlyDictionary<char, int> maxRanks,
        IReadOnlyList<StatShardInfo?> shards, List<ItemInfo> counted)
    {
        _planStats = baseStats;
        _planBlock = block;
        _planRanks = ranks;
        _planMaxRanks = maxRanks;
        _planShards = shards;
        _planItems = counted;
        if (SwapFrom is { } swap && (!swap.IsOn || !Items.Contains(swap))) SwapFrom = null;
        UpdateMetrics();
        UpdateChoices();
        LoadTimeline();
        UpdateCurve();
    }

    private void UpdateMetrics()
    {
        var list = new List<CalcMetric>();
        foreach (var a in _owner.Abilities)
        {
            for (var i = 0; i < a.Values.Count; i++)
                if (a.Values[i].Formula is not null) list.Add(CalcMetric.ForAbility(a.Slot, i, a.Values[i]));
            if (CalcMetric.Cooldown(a.Slot, a.Info.CooldownText) is { } cd) list.Add(cd);
        }
        if (SelectedChoice?.Build?.Model is { } build && _owner.RuneNumbers is { } numbers)
        {
            var game = _shell.Game;
            foreach (var id in build.Runes.PrimaryPerks.Concat(build.Runes.SubPerks).Where(id => id != 0).Distinct())
            {
                if (game.Rune(id) is not { } rune) continue;
                var values = numbers[id];
                for (var i = 0; i < values.Count; i++)
                    if (values[i].Formula is not null) list.Add(CalcMetric.ForRune(id, rune.Name, i, values[i]));
            }
        }
        list.AddRange(CalcMetric.StatMetrics);
        var signature = string.Join("|", list.Select(m => m.Key));
        if (signature == _metricSignature) return;
        _metricSignature = signature;

        _settingMetrics = true;
        Metrics = list;
        FocusMetric = Pick(list, _prefs.Focus);
        CurveMetric = Pick(list, _prefs.Curve ?? _prefs.Focus);
        _settingMetrics = false;
    }

    /// <summary>The saved choice when this champion has it, else the first ability damage number, else health.</summary>
    private static CalcMetric? Pick(IReadOnlyList<CalcMetric> list, string? saved) =>
        list.FirstOrDefault(m => m.Key == saved)
        ?? list.FirstOrDefault(m => m.Group == CalcMetric.AbilitiesGroup && m.HigherIsBetter && m.Label.Contains("damage", StringComparison.OrdinalIgnoreCase))
        ?? list.FirstOrDefault(m => m.Group == CalcMetric.AbilitiesGroup && m.HigherIsBetter)
        ?? list.FirstOrDefault();

    private double Evaluate(CalcMetric metric, IReadOnlyList<ItemInfo> items, int level, IReadOnlyDictionary<char, int> ranks) =>
        _planStats is { } s ? metric.Value(BuildCalculator.StatsAt(s, level, items, _planShards, IncludeShards), ranks) : double.NaN;

    /// <summary>Lists the items worth buying next, or the ones that could replace <see cref="SwapFrom"/>.</summary>
    private void UpdateChoices()
    {
        if (FocusMetric is not { } metric || _planStats is null)
        {
            Choices = [];
            return;
        }
        var game = _shell.Game;
        var replacing = SwapFrom is { } swap ? game.Item(swap.ItemId) : null;
        var build = SelectedChoice?.Build?.Model;
        var aram = build is not null && build.Role is null && IsAramBuild(build, game);
        var candidates = BuildCalculator.Candidates(game, _planItems, replacing, aram);
        var ranked = BuildCalculator.Rank(_planItems, candidates, replacing, metric, items => Evaluate(metric, items, Level, _planRanks));
        var keep = PreviewChoice?.Item.Id;
        var shown = replacing is null
            ? ranked.Where(c => c.Gain > 1e-6).Take(MaxChoices)
            : ranked.Take(MaxSwapChoices);
        Choices = shown.Select(c => new ItemChoiceViewModel(c, game.Version, replacing)).ToList();
        ChoicesEmptyText = replacing is not null ? "No other items of this kind."
            : double.IsNaN(Evaluate(metric, _planItems, Level, _planRanks)) ? $"{metric.Label} isn't available at level {Level} (the ability isn't learned yet)."
            : $"No item's listed stats add to {metric.Label}. Passives and actives aren't counted.";
        PreviewChoice = keep is null ? null : Choices.FirstOrDefault(c => c.Item.Id == keep);
    }

    private static bool IsAramBuild(Core.Model.Build build, GameDataSnapshot game)
    {
        var items = CountableItems(build, game).ToList();
        return items.Any(i => i.OnAram && !i.OnSummonersRift);
    }

    private void UpdatePreview()
    {
        if (PreviewChoice is not { } choice || _planStats is not { } s || _planBlock is not { } before)
        {
            PreviewRows = [];
            PreviewTitle = PreviewGold = "";
            return;
        }
        var game = _shell.Game;
        var replacing = SwapFrom is { } swap ? game.Item(swap.ItemId) : null;
        List<ItemInfo> after = replacing is null ? [.. _planItems, choice.Item] : [.. BuildCalculator.RemoveOne(_planItems, replacing), choice.Item];
        var afterBlock = BuildCalculator.StatsAt(s, Level, after, _planShards, IncludeShards);
        PreviewRows = BuildCalculator.Changes(Metrics, before, afterBlock, _planRanks).Select(c => new MetricChangeRow(c)).ToList();
        PreviewTitle = replacing is null ? $"Adding {choice.Item.Name} at level {Level}" : $"{replacing.Name} → {choice.Item.Name} at level {Level}";
        var gold = choice.Item.TotalGold - (replacing?.TotalGold ?? 0);
        PreviewGold = replacing is null
            ? $"{choice.Item.TotalGold.ToString("N0", CultureInfo.InvariantCulture)} gold"
            : gold == 0 ? "Same cost" : $"{(gold > 0 ? "+" : "−")}{Math.Abs(gold).ToString("N0", CultureInfo.InvariantCulture)} gold";
    }

    private void LoadTimeline()
    {
        var build = SelectedChoice?.Build?.Model;
        var timeline = TimelineFor(build, _planItems, _prefs);
        var version = _shell.Game.Version;
        // Keep the rows when nothing changed, so the level buttons don't lose focus while clicking.
        if (Timeline.Count == timeline.Count && Timeline.Select(t => (t.ItemId, t.Level)).SequenceEqual(timeline.Select(t => (t.Item.Id, t.Level))))
            return;
        Timeline.Clear();
        foreach (var (item, level) in timeline)
            Timeline.Add(new TimelineItemViewModel(item.Id, item.Name, AssetUrls.Item(version, item.Id), level, TimelineChanged));
        TimelineCount = Timeline.Count;
        TimelineIsCustom = build is not null && _prefs.Timeline.ContainsKey(build.Id);
    }

    private void TimelineChanged(TimelineItemViewModel row)
    {
        if (SelectedChoice?.Build?.Model is not { } build) return;
        if (!_prefs.Timeline.TryGetValue(build.Id, out var saved)) _prefs.Timeline[build.Id] = saved = [];
        // Save every row so a later change to the ticked items doesn't shift the ones already placed.
        foreach (var t in Timeline) saved[t.ItemId] = t.Level;
        SavePrefs();
        TimelineIsCustom = true;
        UpdateCurve();
    }

    private void UpdateCurve()
    {
        if (CurveMetric is not { } metric || _planStats is not { } s)
        {
            CurveSeries = [];
            CurveMarkers = [];
            CurveSummary = "";
            return;
        }
        var game = _shell.Game;
        var timeline = Timeline.Select(t => (Item: game.Item(t.ItemId), t.Level)).Where(t => t.Item is not null).Select(t => (t.Item!, t.Level)).ToList();
        var order = SelectedChoice?.Build?.Model.SkillOrder;
        var values = BuildCalculator.Curve(s, timeline, _planShards, IncludeShards, order, _planMaxRanks, metric);
        CurveSeries = [new ChartSeries(metric.Label, values, "Brush.Gold", metric.Format)];
        CurveMarkers = timeline.Select(t => new ChartMarker(t.Level, t.Item1.Name, "Brush.Gold")).ToList();
        var first = values.FirstOrDefault(v => !double.IsNaN(v));
        var last = values[^1];
        CurveSummary = double.IsNaN(last) ? "" : $"{metric.Label}: {metric.Format(first)} at level {Array.FindIndex(values, v => !double.IsNaN(v)) + 1} → {metric.Format(last)} at 18";
    }
}

/// <summary>An item in the best-next or swap list, with what it changes in the chosen number.</summary>
public sealed partial class ItemChoiceViewModel(ItemChoice choice, string version, ItemInfo? replacing) : ObservableObject
{
    public ItemInfo Item { get; } = choice.Item;
    public string Name => Item.Name;
    public string IconUrl { get; } = AssetUrls.Item(version, choice.Item.Id);
    public string GainText { get; } = choice.GainText;
    public string PercentText { get; } = choice.PercentText;
    public bool HasPercent => PercentText.Length > 0;
    public bool IsBetter { get; } = choice.Gain > 1e-6;
    public bool IsWorse { get; } = choice.Gain < -1e-6;
    public IBrush GainBrush => Palette.Hex(IsBetter ? Palette.Good : IsWorse ? Palette.Bad : Palette.Muted);

    public string GoldText { get; } = replacing is null
        ? choice.Item.TotalGold.ToString("N0", CultureInfo.InvariantCulture) + " g"
        : (choice.Item.TotalGold - replacing.TotalGold) switch
        {
            0 => "same cost",
            var d => (d > 0 ? "+" : "−") + Math.Abs(d).ToString("N0", CultureInfo.InvariantCulture) + " g",
        };

    /// <summary>Gain per 1,000 gold, for adding an item ("+12 per 1k").</summary>
    public string ValueText { get; } = replacing is null && choice.Item.TotalGold > 0 && choice.Gain > 1e-6
        ? choice.Metric.FormatDelta((choice.After - choice.Before) * 1000.0 / choice.Item.TotalGold) + " per 1k gold"
        : "";

    public bool HasValueText => ValueText.Length > 0;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One number that an item change moves: "Q · Total damage  +34  (412 → 446)".</summary>
public sealed record MetricChangeRow(MetricChange Change)
{
    public string Label => Change.Metric.Label;
    public string DeltaText => Change.DeltaText;
    public string Text => Change.Text;
    public IBrush Brush => Palette.Hex(Change.IsBetter ? Palette.Good : Palette.Bad);
}

/// <summary>An item on the power curve's timeline with the level it's finished at.</summary>
public sealed partial class TimelineItemViewModel : ObservableObject
{
    private readonly Action<TimelineItemViewModel> _changed;

    public TimelineItemViewModel(string itemId, string name, string iconUrl, int level, Action<TimelineItemViewModel> changed)
    {
        ItemId = itemId;
        Name = name;
        IconUrl = iconUrl;
        _level = level;
        _changed = changed;
    }

    public string ItemId { get; }
    public string Name { get; }
    public string IconUrl { get; }
    public string Tip => $"{Name}: finished at level {Level}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private int _level;

    partial void OnLevelChanged(int value) => _changed(this);

    [RelayCommand]
    private void Earlier() => Level = Math.Max(1, Level - 1);

    [RelayCommand]
    private void Later() => Level = Math.Min(ChampionMath.MaxLevel, Level + 1);
}
