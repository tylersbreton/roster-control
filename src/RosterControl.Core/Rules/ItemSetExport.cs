using System.Text.Json;
using System.Text.Json.Nodes;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>
/// A build's items in the League client's item set format, for pasting into the client (Collection → Item Sets →
/// Import from clipboard). Only text on the clipboard: nothing talks to the client.
/// </summary>
public static class ItemSetExport
{
    public const int SummonersRiftMap = 11;

    /// <param name="skipped">Items no longer in the game (left out).</param>
    public static string ToClientJson(Build build, ChampionInfo champion, GameDataSnapshot game, out int skipped)
    {
        skipped = 0;
        var blocks = new JsonArray();
        foreach (var block in build.Items.Blocks)
        {
            // Repeated items in a block become one entry with a count (two potions → ×2), in first-seen order.
            var counts = new List<(string Id, int Count)>();
            foreach (var id in block.Items)
            {
                if (game.Item(id) is null)
                {
                    skipped++;
                    continue;
                }
                var index = counts.FindIndex(c => c.Id == id);
                if (index >= 0) counts[index] = (id, counts[index].Count + 1);
                else counts.Add((id, 1));
            }
            if (counts.Count == 0) continue;
            blocks.Add(new JsonObject
            {
                ["type"] = string.IsNullOrWhiteSpace(block.Name) ? "Items" : block.Name.Trim(),
                ["items"] = new JsonArray(counts.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["count"] = c.Count }).ToArray()),
            });
        }
        var set = new JsonObject
        {
            ["title"] = Title(champion, build),
            ["type"] = "custom",
            ["map"] = "any",
            ["mode"] = "any",
            ["priority"] = false,
            ["sortrank"] = 0,
            ["startedFrom"] = "blank",
            ["associatedChampions"] = new JsonArray(champion.Key),
            ["associatedMaps"] = new JsonArray(SummonersRiftMap),
            ["blocks"] = blocks,
            ["preferredItemSlots"] = new JsonArray(),
        };
        return set.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>"Ahri · Standard", at most 75 characters (the client's limit).</summary>
    public static string Title(ChampionInfo champion, Build build)
    {
        var title = $"{champion.Name} · {(string.IsNullOrWhiteSpace(build.Name) ? "Build" : build.Name.Trim())}";
        return title.Length > 75 ? title[..75] : title;
    }
}
