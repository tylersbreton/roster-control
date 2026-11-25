using RosterControl.Core.GameData;
using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>
/// Rune page editing rules, matching the League client:
/// one primary path (keystone + one rune per row), a different secondary path with two runes from two different
/// non-keystone rows (choosing a third replaces the oldest; choosing in an occupied row replaces that row's rune),
/// and one stat shard per shard row.
/// </summary>
public static class RunePageRules
{
    public static void Normalize(RunePageData page)
    {
        while (page.PrimaryPerks.Count < 4) page.PrimaryPerks.Add(0);
        if (page.PrimaryPerks.Count > 4) page.PrimaryPerks.RemoveRange(4, page.PrimaryPerks.Count - 4);
        while (page.Shards.Count < 3) page.Shards.Add(0);
        if (page.Shards.Count > 3) page.Shards.RemoveRange(3, page.Shards.Count - 3);
        if (page.SubPerks.Count > 2) page.SubPerks.RemoveRange(0, page.SubPerks.Count - 2);
    }

    public static bool SelectPrimaryTree(RunePageData page, int treeId)
    {
        Normalize(page);
        if (page.PrimaryStyleId == treeId) return false;
        page.PrimaryStyleId = treeId;
        for (var i = 0; i < page.PrimaryPerks.Count; i++) page.PrimaryPerks[i] = 0;
        if (page.SubStyleId == treeId)
        {
            page.SubStyleId = null;
            page.SubPerks.Clear();
        }
        return true;
    }

    public static bool SelectPrimaryRune(RunePageData page, GameDataSnapshot data, int runeId)
    {
        Normalize(page);
        var rune = data.Rune(runeId);
        if (rune is null || rune.TreeId != page.PrimaryStyleId || rune.Row is < 0 or > 3) return false;
        if (page.PrimaryPerks[rune.Row] == runeId) return false;
        page.PrimaryPerks[rune.Row] = runeId;
        return true;
    }

    public static bool SelectSubTree(RunePageData page, int treeId)
    {
        Normalize(page);
        if (treeId == page.PrimaryStyleId || page.SubStyleId == treeId) return false;
        page.SubStyleId = treeId;
        page.SubPerks.Clear();
        return true;
    }

    public static bool SelectSubRune(RunePageData page, GameDataSnapshot data, int runeId)
    {
        Normalize(page);
        var rune = data.Rune(runeId);
        if (rune is null || rune.TreeId != page.SubStyleId || rune.IsKeystone) return false;
        if (page.SubPerks.Contains(runeId)) return false;
        var sameRow = page.SubPerks.FindIndex(id => data.Rune(id)?.Row == rune.Row);
        if (sameRow >= 0) page.SubPerks.RemoveAt(sameRow);
        else if (page.SubPerks.Count >= 2) page.SubPerks.RemoveAt(0);
        page.SubPerks.Add(runeId);
        return true;
    }

    public static bool SelectShard(RunePageData page, GameDataSnapshot data, int row, int shardId)
    {
        Normalize(page);
        if (row < 0 || row >= data.ShardRows.Count || row >= page.Shards.Count) return false;
        if (data.ShardRows[row].Options.All(o => o.Id != shardId)) return false;
        if (page.Shards[row] == shardId) return false;
        page.Shards[row] = shardId;
        return true;
    }

    /// <summary>Removes selections that no longer exist in this patch's data. Returns a description per removal.</summary>
    public static IReadOnlyList<string> Sanitize(RunePageData page, GameDataSnapshot data)
    {
        Normalize(page);
        var removed = new List<string>();
        if (page.PrimaryStyleId is { } p && data.Tree(p) is null)
        {
            removed.Add($"Primary path {p} no longer exists");
            page.PrimaryStyleId = null;
        }
        if (page.SubStyleId is { } s && (data.Tree(s) is null || s == page.PrimaryStyleId))
        {
            removed.Add($"Secondary path {s} is not available");
            page.SubStyleId = null;
            page.SubPerks.Clear();
        }
        for (var i = 0; i < 4; i++)
        {
            var id = page.PrimaryPerks[i];
            if (id == 0) continue;
            var rune = data.Rune(id);
            if (rune is null || rune.TreeId != page.PrimaryStyleId || rune.Row != i)
            {
                removed.Add(rune is null ? $"Rune {id} no longer exists" : $"{rune.Name} moved");
                page.PrimaryPerks[i] = 0;
            }
        }
        for (var i = page.SubPerks.Count - 1; i >= 0; i--)
        {
            var rune = data.Rune(page.SubPerks[i]);
            if (rune is null || rune.TreeId != page.SubStyleId || rune.IsKeystone)
            {
                removed.Add(rune is null ? $"Rune {page.SubPerks[i]} no longer exists" : $"{rune.Name} moved");
                page.SubPerks.RemoveAt(i);
            }
        }
        for (var i = 0; i < 3; i++)
        {
            var id = page.Shards[i];
            if (id == 0) continue;
            if (i >= data.ShardRows.Count || data.ShardRows[i].Options.All(o => o.Id != id))
            {
                removed.Add($"Stat shard {id} is no longer offered in that row");
                page.Shards[i] = 0;
            }
        }
        return removed;
    }

    public static bool IsComplete(RunePageData page) =>
        page.PrimaryStyleId is not null
        && page.SubStyleId is not null
        && page.PrimaryPerks.Count == 4 && page.PrimaryPerks.All(id => id != 0)
        && page.SubPerks.Count == 2
        && page.Shards.Count == 3 && page.Shards.All(id => id != 0);

    /// <summary>Number of selections made out of the 9 a full page needs (4 primary + 2 secondary + 3 shards).</summary>
    public static int SelectedCount(RunePageData page) =>
        page.PrimaryPerks.Count(id => id != 0) + page.SubPerks.Count + page.Shards.Count(id => id != 0);
}
