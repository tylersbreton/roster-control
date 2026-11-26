using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Tests;

public class BuildTotalsTests
{
    [Test]
    public void Stats_are_read_from_the_item_description()
    {
        var stats = ItemStats.Parse("<mainText><stats><attention>75</attention> Attack Damage<br><attention>25%</attention> Critical Strike Chance</stats><br><br><passive>Perfection</passive></mainText>");
        Assert.That(stats.Select(s => s.Text), Is.EqualTo(new[] { "75 Attack Damage", "25% Critical Strike Chance" }));
        Assert.That(ItemStats.Parse("<mainText><stats></stats><br>Restores health.</mainText>"), Is.Empty, "consumables have no stats");
        Assert.That(ItemStats.Parse(null), Is.Empty);
        Assert.That(ItemStats.Parse("no stats here"), Is.Empty);
    }

    [Test]
    public void Gold_and_stats_are_summed_over_the_items()
    {
        // Infinity Edge, Kraken Slayer, Berserker's Greaves (real 16.23 data).
        var totals = ItemStats.Sum(["3031", "6672", "3006"], Fixtures.Game);
        Assert.That(totals.Gold, Is.EqualTo(3500 + 3000 + 1100));
        Assert.That(totals.GoldText, Is.EqualTo("7,600 gold"));
        Assert.That(totals.ItemCount, Is.EqualTo(3));
        var text = totals.Stats.Select(s => s.Text).ToList();
        Assert.That(text, Is.EqualTo(new[]
        {
            "120 Attack Damage", "70% Attack Speed", "25% Critical Strike Chance", "30% Critical Strike Damage", "45 Move Speed", "4% Move Speed",
        }), "in a fixed order; flat and % move speed stay apart");
        Assert.That(totals.Summary(3), Is.EqualTo("7,600 gold · 120 AD · 70% AS · 25% Crit"));
    }

    [Test]
    public void Duplicates_count_twice_and_removed_items_are_skipped()
    {
        var totals = ItemStats.Sum(["1056", "1056", "999999"], Fixtures.Game);
        Assert.That(totals.Gold, Is.EqualTo(800));
        Assert.That(totals.MissingItems, Is.EqualTo(1));
        Assert.That(totals.Stats.Select(s => s.ShortText), Is.EqualTo(new[] { "36 AP", "180 HP" }));
    }

    [Test]
    public void Build_totals_cover_the_core_block()
    {
        var build = new Build
        {
            Items = new ItemSetData { Blocks = [new() { Name = "Starting Items", Items = ["1056"] }, new() { Name = "Core Build", Items = ["3089"] }] },
        };
        Assert.That(ItemStats.Core(build, Fixtures.Game)!.Gold, Is.EqualTo(3500));
        build.Items.Blocks[1].Items.Clear();
        Assert.That(ItemStats.Core(build, Fixtures.Game)!.Gold, Is.EqualTo(400), "no core items: the first block with items");
        build.Items.Blocks[0].Items.Clear();
        Assert.That(ItemStats.Core(build, Fixtures.Game), Is.Null);
    }
}
