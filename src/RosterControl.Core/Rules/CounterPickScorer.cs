using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>A roster entry together with the role of the group it is in (drives which matchups apply).</summary>
public sealed record ScopedEntry(RosterEntry Entry, Position? Role);

/// <summary>Why a candidate is good or bad into one enemy.</summary>
/// <param name="Inferred">True when derived from the enemy's own matchup list ("Enemy is weak against Candidate").</param>
/// <param name="Role">Role the matchup was recorded for (null = all roles).</param>
public sealed record CounterReason(string EnemyId, bool Favorable, string Note, bool Inferred, Position? Role = null);

/// <summary>A champion you could pick (in one role, when it comes from a roster group), with its reasons.</summary>
public sealed record CounterCandidate(string ChampionId, Position? Role, IReadOnlyList<CounterReason> Reasons, RosterEntry? Entry)
{
    public int Favorable => Reasons.Count(r => r.Favorable);
    public int Unfavorable => Reasons.Count(r => !r.Favorable);
    public int Score => Favorable - Unfavorable;
    public bool InRoster => Entry is not null;
}

/// <summary>
/// Which recorded matchups apply to a champion in a role: a matchup recorded for that role overrides the all-roles
/// matchup for the same opponent; matchups recorded for other roles don't apply. With no role (champion outside the
/// roster, "all groups") every matchup applies, preferring the all-roles one.
/// </summary>
public static class MatchupResolver
{
    /// <param name="relevant">Matchups between the same two champions (any roles).</param>
    public static Matchup? Pick(IEnumerable<Matchup> relevant, Position? role)
    {
        var list = relevant.ToList();
        if (role is { } r)
            return list.FirstOrDefault(m => m.Role == r) ?? list.FirstOrDefault(m => m.Role is null);
        return list.FirstOrDefault(m => m.Role is null) ?? list.FirstOrDefault();
    }

    /// <summary>The applicable reason (explicit, or inferred from the opponent's own list) for champion vs opponent.</summary>
    public static CounterReason? Reason(string championId, string opponentId, Position? role, ILookup<(string, string), Matchup> byPair)
    {
        if (Pick(byPair[Key(championId, opponentId)], role) is { } own)
            return new CounterReason(opponentId, own.Kind == MatchupKind.GoodAgainst, own.Note, Inferred: false, own.Role);
        // "Opponent is weak against Champion" => Champion is good into Opponent (and vice versa).
        if (Pick(byPair[Key(opponentId, championId)], role) is { } theirs)
            return new CounterReason(opponentId, theirs.Kind == MatchupKind.WeakAgainst, theirs.Note, Inferred: true, theirs.Role);
        return null;
    }

    public static ILookup<(string, string), Matchup> Index(IEnumerable<Matchup> matchups) =>
        matchups.ToLookup(m => Key(m.ChampionId, m.OpponentId));

    public static (string, string) Key(string champion, string opponent) => (champion.ToLowerInvariant(), opponent.ToLowerInvariant());
}

/// <summary>Ranks the player's champions against a set of enemy picks using the recorded matchups.</summary>
public static class CounterPickScorer
{
    /// <param name="enemyIds">Enemy champion ids (any number).</param>
    /// <param name="matchups">All recorded matchups.</param>
    /// <param name="entries">Roster entries in scope with their group's role; each placement is ranked separately.</param>
    /// <param name="includeOutsideRoster">Also return champions outside the scope that have matchups vs the enemies.</param>
    /// <param name="outsideRole">Role used for champions outside the roster (the scope's role, if any).</param>
    public static IReadOnlyList<CounterCandidate> Rank(
        IReadOnlyCollection<string> enemyIds,
        IReadOnlyCollection<Matchup> matchups,
        IReadOnlyCollection<ScopedEntry> entries,
        bool includeOutsideRoster,
        Position? outsideRole = null)
    {
        var enemies = new HashSet<string>(enemyIds, StringComparer.OrdinalIgnoreCase);
        var byPair = MatchupResolver.Index(matchups);

        List<CounterReason> ReasonsFor(string championId, Position? role) =>
            enemies.Select(e => MatchupResolver.Reason(championId, e, role, byPair)).Where(r => r is not null).Cast<CounterReason>().ToList();

        var candidates = new List<CounterCandidate>();
        var rostered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scoped in entries)
        {
            var id = scoped.Entry.ChampionId;
            rostered.Add(id);
            if (enemies.Contains(id)) continue; // an enemy pick can't be your pick
            candidates.Add(new CounterCandidate(id, scoped.Role, ReasonsFor(id, scoped.Role), scoped.Entry));
        }

        if (includeOutsideRoster)
        {
            // Champions mentioned in matchups with the enemies (either side).
            var mentioned = matchups
                .SelectMany(m => enemies.Contains(m.OpponentId) ? [m.ChampionId] : enemies.Contains(m.ChampionId) ? new[] { m.OpponentId } : [])
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var id in mentioned)
            {
                if (rostered.Contains(id) || enemies.Contains(id)) continue;
                var reasons = ReasonsFor(id, outsideRole);
                if (reasons.Count > 0) candidates.Add(new CounterCandidate(id, outsideRole, reasons, null));
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Favorable)
            .ThenByDescending(c => c.InRoster)
            .ThenBy(c => c.Entry?.Priority ?? ChampionPriority.Learning + 1)
            .ThenByDescending(c => c.Entry?.Proficiency ?? 0)
            .ThenBy(c => c.ChampionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>One of your champions threatened by a ban candidate.</summary>
public sealed record ThreatenedEntry(ScopedEntry Scoped, string Note, bool Inferred, double Weight);

public sealed record BanSuggestion(string OpponentId, IReadOnlyList<ThreatenedEntry> Threatened, int Answers)
{
    /// <summary>Priority-weighted number of your champions it counters, minus a little for each of your answers to it.</summary>
    public double Score => Threatened.Sum(t => t.Weight) - 0.5 * Answers;
}

/// <summary>
/// Suggests bans: opponents that counter the most (and most important) of your champions, according to your matchups.
/// Mains weigh most; proficiency adds a little. Champions you have good answers to rank lower.
/// </summary>
public static class BanSuggester
{
    public static double Weight(RosterEntry entry) => entry.Priority switch
    {
        ChampionPriority.Main => 3,
        ChampionPriority.Secondary => 2,
        ChampionPriority.Pocket => 1.25,
        _ => 1,
    } + entry.Proficiency * 0.1;

    public static IReadOnlyList<BanSuggestion> Suggest(IReadOnlyCollection<Matchup> matchups, IReadOnlyCollection<ScopedEntry> entries, int count = 5)
    {
        var byPair = MatchupResolver.Index(matchups);
        var yours = entries.Select(e => e.Entry.ChampionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Every champion that appears in a matchup with one of your champions is a potential threat.
        var opponents = matchups
            .SelectMany(m => yours.Contains(m.ChampionId) ? [m.OpponentId] : yours.Contains(m.OpponentId) ? new[] { m.ChampionId } : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var suggestions = new List<BanSuggestion>();
        foreach (var opponent in opponents)
        {
            var threatened = new List<ThreatenedEntry>();
            var answers = 0;
            foreach (var scoped in entries)
            {
                if (string.Equals(scoped.Entry.ChampionId, opponent, StringComparison.OrdinalIgnoreCase)) continue;
                var reason = MatchupResolver.Reason(scoped.Entry.ChampionId, opponent, scoped.Role, byPair);
                if (reason is null) continue;
                if (reason.Favorable) answers++;
                else threatened.Add(new ThreatenedEntry(scoped, reason.Note, reason.Inferred, Weight(scoped.Entry)));
            }
            if (threatened.Count == 0) continue;
            suggestions.Add(new BanSuggestion(opponent,
                threatened.OrderByDescending(t => t.Weight).ThenBy(t => t.Scoped.Entry.ChampionId, StringComparer.OrdinalIgnoreCase).ToList(), answers));
        }

        return suggestions
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Threatened.Count)
            .ThenBy(s => s.OpponentId, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
    }
}
