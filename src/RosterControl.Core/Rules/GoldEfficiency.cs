using RosterControl.Core.GameData;

namespace RosterControl.Core.Rules;

/// <summary>What one point of a stat costs, taken from the cheapest basic item that gives only that stat (e.g. Long Sword: 350 gold / 10 AD = 35), the usual convention.</summary>
public sealed record StatGoldValue(string Name, bool IsPercent, double GoldPerPoint, string ReferenceItem)
{
    /// <summary>The reference item's price.</summary>
    public int Gold { get; init; }

    public string Text => $"{ItemStats.ShortName(Name)} {ChampionMath.Number(GoldPerPoint, GoldPerPoint >= 10 ? 1 : 2)}g ({ReferenceItem})";
}

/// <summary>One stat of an item with what it's worth in gold.</summary>
public sealed record StatWorth(StatTotal Stat, double Gold);

/// <summary>An item's stats valued in gold, compared with its cost. Passives aren't valued.</summary>
/// <param name="Unvalued">Stats without a reference item (not counted in <see cref="Worth"/>).</param>
public sealed record ItemEfficiency(int Cost, double Worth, IReadOnlyList<StatWorth> Parts, IReadOnlyList<StatTotal> Unvalued)
{
    /// <summary>Worth / cost × 100; 100 % = the stats alone are worth the price.</summary>
    public double Percent => Cost > 0 ? Worth / Cost * 100 : 0;
    public string PercentText => $"{ChampionMath.Number(Percent, 0)}%";
}

/// <summary>Gold efficiency of items from the current patch's basic items (data-driven, so it follows every patch).</summary>
public static class GoldEfficiency
{
    public static IReadOnlyDictionary<(string Name, bool IsPercent), StatGoldValue> References(GameDataSnapshot game)
    {
        var refs = new Dictionary<(string, bool), StatGoldValue>();
        foreach (var item in game.ShopItems)
        {
            if (item.Tier is not (ItemTier.Basic or ItemTier.Boots) || item.TotalGold <= 0 || !item.OnSummonersRift) continue;
            var stats = ItemStats.Parse(item.Description);
            if (stats.Count != 1 || stats[0].Value <= 0) continue;
            var s = stats[0];
            var perPoint = item.TotalGold / s.Value;
            var key = (s.Name, s.IsPercent);
            if (!refs.TryGetValue(key, out var existing) || item.TotalGold < existing.Gold)
                refs[key] = new StatGoldValue(s.Name, s.IsPercent, perPoint, item.Name) { Gold = item.TotalGold };
        }
        return refs;
    }

    /// <summary>Null for items without a cost or without stats.</summary>
    public static ItemEfficiency? Of(ItemInfo item, IReadOnlyDictionary<(string Name, bool IsPercent), StatGoldValue> references)
    {
        if (item.TotalGold <= 0) return null;
        var stats = ItemStats.Parse(item.Description);
        if (stats.Count == 0) return null;
        var parts = new List<StatWorth>();
        var unvalued = new List<StatTotal>();
        foreach (var s in stats)
        {
            if (references.TryGetValue((s.Name, s.IsPercent), out var r)) parts.Add(new StatWorth(s, s.Value * r.GoldPerPoint));
            else unvalued.Add(s);
        }
        if (parts.Count == 0) return null;
        return new ItemEfficiency(item.TotalGold, parts.Sum(p => p.Gold), parts, unvalued);
    }
}
