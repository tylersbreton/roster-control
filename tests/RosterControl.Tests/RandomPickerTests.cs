using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Tests;

/// <summary>Random pick (1.7): candidates from groups or every champion, weights and the pick itself.</summary>
public class RandomPickerTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(["Ahri", "Syndra", "Orianna", "Zed", "Garen"], StringComparer.OrdinalIgnoreCase);

    private static RosterGroup Group(int id, string name, params (string Id, ChampionPriority Priority, int Stars)[] entries) => new()
    {
        Id = id,
        Name = name,
        Entries = entries.Select(e => new RosterEntry { GroupId = id, ChampionId = e.Id, Priority = e.Priority, Proficiency = e.Stars }).ToList(),
    };

    private static readonly RosterGroup Mid = Group(1, "Middle",
        ("Ahri", ChampionPriority.Main, 4), ("Syndra", ChampionPriority.Learning, 1), ("Removed", ChampionPriority.Main, 5));
    private static readonly RosterGroup Flex = Group(2, "Flex",
        ("Ahri", ChampionPriority.Pocket, 2), ("Garen", ChampionPriority.Secondary, 0));

    [Test]
    public void Equal_chances_count_each_champion_once()
    {
        var candidates = RandomPicker.FromGroups([Mid, Flex], weighted: false, Known);
        Assert.That(candidates.Select(c => c.ChampionId), Is.EquivalentTo(new[] { "Ahri", "Syndra", "Garen" }), "Ahri once, the removed champion skipped");
        Assert.That(candidates.All(c => c.Weight == 1));
        Assert.That(RandomPicker.Chance(candidates[0], candidates), Is.EqualTo(1.0 / 3).Within(1e-9));
        var ahri = candidates.Single(c => c.ChampionId == "Ahri");
        Assert.That(ahri.Placements.Select(p => p.Group.Name), Is.EqualTo(new[] { "Middle", "Flex" }));
    }

    [Test]
    public void Weighted_uses_the_best_placement()
    {
        var candidates = RandomPicker.FromGroups([Mid, Flex], weighted: true, Known);
        Assert.That(candidates.Single(c => c.ChampionId == "Ahri").Weight, Is.EqualTo(3.4).Within(1e-9), "Main 4★ beats Pocket 2★");
        Assert.That(candidates.Single(c => c.ChampionId == "Syndra").Weight, Is.EqualTo(1.1).Within(1e-9));
        Assert.That(candidates.Single(c => c.ChampionId == "Garen").Weight, Is.EqualTo(2).Within(1e-9));
        Assert.That(RandomPicker.Chance(candidates.Single(c => c.ChampionId == "Ahri"), candidates), Is.EqualTo(3.4 / 6.5).Within(1e-9));
    }

    [Test]
    public void All_champions_are_equally_likely()
    {
        var candidates = RandomPicker.FromAll(Known);
        Assert.That(candidates, Has.Count.EqualTo(5));
        Assert.That(candidates.All(c => c.Weight == 1 && c.Placements.Count == 0));
    }

    [Test]
    public void Pick_follows_the_weights_and_can_repeat()
    {
        Assert.That(RandomPicker.Pick([], new Random(1)), Is.Null);
        var candidates = RandomPicker.FromGroups([Mid, Flex], weighted: true, Known);
        var random = new Random(42);
        var counts = new Dictionary<string, int>();
        string? previous = null;
        var repeats = 0;
        for (var i = 0; i < 20000; i++)
        {
            var id = RandomPicker.Pick(candidates, random)!.ChampionId;
            counts[id] = counts.GetValueOrDefault(id) + 1;
            if (id == previous) repeats++;
            previous = id;
        }
        Assert.That(counts["Ahri"] / 20000.0, Is.EqualTo(3.4 / 6.5).Within(0.02));
        Assert.That(counts["Syndra"] / 20000.0, Is.EqualTo(1.1 / 6.5).Within(0.02));
        Assert.That(repeats, Is.GreaterThan(0), "pure random: the same champion can come up twice in a row");
    }
}
