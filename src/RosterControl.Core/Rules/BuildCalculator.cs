using System.Runtime.CompilerServices;
using RosterControl.Core.GameData;

namespace RosterControl.Core.Rules;

/// <summary>
/// A number the calculator can measure for a set of stats and ability ranks: a stat (health, AP…), an ability's value
/// (Q total damage, W cooldown) or a rune's value. Used to rank items, preview swaps and draw power curves.
/// </summary>
public sealed class CalcMetric
{
    private readonly Func<ChampionStatBlock, IReadOnlyDictionary<char, int>, double> _value;
    private readonly Func<double, string> _format;

    private CalcMetric(string key, string label, string group, bool higherIsBetter, Func<ChampionStatBlock, IReadOnlyDictionary<char, int>, double> value, Func<double, string> format)
    {
        Key = key;
        Label = label;
        Group = group;
        HigherIsBetter = higherIsBetter;
        _value = value;
        _format = format;
    }

    /// <summary>Stable id, e.g. "stat:health", "ability:Q:0", "cooldown:Q", "rune:8112:0".</summary>
    public string Key { get; }
    /// <summary>e.g. "Health", "Q · Total damage".</summary>
    public string Label { get; }
    /// <summary>"Stats", "Abilities" or "Runes".</summary>
    public string Group { get; }
    /// <summary>False for cooldowns: a smaller number is the improvement.</summary>
    public bool HigherIsBetter { get; }

    public bool IsStat => Group == StatsGroup;

    public const string StatsGroup = "Stats";
    public const string AbilitiesGroup = "Abilities";
    public const string RunesGroup = "Runes";

    /// <summary>The value for these stats and ranks; NaN when it doesn't apply (the ability isn't learned yet).</summary>
    public double Value(ChampionStatBlock stats, IReadOnlyDictionary<char, int> ranks) => _value(stats, ranks);

    public string Format(double value) => double.IsNaN(value) ? "—" : _format(value);

    /// <summary>"+34", "−0.8s"; the sign is the number's, not whether it's better.</summary>
    public string FormatDelta(double delta)
    {
        if (double.IsNaN(delta)) return "—";
        var text = _format(Math.Abs(delta));
        return (delta < 0 ? "−" : "+") + text;
    }

    /// <summary>How much better <paramref name="after"/> is than <paramref name="before"/> (negative when worse; cooldowns count down).</summary>
    public double Gain(double before, double after)
    {
        if (double.IsNaN(before) || double.IsNaN(after)) return 0;
        return HigherIsBetter ? after - before : before - after;
    }

    public override string ToString() => Label;

    private static string Whole(double v) => ChampionMath.Number(v, 0);
    private static string Pct(double fraction) => ChampionMath.Number(fraction * 100, Math.Abs(fraction) >= 1 ? 0 : 1) + "%";

    private static CalcMetric Stat(string key, string label, Func<ChampionStatBlock, double> value, Func<double, string>? format = null) =>
        new("stat:" + key, label, StatsGroup, true, (s, _) => value(s), format ?? Whole);

    /// <summary>The stats offered everywhere (the Compare page's curve uses these for two different champions).</summary>
    public static IReadOnlyList<CalcMetric> StatMetrics { get; } =
    [
        Stat("health", "Health", s => s.Health),
        Stat("ad", "Attack damage", s => s.AttackDamage),
        Stat("ap", "Ability power", s => s.AbilityPower),
        Stat("armor", "Armor", s => s.Armor),
        Stat("mr", "Magic resist", s => s.MagicResist),
        Stat("as", "Attack speed", s => s.AttackSpeed, v => ChampionMath.Number(v, 3)),
        Stat("ah", "Ability haste", s => s.AbilityHaste),
        Stat("ms", "Move speed", s => s.MoveSpeed),
        Stat("mana", "Mana", s => s.Mana),
        Stat("crit", "Crit chance", s => s.CritChance, Pct),
        Stat("lethality", "Lethality", s => s.Lethality),
        Stat("ehp-physical", "Effective health vs physical", s => s.Health * (1 + Math.Max(0, s.Armor) / 100)),
        Stat("ehp-magic", "Effective health vs magic", s => s.Health * (1 + Math.Max(0, s.MagicResist) / 100)),
        Stat("dps", "Auto-attack damage per second", s => s.AttackDamage * s.AttackSpeed * (1 + Math.Min(1, s.CritChance) * (s.CritDamage - 1))),
    ];

    public static CalcMetric? StatByKey(string? key) => StatMetrics.FirstOrDefault(m => m.Key == key);

    /// <summary>One of an ability's numbers, at the rank the ranks give it (NaN before it's learned; passives use rank 1).</summary>
    public static CalcMetric ForAbility(char slot, int index, AbilityValue value)
    {
        var formula = value.Formula ?? throw new ArgumentException("The value has no formula.", nameof(value));
        var key = slot == 'P' ? "Passive" : slot.ToString();
        return new CalcMetric($"ability:{slot}:{index}", $"{key} · {value.Label}", AbilitiesGroup, true,
            (s, ranks) =>
            {
                var rank = slot == 'P' ? 1 : ranks.GetValueOrDefault(slot);
                return rank <= 0 ? double.NaN : ChampionMath.Evaluate(formula, rank, s.Level, s);
            },
            v => ChampionMath.Format(formula, v));
    }

    /// <summary>An ability's cooldown after ability haste, from Data Dragon's per-rank text ("11/10/9/8/7"); null without one.</summary>
    public static CalcMetric? Cooldown(char slot, string cooldownText)
    {
        var series = ChampionMath.ParseSeries(cooldownText);
        if (series.Count == 0 || slot == 'P') return null;
        return new CalcMetric($"cooldown:{slot}", $"{slot} · Cooldown", AbilitiesGroup, false,
            (s, ranks) =>
            {
                var rank = ranks.GetValueOrDefault(slot);
                return rank <= 0 ? double.NaN : series[Math.Min(rank, series.Count) - 1] * s.CooldownMultiplier;
            },
            v => ChampionMath.Number(v, v >= 10 ? 1 : 2) + "s");
    }

    /// <summary>One of a rune's numbers (runes have no ranks; level and stats still matter).</summary>
    public static CalcMetric ForRune(int runeId, string runeName, int index, AbilityValue value)
    {
        var formula = value.Formula ?? throw new ArgumentException("The value has no formula.", nameof(value));
        return new CalcMetric($"rune:{runeId}:{index}", $"{runeName} · {value.Label}", RunesGroup, true,
            (s, _) => ChampionMath.Evaluate(formula, 1, s.Level, s),
            v => ChampionMath.Format(formula, v));
    }
}

/// <summary>A metric before and after a change (adding or swapping an item).</summary>
public sealed record MetricChange(CalcMetric Metric, double Before, double After)
{
    public double Delta => After - Before;
    public double Gain => Metric.Gain(Before, After);
    public bool IsBetter => Gain > 0;
    public string DeltaText => Metric.FormatDelta(Delta);
    public string Text => $"{Metric.Format(Before)} → {Metric.Format(After)}";
}

/// <summary>An item ranked by what it changes in a metric.</summary>
public sealed record ItemChoice(ItemInfo Item, double Before, double After, CalcMetric Metric)
{
    public double Gain => Metric.Gain(Before, After);
    public string GainText => Metric.FormatDelta(After - Before);

    /// <summary>"+14%" of the current value; empty when there's no current value.</summary>
    public string PercentText => Math.Abs(Before) < 1e-9 || double.IsNaN(Before) || double.IsNaN(After)
        ? ""
        : (After >= Before ? "+" : "−") + ChampionMath.Number(Math.Abs(After - Before) / Math.Abs(Before) * 100, 0) + "%";
}

/// <summary>
/// The calculator's what-ifs: stats with a set of items, the items worth buying next, swapping one item for another, and
/// how a number grows from level 1 to 18 as items are finished.
/// </summary>
public static class BuildCalculator
{
    private static readonly ConditionalWeakTable<ItemInfo, IReadOnlyList<StatTotal>> StatCache = new();

    /// <summary>The stats listed on an item (cached; descriptions don't change within a patch).</summary>
    public static IReadOnlyList<StatTotal> StatsOf(ItemInfo item) => StatCache.GetValue(item, i => ItemStats.Parse(i.Description));

    /// <summary>Champion stats at <paramref name="level"/> with <paramref name="items"/> and (optionally) the stat shards.</summary>
    public static ChampionStatBlock StatsAt(ChampionStats champion, int level, IEnumerable<ItemInfo> items, IReadOnlyList<StatShardInfo?> shards, bool includeShards)
    {
        var bonuses = items.SelectMany(StatsOf).ToList();
        if (includeShards) bonuses.AddRange(ChampionMath.ShardStats(shards, level));
        return ChampionMath.Compute(champion, level, bonuses);
    }

    /// <summary>
    /// Items that could be bought next (nothing to replace) or instead of <paramref name="replacing"/>: finished items of
    /// the same kind, one per name (mode variants are the same item), on the chosen map, not already in <paramref name="owned"/>.
    /// </summary>
    public static IReadOnlyList<ItemInfo> Candidates(GameDataSnapshot game, IReadOnlyCollection<ItemInfo> owned, ItemInfo? replacing, bool aram)
    {
        var ownedNames = owned.Where(i => replacing is null || i.Name != replacing.Name).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasBoots = owned.Any(i => i.Tier == ItemTier.Boots && (replacing is null || i.Name != replacing.Name));
        bool Fits(ItemInfo i)
        {
            if (replacing is null)
                return i.Tier == ItemTier.Legendary || (!hasBoots && IsFinishedBoots(i));
            if (replacing.Tier == ItemTier.Legendary) return i.Tier == ItemTier.Legendary;
            if (replacing.Tier == ItemTier.Boots) return IsFinishedBoots(i);
            return i.Tier == replacing.Tier;
        }
        return game.ShopItems
            .Where(i => (aram ? i.OnAram : i.OnSummonersRift) && Fits(i) && !ownedNames.Contains(i.Name) && (replacing is null || i.Name != replacing.Name))
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Boots past the basic Boots (they're built from something).</summary>
    public static bool IsFinishedBoots(ItemInfo item) => item.Tier == ItemTier.Boots && item.From.Count > 0;

    /// <summary>
    /// Ranks <paramref name="candidates"/> by how much they improve <paramref name="metric"/> when added to
    /// <paramref name="owned"/> (or put in place of <paramref name="replacing"/>), best first; ties go to the cheaper item.
    /// </summary>
    public static IReadOnlyList<ItemChoice> Rank(IReadOnlyList<ItemInfo> owned, IEnumerable<ItemInfo> candidates, ItemInfo? replacing,
        CalcMetric metric, Func<IReadOnlyList<ItemInfo>, double> evaluate)
    {
        var before = evaluate(owned);
        var without = replacing is null ? owned.ToList() : RemoveOne(owned, replacing);
        return candidates
            .Select(c => new ItemChoice(c, before, evaluate([.. without, c]), metric))
            .OrderByDescending(c => c.Gain)
            .ThenBy(c => c.Item.TotalGold)
            .ThenBy(c => c.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary><paramref name="items"/> with the first copy of <paramref name="item"/> taken out.</summary>
    public static List<ItemInfo> RemoveOne(IReadOnlyList<ItemInfo> items, ItemInfo item)
    {
        var list = items.ToList();
        var i = list.FindIndex(x => x.Id == item.Id);
        if (i < 0) i = list.FindIndex(x => x.Name == item.Name);
        if (i >= 0) list.RemoveAt(i);
        return list;
    }

    /// <summary>Every metric that differs between two stat blocks (abilities first, then runes, then stats).</summary>
    public static IReadOnlyList<MetricChange> Changes(IEnumerable<CalcMetric> metrics, ChampionStatBlock before, ChampionStatBlock after, IReadOnlyDictionary<char, int> ranks)
    {
        var list = new List<MetricChange>();
        foreach (var m in metrics)
        {
            var a = m.Value(before, ranks);
            var b = m.Value(after, ranks);
            if (double.IsNaN(a) || double.IsNaN(b)) continue;
            if (Math.Abs(b - a) < Math.Max(0.005, Math.Abs(a) * 1e-4)) continue;
            list.Add(new MetricChange(m, a, b));
        }
        return list
            .OrderBy(c => c.Metric.Group switch { CalcMetric.AbilitiesGroup => 0, CalcMetric.RunesGroup => 1, _ => 2 })
            .ToList();
    }

    /// <summary>
    /// <paramref name="metric"/> at levels 1–18: items count from the level they're finished, stat shards grow with level and
    /// ability ranks follow the skill order. NaN where the metric doesn't apply (an ability not learned yet).
    /// </summary>
    public static double[] Curve(ChampionStats champion, IReadOnlyList<(ItemInfo Item, int Level)> timeline, IReadOnlyList<StatShardInfo?> shards,
        bool includeShards, string? skillOrder, IReadOnlyDictionary<char, int> maxRanks, CalcMetric metric)
    {
        var values = new double[ChampionMath.MaxLevel];
        for (var level = 1; level <= ChampionMath.MaxLevel; level++)
        {
            var items = timeline.Where(t => t.Level <= level).Select(t => t.Item);
            var stats = StatsAt(champion, level, items, shards, includeShards);
            values[level - 1] = metric.Value(stats, ChampionMath.RanksAt(skillOrder, level, maxRanks));
        }
        return values;
    }
}

/// <summary>When each item of a build is finished, for the power curve.</summary>
public static class ItemTimeline
{
    /// <summary>The starting guess for core items, in build order (later items are finished at 18).</summary>
    public static readonly IReadOnlyList<int> Spread = [6, 9, 11, 13, 15, 17];

    /// <summary>Starter items (Doran's, support and jungle items) at level 1, the rest spread over <see cref="Spread"/>.</summary>
    public static IReadOnlyList<int> DefaultLevels(IReadOnlyList<ItemInfo> items)
    {
        var levels = new List<int>(items.Count);
        var next = 0;
        foreach (var item in items)
        {
            if (item.Tier is ItemTier.Starter or ItemTier.Consumable or ItemTier.Trinket)
            {
                levels.Add(1);
                continue;
            }
            levels.Add(next < Spread.Count ? Spread[next] : ChampionMath.MaxLevel);
            next++;
        }
        return levels;
    }
}
