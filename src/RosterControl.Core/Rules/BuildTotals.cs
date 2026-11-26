using System.Globalization;
using System.Text.RegularExpressions;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>One stat summed over several items, e.g. 95 Attack Damage or 35 % Attack Speed.</summary>
public sealed record StatTotal(string Name, double Value, bool IsPercent)
{
    public string ValueText => Format(Value) + (IsPercent ? "%" : "");
    public string Text => $"{ValueText} {Name}";

    /// <summary>Compact form for small spaces: "95 AD", "35% AS".</summary>
    public string ShortText => $"{ValueText} {ItemStats.ShortName(Name)}";

    internal static string Format(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.05 ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>Gold cost and combined stats of a set of items (a build's core block).</summary>
/// <param name="MissingItems">Items no longer in the game, so not counted.</param>
public sealed record BuildTotals(int Gold, int ItemCount, IReadOnlyList<StatTotal> Stats, int MissingItems)
{
    public string GoldText => Gold.ToString("N0", CultureInfo.InvariantCulture) + " gold";
    public bool IsEmpty => ItemCount == 0;

    /// <summary>One line: "9,100 gold · 95 AD · 20 AH · 300 HP" (the first <paramref name="maxStats"/> stats).</summary>
    public string Summary(int maxStats = 5) =>
        string.Join(" · ", new[] { GoldText }.Concat(Stats.Take(maxStats).Select(s => s.ShortText)));
}

/// <summary>Item stats as listed in each item's description (Data Dragon's &lt;stats&gt; section), which is current every patch.</summary>
public static partial class ItemStats
{
    /// <summary>Display order; anything else follows alphabetically.</summary>
    private static readonly string[] Order =
    [
        "Attack Damage", "Ability Power", "Attack Speed", "Critical Strike Chance", "Critical Strike Damage", "Lethality",
        "Armor Penetration", "Magic Penetration", "Ability Haste", "Health", "Armor", "Magic Resist", "Mana", "Move Speed",
        "Life Steal", "Omnivamp", "Heal and Shield Power", "Tenacity", "Adaptive Force",
    ];

    private static readonly Dictionary<string, string> Short = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Attack Damage"] = "AD",
        ["Ability Power"] = "AP",
        ["Attack Speed"] = "AS",
        ["Critical Strike Chance"] = "Crit",
        ["Critical Strike Damage"] = "Crit Dmg",
        ["Armor Penetration"] = "Armor Pen",
        ["Magic Penetration"] = "Magic Pen",
        ["Ability Haste"] = "AH",
        ["Health"] = "HP",
        ["Magic Resist"] = "MR",
        ["Move Speed"] = "MS",
        ["Heal and Shield Power"] = "Heal & Shield",
    };

    public static string ShortName(string name) => Short.GetValueOrDefault(name, name);

    [GeneratedRegex("<stats>(.*?)</stats>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StatsBlock();

    [GeneratedRegex(@"<attention>\s*([-+]?\d+(?:\.\d+)?)\s*(%?)\s*</attention>\s*([^<]+)", RegexOptions.IgnoreCase)]
    private static partial Regex StatLine();

    /// <summary>The stats an item grants, from its description.</summary>
    public static IReadOnlyList<StatTotal> Parse(string? description)
    {
        if (string.IsNullOrEmpty(description) || StatsBlock().Match(description) is not { Success: true } block) return [];
        var stats = new List<StatTotal>();
        foreach (Match m in StatLine().Matches(block.Groups[1].Value))
        {
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            var name = m.Groups[3].Value.Trim();
            if (name.Length > 0) stats.Add(new StatTotal(name, value, m.Groups[2].Value == "%"));
        }
        return stats;
    }

    /// <summary>Sums gold and stats over <paramref name="itemIds"/> (duplicates count twice).</summary>
    public static BuildTotals Sum(IEnumerable<string> itemIds, GameDataSnapshot game)
    {
        var gold = 0;
        var count = 0;
        var missing = 0;
        var sums = new Dictionary<(string Name, bool Percent), double>();
        foreach (var id in itemIds)
        {
            if (game.Item(id) is not { } item)
            {
                missing++;
                continue;
            }
            count++;
            gold += item.TotalGold;
            foreach (var s in Parse(item.Description))
                sums[(s.Name, s.IsPercent)] = sums.GetValueOrDefault((s.Name, s.IsPercent)) + s.Value;
        }
        var stats = sums.Select(kv => new StatTotal(kv.Key.Name, kv.Value, kv.Key.Percent))
            .OrderBy(s => Array.IndexOf(Order, s.Name) is var i && i >= 0 ? i : Order.Length)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.IsPercent)
            .ToList();
        return new BuildTotals(gold, count, stats, missing);
    }

    /// <summary>Totals for the build's core block (see <see cref="BuildSelection.CoreBlock"/>); null when it has no items.</summary>
    public static BuildTotals? Core(Build build, GameDataSnapshot game) =>
        BuildSelection.CoreBlock(build) is { Items.Count: > 0 } core ? Sum(core.Items, game) : null;
}
