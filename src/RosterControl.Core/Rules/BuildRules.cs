using RosterControl.Core.GameData;
using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>Which of a champion's builds is "the" build for a role (opened first, shown in previews).</summary>
public static class BuildSelection
{
    /// <summary>
    /// The default for <paramref name="role"/>, then the any-role default, then the first build for that role,
    /// then (with no role) any default, then the first build.
    /// </summary>
    public static T? PickDefault<T>(IReadOnlyList<T> builds, Position? role, Func<T, Build> model) where T : class
    {
        var defaults = builds.Where(b => model(b).IsDefault).ToList();
        return (role is not null ? defaults.FirstOrDefault(b => model(b).Role == role) : null)
               ?? defaults.FirstOrDefault(b => model(b).Role is null)
               ?? (role is not null ? builds.FirstOrDefault(b => model(b).Role == role) : null)
               ?? (role is null ? defaults.FirstOrDefault() : null)
               ?? builds.FirstOrDefault();
    }

    public static Build? PickDefault(IReadOnlyList<Build> builds, Position? role) => PickDefault(builds, role, b => b);

    /// <summary>The block shown as a build's summary: the one named "core", else the first with items.</summary>
    public static ItemBlockData? CoreBlock(Build build) =>
        build.Items.Blocks.FirstOrDefault(b => b.Name.Contains("core", StringComparison.OrdinalIgnoreCase) && b.Items.Count > 0)
        ?? build.Items.Blocks.FirstOrDefault(b => b.Items.Count > 0);
}

public enum BuildChangeKind
{
    ItemChanged,
    ItemRemoved,
    RuneRemoved,
}

/// <param name="ItemId">The item concerned (for its icon), when it's an item change.</param>
public sealed record BuildChange(BuildChangeKind Kind, string? ItemId, string Text);

/// <summary>What changed in the game since a build was last saved (items reworked or removed, runes removed).</summary>
public static class PatchReview
{
    /// <param name="baseline">Items as they were on the build's patch; null when unavailable (only removals are found then).</param>
    public static IReadOnlyList<BuildChange> Changes(Build build, GameDataSnapshot game, IReadOnlyDictionary<string, ItemInfo>? baseline)
    {
        if (string.IsNullOrEmpty(build.GameVersion) || build.GameVersion == game.Version) return [];
        var changes = new List<BuildChange>();
        foreach (var id in build.Items.Blocks.SelectMany(b => b.Items).Distinct(StringComparer.Ordinal))
        {
            var now = game.Item(id);
            var before = baseline?.GetValueOrDefault(id);
            if (now is null)
            {
                changes.Add(new BuildChange(BuildChangeKind.ItemRemoved, id, $"{before?.Name ?? "Item " + id} was removed from the game"));
                continue;
            }
            if (baseline is null) continue;
            if (ItemCatalog.DescribeChange(before, now) is { } change)
                changes.Add(new BuildChange(BuildChangeKind.ItemChanged, id, $"{now.Name}: {change}"));
        }

        var runes = build.Runes;
        if (runes.PrimaryStyleId is { } primary && game.Tree(primary) is null)
            changes.Add(new BuildChange(BuildChangeKind.RuneRemoved, null, "The primary rune path no longer exists"));
        if (runes.SubStyleId is { } sub && game.Tree(sub) is null)
            changes.Add(new BuildChange(BuildChangeKind.RuneRemoved, null, "The secondary rune path no longer exists"));
        var missingRunes = runes.PrimaryPerks.Concat(runes.SubPerks).Count(id => id != 0 && game.Rune(id) is null);
        if (missingRunes > 0)
            changes.Add(new BuildChange(BuildChangeKind.RuneRemoved, null, $"{missingRunes} rune{(missingRunes == 1 ? " was" : "s were")} removed from the game"));
        var missingShards = runes.Shards.Count(id => id != 0 && game.Shard(id) is null);
        if (missingShards > 0)
            changes.Add(new BuildChange(BuildChangeKind.RuneRemoved, null, $"{missingShards} stat shard{(missingShards == 1 ? " was" : "s were")} changed"));
        return changes;
    }
}
