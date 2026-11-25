using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Tests;

public class CounterPickScorerTests
{
    internal static Matchup M(string champion, string opponent, MatchupKind kind, string note = "", Position? role = null) =>
        new() { ChampionId = champion, OpponentId = opponent, Kind = kind, Note = note, Role = role };

    internal static ScopedEntry E(string champion, ChampionPriority priority = ChampionPriority.Main, int proficiency = 3, Position? role = null, int group = 1) =>
        new(new RosterEntry { ChampionId = champion, Priority = priority, Proficiency = proficiency, GroupId = group }, role);

    [Test]
    public void Candidates_are_ranked_by_good_minus_bad_matchups()
    {
        var matchups = new[]
        {
            M("Malphite", "Vayne", MatchupKind.GoodAgainst),
            M("Malphite", "Garen", MatchupKind.GoodAgainst),
            M("Darius", "Garen", MatchupKind.GoodAgainst),
            M("Darius", "Vayne", MatchupKind.WeakAgainst),
            M("Jinx", "Vayne", MatchupKind.WeakAgainst),
        };
        var ranked = CounterPickScorer.Rank(["Garen", "Vayne"], matchups, [E("Malphite"), E("Darius"), E("Jinx")], includeOutsideRoster: false);
        Assert.That(ranked.Select(r => (r.ChampionId, r.Score)), Is.EqualTo(new[] { ("Malphite", 2), ("Darius", 0), ("Jinx", -1) }));
    }

    [Test]
    public void Matchups_recorded_on_the_enemy_count_inversely()
    {
        var ranked = CounterPickScorer.Rank(["Veigar"], [M("Veigar", "Syndra", MatchupKind.GoodAgainst, "cage")], [E("Syndra")], false);
        var syndra = ranked.Single();
        Assert.That(syndra.Score, Is.EqualTo(-1));
        Assert.That(syndra.Reasons.Single().Inferred, Is.True);
        Assert.That(syndra.Reasons.Single().Note, Is.EqualTo("cage"));
    }

    [Test]
    public void Explicit_matchups_win_over_inferred_ones()
    {
        var matchups = new[] { M("Ahri", "Fizz", MatchupKind.GoodAgainst), M("Fizz", "Ahri", MatchupKind.GoodAgainst) };
        var ahri = CounterPickScorer.Rank(["Fizz"], matchups, [E("Ahri")], false).Single();
        Assert.That(ahri.Reasons.Single().Favorable, Is.True);
        Assert.That(ahri.Reasons.Single().Inferred, Is.False);
    }

    [Test]
    public void Enemies_are_never_suggested_and_outside_roster_is_optional()
    {
        var matchups = new[] { M("Zed", "Lux", MatchupKind.GoodAgainst), M("Lux", "Zed", MatchupKind.WeakAgainst) };
        var withOutside = CounterPickScorer.Rank(["Lux"], matchups, [E("Ahri")], includeOutsideRoster: true);
        Assert.That(withOutside.Select(c => c.ChampionId), Does.Contain("Zed").And.Not.Contain("Lux"));
        var rosterOnly = CounterPickScorer.Rank(["Lux"], matchups, [E("Ahri")], includeOutsideRoster: false);
        Assert.That(rosterOnly.Select(c => c.ChampionId), Is.EqualTo(new[] { "Ahri" }));
    }

    [Test]
    public void Ties_prefer_higher_priority_then_proficiency()
    {
        var ranked = CounterPickScorer.Rank(["Garen"], [], [E("A", ChampionPriority.Pocket, 5), E("B", ChampionPriority.Main, 1), E("C", ChampionPriority.Main, 4)], false);
        Assert.That(ranked.Select(r => r.ChampionId), Is.EqualTo(new[] { "C", "B", "A" }));
    }

    [Test]
    public void Role_specific_matchups_override_all_roles_for_that_role_only()
    {
        var matchups = new[]
        {
            M("Pantheon", "Darius", MatchupKind.GoodAgainst, "all roles"),
            M("Pantheon", "Darius", MatchupKind.WeakAgainst, "top lane", Position.Top),
        };
        var ranked = CounterPickScorer.Rank(["Darius"], matchups,
            [E("Pantheon", role: Position.Top, group: 1), E("Pantheon", role: Position.Support, group: 5)], false);

        var top = ranked.Single(c => c.Role == Position.Top);
        var support = ranked.Single(c => c.Role == Position.Support);
        Assert.That(top.Score, Is.EqualTo(-1));
        Assert.That(top.Reasons.Single().Note, Is.EqualTo("top lane"));
        Assert.That(support.Score, Is.EqualTo(1), "support falls back to the all-roles matchup");
        Assert.That(support.Reasons.Single().Note, Is.EqualTo("all roles"));
    }

    [Test]
    public void Matchups_for_another_role_do_not_apply()
    {
        var matchups = new[] { M("Pantheon", "Darius", MatchupKind.WeakAgainst, role: Position.Top) };
        var support = CounterPickScorer.Rank(["Darius"], matchups, [E("Pantheon", role: Position.Support)], false).Single();
        Assert.That(support.Reasons, Is.Empty);
        var noRole = CounterPickScorer.Rank(["Darius"], matchups, [E("Pantheon")], false).Single();
        Assert.That(noRole.Score, Is.EqualTo(-1), "without a role, any recorded matchup is used");
    }
}

public class BanSuggesterTests
{
    private static Matchup M(string c, string o, MatchupKind k, Position? role = null, string note = "") => CounterPickScorerTests.M(c, o, k, note, role);
    private static ScopedEntry E(string c, ChampionPriority p = ChampionPriority.Main, Position? role = null) => CounterPickScorerTests.E(c, p, 3, role);

    [Test]
    public void Opponents_countering_your_mains_rank_first()
    {
        var matchups = new[]
        {
            M("Ahri", "Fizz", MatchupKind.WeakAgainst, note: "hold R"),
            M("Syndra", "Fizz", MatchupKind.WeakAgainst),
            M("Orianna", "Kassadin", MatchupKind.WeakAgainst),
            M("Ahri", "Veigar", MatchupKind.GoodAgainst),
        };
        var bans = BanSuggester.Suggest(matchups, [E("Ahri"), E("Syndra", ChampionPriority.Learning), E("Orianna", ChampionPriority.Secondary)]);
        Assert.That(bans.Select(b => b.OpponentId), Is.EqualTo(new[] { "Fizz", "Kassadin" }));
        Assert.That(bans[0].Threatened.Select(t => t.Scoped.Entry.ChampionId), Is.EqualTo(new[] { "Ahri", "Syndra" }), "Mains first");
        Assert.That(bans[0].Threatened[0].Note, Is.EqualTo("hold R"));
        Assert.That(bans.Select(b => b.OpponentId), Does.Not.Contain("Veigar"), "only threats are suggested");
    }

    [Test]
    public void Inferred_threats_count_and_answers_lower_the_score()
    {
        var matchups = new[]
        {
            M("Zed", "Ahri", MatchupKind.GoodAgainst),      // recorded on Zed: he beats Ahri
            M("Zed", "Lissandra", MatchupKind.WeakAgainst), // ...but Lissandra beats him
            M("Fizz", "Ahri", MatchupKind.GoodAgainst),
        };
        var bans = BanSuggester.Suggest(matchups, [E("Ahri"), E("Lissandra")]);
        var zed = bans.Single(b => b.OpponentId == "Zed");
        var fizz = bans.Single(b => b.OpponentId == "Fizz");
        Assert.That(zed.Threatened.Single().Inferred, Is.True);
        Assert.That(zed.Answers, Is.EqualTo(1));
        Assert.That(fizz.Score, Is.GreaterThan(zed.Score));
        Assert.That(bans[0].OpponentId, Is.EqualTo("Fizz"));
    }

    [Test]
    public void Bans_use_the_role_of_each_group()
    {
        var matchups = new[] { M("Pantheon", "Darius", MatchupKind.WeakAgainst, Position.Top) };
        Assert.That(BanSuggester.Suggest(matchups, [E("Pantheon", role: Position.Support)]), Is.Empty);
        Assert.That(BanSuggester.Suggest(matchups, [E("Pantheon", role: Position.Top)]).Single().OpponentId, Is.EqualTo("Darius"));
    }
}

public class RecipeTests
{
    private static GameDataSnapshot Game => Fixtures.Game;

    [Test]
    public void Recipe_tree_follows_components_with_their_costs()
    {
        var ie = Game.Items["3031"];
        var tree = ItemCatalog.BuildRecipe(ie, Game.Items);
        Assert.That(tree.Item.Name, Is.EqualTo("Infinity Edge"));
        Assert.That(ie.CombineCost, Is.GreaterThan(0).And.LessThan(ie.TotalGold));
        // Components the trimmed fixture knows about are resolved; the total of a node equals its combine cost + components.
        foreach (var c in tree.Components) Assert.That(ie.From, Does.Contain(c.Item.Id));
    }

    [Test]
    public void Builds_into_lists_shop_items_only()
    {
        var boots = Game.Items["1001"];
        var into = ItemCatalog.BuildsInto(boots, Game.Items, Game.ShopItemIds);
        Assert.That(into.Select(i => i.Name), Does.Contain("Berserker's Greaves"));
        Assert.That(into.All(i => Game.ShopItemIds.Contains(i.Id)), Is.True);
        Assert.That(into.Select(i => i.TotalGold), Is.Ordered);

        var longSword = Game.Items["1036"];
        var swordInto = ItemCatalog.BuildsInto(longSword, Game.Items, Game.ShopItemIds).Select(i => i.Id).ToList();
        Assert.That(swordInto, Does.Not.Contain("323004"), "mode variants are not shown");
    }

    [Test]
    public void Recipe_depth_is_bounded()
    {
        var item = new ItemInfo("x", "Loop", "", "", 100, [], ["x"], [], "x.png", true, true, ItemTier.Legendary);
        var tree = ItemCatalog.BuildRecipe(item, new Dictionary<string, ItemInfo> { ["x"] = item }, maxDepth: 3);
        Assert.That(tree.Depth, Is.EqualTo(3), "self-referencing data can't recurse forever");
    }
}
