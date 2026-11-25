namespace RosterControl.Core.Rules;

/// <summary>
/// Skill order helpers. A skill order is a string with one character per champion level (Q, W, E, R or '-').
/// Validation is advisory: invalid levels are highlighted but never blocked (some champions have special rules).
/// </summary>
public static class SkillOrderRules
{
    public const int MaxLevel = 18;
    public static readonly IReadOnlyList<char> Slots = ['Q', 'W', 'E', 'R'];

    public static char At(string? order, int level) =>
        order is not null && level >= 1 && level <= order.Length ? char.ToUpperInvariant(order[level - 1]) : '-';

    /// <summary>Assigns <paramref name="slot"/> at <paramref name="level"/> (1-based); assigning the same slot again clears it.</summary>
    public static string Set(string? order, int level, char slot)
    {
        if (level is < 1 or > MaxLevel) return order ?? "";
        var chars = (order ?? "").PadRight(MaxLevel, '-').ToCharArray();
        slot = char.ToUpperInvariant(slot);
        chars[level - 1] = chars[level - 1] == slot ? '-' : slot;
        return new string(chars).TrimEnd('-');
    }

    public static int RankAt(string? order, char slot, int level)
    {
        var rank = 0;
        for (var l = 1; l <= level; l++)
            if (At(order, l) == slot) rank++;
        return rank;
    }

    /// <summary>Levels whose assignment breaks the usual rules (too many ranks, basic rank above ceil(level/2), ultimate before 6/11/16).</summary>
    public static IReadOnlySet<int> InvalidLevels(string? order, int basicMaxRank = 5, int ultimateMaxRank = 3)
    {
        var invalid = new HashSet<int>();
        var ranks = new Dictionary<char, int> { ['Q'] = 0, ['W'] = 0, ['E'] = 0, ['R'] = 0 };
        for (var level = 1; level <= MaxLevel; level++)
        {
            var slot = At(order, level);
            if (!ranks.ContainsKey(slot)) continue;
            var rank = ++ranks[slot];
            if (slot == 'R' && ultimateMaxRank <= 3)
            {
                var required = 6 + 5 * (rank - 1); // 6, 11, 16
                if (rank > ultimateMaxRank || level < required) invalid.Add(level);
            }
            else
            {
                var max = slot == 'R' ? ultimateMaxRank : basicMaxRank;
                if (rank > max || rank > (level + 1) / 2) invalid.Add(level);
            }
        }
        return invalid;
    }

    /// <summary>
    /// The order in which basic abilities are maxed ("Q &gt; E &gt; W"): abilities that reach their last rank first come
    /// first; abilities never maxed follow by rank count, then by first point.
    /// </summary>
    public static IReadOnlyList<char> MaxOrder(string? order, int basicMaxRank = 5)
    {
        var stats = new[] { 'Q', 'W', 'E' }.Select(slot =>
        {
            int rank = 0, maxedAt = int.MaxValue, first = int.MaxValue;
            for (var level = 1; level <= MaxLevel; level++)
            {
                if (At(order, level) != slot) continue;
                rank++;
                if (first == int.MaxValue) first = level;
                if (rank == basicMaxRank && maxedAt == int.MaxValue) maxedAt = level;
            }
            return (slot, rank, maxedAt, first);
        }).Where(s => s.rank > 0).ToList();
        return stats.OrderBy(s => s.maxedAt).ThenByDescending(s => s.rank).ThenBy(s => s.first).Select(s => s.slot).ToList();
    }

    public static bool IsEmpty(string? order) => string.IsNullOrEmpty(order) || order.All(c => c == '-');
}
