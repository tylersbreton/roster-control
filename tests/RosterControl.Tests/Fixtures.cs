using System.Text.Json;
using RosterControl.Core.GameData;
using RosterControl.Integrations;

namespace RosterControl.Tests;

/// <summary>Real Data Dragon / Community Dragon documents (patch 16.23.1; champion and item files trimmed).</summary>
internal static class Fixtures
{
    private static readonly Lazy<GameDataSnapshot> LazyGame = new(Build);

    public static GameDataSnapshot Game => LazyGame.Value;

    public static JsonDocument Load(string file) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Positions = new Dictionary<string, IReadOnlyList<string>>
    {
        ["Ahri"] = ["MIDDLE"],
        ["Darius"] = ["TOP"],
        ["MonkeyKing"] = ["TOP", "JUNGLE"],
    };

    private static GameDataSnapshot Build()
    {
        using var champions = Load("champion.json");
        using var items = Load("item.json");
        using var runes = Load("runesReforged.json");
        using var styles = Load("perkstyles.json");
        using var perks = Load("perks.json");
        using var spells = Load("summoner.json");
        var (itemList, shop) = GameDataParser.ParseItems(items.RootElement);
        return new GameDataSnapshot("16.23.1",
            GameDataParser.ParseChampions(champions.RootElement, Positions),
            itemList, shop,
            GameDataParser.ParseRuneTrees(runes.RootElement, styles.RootElement),
            GameDataParser.ParseShardRows(styles.RootElement, perks.RootElement),
            GameDataParser.ParseSummonerSpells(spells.RootElement),
            positionsAvailable: true);
    }

    public static RuneTreeInfo Tree(string key) => Game.RuneTrees.Single(t => t.Key == key);
}
