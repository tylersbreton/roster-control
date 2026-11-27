using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;
using RosterControl.Integrations;

namespace RosterControl.Tests;

/// <summary>The calculator's what-ifs (best next item, swaps, power curve) and build warnings (1.6).</summary>
public class PlanningTests
{
    private static ChampionInfo Ahri => Fixtures.Game.Champion("Ahri")!;

    private static IReadOnlyDictionary<char, IReadOnlyList<AbilityValue>> AhriNumbers()
    {
        using var doc = Fixtures.Load(Path.Combine("cdragon", "ahri.bin.json"));
        return AbilityCalculations.Parse(doc.RootElement);
    }

    private static readonly IReadOnlyDictionary<char, int> MaxRanks = new Dictionary<char, int> { ['Q'] = 5, ['W'] = 5, ['E'] = 5, ['R'] = 3 };

    private static CalcMetric Stat(string key) => CalcMetric.StatByKey(key)!;

    [Test]
    public void Metrics_measure_stats_abilities_and_cooldowns()
    {
        var game = Fixtures.Game;
        var rabadon = game.Item("3089")!;
        var stats = BuildCalculator.StatsAt(Ahri.Stats!, 11, [rabadon], [], includeShards: false);
        var ranks = ChampionMath.RanksAt("QWEQQRQEQEREEWWRWW", 11, MaxRanks);

        Assert.That(Stat("stat:ap").Value(stats, ranks), Is.EqualTo(stats.AbilityPower));
        Assert.That(Stat("stat:ehp-magic").Value(stats, ranks), Is.EqualTo(stats.Health * (1 + stats.MagicResist / 100)).Within(1e-9));

        var q = AhriNumbers()['Q'];
        var index = q.ToList().FindIndex(v => v.Formula is not null);
        var qMetric = CalcMetric.ForAbility('Q', index, q[index]);
        Assert.That(qMetric.Label, Does.StartWith("Q · "));
        Assert.That(qMetric.Value(stats, ranks), Is.EqualTo(ChampionMath.Evaluate(q[index].Formula!, ranks['Q'], 11, stats)).Within(1e-9));

        var r = AhriNumbers()['R'].First(v => v.Formula is not null);
        var rMetric = CalcMetric.ForAbility('R', 0, r);
        var level3 = ChampionMath.RanksAt("QWEQQRQEQEREEWWRWW", 3, MaxRanks);
        Assert.That(rMetric.Value(BuildCalculator.StatsAt(Ahri.Stats!, 3, [], [], false), level3), Is.NaN, "R isn't learned at level 3");

        var cooldown = CalcMetric.Cooldown('Q', "7/7/7/7/7")!;
        Assert.That(cooldown.HigherIsBetter, Is.False);
        var hasted = ChampionMath.Compute(Ahri.Stats!, 11, [new StatTotal("Ability Haste", 25, false)]);
        Assert.That(cooldown.Value(hasted, ranks), Is.EqualTo(7 * 100.0 / 125).Within(1e-9));
        Assert.That(cooldown.Gain(7, 5.6), Is.GreaterThan(0), "a shorter cooldown is an improvement");
        Assert.That(cooldown.FormatDelta(5.6 - 7), Is.EqualTo("−1.4s"));
        Assert.That(CalcMetric.Cooldown('P', "10"), Is.Null, "passives have no cooldown metric");
    }

    [Test]
    public void Best_next_item_ranks_finished_items_by_the_chosen_number()
    {
        var game = Fixtures.Game;
        var owned = new List<ItemInfo> { game.Item("4646")! }; // Stormsurge
        var candidates = BuildCalculator.Candidates(game, owned, replacing: null, aram: false);

        Assert.That(candidates.All(c => c.Tier == ItemTier.Legendary || BuildCalculator.IsFinishedBoots(c)), Is.True);
        Assert.That(candidates.Select(c => c.Name), Does.Not.Contain("Stormsurge"), "items you have aren't offered");
        Assert.That(candidates.Count(c => c.Name == "Manamune"), Is.EqualTo(1), "mode variants of one item are listed once");
        Assert.That(candidates.Select(c => c.Name), Does.Contain("Berserker's Greaves"), "boots are offered while there are none");
        Assert.That(candidates.Select(c => c.Name), Does.Not.Contain("Boots"), "basic Boots aren't a finished item");

        var ap = Stat("stat:ap");
        var ranked = BuildCalculator.Rank(owned, candidates, null, ap,
            items => ap.Value(BuildCalculator.StatsAt(Ahri.Stats!, 11, items, [], false), MaxRanks));
        Assert.That(ranked[0].Item.Name, Is.EqualTo("Rabadon's Deathcap"));
        Assert.That(ranked[0].Gain, Is.GreaterThan(0));
        Assert.That(ranked[0].GainText, Does.StartWith("+"));
        Assert.That(ranked.Where(c => c.Item.Name == "Infinity Edge").Single().Gain, Is.EqualTo(0), "Infinity Edge adds no AP");

        var withBoots = new List<ItemInfo> { game.Item("3006")! };
        Assert.That(BuildCalculator.Candidates(game, withBoots, null, false).Any(BuildCalculator.IsFinishedBoots), Is.False, "no second pair of boots");
    }

    [Test]
    public void Swapping_an_item_compares_items_of_the_same_kind()
    {
        var game = Fixtures.Game;
        var rabadon = game.Item("3089")!;
        var greaves = game.Item("3006")!;
        var owned = new List<ItemInfo> { rabadon, greaves };

        var forRabadon = BuildCalculator.Candidates(game, owned, rabadon, aram: false);
        Assert.That(forRabadon, Is.Not.Empty);
        Assert.That(forRabadon.All(i => i.Tier == ItemTier.Legendary && i.Name != "Rabadon's Deathcap"), Is.True);
        Assert.That(BuildCalculator.Candidates(game, owned, greaves, false).All(BuildCalculator.IsFinishedBoots), Is.True);

        var ap = Stat("stat:ap");
        var stormsurge = game.Item("4646")!;
        var ranked = BuildCalculator.Rank(owned, [stormsurge], rabadon, ap,
            items => ap.Value(BuildCalculator.StatsAt(Ahri.Stats!, 11, items, [], false), MaxRanks));
        Assert.That(ranked.Single().Gain, Is.LessThan(0), "Stormsurge has less AP than Rabadon's");

        var before = BuildCalculator.StatsAt(Ahri.Stats!, 11, owned, [], false);
        var after = BuildCalculator.StatsAt(Ahri.Stats!, 11, [.. BuildCalculator.RemoveOne(owned, rabadon), stormsurge], [], false);
        var changes = BuildCalculator.Changes(CalcMetric.StatMetrics, before, after, MaxRanks);
        Assert.That(changes.Select(c => c.Metric.Key), Does.Contain("stat:ap").And.Contain("stat:ms"));
        Assert.That(changes.Single(c => c.Metric.Key == "stat:ap").IsBetter, Is.False);
        Assert.That(changes.Select(c => c.Metric.Key), Does.Not.Contain("stat:armor"), "unchanged numbers aren't listed");
    }

    [Test]
    public void Power_curve_counts_items_from_the_level_they_are_finished()
    {
        var game = Fixtures.Game;
        var rabadon = game.Item("3089")!;
        var doran = game.Item("1056")!;
        Assert.That(ItemTimeline.DefaultLevels([doran, rabadon, game.Item("4646")!]), Is.EqualTo(new[] { 1, 6, 9 }), "starters at 1, then the spread");
        Assert.That(ItemTimeline.DefaultLevels(Enumerable.Repeat(rabadon, 8).ToList()), Is.EqualTo(new[] { 6, 9, 11, 13, 15, 17, 18, 18 }));

        var ap = Stat("stat:ap");
        var curve = BuildCalculator.Curve(Ahri.Stats!, [(doran, 1), (rabadon, 9)], [], false, null, MaxRanks, ap);
        Assert.That(curve, Has.Length.EqualTo(18));
        var doranAp = BuildCalculator.StatsOf(doran).Where(s => s.Name == "Ability Power").Sum(s => s.Value);
        Assert.That(curve[0], Is.EqualTo(doranAp));
        Assert.That(curve[7], Is.EqualTo(doranAp), "Rabadon's isn't finished at level 8");
        Assert.That(curve[8], Is.GreaterThan(curve[7]), "it counts from level 9");

        var health = BuildCalculator.Curve(Ahri.Stats!, [], [], false, null, MaxRanks, Stat("stat:health"));
        Assert.That(health[0], Is.EqualTo(590));
        Assert.That(health, Is.Ordered, "health grows every level");

        var r = AhriNumbers()['R'].First(v => v.Formula is not null);
        var rCurve = BuildCalculator.Curve(Ahri.Stats!, [], [], false, "QWEQQRQEQEREEWWRWW", MaxRanks, CalcMetric.ForAbility('R', 0, r));
        Assert.That(rCurve[4], Is.NaN, "no R before level 6");
        Assert.That(rCurve[5], Is.Not.NaN);
    }

    [Test]
    public void Attack_damage_growth_falls_back_when_data_dragon_reports_zero()
    {
        // Data Dragon has said 0 AD per level for every champion since 16.22; the game files and Meraki still have it.
        using var doc = Fixtures.Load("champion.json");
        Assert.That(GameDataParser.ParseChampions(doc.RootElement, new Dictionary<string, IReadOnlyList<string>>()).Single(c => c.Id == "Ahri").Stats!.AttackDamagePerLevel, Is.EqualTo(0));
        var growth = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Ahri"] = 3 };
        var ahri = GameDataParser.ParseChampions(doc.RootElement, new Dictionary<string, IReadOnlyList<string>>(), growth).Single(c => c.Id == "Ahri");
        Assert.That(ahri.Stats!.AttackDamagePerLevel, Is.EqualTo(3));
        Assert.That(ahri.Stats.AttackDamageGrowthUnknown, Is.False);
        var unknown = GameDataParser.ParseChampions(doc.RootElement, new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, double>()).Single(c => c.Id == "Ahri");
        Assert.That(unknown.Stats!.AttackDamageGrowthUnknown, Is.True, "no source had a value");
        var zero = GameDataParser.ParseChampions(doc.RootElement, new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, double> { ["Ahri"] = 0 }).Single(c => c.Id == "Ahri");
        Assert.That(zero.Stats!.AttackDamageGrowthUnknown, Is.False, "a real 0 (like Senna's) is known");
        Assert.That(ChampionMath.Compute(ahri.Stats, 18, []).AttackDamage, Is.EqualTo(53 + 3 * 17).Within(1e-9));

        // The champion's own game file has it too (the calculator uses that, so new champions are covered).
        using var bin = Fixtures.Load(Path.Combine("cdragon", "ahri.bin.json"));
        Assert.That(AbilityCalculations.AttackDamagePerLevel(bin.RootElement), Is.EqualTo(3));
    }

    // ── Build warnings ────────────────────────────────────────────────────────────────────────────

    private static ItemInfo Item(string id, string name, ItemTier tier, string passive = "", string[]? from = null, string[]? tags = null, bool rift = true, bool aram = true, int gold = 3000) =>
        new(id, name, passive.Length > 0 ? $"<mainText><stats></stats><br><passive>{passive}</passive> something</mainText>" : "<mainText><stats></stats></mainText>", "",
            gold, tags ?? [], from ?? [], [], id + ".png", rift, aram, tier);

    private static readonly ItemInfo[] TestItems =
    [
        Item("1001", "Boots", ItemTier.Boots, gold: 300),
        Item("3006", "Berserker's Greaves", ItemTier.Boots, from: ["1001"], gold: 1100),
        Item("3020", "Sorcerer's Shoes", ItemTier.Boots, from: ["1001"], gold: 1100),
        Item("3053", "Sterak's Gage", ItemTier.Legendary, "Lifeline"),
        Item("3156", "Maw of Malmortius", ItemTier.Legendary, "Lifeline"),
        Item("3071", "Black Cleaver", ItemTier.Legendary, "Carve"),
        Item("3089", "Rabadon's Deathcap", ItemTier.Legendary, "Magical Opus"),
        Item("3157", "Zhonya's Hourglass", ItemTier.Legendary),
        Item("3135", "Void Staff", ItemTier.Legendary),
        Item("3165", "Morellonomicon", ItemTier.Legendary),
        Item("3116", "Rylai's Crystal Scepter", ItemTier.Legendary),
        Item("3003", "Archangel's Staff", ItemTier.Legendary),
        Item("3057", "Sheen", ItemTier.Epic, "Spellblade", gold: 900),
        Item("3078", "Trinity Force", ItemTier.Legendary, "Spellblade", from: ["3057"]),
        Item("3100", "Lich Bane", ItemTier.Legendary, "Spellblade", from: ["3057"]),
        Item("1101", "Scorchclaw Pup", ItemTier.Starter, tags: ["Jungle"], aram: false, gold: 450),
        Item("3865", "World Atlas", ItemTier.Starter, tags: ["GoldPer", "Vision", "Lane"], aram: false, gold: 400),
        Item("3177", "Guardian's Blade", ItemTier.Starter, rift: false, gold: 950),
    ];

    private static GameDataSnapshot WarningGame { get; } = new(Fixtures.Game.Version, Fixtures.Game.Champions, TestItems, TestItems.Select(i => i.Id),
        Fixtures.Game.RuneTrees, Fixtures.Game.ShardRows, Fixtures.Game.SummonerSpells, true);

    private static Build BuildWith(Position? role, params string[] core) => new()
    {
        ChampionId = "Ahri",
        Role = role,
        Items = new ItemSetData { Blocks = [new ItemBlockData { Name = "Core Build", Items = [.. core] }] },
    };

    private static IReadOnlyList<string> Codes(Build build) => BuildWarnings.Check(build, WarningGame).Select(w => w.Code).ToList();

    [Test]
    public void A_new_build_and_a_sound_build_have_no_warnings()
    {
        Assert.That(Codes(new Build { ChampionId = "Ahri", Items = ItemSetData.CreateDefault() }), Is.Empty);
        // Boots listed before what they build into is a build path, not two pairs.
        var sound = BuildWith(Position.Middle, "1001", "3020", "3089", "3157", "3135");
        sound.SummonerSpells = ["SummonerFlash", "SummonerDot"];
        sound.SkillOrder = "QWEQQRQEQEREEWWRWW";
        Assert.That(Codes(sound), Is.Empty, string.Join("; ", BuildWarnings.Check(sound, WarningGame).Select(w => w.Text)));
    }

    [Test]
    public void Core_items_that_dont_work_together_are_flagged()
    {
        var build = BuildWith(Position.Top, "3006", "3020", "3053", "3156", "3053", "3071", "3089", "3157");
        var warnings = BuildWarnings.Check(build, WarningGame);
        var codes = warnings.Select(w => w.Code).ToList();
        Assert.That(codes, Does.Contain("two-boots"));
        Assert.That(codes, Does.Contain("duplicate:sterak'sgage".Replace("'", "")));
        Assert.That(codes, Does.Contain("same-passive:lifeline"));
        Assert.That(codes, Does.Contain("too-many"));
        Assert.That(warnings.Single(w => w.Code == "same-passive:lifeline").Text, Is.EqualTo("Sterak's Gage and Maw of Malmortius share the Lifeline passive"));

        // A component and the item it builds into share the passive, but that's a build path.
        Assert.That(Codes(BuildWith(Position.Top, "3057", "3078", "3006")), Does.Not.Contain("same-passive:spellblade"));
        Assert.That(Codes(BuildWith(Position.Top, "3078", "3100", "3006")), Does.Contain("same-passive:spellblade"));
    }

    [Test]
    public void Missing_boots_role_items_and_maps_are_flagged()
    {
        Assert.That(Codes(BuildWith(Position.Middle, "3089", "3157", "3135")), Does.Contain("no-boots"));
        Assert.That(Codes(BuildWith(Position.Middle, "3089", "3157")), Does.Not.Contain("no-boots"), "too early to tell");

        var jungle = BuildWith(Position.Jungle, "3006", "3071");
        jungle.SummonerSpells = ["SummonerFlash", "SummonerDot"];
        Assert.That(Codes(jungle), Does.Contain("jungle-no-smite").And.Contain("jungle-no-pet"));
        jungle.SummonerSpells = ["SummonerFlash", BuildWarnings.SmiteId];
        jungle.Items.Blocks.Insert(0, new ItemBlockData { Name = "Starting Items", Items = ["1101"] });
        Assert.That(Codes(jungle), Is.Empty);

        var top = BuildWith(Position.Top, "1101", "3006");
        top.SummonerSpells = ["SummonerFlash", BuildWarnings.SmiteId];
        Assert.That(Codes(top), Does.Contain("smite-off-role").And.Contain("pet-off-role"));

        Assert.That(Codes(BuildWith(Position.Support, "3006", "3165")), Does.Contain("support-no-quest"));
        Assert.That(Codes(BuildWith(Position.Bottom, "3865", "3006")), Does.Contain("support-item-off-role"));

        Assert.That(Codes(BuildWith(Position.Middle, "3177", "3006")).Any(c => c.StartsWith("map:", StringComparison.Ordinal)), Is.True, "an ARAM-only item in a Rift role build");
        Assert.That(Codes(BuildWith(null, "3177", "1101")), Does.Contain("mixed-maps"));
        Assert.That(Codes(BuildWith(null, "3177", "3006")), Is.Empty, "an ARAM build without a role is fine");
    }

    [Test]
    public void Unfinished_rune_pages_and_skill_order_gaps_are_flagged()
    {
        var build = BuildWith(null);
        build.Runes.PrimaryStyleId = 8100;
        build.Runes.PrimaryPerks = [8112, 0, 0, 0];
        var rune = BuildWarnings.Check(build, WarningGame).Single(w => w.Code == "runes-incomplete");
        Assert.That(rune.Text, Is.EqualTo("The rune page isn't finished: 3 primary runes, the secondary path and 3 stat shards missing"));

        build.SkillOrder = "QWE-Q";
        Assert.That(BuildWarnings.Check(build, WarningGame).Single(w => w.Code == "skill-gap").Text, Does.Contain("Level 4"));
        Assert.That(BuildWarnings.SkillGap("QWEQ--------------"), Is.Null, "unfilled levels at the end aren't a gap");
        Assert.That(BuildWarnings.SkillGap("-WEQ"), Is.EqualTo(1));
    }
}
