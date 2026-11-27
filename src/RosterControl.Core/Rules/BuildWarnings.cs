using System.Text.RegularExpressions;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>A likely mistake in a build.</summary>
/// <param name="Code">Stable id for ignoring it per build, e.g. "same-passive:lifeline" (specific enough that a new problem shows again).</param>
/// <param name="Text">One line: what's wrong.</param>
/// <param name="Detail">Why it matters or what to check.</param>
public sealed record BuildWarning(string Code, string Text, string Detail);

/// <summary>
/// Checks a build for things that are probably mistakes: missing or doubled boots, items whose unique passives don't stack,
/// more than six core items, items from another map, Smite or role items in the wrong role, an unfinished rune page and gaps
/// in the skill order. Only things that are there and look wrong are flagged, so a new, empty build has no warnings.
/// </summary>
public static partial class BuildWarnings
{
    public const int MaxItems = 6;
    public const string SmiteId = "SummonerSmite";

    [GeneratedRegex("<passive>([^<]+)</passive>", RegexOptions.IgnoreCase)]
    private static partial Regex Passive();

    /// <summary>The named passives in an item's description ("Lifeline", "Spellblade").</summary>
    public static IReadOnlyList<string> Passives(ItemInfo item) =>
        Passive().Matches(item.Description).Select(m => m.Groups[1].Value.Trim().TrimEnd(':').Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<BuildWarning> Check(Build build, GameDataSnapshot game)
    {
        var list = new List<BuildWarning>();
        var all = build.Items.Blocks.SelectMany(b => b.Items).Select(game.Item).OfType<ItemInfo>().ToList();
        var coreBlock = BuildSelection.CoreBlock(build);
        var core = (coreBlock?.Items ?? []).Select(game.Item).OfType<ItemInfo>().ToList();
        var coreName = string.IsNullOrWhiteSpace(coreBlock?.Name) ? "core" : coreBlock!.Name;
        var role = build.Role;

        // ── Core block ─────────────────────────────────────────────────────────────────────────────
        var counted = core.Where(i => i.Tier is not (ItemTier.Consumable or ItemTier.Trinket)).ToList();
        // A component listed before what it builds into (Boots, then Sorcerer's Shoes) is a build path, not a second item.
        var finished = counted.Where(i => !counted.Any(o => o.Id != i.Id && o.From.Contains(i.Id))).ToList();

        var boots = finished.Where(i => i.Tier == ItemTier.Boots).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (boots.Count > 1)
            list.Add(new BuildWarning("two-boots", $"Two pairs of boots in {coreName}: {Join(boots)}",
                "You can only carry one pair. Put the alternative in a situational block."));

        var legendaries = finished.Where(i => i.Tier == ItemTier.Legendary).ToList();
        foreach (var g in legendaries.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            list.Add(new BuildWarning("duplicate:" + Key(g.Key), $"{g.Key} is in {coreName} twice",
                "Legendary items can only be bought once."));

        var byPassive = legendaries.Concat(finished.Where(i => i.Tier == ItemTier.Boots))
            .DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(i => Passives(i).Select(p => (Passive: p, Item: i)))
            .GroupBy(x => x.Passive, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var g in byPassive)
            list.Add(new BuildWarning("same-passive:" + Key(g.Key), $"{Join(g.Select(x => x.Item.Name))} share the {g.Key} passive",
                $"Items with the same unique passive don't stack: only one {g.Key} works."));

        if (finished.Count > MaxItems)
            list.Add(new BuildWarning("too-many", $"{coreName} has {finished.Count} items",
                $"You can carry {MaxItems} items (plus a trinket). Move the extras to a situational block."));

        if (legendaries.Count >= 3 && !all.Any(i => i.Tier == ItemTier.Boots))
            list.Add(new BuildWarning("no-boots", "No boots in this build",
                "Most champions buy boots. Ignore this if the build skips them on purpose."));

        // ── Map ────────────────────────────────────────────────────────────────────────────────────
        var distinct = all.DistinctBy(i => i.Id).ToList();
        if (role is not null)
        {
            var notOnRift = distinct.Where(i => !i.OnSummonersRift).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (notOnRift.Count > 0)
                list.Add(new BuildWarning("map:" + Key(string.Join(",", notOnRift)), $"Not on Summoner's Rift: {Join(notOnRift)}",
                    $"This is a {role.Value.DisplayName()} build, but these items are only in other modes (e.g. ARAM)."));
        }
        else
        {
            var aramOnly = distinct.Where(i => i.OnAram && !i.OnSummonersRift).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var riftOnly = distinct.Where(i => i.OnSummonersRift && !i.OnAram).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (aramOnly.Count > 0 && riftOnly.Count > 0)
                list.Add(new BuildWarning("mixed-maps", $"Mixes ARAM-only ({Join(aramOnly)}) and Rift-only items ({Join(riftOnly)})",
                    "No single map has all of these items."));
        }

        // ── Role: Smite, jungle pets, support item ─────────────────────────────────────────────────
        var spells = build.SummonerSpells.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var hasSmite = spells.Contains(SmiteId, StringComparer.OrdinalIgnoreCase);
        var pets = distinct.Where(IsJunglePet).ToList();
        var supportItems = distinct.Where(IsSupportItem).ToList();
        if (role == Position.Jungle)
        {
            if (spells.Count > 0 && !hasSmite)
                list.Add(new BuildWarning("jungle-no-smite", "Jungle build without Smite", "Junglers need Smite for camps, objectives and their jungle companion."));
            if (all.Count > 0 && pets.Count == 0)
                list.Add(new BuildWarning("jungle-no-pet", "No jungle companion item", "Jungle builds start with a companion: Scorchclaw Pup, Gustwalker Hatchling or Mosstomper Seedling."));
        }
        else if (role is not null)
        {
            if (hasSmite)
                list.Add(new BuildWarning("smite-off-role", $"Smite in a {role.Value.DisplayName()} build", "Smite is for the jungle. Change the role if this is a jungle build."));
            if (pets.Count > 0)
                list.Add(new BuildWarning("pet-off-role", $"{pets[0].Name} in a {role.Value.DisplayName()} build", "Jungle companions are for the jungle."));
        }
        if (role == Position.Support && all.Count > 0 && supportItems.Count == 0)
            list.Add(new BuildWarning("support-no-quest", "No support item", "Supports start with World Atlas, which upgrades as you earn gold."));
        else if (role is not null and not Position.Support && supportItems.Count > 0)
            list.Add(new BuildWarning("support-item-off-role", $"{supportItems[0].Name} in a {role.Value.DisplayName()} build", "The support item is meant for the support; it shares gold poorly in other roles."));

        if (spells.Count == 2 && string.Equals(spells[0], spells[1], StringComparison.OrdinalIgnoreCase))
            list.Add(new BuildWarning("same-spell", "The same summoner spell twice", "Pick two different summoner spells."));

        // ── Runes and skill order ──────────────────────────────────────────────────────────────────
        var runes = build.Runes;
        if (runes.PrimaryStyleId is not null && !RunePageRules.IsComplete(runes))
        {
            static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
            var missingPrimary = Math.Max(0, 4 - runes.PrimaryPerks.Count(id => id != 0));
            var missingSecondary = Math.Max(0, 2 - runes.SubPerks.Count(id => id != 0));
            var missingShards = Math.Max(0, 3 - runes.Shards.Count(id => id != 0));
            var parts = new List<string>();
            if (missingPrimary > 0) parts.Add(Count(missingPrimary, "primary rune"));
            if (runes.SubStyleId is null) parts.Add("the secondary path");
            else if (missingSecondary > 0) parts.Add(Count(missingSecondary, "secondary rune"));
            if (missingShards > 0) parts.Add(Count(missingShards, "stat shard"));
            list.Add(new BuildWarning("runes-incomplete", "The rune page isn't finished" + (parts.Count > 0 ? $": {Join(parts)} missing" : ""),
                "The client won't accept an unfinished page."));
        }

        if (SkillGap(build.SkillOrder) is { } gap)
            list.Add(new BuildWarning("skill-gap", $"Level {gap} has no ability in the skill order", "Every level from 1 up to the last one you filled needs an ability."));

        return list;
    }

    /// <summary>The first empty level before the last filled one; null when there's no gap.</summary>
    public static int? SkillGap(string? order)
    {
        if (string.IsNullOrEmpty(order)) return null;
        var last = order.LastIndexOfAny(['Q', 'W', 'E', 'R']);
        for (var i = 0; i < last; i++)
            if (order[i] is not ('Q' or 'W' or 'E' or 'R')) return i + 1;
        return null;
    }

    public static bool IsJunglePet(ItemInfo item) => item.Tags.Contains("Jungle", StringComparer.OrdinalIgnoreCase) && item.Tier == ItemTier.Starter;

    public static bool IsSupportItem(ItemInfo item) =>
        item.Tags.Contains("GoldPer", StringComparer.OrdinalIgnoreCase) && item.Tags.Contains("Vision", StringComparer.OrdinalIgnoreCase);

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    private static string Key(string text) => new(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ',').ToArray());
}
