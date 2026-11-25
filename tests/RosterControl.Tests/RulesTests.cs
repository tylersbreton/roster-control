using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Tests;

public class RunePageRulesTests
{
    private static GameDataSnapshot Game => Fixtures.Game;

    private static RunePageData NewPage(string primary = "Domination", string? secondary = "Sorcery")
    {
        var page = new RunePageData();
        RunePageRules.SelectPrimaryTree(page, Fixtures.Tree(primary).Id);
        if (secondary is not null) RunePageRules.SelectSubTree(page, Fixtures.Tree(secondary).Id);
        return page;
    }

    [Test]
    public void Choosing_a_primary_rune_sets_its_row()
    {
        var page = NewPage();
        var tree = Fixtures.Tree("Domination");
        Assert.That(RunePageRules.SelectPrimaryRune(page, Game, tree.Rows[0][1].Id), Is.True);
        Assert.That(RunePageRules.SelectPrimaryRune(page, Game, tree.Rows[2][0].Id), Is.True);
        Assert.That(page.PrimaryPerks, Is.EqualTo(new[] { tree.Rows[0][1].Id, 0, tree.Rows[2][0].Id, 0 }));
        Assert.That(RunePageRules.SelectPrimaryRune(page, Game, tree.Rows[0][2].Id), Is.True, "replaces the keystone");
        Assert.That(page.PrimaryPerks[0], Is.EqualTo(tree.Rows[0][2].Id));
    }

    [Test]
    public void Runes_from_another_tree_are_rejected()
    {
        var page = NewPage();
        Assert.That(RunePageRules.SelectPrimaryRune(page, Game, Fixtures.Tree("Precision").Rows[0][0].Id), Is.False);
        Assert.That(RunePageRules.SelectSubRune(page, Game, Fixtures.Tree("Domination").Rows[1][0].Id), Is.False);
    }

    [Test]
    public void Secondary_allows_two_runes_from_different_rows_and_replaces_the_oldest()
    {
        var page = NewPage();
        var sorcery = Fixtures.Tree("Sorcery");
        var a = sorcery.Rows[1][0].Id;
        var b = sorcery.Rows[2][0].Id;
        var c = sorcery.Rows[3][0].Id;
        RunePageRules.SelectSubRune(page, Game, a);
        RunePageRules.SelectSubRune(page, Game, b);
        Assert.That(page.SubPerks, Is.EqualTo(new[] { a, b }));
        RunePageRules.SelectSubRune(page, Game, c);
        Assert.That(page.SubPerks, Is.EqualTo(new[] { b, c }), "third pick replaces the oldest");
        var sameRowAsB = sorcery.Rows[2][1].Id;
        RunePageRules.SelectSubRune(page, Game, sameRowAsB);
        Assert.That(page.SubPerks, Is.EqualTo(new[] { c, sameRowAsB }), "a pick in an occupied row replaces that row");
    }

    [Test]
    public void Keystones_cannot_be_taken_as_secondary_runes()
    {
        var page = NewPage();
        Assert.That(RunePageRules.SelectSubRune(page, Game, Fixtures.Tree("Sorcery").Rows[0][0].Id), Is.False);
    }

    [Test]
    public void Changing_primary_to_the_secondary_tree_clears_the_secondary()
    {
        var page = NewPage();
        RunePageRules.SelectSubRune(page, Game, Fixtures.Tree("Sorcery").Rows[1][0].Id);
        RunePageRules.SelectPrimaryTree(page, Fixtures.Tree("Sorcery").Id);
        Assert.That(page.SubStyleId, Is.Null);
        Assert.That(page.SubPerks, Is.Empty);
        Assert.That(page.PrimaryPerks, Is.All.EqualTo(0));
        Assert.That(RunePageRules.SelectSubTree(page, Fixtures.Tree("Sorcery").Id), Is.False, "secondary can't equal primary");
    }

    [Test]
    public void Shards_must_belong_to_their_row()
    {
        var page = NewPage();
        Assert.That(RunePageRules.SelectShard(page, Game, 0, 5005), Is.True, "attack speed is an offense shard");
        Assert.That(RunePageRules.SelectShard(page, Game, 0, 5011), Is.False, "flat health is a defense shard");
        Assert.That(page.Shards[0], Is.EqualTo(5005));
    }

    [Test]
    public void A_full_page_is_complete()
    {
        var page = NewPage();
        var dom = Fixtures.Tree("Domination");
        var sor = Fixtures.Tree("Sorcery");
        for (var row = 0; row < 4; row++) RunePageRules.SelectPrimaryRune(page, Game, dom.Rows[row][0].Id);
        RunePageRules.SelectSubRune(page, Game, sor.Rows[1][0].Id);
        Assert.That(RunePageRules.IsComplete(page), Is.False);
        RunePageRules.SelectSubRune(page, Game, sor.Rows[3][0].Id);
        for (var i = 0; i < 3; i++) RunePageRules.SelectShard(page, Game, i, Game.ShardRows[i].Options[0].Id);
        Assert.That(RunePageRules.IsComplete(page), Is.True);
        Assert.That(RunePageRules.SelectedCount(page), Is.EqualTo(9));
    }

    [Test]
    public void Sanitize_removes_runes_that_no_longer_exist()
    {
        var page = NewPage();
        page.PrimaryPerks[0] = 999999;
        page.SubPerks.Add(888888);
        page.Shards[1] = 5011; // defense shard in the flex row
        var removed = RunePageRules.Sanitize(page, Game);
        Assert.That(removed, Has.Count.EqualTo(3));
        Assert.That(page.PrimaryPerks[0], Is.Zero);
        Assert.That(page.SubPerks, Is.Empty);
        Assert.That(page.Shards[1], Is.Zero);
    }
}

public class SkillOrderRulesTests
{
    [Test]
    public void Setting_the_same_slot_again_clears_the_level()
    {
        var order = SkillOrderRules.Set("", 3, 'e');
        Assert.That(order, Is.EqualTo("--E"));
        Assert.That(SkillOrderRules.Set(order, 3, 'E'), Is.Empty);
        Assert.That(SkillOrderRules.At("QWE", 2), Is.EqualTo('W'));
        Assert.That(SkillOrderRules.At("QWE", 10), Is.EqualTo('-'));
    }

    [Test]
    public void Standard_order_is_valid()
    {
        Assert.That(SkillOrderRules.InvalidLevels("QWEQQRQWQWRWWEEREE"), Is.Empty);
    }

    [Test]
    public void Early_ultimate_and_over_levelled_basics_are_flagged()
    {
        Assert.That(SkillOrderRules.InvalidLevels("QQ"), Is.EquivalentTo(new[] { 2 }), "rank 2 at level 2");
        Assert.That(SkillOrderRules.InvalidLevels("QWER"), Is.EquivalentTo(new[] { 4 }), "R before 6");
        Assert.That(SkillOrderRules.InvalidLevels("QWEQQRQQ"), Is.EquivalentTo(new[] { 8 }), "sixth rank of Q");
    }

    [Test]
    public void Max_order_follows_which_ability_is_maxed_first()
    {
        Assert.That(SkillOrderRules.MaxOrder("QWEQQRQWQWRWWEEREE"), Is.EqualTo(new[] { 'Q', 'W', 'E' }));
        Assert.That(SkillOrderRules.MaxOrder("QEWQQRQEQEREEWWRWW"), Is.EqualTo(new[] { 'Q', 'E', 'W' }));
        Assert.That(SkillOrderRules.MaxOrder("QE"), Is.EqualTo(new[] { 'Q', 'E' }));
        Assert.That(SkillOrderRules.MaxOrder(""), Is.Empty);
    }
}

public class ItemCatalogTests
{
    private static IReadOnlyList<ItemInfo> Shop => Fixtures.Game.ShopItems;

    [Test]
    public void Category_filters_are_combined()
    {
        var crit = ItemCatalog.Filter(Shop, null, ["crit"], ItemMap.SummonersRift).Select(i => i.Name).ToList();
        Assert.That(crit, Does.Contain("Infinity Edge"));
        var critAndAs = ItemCatalog.Filter(Shop, null, ["crit", "as"], ItemMap.SummonersRift).Select(i => i.Name).ToList();
        Assert.That(critAndAs, Does.Not.Contain("Infinity Edge"), "IE has no attack speed");
    }

    [Test]
    public void Search_matches_names_and_initials()
    {
        Assert.That(ItemCatalog.Filter(Shop, "ie", [], ItemMap.SummonersRift).Select(i => i.Name), Does.Contain("Infinity Edge"));
        Assert.That(ItemCatalog.Filter(Shop, "bork", [], ItemMap.SummonersRift).Select(i => i.Name), Does.Contain("Blade of The Ruined King"));
        Assert.That(ItemCatalog.Filter(Shop, "doran", [], ItemMap.SummonersRift).Select(i => i.Name), Does.Contain("Doran's Ring"));
    }

    [Test]
    public void Map_filter_hides_items_not_on_that_map()
    {
        var aram = ItemCatalog.Filter(Shop, null, [], ItemMap.Aram).Select(i => i.Name).ToList();
        Assert.That(aram, Does.Not.Contain("Stealth Ward"));
        Assert.That(ItemCatalog.Filter(Shop, null, [], ItemMap.SummonersRift).Select(i => i.Name), Does.Contain("Stealth Ward"));
    }

    [Test]
    public void Groups_follow_tier_order_and_price()
    {
        var groups = ItemCatalog.GroupByTier(Shop);
        Assert.That(groups[0].Tier, Is.EqualTo(ItemTier.Starter));
        Assert.That(groups.Last().Tier, Is.EqualTo(ItemTier.Legendary));
        foreach (var g in groups) Assert.That(g.Items.Select(i => i.TotalGold), Is.Ordered);
    }
}

