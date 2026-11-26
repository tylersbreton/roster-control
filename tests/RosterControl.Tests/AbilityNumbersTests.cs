using RosterControl.Core.GameData;
using RosterControl.Integrations;

namespace RosterControl.Tests;

/// <summary>Ability numbers from Community Dragon game files (real 16.23 data for Ahri, Garen and Malphite).</summary>
public class AbilityNumbersTests
{
    private static IReadOnlyDictionary<char, IReadOnlyList<AbilityValue>> Parse(string champion, Dictionary<char, int>? ranks = null)
    {
        using var doc = Fixtures.Load(Path.Combine("cdragon", champion + ".bin.json"));
        return AbilityCalculations.Parse(doc.RootElement, ranks);
    }

    private static string Text(IReadOnlyDictionary<char, IReadOnlyList<AbilityValue>> values, char slot, string label) =>
        values[slot].Single(v => v.Label == label).Text;

    [Test]
    public void Damage_per_rank_with_ratios()
    {
        var ahri = Parse("ahri");
        Assert.That(Text(ahri, 'Q', "Total damage"), Is.EqualTo("35/60/85/110/135 (+50% AP)"));
        Assert.That(Text(ahri, 'E', "Total damage"), Is.EqualTo("80/120/160/200/240 (+85% AP)"));
        Assert.That(Text(ahri, 'R', "Damage"), Is.EqualTo("75/125/175 (+35% AP)"), "ultimates have 3 ranks; data value names match case-insensitively");
        Assert.That(Text(ahri, 'P', "Champion heal"), Is.EqualTo("75–165 (by level) (+30% AP)"), "passives scale with champion level");
    }

    [Test]
    public void Health_and_armor_ratios_and_percentages()
    {
        var garen = Parse("garen");
        Assert.That(Text(garen, 'W', "Total shield"), Is.EqualTo("65/85/105/125/145 (+18% bonus health)"));
        Assert.That(Text(garen, 'E', "Total damage"), Is.EqualTo("4/7/10/13/16 (+40/43/46/49/52% AD)"), "per-rank ratios");
        Assert.That(Text(garen, 'P', "Regen"), Is.EqualTo("1.5%–10.1% (by level)"));

        var malphite = Parse("malphite");
        Assert.That(malphite['E'].Single(v => v.Label == "Damage").Text, Is.EqualTo("60/95/130/165/200 (+40% armor) (+60% AP)"));
        Assert.That(malphite['P'].Single(v => v.Label == "Total shield").Text, Is.EqualTo("10% max health"));
    }

    [Test]
    public void Ranks_come_from_data_dragon()
    {
        var ahri = Parse("ahri", new Dictionary<char, int> { ['Q'] = 3 });
        Assert.That(Text(ahri, 'Q', "Total damage"), Is.EqualTo("35/60/85 (+50% AP)"));
    }

    [Test]
    public void Labels_are_readable()
    {
        Assert.That(AbilityCalculations.Label("TotalDamageTT"), Is.EqualTo("Total damage"));
        Assert.That(AbilityCalculations.Label("RCalculatedDamage"), Is.EqualTo("Damage"));
        Assert.That(AbilityCalculations.Label("PBaseDamage"), Is.EqualTo("Base damage"));
        Assert.That(AbilityCalculations.Label("PercentHPBlast"), Is.EqualTo("Percent HP blast"));
        Assert.That(AbilityCalculations.Label("TTFirstHitEnergy"), Is.EqualTo("First hit energy"));
        Assert.That(AbilityCalculations.Label("ShieldAmountForTooltip"), Is.EqualTo("Shield amount"));
    }

    [Test]
    public void Unknown_or_broken_documents_give_nothing()
    {
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        Assert.That(AbilityCalculations.Parse(empty.RootElement), Is.Empty);
        using var unknownPart = System.Text.Json.JsonDocument.Parse("""
            { "mSpellCalculations": { "Damage": { "__type": "GameCalculation", "mFormulaParts": [ { "__type": "SomethingNew" } ] },
                                      "Shield": { "__type": "GameCalculation", "mFormulaParts": [ { "__type": "NumberCalculationPart", "mNumber": 50 } ] } } }
            """);
        Assert.That(AbilityCalculations.ParseSpell(unknownPart.RootElement, 5).Select(v => $"{v.Label}: {v.Text}"), Is.EqualTo(new[] { "Shield: 50" }),
            "a calculation with an unknown part is left out instead of shown wrong");
    }

    [Test]
    public void Patch_of_the_game_files()
    {
        Assert.That(GameDataClient.PatchOf("16.23.8418428+branch.releases-16-23.content.release"), Is.EqualTo("16.23"));
        Assert.That(GameDataClient.PatchOf(null), Is.Null);
        Assert.That(GameDataClient.PatchOf("pbe"), Is.Null);
    }

    [Test]
    public void Cooldown_cost_and_range_from_data_dragon()
    {
        using var doc = Fixtures.Load("Ahri.json");
        var abilities = GameDataParser.ParseAbilities(doc.RootElement, "Ahri", "16.23.1")!;
        var q = abilities['Q']!;
        Assert.That((q.CooldownText, q.CostText, q.RangeText), Is.EqualTo(("7s", "55/65/75/85/95 Mana", "970")));
        Assert.That(abilities['R']!.CooldownText, Is.EqualTo("140/120/100s"));
        Assert.That(GameDataParser.RangeText("25000"), Is.EqualTo("Global"));
        Assert.That(GameDataParser.CooldownText("0"), Is.Empty);
    }

    [Test, Explicit("Prints every parsed number for a folder of .bin.json files (set RC_BIN_DIR)")]
    public void Dump()
    {
        var dir = Environment.GetEnvironmentVariable("RC_BIN_DIR") ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "cdragon");
        foreach (var file in Directory.GetFiles(dir, "*.bin.json"))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
            TestContext.Out.WriteLine("== " + Path.GetFileName(file));
            foreach (var (slot, values) in AbilityCalculations.Parse(doc.RootElement))
                foreach (var v in values) TestContext.Out.WriteLine($"  {slot}  {v.Label}: {v.Text}");
        }
    }
}
