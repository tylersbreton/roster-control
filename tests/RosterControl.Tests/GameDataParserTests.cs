using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Integrations;

namespace RosterControl.Tests;

public class GameDataParserTests
{
    private static GameDataSnapshot Game => Fixtures.Game;

    [Test]
    public void Champions_are_parsed_with_positions_and_sorted_by_name()
    {
        Assert.That(Game.Champions.Select(c => c.Name), Is.Ordered.Using((IComparer<string>)StringComparer.OrdinalIgnoreCase));
        var wukong = Game.Champion("MonkeyKing")!;
        Assert.That(wukong.Name, Is.EqualTo("Wukong"));
        Assert.That(wukong.Positions, Is.EqualTo(new[] { Position.Top, Position.Jungle }));
        Assert.That(Game.Champion("Ahri")!.Key, Is.EqualTo(103));
        Assert.That(Game.Champion("Garen")!.Positions, Is.Empty);
    }

    [Test]
    public void Champion_search_ignores_case_and_punctuation()
    {
        Assert.That(Game.Champion("KogMaw")!.Matches("kogm"), Is.True);
        Assert.That(Game.Champion("KogMaw")!.Matches("kog'maw"), Is.True);
        Assert.That(Game.Champion("MonkeyKing")!.Matches("monkey"), Is.True, "searches the id too");
        Assert.That(Game.Champion("Ahri")!.Matches("zed"), Is.False);
    }

    [Test]
    public void Shop_dedupes_mode_variants_and_excludes_unpurchasable_items()
    {
        var shop = Game.ShopItems.Select(i => i.Id).ToList();
        Assert.That(shop, Does.Contain("3004"), "Manamune");
        Assert.That(shop, Does.Not.Contain("323004"), "ARAM/mode variant of Manamune");
        Assert.That(shop, Does.Not.Contain("3850"), "Spellthief's Edge is not purchasable");
        Assert.That(shop.Count(id => Game.Items[id].Name == "Scorchclaw Pup"), Is.EqualTo(1));
        Assert.That(Game.Item("323004"), Is.Not.Null, "variants are still known for old builds");
    }

    [TestCase("1056", ItemTier.Starter)] // Doran's Ring
    [TestCase("1082", ItemTier.Starter)] // Dark Seal
    [TestCase("3865", ItemTier.Starter)] // World Atlas
    [TestCase("3869", ItemTier.Starter)] // Celestial Opposition (support quest upgrade)
    [TestCase("1101", ItemTier.Starter)] // Jungle pet
    [TestCase("1036", ItemTier.Basic)] // Long Sword
    [TestCase("1001", ItemTier.Boots)]
    [TestCase("3006", ItemTier.Boots)] // Berserker's Greaves
    [TestCase("3031", ItemTier.Legendary)] // Infinity Edge
    [TestCase("4646", ItemTier.Legendary)] // Stormsurge (GoldPer but not a support item)
    [TestCase("2003", ItemTier.Consumable)]
    [TestCase("2055", ItemTier.Consumable)] // Control Ward
    [TestCase("3340", ItemTier.Trinket)]
    public void Items_are_classified_like_the_client_shop(string id, ItemTier tier) =>
        Assert.That(Game.Items[id].Tier, Is.EqualTo(tier), Game.Items[id].Name);

    [Test]
    public void Rune_trees_follow_client_order_with_keystones_and_backgrounds()
    {
        Assert.That(Game.RuneTrees.Select(t => t.Key), Is.EqualTo(new[] { "Precision", "Domination", "Sorcery", "Resolve", "Inspiration" }));
        foreach (var tree in Game.RuneTrees)
        {
            Assert.That(tree.Rows, Has.Count.EqualTo(4), tree.Key);
            Assert.That(tree.Rows[0].All(r => r.IsKeystone), Is.True);
            Assert.That(tree.BackgroundUrl(null, tree.Rows[0][0].Id), Does.StartWith("https://raw.communitydragon.org/"));
            Assert.That(tree.Tagline, Is.Not.Empty);
        }
        Assert.That(Game.Rune(8112)!.Name, Is.EqualTo("Electrocute"));
    }

    [Test]
    public void Stat_shards_come_from_community_dragon()
    {
        Assert.That(Game.ShardRows.Select(r => r.Label), Is.EqualTo(new[] { "Offense", "Flex", "Defense" }));
        Assert.That(Game.ShardRows.All(r => r.Options.Count == 3), Is.True);
        Assert.That(Game.Shard(5008)!.Name, Is.EqualTo("Adaptive Force"));
        Assert.That(Game.Shard(5008)!.IconUrl, Is.EqualTo(AssetUrls.CommunityDragonGameData + "v1/perk-images/statmods/statmodsadaptiveforceicon.png"));
    }

    [Test]
    public void Summoner_spells_are_limited_to_rift_and_aram_with_flash_first()
    {
        var ids = Game.SummonerSpells.Select(s => s.Id).ToList();
        Assert.That(ids[0], Is.EqualTo("SummonerFlash"));
        Assert.That(ids, Does.Contain("SummonerSmite").And.Contain("SummonerSnowball"));
        Assert.That(ids.Any(i => i.EndsWith("_Jade", StringComparison.Ordinal) || i.Contains("Placeholder") || i.Contains("Cherry")), Is.False);
    }

    [Test]
    public void Abilities_are_parsed_with_icons_and_ranks()
    {
        using var doc = Fixtures.Load("Ahri.json");
        var abilities = GameDataParser.ParseAbilities(doc.RootElement, "Ahri", "16.23.1")!;
        Assert.That(abilities.Abilities.Select(a => a.Slot), Is.EqualTo(new[] { 'P', 'Q', 'W', 'E', 'R' }));
        Assert.That(abilities['R']!.MaxRank, Is.EqualTo(3));
        Assert.That(abilities['Q']!.ImageUrl, Is.EqualTo("https://ddragon.leagueoflegends.com/cdn/16.23.1/img/spell/AhriQ.png"));
        Assert.That(abilities['P']!.ImageUrl, Does.Contain("/img/passive/"));
    }

    [Test]
    public void Community_dragon_paths_are_lower_cased()
    {
        Assert.That(AssetUrls.CommunityDragon("/lol-game-data/assets/v1/perk-images/StatMods/StatModsArmorIcon.png"),
            Is.EqualTo("https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/perk-images/statmods/statmodsarmoricon.png"));
    }
}
