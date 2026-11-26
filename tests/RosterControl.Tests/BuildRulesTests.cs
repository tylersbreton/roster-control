using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Tests;

public class BuildRulesTests
{
    private static Build B(string name, Position? role = null, bool isDefault = false) => new() { Name = name, Role = role, IsDefault = isDefault };

    [Test]
    public void The_default_for_the_role_wins()
    {
        var builds = new[] { B("First"), B("Mid", Position.Middle), B("Mid default", Position.Middle, true), B("Any default", null, true) };
        Assert.That(BuildSelection.PickDefault(builds, Position.Middle)!.Name, Is.EqualTo("Mid default"));
        Assert.That(BuildSelection.PickDefault(builds, Position.Top)!.Name, Is.EqualTo("Any default"), "then the any-role default");
        Assert.That(BuildSelection.PickDefault(builds, null)!.Name, Is.EqualTo("Any default"));
    }

    [Test]
    public void Without_defaults_a_build_for_the_role_then_the_first()
    {
        var builds = new[] { B("First"), B("Support", Position.Support) };
        Assert.That(BuildSelection.PickDefault(builds, Position.Support)!.Name, Is.EqualTo("Support"));
        Assert.That(BuildSelection.PickDefault(builds, Position.Top)!.Name, Is.EqualTo("First"));
        Assert.That(BuildSelection.PickDefault(builds, null)!.Name, Is.EqualTo("First"));
        Assert.That(BuildSelection.PickDefault(Array.Empty<Build>(), null), Is.Null);
    }

    [Test]
    public void A_default_for_another_role_is_not_used_for_this_role()
    {
        var builds = new[] { B("First"), B("Top default", Position.Top, true) };
        Assert.That(BuildSelection.PickDefault(builds, Position.Middle)!.Name, Is.EqualTo("First"));
        Assert.That(BuildSelection.PickDefault(builds, null)!.Name, Is.EqualTo("Top default"), "no role: any default");
    }

    [Test]
    public void The_core_block_is_the_one_named_core_else_the_first_with_items()
    {
        var build = new Build { Items = new ItemSetData { Blocks = [new() { Name = "Start", Items = ["1056"] }, new() { Name = "Core build", Items = ["3020"] }] } };
        Assert.That(BuildSelection.CoreBlock(build)!.Name, Is.EqualTo("Core build"));
        build.Items.Blocks[1].Items.Clear();
        Assert.That(BuildSelection.CoreBlock(build)!.Name, Is.EqualTo("Start"));
    }
}

public class PatchReviewTests
{
    private static GameDataSnapshot Game => Fixtures.Game;

    private static Build OldBuild(params string[] items) => new()
    {
        ChampionId = "Ahri",
        Name = "Standard",
        GameVersion = "16.1.1",
        Items = new ItemSetData { Blocks = [new ItemBlockData { Name = "Core", Items = [.. items] }] },
    };

    [Test]
    public void A_build_on_the_current_patch_needs_no_review()
    {
        var build = OldBuild("9999");
        build.GameVersion = Game.Version;
        Assert.That(PatchReview.Changes(build, Game, null), Is.Empty);
    }

    [Test]
    public void Removed_items_are_found_even_without_the_old_patch_data()
    {
        var existing = Game.ShopItems.First().Id;
        var changes = PatchReview.Changes(OldBuild(existing, "9999"), Game, baseline: null);
        Assert.That(changes.Select(c => (c.Kind, c.ItemId)), Is.EqualTo(new[] { (BuildChangeKind.ItemRemoved, (string?)"9999") }));
    }

    [Test]
    public void Changed_items_are_described_against_the_old_patch()
    {
        var item = Game.ShopItems.First(i => i.TotalGold > 0);
        var before = item with { TotalGold = item.TotalGold - 100 };
        var baseline = new Dictionary<string, ItemInfo> { [item.Id] = before };
        var changes = PatchReview.Changes(OldBuild(item.Id, item.Id), Game, baseline);
        Assert.That(changes, Has.Count.EqualTo(1), "each item once");
        Assert.That(changes[0].Kind, Is.EqualTo(BuildChangeKind.ItemChanged));
        Assert.That(changes[0].Text, Does.StartWith(item.Name + ":"));

        Assert.That(PatchReview.Changes(OldBuild(item.Id), Game, new Dictionary<string, ItemInfo> { [item.Id] = item }), Is.Empty, "unchanged items aren't listed");
    }

    [Test]
    public void Removed_runes_and_paths_are_found()
    {
        var build = OldBuild();
        build.Runes = new RunePageData { PrimaryStyleId = 1, SubStyleId = Game.RuneTrees[0].Id, PrimaryPerks = [99991, 0, 0, 0], SubPerks = [99992], Shards = [0, 0, 0] };
        var changes = PatchReview.Changes(build, Game, null);
        Assert.That(changes.Select(c => c.Text), Is.EqualTo(new[] { "The primary rune path no longer exists", "2 runes were removed from the game" }));
    }
}
