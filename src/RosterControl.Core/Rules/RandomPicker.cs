using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>A champion that can come up in a random pick, with its chance weight and the placements it came from.</summary>
/// <param name="Placements">The ticked groups the champion is in (empty when picking from every champion).</param>
public sealed record PickCandidate(string ChampionId, double Weight, IReadOnlyList<PickPlacement> Placements);

/// <summary>A champion's placement in one ticked group.</summary>
public sealed record PickPlacement(RosterGroup Group, RosterEntry Entry);

/// <summary>Random champion picks from roster groups or from every champion.</summary>
public static class RandomPicker
{
    /// <summary>
    /// One candidate per champion in the ticked groups. With <paramref name="weighted"/>, a champion's weight is its best
    /// placement's priority and stars (the same weights as ban suggestions: Main 3, Secondary 2, Pocket 1.25, Learning 1,
    /// +0.1 per star); otherwise every champion is equally likely, however many of the groups it's in.
    /// </summary>
    /// <param name="known">Champions in the game data; placements of others (e.g. removed from the game) are skipped.</param>
    public static IReadOnlyList<PickCandidate> FromGroups(IEnumerable<RosterGroup> groups, bool weighted, IReadOnlySet<string> known) =>
        groups
            .SelectMany(g => g.Entries.Select(e => new PickPlacement(g, e)))
            .Where(p => known.Contains(p.Entry.ChampionId))
            .GroupBy(p => p.Entry.ChampionId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PickCandidate(g.Key, weighted ? g.Max(p => BanSuggester.Weight(p.Entry)) : 1, g.ToList()))
            .ToList();

    /// <summary>Every champion, equally likely.</summary>
    public static IReadOnlyList<PickCandidate> FromAll(IEnumerable<string> championIds) =>
        championIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => new PickCandidate(id, 1, [])).ToList();

    /// <summary>The chance of <paramref name="candidate"/> coming up (0–1).</summary>
    public static double Chance(PickCandidate candidate, IReadOnlyCollection<PickCandidate> candidates)
    {
        var total = candidates.Sum(c => c.Weight);
        return total > 0 ? candidate.Weight / total : 0;
    }

    /// <summary>A weighted random candidate, or null when there are none. The same champion can come up twice in a row.</summary>
    public static PickCandidate? Pick(IReadOnlyList<PickCandidate> candidates, Random random)
    {
        if (candidates.Count == 0) return null;
        var total = candidates.Sum(c => c.Weight);
        var roll = random.NextDouble() * total;
        foreach (var c in candidates)
        {
            roll -= c.Weight;
            if (roll < 0) return c;
        }
        return candidates[^1];
    }
}
