using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;
using RosterControl.Integrations;

namespace RosterControl.Tests;

/// <summary>Champion stats per level, ability and rune numbers for a build, item sets and gold efficiency (1.5).</summary>
public class CalculatorTests
{
    private static ChampionInfo Ahri => Fixtures.Game.Champion("Ahri")!;

    private static IReadOnlyDictionary<char, IReadOnlyList<AbilityValue>> Abilities(string champion)
    {
        using var doc = Fixtures.Load(Path.Combine("cdragon", champion + ".bin.json"));
        return AbilityCalculations.Parse(doc.RootElement);
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<AbilityValue>> Runes()
    {
        using var doc = Fixtures.Load(Path.Combine("cdragon", "perks.bin.json"));
        return AbilityCalculations.ParsePerks(doc.RootElement);
    }

    [Test]
    public void Base_stats_come_from_data_dragon_and_grow_along_the_games_curve()
    {
        var stats = Ahri.Stats!;
        Assert.That(stats.Health, Is.EqualTo(590));
        Assert.That(stats.IsRanged, Is.True);
        Assert.That(stats.AdaptiveAp, Is.True, "magic 8 > attack 3");
        Assert.That(ChampionMath.Growth(1), Is.EqualTo(0));
        Assert.That(ChampionMath.Growth(18), Is.EqualTo(17).Within(1e-9));

        var level1 = ChampionMath.Compute(stats, 1, []);
        var level18 = ChampionMath.Compute(stats, 18, []);
        Assert.That(level1.Health, Is.EqualTo(590));
        Assert.That(level18.Health, Is.EqualTo(590 + 104 * 17).Within(1e-6));
        Assert.That(level18.Armor, Is.EqualTo(21 + 4.2 * 17).Within(1e-6));
        Assert.That(level18.AttackSpeed, Is.EqualTo(0.668 * (1 + 0.022 * 17)).Within(1e-6), "attack speed growth counts as bonus attack speed");
        var level9 = ChampionMath.Compute(stats, 9, []);
        Assert.That(level9.BaseHealth, Is.EqualTo(590 + 104 * 8 * (0.7025 + 0.0175 * 8)).Within(1e-6));
    }

    [Test]
    public void Item_stats_and_adaptive_force_add_up()
    {
        var stats = ChampionMath.Compute(Ahri.Stats!, 11,
        [
            new StatTotal("Ability Power", 130, false),
            new StatTotal("Ability Haste", 20, false),
            new StatTotal("Magic Penetration", 15, false),
            new StatTotal("Magic Penetration", 40, true),
            new StatTotal("Move Speed", 45, false),
            new StatTotal("Move Speed", 5, true),
            new StatTotal("Adaptive Force", 18, false),
            new StatTotal("Base Mana Regen", 50, true),
        ]);
        Assert.That(stats.AbilityPower, Is.EqualTo(148), "adaptive force goes to AP when AP is higher");
        Assert.That(stats.CooldownMultiplier, Is.EqualTo(100.0 / 120).Within(1e-9));
        Assert.That(stats.MagicPenetration, Is.EqualTo(15));
        Assert.That(stats.PercentMagicPenetration, Is.EqualTo(0.4).Within(1e-9));
        Assert.That(stats.MoveSpeed, Is.EqualTo((330 + 45) * 1.05).Within(1e-9));
        Assert.That(stats.NotCounted, Is.EqualTo(new[] { "Base Mana Regen" }));

        var fighter = ChampionMath.Compute(Fixtures.Game.Champion("Darius")!.Stats!, 1, [new StatTotal("Adaptive Force", 9, false)]);
        Assert.That(fighter.BonusAttackDamage, Is.EqualTo(5.4).Within(1e-9), "a tie goes by the champion: Darius gets 0.6 AD per force");
    }

    [Test]
    public void Stat_shards_read_from_their_text()
    {
        var shards = Fixtures.Game.ShardRows.SelectMany(r => r.Options).GroupBy(s => s.Id).Select(g => g.First()).ToList();
        var byName = shards.ToDictionary(s => s.Id);
        var level1 = ChampionMath.ShardStats([byName[5008], byName[5005], byName[5001]], 1);
        Assert.That(level1.Select(s => s.Text), Is.EqualTo(new[] { "9 Adaptive Force", "10% Attack Speed", "10 Health" }));
        Assert.That(ChampionMath.ShardStats([byName[5001]], 18).Single().Value, Is.EqualTo(180), "10–180 health by level");
    }

    [Test]
    public void Ability_numbers_are_calculated_for_a_rank_level_and_stats()
    {
        var ahri = Abilities("ahri");
        var q = ahri['Q'].Single(v => v.Label == "Total damage").Formula!;
        var stats = ChampionMath.Compute(Ahri.Stats!, 9, [new StatTotal("Ability Power", 100, false)]);
        Assert.That(ChampionMath.Evaluate(q, 5, 9, stats), Is.EqualTo(135 + 50).Within(1e-6));
        Assert.That(ChampionMath.Evaluate(q, 1, 9, stats), Is.EqualTo(35 + 50).Within(1e-6));
        Assert.That(ChampionMath.Format(q, 185.4), Is.EqualTo("185"));

        var heal = ahri['P'].Single(v => v.Label == "Champion heal").Formula!;
        var noItems = ChampionMath.Compute(Ahri.Stats!, 11, []);
        Assert.That(ChampionMath.Evaluate(heal, 1, 11, noItems), Is.EqualTo(75 + 90 * 10 / 17.0).Within(1e-6), "by-level values grow evenly");

        var garen = Abilities("garen");
        var shield = garen['W'].Single(v => v.Label == "Total shield").Formula!;
        var tank = ChampionMath.Compute(Fixtures.Game.Champion("Garen")!.Stats!, 1, [new StatTotal("Health", 400, false)]);
        Assert.That(ChampionMath.Evaluate(shield, 1, 1, tank), Is.EqualTo(65 + 0.18 * 400).Within(1e-3), "bonus health only (game data is single precision)");
    }

    [Test]
    public void Ranks_follow_the_skill_order()
    {
        var max = new Dictionary<char, int> { ['Q'] = 5, ['W'] = 5, ['E'] = 5, ['R'] = 3 };
        var ranks = ChampionMath.RanksAt("QWEQQRQWQWRWWEEREE", 11, max);
        Assert.That(ranks['Q'], Is.EqualTo(5));
        Assert.That(ranks['W'], Is.EqualTo(3));
        Assert.That(ranks['E'], Is.EqualTo(1));
        Assert.That(ranks['R'], Is.EqualTo(2));
        Assert.That(ChampionMath.RanksAt("", 3, max)['R'], Is.EqualTo(3), "no skill order: max ranks");
        Assert.That(ChampionMath.ParseSeries("11/10/9.5s"), Is.EqualTo(new[] { 11, 10, 9.5 }));
    }

    [Test]
    public void Keystone_numbers_come_from_the_rune_game_file()
    {
        var runes = Runes();
        Assert.That(runes.ContainsKey(8010), Is.False, "Conqueror has no formula in the game files");

        var electrocute = runes[8112].Single(v => v.Label == "Total damage");
        Assert.That(electrocute.Text, Is.EqualTo("70–240 (by level) (+5% AP) (+10% bonus AD)"));
        var mage = ChampionMath.Compute(Ahri.Stats!, 1, [new StatTotal("Ability Power", 100, false)]);
        Assert.That(ChampionMath.Evaluate(electrocute.Formula!, 1, 1, mage), Is.EqualTo(70 + 5).Within(1e-3));
        Assert.That(ChampionMath.Evaluate(electrocute.Formula!, 1, 18, ChampionMath.Compute(Ahri.Stats!, 18, [])), Is.EqualTo(240).Within(1e-6));

        // Grasp: % max health, less for ranged champions.
        var grasp = runes[8437].Single(v => v.Label == "Damage").Formula!;
        var melee = ChampionMath.Compute(Fixtures.Game.Champion("Darius")!.Stats!, 1, []);
        Assert.That(ChampionMath.Evaluate(grasp, 1, 1, melee), Is.EqualTo(0.035 * melee.Health).Within(1e-3));
        Assert.That(ChampionMath.Evaluate(grasp, 1, 1, ChampionMath.Compute(Ahri.Stats!, 1, [])), Is.EqualTo(0.035 * 590 * 0.4).Within(1e-4));

        // Fleet Footwork's heal grows along the stat curve, not evenly.
        var fleet = runes[8021].Single(v => v.Label == "Heal amount").Formula!;
        var levels = Enumerable.Range(1, 18).Select(l => fleet.ByLevel![l - 1]).ToList();
        Assert.That(levels[0], Is.EqualTo(15).Within(1e-6));
        Assert.That(levels[17], Is.EqualTo(160).Within(1e-6));
        Assert.That(levels[8], Is.LessThan(15 + 145 * 8 / 17.0), "slower early than even steps");
    }

    [Test]
    public void Builds_copy_as_a_client_item_set()
    {
        var build = new Build
        {
            Name = "Standard",
            Items = new ItemSetData
            {
                Blocks =
                [
                    new ItemBlockData { Name = "Starting Items", Items = ["1056", "2003", "2003"] },
                    new ItemBlockData { Name = "Core", Items = ["3089", "999999"] },
                    new ItemBlockData { Name = "Empty" },
                ],
            },
        };
        var json = ItemSetExport.ToClientJson(build, Ahri, Fixtures.Game, out var skipped);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.That(skipped, Is.EqualTo(1), "an item no longer in the game");
        Assert.That(root.GetProperty("title").GetString(), Is.EqualTo("Ahri · Standard"));
        Assert.That(root.GetProperty("associatedChampions")[0].GetInt32(), Is.EqualTo(Ahri.Key));
        Assert.That(root.GetProperty("associatedMaps")[0].GetInt32(), Is.EqualTo(11));
        var blocks = root.GetProperty("blocks");
        Assert.That(blocks.GetArrayLength(), Is.EqualTo(2), "empty blocks are left out");
        var start = blocks[0].GetProperty("items");
        Assert.That(start.GetArrayLength(), Is.EqualTo(2));
        Assert.That(start[1].GetProperty("id").GetString(), Is.EqualTo("2003"));
        Assert.That(start[1].GetProperty("count").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public void Gold_efficiency_uses_the_cheapest_basic_item_per_stat()
    {
        var refs = GoldEfficiency.References(Fixtures.Game);
        Assert.That(refs[("Attack Damage", false)].GoldPerPoint, Is.EqualTo(35), "Long Sword: 350 / 10");
        Assert.That(refs[("Attack Damage", false)].ReferenceItem, Is.EqualTo("Long Sword"));
        var ie = GoldEfficiency.Of(Fixtures.Game.Item("3031")!, refs)!;
        var ad = ItemStats.Parse(Fixtures.Game.Item("3031")!.Description).Single(s => s.Name == "Attack Damage").Value;
        Assert.That(ie.Worth, Is.EqualTo(ad * 35).Within(1e-6));
        Assert.That(ie.Unvalued.Select(s => s.Name), Has.Some.EqualTo("Critical Strike Chance"), "no basic crit item in this data");
        Assert.That(GoldEfficiency.Of(Fixtures.Game.Item("3340")!, refs), Is.Null, "free trinket");
    }

    [Test]
    public void Shared_roster_files_round_trip_without_notes()
    {
        var groups = new List<RosterGroup>
        {
            new() { Name = "Mid", Role = Position.Middle, Color = "#6FA8F5", Entries = [new RosterEntry { ChampionId = "Ahri", Proficiency = 5, Priority = ChampionPriority.Main }] },
        };
        var builds = new[]
        {
            new Build { ChampionId = "Ahri", Name = "Standard", Notes = "my secret plan", SkillOrder = "QWE" },
            new Build { ChampionId = "Garen", Name = "Not in this roster" },
        };
        var file = SharedRosterFile.Create("Clash", groups, includeRatings: true, builds, "1.5.0", "16.23.1");
        Assert.That(SharedRosterFile.TryParse(file.Serialize(), out var back, out _), Is.True);
        Assert.That(back.Name, Is.EqualTo("Clash"));
        Assert.That(back.Groups.Single().Entries.Single().Proficiency, Is.EqualTo(5));
        Assert.That(back.Builds.Select(b => b.ChampionId), Is.EqualTo(new[] { "Ahri" }), "only builds of the roster's champions");
        Assert.That(back.Builds.Single().Notes, Is.Empty);

        var unrated = SharedRosterFile.Create("Clash", groups, includeRatings: false, null, null, null);
        Assert.That(SharedRosterFile.TryParse(unrated.Serialize(), out var u, out _), Is.True);
        Assert.That(u.Groups.Single().Entries.Single().Proficiency, Is.Null);
        Assert.That(u.Builds, Is.Empty);
    }

    [Test]
    public void Shared_roster_files_are_checked_and_cleaned()
    {
        Assert.That(SharedRosterFile.TryParse("{\"groups\": []}", out _, out var error), Is.False, "no file kind");
        Assert.That(error, Does.Contain("isn't a Roster Control roster file"));
        Assert.That(SharedRosterFile.TryParse("not json", out _, out _), Is.False);

        const string hostile = """
            {"fileKind":"roster","format":7,"name":"   ","groups":[{"name":"","role":"Middle","color":"red","entries":[{"championId":"  Ahri ","proficiency":99,"priority":"Main"},{"championId":""}]}],
             "builds":[{"id":42,"championId":"Ahri","name":"","isDefault":true,"skillOrder":"QQXZ","notes":"x","summonerSpells":null}]}
            """;
        Assert.That(SharedRosterFile.TryParse(hostile, out var file, out _), Is.True);
        Assert.That(file.IsFromNewerVersion, Is.True);
        Assert.That(file.Name, Is.EqualTo("Shared roster"));
        var group = file.Groups.Single();
        Assert.That(group.Name, Is.EqualTo("Group"));
        Assert.That(group.Color, Is.Null);
        Assert.That(group.Entries.Single().ChampionId, Is.EqualTo("Ahri"));
        Assert.That(group.Entries.Single().Proficiency, Is.EqualTo(5));
        var build = file.Builds.Single();
        Assert.That(build.Id, Is.EqualTo(0));
        Assert.That(build.IsDefault, Is.False);
        Assert.That(build.SkillOrder, Is.EqualTo("QQ"));
        Assert.That(build.Notes, Is.Empty);
        Assert.That(build.SummonerSpells, Is.EqualTo(new[] { "", "" }));
    }
}
