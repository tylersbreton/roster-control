using System.Globalization;
using System.Text.RegularExpressions;
using RosterControl.Core.GameData;

namespace RosterControl.Core.Rules;

/// <summary>The game's stat numbers used by ability formulas (only the ones identified with certainty).</summary>
public static class StatKind
{
    public const int AbilityPower = 0;
    public const int Armor = 1;
    public const int AttackDamage = 2;
    public const int AttackSpeed = 4;
    public const int MagicResist = 6;
    public const int MoveSpeed = 7;
    public const int CritDamage = 8;
    public const int CritChance = 9;
    public const int Health = 12;
    public const int Lethality = 29;

    public static bool IsKnown(int stat) => stat is AbilityPower or Armor or AttackDamage or AttackSpeed or MagicResist or MoveSpeed or CritDamage or CritChance or Health or Lethality;
}

/// <summary>A champion's stats at one level with a set of items and stat shards (base and bonus kept apart, as formulas need both).</summary>
public sealed class ChampionStatBlock
{
    public int Level { get; init; }
    public double BaseHealth { get; init; }
    public double BonusHealth { get; set; }
    public double BaseMana { get; init; }
    public double BonusMana { get; set; }
    public double BaseAttackDamage { get; init; }
    public double BonusAttackDamage { get; set; }
    public double AbilityPower { get; set; }
    public double BaseArmor { get; init; }
    public double BonusArmor { get; set; }
    public double BaseMagicResist { get; init; }
    public double BonusMagicResist { get; set; }
    public double BaseAttackSpeed { get; init; }
    /// <summary>Bonus attack speed as a fraction (0.35 = +35%), including growth from levels.</summary>
    public double BonusAttackSpeed { get; set; }
    public double AbilityHaste { get; set; }
    /// <summary>Fraction, at most 1.</summary>
    public double CritChance { get; set; }
    /// <summary>Extra critical strike damage as a fraction (on top of the base 175%).</summary>
    public double BonusCritDamage { get; set; }
    public double BaseMoveSpeed { get; init; }
    public double FlatMoveSpeed { get; set; }
    /// <summary>Fraction.</summary>
    public double PercentMoveSpeed { get; set; }
    public double Lethality { get; set; }
    public double ArmorPenetration { get; set; }
    public double MagicPenetration { get; set; }
    public double PercentMagicPenetration { get; set; }
    public double LifeSteal { get; set; }
    public double Omnivamp { get; set; }
    public double HealShieldPower { get; set; }
    public double Tenacity { get; set; }
    public double AttackRange { get; init; }
    public bool IsRanged { get; init; }
    public string Resource { get; init; } = "";
    /// <summary>Stats from the items that aren't part of the calculation (e.g. regeneration), by name.</summary>
    public List<string> NotCounted { get; } = [];

    public const double BaseCritDamage = 1.75;

    public double Health => BaseHealth + BonusHealth;
    public double Mana => BaseMana + BonusMana;
    public double AttackDamage => BaseAttackDamage + BonusAttackDamage;
    public double Armor => BaseArmor + BonusArmor;
    public double MagicResist => BaseMagicResist + BonusMagicResist;
    public double AttackSpeed => BaseAttackSpeed * (1 + BonusAttackSpeed);
    public double MoveSpeed => (BaseMoveSpeed + FlatMoveSpeed) * (1 + PercentMoveSpeed);
    public double CritDamage => BaseCritDamage + BonusCritDamage;

    /// <summary>Cooldowns are multiplied by this (100 / (100 + ability haste)).</summary>
    public double CooldownMultiplier => 100 / (100 + Math.Max(0, AbilityHaste));

    /// <summary>The stat an ability formula refers to; <paramref name="formula"/>: 0 total, 1 base, 2 bonus.</summary>
    public double Get(int stat, int formula) => stat switch
    {
        StatKind.AbilityPower => formula == 1 ? 0 : AbilityPower,
        StatKind.Armor => Pick(BaseArmor, BonusArmor, formula),
        StatKind.AttackDamage => Pick(BaseAttackDamage, BonusAttackDamage, formula),
        StatKind.AttackSpeed => formula switch { 1 => BaseAttackSpeed, 2 => BonusAttackSpeed, _ => AttackSpeed },
        StatKind.MagicResist => Pick(BaseMagicResist, BonusMagicResist, formula),
        StatKind.MoveSpeed => formula switch { 1 => BaseMoveSpeed, 2 => MoveSpeed - BaseMoveSpeed, _ => MoveSpeed },
        StatKind.CritDamage => formula switch { 1 => BaseCritDamage, 2 => BonusCritDamage, _ => CritDamage },
        StatKind.CritChance => formula == 1 ? 0 : CritChance,
        StatKind.Health => Pick(BaseHealth, BonusHealth, formula),
        StatKind.Lethality => formula == 1 ? 0 : Lethality,
        _ => 0,
    };

    private static double Pick(double baseValue, double bonus, int formula) => formula switch
    {
        1 => baseValue,
        2 => bonus,
        _ => baseValue + bonus,
    };
}

/// <summary>Champion stats per level and ability numbers for a level, ranks and a set of items (the Abilities calculator).</summary>
public static partial class ChampionMath
{
    public const int MaxLevel = 18;

    /// <summary>How much of a stat's growth applies at <paramref name="level"/> (the game's curve: 0 at level 1, 17 at 18).</summary>
    public static double Growth(int level)
    {
        var l = Math.Clamp(level, 1, MaxLevel) - 1;
        return l * (0.7025 + 0.0175 * l);
    }

    /// <summary>Champion stats at <paramref name="level"/> plus <paramref name="bonuses"/> (item and shard stats).</summary>
    public static ChampionStatBlock Compute(ChampionStats stats, int level, IEnumerable<StatTotal> bonuses)
    {
        var g = Growth(level);
        var block = new ChampionStatBlock
        {
            Level = Math.Clamp(level, 1, MaxLevel),
            BaseHealth = stats.Health + stats.HealthPerLevel * g,
            BaseMana = stats.Mana + stats.ManaPerLevel * g,
            BaseAttackDamage = stats.AttackDamage + stats.AttackDamagePerLevel * g,
            BaseArmor = stats.Armor + stats.ArmorPerLevel * g,
            BaseMagicResist = stats.MagicResist + stats.MagicResistPerLevel * g,
            BaseAttackSpeed = stats.AttackSpeed,
            BonusAttackSpeed = stats.AttackSpeedPerLevel / 100 * g,
            BaseMoveSpeed = stats.MoveSpeed,
            AttackRange = stats.AttackRange,
            IsRanged = stats.IsRanged,
            Resource = stats.Resource,
        };
        var adaptive = 0.0;
        foreach (var s in bonuses)
        {
            var v = s.Value;
            var fraction = v / 100;
            switch (s.Name.Trim().ToLowerInvariant())
            {
                case "attack damage": block.BonusAttackDamage += v; break;
                case "ability power": block.AbilityPower += v; break;
                case "attack speed": block.BonusAttackSpeed += fraction; break;
                case "critical strike chance": block.CritChance += fraction; break;
                case "critical strike damage": block.BonusCritDamage += fraction; break;
                case "lethality": block.Lethality += v; break;
                case "armor penetration": block.ArmorPenetration += fraction; break;
                case "magic penetration" when s.IsPercent: block.PercentMagicPenetration += fraction; break;
                case "magic penetration": block.MagicPenetration += v; break;
                case "ability haste": block.AbilityHaste += v; break;
                case "health" when !s.IsPercent: block.BonusHealth += v; break;
                case "armor": block.BonusArmor += v; break;
                case "magic resist": block.BonusMagicResist += v; break;
                case "mana" when !s.IsPercent: block.BonusMana += v; break;
                case "move speed" when s.IsPercent: block.PercentMoveSpeed += fraction; break;
                case "move speed": block.FlatMoveSpeed += v; break;
                case "life steal": block.LifeSteal += fraction; break;
                case "omnivamp": block.Omnivamp += fraction; break;
                case "heal and shield power": block.HealShieldPower += fraction; break;
                case "tenacity" or "tenacity and slow resist": block.Tenacity += fraction; break;
                case "adaptive force": adaptive += v; break;
                default:
                    if (!block.NotCounted.Contains(s.Name, StringComparer.OrdinalIgnoreCase)) block.NotCounted.Add(s.Name);
                    break;
            }
        }
        block.CritChance = Math.Min(block.CritChance, 1);
        if (adaptive > 0)
        {
            // Adaptive force becomes whichever of bonus AD and AP is higher (1 force = 0.6 AD or 1 AP).
            var toAp = block.AbilityPower > block.BonusAttackDamage || (Math.Abs(block.AbilityPower - block.BonusAttackDamage) < 1e-9 && stats.AdaptiveAp);
            if (toAp) block.AbilityPower += adaptive;
            else block.BonusAttackDamage += adaptive * 0.6;
        }
        return block;
    }

    [GeneratedRegex(@"\+?\s*(?<from>\d+(?:\.\d+)?)\s*(?:[-–]\s*(?<to>\d+(?:\.\d+)?))?\s*(?<pct>%?)\s*(?<name>[^(]+?)\s*(?<level>\(based on level\))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ShardText();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    /// <summary>The stats the chosen stat shards give at <paramref name="level"/> ("+10-180 Health (based on level)" grows evenly).</summary>
    public static IReadOnlyList<StatTotal> ShardStats(IEnumerable<StatShardInfo?> shards, int level)
    {
        var list = new List<StatTotal>();
        foreach (var shard in shards)
        {
            if (shard is null) continue;
            var text = Tags().Replace(shard.ShortDesc ?? "", "").Trim();
            var m = ShardText().Match(text);
            if (!m.Success || !double.TryParse(m.Groups["from"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var from)) continue;
            var value = from;
            if (m.Groups["to"].Success && double.TryParse(m.Groups["to"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var to))
                value = from + (to - from) * (Math.Clamp(level, 1, MaxLevel) - 1) / (MaxLevel - 1.0);
            var name = m.Groups["name"].Value.Trim();
            if (name.Length > 0) list.Add(new StatTotal(name, value, m.Groups["pct"].Value == "%"));
        }
        return list;
    }

    /// <summary>The value of <paramref name="formula"/> at an ability <paramref name="rank"/> (1-based) and champion <paramref name="level"/>.</summary>
    public static double Evaluate(AbilityFormula formula, int rank, int level, ChampionStatBlock stats)
    {
        var r = Math.Clamp(rank, 1, Math.Max(1, formula.Ranks)) - 1;
        var value = formula.Base.Count > r ? formula.Base[r] : 0;
        if (formula.ByLevel is { Count: > 0 } byLevel) value += byLevel[Math.Clamp(level, 1, byLevel.Count) - 1];
        foreach (var ratio in formula.Ratios)
        {
            var coefficient = ratio.Coefficient.Count == 0 ? 0 : ratio.Coefficient[Math.Min(r, ratio.Coefficient.Count - 1)];
            value += coefficient * stats.Get(ratio.Stat, ratio.Formula);
        }
        if (stats.IsRanged) value *= formula.RangedMultiplier;
        return value;
    }

    /// <summary>"412", "8.5%", "1.25" — whole numbers for big values, a decimal for small ones.</summary>
    public static string Format(AbilityFormula formula, double value)
    {
        if (formula.IsPercent)
        {
            var pct = value * 100;
            return Number(pct, Math.Abs(pct) >= 100 ? 0 : 1) + "%";
        }
        var digits = formula.Precision is { } p ? Math.Clamp(p, 0, 2) : Math.Abs(value) >= 100 ? 0 : Math.Abs(value) >= 10 ? 1 : 2;
        return Number(value, digits);
    }

    public static string Number(double value, int digits)
    {
        var rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
        var text = rounded.ToString(digits == 0 ? "#,0" : "#,0." + new string('#', digits), CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>"11/10/9/8/7s" → [11, 10, 9, 8, 7]; empty when there are no numbers.</summary>
    public static IReadOnlyList<double> ParseSeries(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var list = new List<double>();
        foreach (var part in text.Trim().TrimEnd('s', 'S').Split('/'))
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return [];
            list.Add(v);
        }
        return list;
    }

    /// <summary>Rank of each ability at <paramref name="level"/> from a skill order; with no skill order, every ability at max rank.</summary>
    public static IReadOnlyDictionary<char, int> RanksAt(string? skillOrder, int level, IReadOnlyDictionary<char, int> maxRanks)
    {
        var ranks = new Dictionary<char, int>();
        var none = SkillOrderRules.IsEmpty(skillOrder);
        foreach (var (slot, max) in maxRanks)
            ranks[slot] = none ? max : Math.Min(max, SkillOrderRules.RankAt(skillOrder, slot, level));
        return ranks;
    }
}
