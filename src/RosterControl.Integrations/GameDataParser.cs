using System.Globalization;
using System.Text.Json;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Integrations;

/// <summary>Parses Data Dragon / Community Dragon documents into the application's reference models. Tolerant of missing fields.</summary>
public static class GameDataParser
{
    public const string SummonersRiftMap = "11";
    public const string AramMap = "12";

    /// <summary>League client path colours.</summary>
    public static string TreeColor(string key) => key switch
    {
        "Precision" => "#C8AA6E",
        "Domination" => "#D44242",
        "Sorcery" => "#9FAAFC",
        "Resolve" => "#A1D586",
        "Inspiration" => "#49AAB9",
        _ => "#C8AA6E",
    };

    public static IReadOnlyList<ChampionInfo> ParseChampions(JsonElement root, IReadOnlyDictionary<string, IReadOnlyList<string>> positions)
    {
        var list = new List<ChampionInfo>();
        if (!root.TryGetProperty("data", out var data)) return list;
        foreach (var prop in data.EnumerateObject())
        {
            var c = prop.Value;
            var id = Str(c, "id") ?? prop.Name;
            var pos = positions.TryGetValue(id, out var p)
                ? p.Select(PositionInfo.Parse).Where(x => x is not null).Select(x => x!.Value).Distinct().ToList()
                : [];
            list.Add(new ChampionInfo(
                id,
                int.TryParse(Str(c, "key"), out var key) ? key : 0,
                Str(c, "name") ?? id,
                Str(c, "title") ?? "",
                StrArray(c, "tags"),
                c.TryGetProperty("image", out var img) ? Str(img, "full") ?? id + ".png" : id + ".png",
                pos,
                ParseStats(c)));
        }
        return list;
    }

    /// <summary>Base stats and growth (Data Dragon <c>stats</c>); null when missing.</summary>
    internal static ChampionStats? ParseStats(JsonElement champion)
    {
        if (!champion.TryGetProperty("stats", out var st) || st.ValueKind != JsonValueKind.Object) return null;
        double N(string name) => st.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
        var info = champion.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        int I(string name) => info.ValueKind == JsonValueKind.Object && info.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
        var resource = Str(champion, "partype") ?? "";
        return new ChampionStats(
            N("hp"), N("hpperlevel"),
            N("mp"), N("mpperlevel"),
            N("movespeed"),
            N("armor"), N("armorperlevel"),
            N("spellblock"), N("spellblockperlevel"),
            N("attackrange"),
            N("attackdamage"), N("attackdamageperlevel"),
            N("attackspeed"), N("attackspeedperlevel"),
            AdaptiveAp: I("magic") > I("attack"),
            Resource: resource == "None" ? "" : resource);
    }

    /// <summary>All items, plus the ids shown in the shop (purchasable on Summoner's Rift or ARAM, de-duplicated by name).</summary>
    public static (IReadOnlyList<ItemInfo> Items, IReadOnlyList<string> ShopIds) ParseItems(JsonElement root)
    {
        var items = new List<ItemInfo>();
        var shopCandidates = new List<ItemInfo>();
        if (!root.TryGetProperty("data", out var data)) return (items, []);
        foreach (var prop in data.EnumerateObject())
        {
            var i = prop.Value;
            var tags = StrArray(i, "tags");
            var from = StrArray(i, "from");
            var into = StrArray(i, "into");
            var maps = i.TryGetProperty("maps", out var m) ? m : default;
            bool OnMap(string map) => maps.ValueKind == JsonValueKind.Object && maps.TryGetProperty(map, out var v) && v.ValueKind == JsonValueKind.True;
            var gold = i.TryGetProperty("gold", out var g) ? g : default;
            var total = gold.ValueKind == JsonValueKind.Object && gold.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
            var purchasable = gold.ValueKind == JsonValueKind.Object && gold.TryGetProperty("purchasable", out var pu) && pu.ValueKind == JsonValueKind.True;
            var combine = gold.ValueKind == JsonValueKind.Object && gold.TryGetProperty("base", out var b) && b.TryGetInt32(out var bv) ? bv : total;
            var item = new ItemInfo(
                prop.Name,
                Str(i, "name") ?? prop.Name,
                Str(i, "description") ?? "",
                Str(i, "plaintext") ?? "",
                total,
                tags,
                from,
                into,
                i.TryGetProperty("image", out var img) ? Str(img, "full") ?? prop.Name + ".png" : prop.Name + ".png",
                OnMap(SummonersRiftMap),
                OnMap(AramMap),
                ItemCatalog.Classify(tags, from, into),
                combine);
            items.Add(item);

            var inStore = !i.TryGetProperty("inStore", out var s) || s.ValueKind != JsonValueKind.False;
            var hidden = i.TryGetProperty("hideFromAll", out var h) && h.ValueKind == JsonValueKind.True;
            var champOnly = i.TryGetProperty("requiredChampion", out var rc) && rc.ValueKind == JsonValueKind.String && rc.GetString() is { Length: > 0 };
            var allyOnly = i.TryGetProperty("requiredAlly", out var ra) && ra.ValueKind == JsonValueKind.String && ra.GetString() is { Length: > 0 };
            if (purchasable && inStore && !hidden && !champOnly && !allyOnly && (item.OnSummonersRift || item.OnAram) && item.Name.Length > 0)
                shopCandidates.Add(item);
        }
        // Mode variants share a name with the standard item but have 6-digit ids; keep the lowest id.
        var shop = shopCandidates
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(grp => grp.OrderBy(i => int.TryParse(i.Id, out var n) ? n : int.MaxValue).First())
            .Select(i => i.Id)
            .ToList();
        return (items, shop);
    }

    public static IReadOnlyList<RuneTreeInfo> ParseRuneTrees(JsonElement runesReforged, JsonElement? perkStyles)
    {
        var styleInfo = new Dictionary<int, (string Tagline, Dictionary<string, string> Backgrounds)>();
        if (perkStyles is { } ps && ps.TryGetProperty("styles", out var styles))
        {
            foreach (var st in styles.EnumerateArray())
            {
                if (!st.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var sid)) continue;
                var backgrounds = new Dictionary<string, string>();
                if (st.TryGetProperty("assetMap", out var map) && map.ValueKind == JsonValueKind.Object)
                {
                    foreach (var a in map.EnumerateObject())
                        if (a.Name.StartsWith('p') && a.Value.GetString() is { } path && path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                            backgrounds[a.Name] = AssetUrls.CommunityDragon(path);
                }
                styleInfo[sid] = (Str(st, "tooltip") ?? "", backgrounds);
            }
        }

        var trees = new List<RuneTreeInfo>();
        if (runesReforged.ValueKind != JsonValueKind.Array) return trees;
        foreach (var tree in runesReforged.EnumerateArray())
        {
            var treeId = tree.GetProperty("id").GetInt32();
            var key = Str(tree, "key") ?? "";
            var rows = new List<IReadOnlyList<RuneInfo>>();
            if (tree.TryGetProperty("slots", out var slots))
            {
                var rowIndex = 0;
                foreach (var slot in slots.EnumerateArray())
                {
                    var runes = new List<RuneInfo>();
                    foreach (var r in slot.GetProperty("runes").EnumerateArray())
                    {
                        runes.Add(new RuneInfo(
                            r.GetProperty("id").GetInt32(),
                            Str(r, "key") ?? "",
                            Str(r, "name") ?? "",
                            Str(r, "icon") ?? "",
                            Str(r, "shortDesc") ?? "",
                            Str(r, "longDesc") ?? "",
                            treeId,
                            rowIndex));
                    }
                    rows.Add(runes);
                    rowIndex++;
                }
            }
            styleInfo.TryGetValue(treeId, out var info);
            trees.Add(new RuneTreeInfo(treeId, key, Str(tree, "name") ?? key, Str(tree, "icon") ?? "", TreeColor(key), info.Tagline ?? "",
                rows, (IReadOnlyDictionary<string, string>?)info.Backgrounds ?? new Dictionary<string, string>()));
        }
        // Client order: Precision, Domination, Sorcery, Resolve, Inspiration.
        int Order(RuneTreeInfo t) => t.Key switch { "Precision" => 0, "Domination" => 1, "Sorcery" => 2, "Resolve" => 3, "Inspiration" => 4, _ => 5 };
        return trees.OrderBy(Order).ThenBy(t => t.Id).ToList();
    }

    /// <summary>Stat shard rows (Offense / Flex / Defense) from Community Dragon; Data Dragon does not publish them.</summary>
    public static IReadOnlyList<StatShardRow> ParseShardRows(JsonElement perkStyles, JsonElement perks)
    {
        var perkById = new Dictionary<int, JsonElement>();
        if (perks.ValueKind == JsonValueKind.Array)
            foreach (var p in perks.EnumerateArray())
                if (p.TryGetProperty("id", out var id) && id.TryGetInt32(out var v)) perkById[v] = p;

        if (!perkStyles.TryGetProperty("styles", out var styles)) return [];
        foreach (var st in styles.EnumerateArray())
        {
            if (!st.TryGetProperty("slots", out var slots)) continue;
            var rows = new List<StatShardRow>();
            foreach (var slot in slots.EnumerateArray())
            {
                if (Str(slot, "type") != "kStatMod") continue;
                var options = new List<StatShardInfo>();
                foreach (var idEl in slot.GetProperty("perks").EnumerateArray())
                {
                    var id = idEl.GetInt32();
                    if (!perkById.TryGetValue(id, out var perk)) continue;
                    options.Add(new StatShardInfo(id, Str(perk, "name") ?? id.ToString(CultureInfo.InvariantCulture),
                        AssetUrls.CommunityDragon(Str(perk, "iconPath") ?? ""), Str(perk, "shortDesc") ?? ""));
                }
                rows.Add(new StatShardRow(Str(slot, "slotLabel") is { Length: > 0 } label ? label : $"Row {rows.Count + 1}", options));
            }
            if (rows.Count > 0) return rows; // every style carries the same shard rows
        }
        return [];
    }

    public static IReadOnlyList<SummonerSpellInfo> ParseSummonerSpells(JsonElement root)
    {
        var list = new List<SummonerSpellInfo>();
        if (!root.TryGetProperty("data", out var data)) return list;
        foreach (var prop in data.EnumerateObject())
        {
            var s = prop.Value;
            var modes = StrArray(s, "modes");
            var sr = modes.Contains("CLASSIC");
            var aram = modes.Contains("ARAM");
            if (!sr && !aram) continue;
            if (prop.Name.Contains("Placeholder", StringComparison.OrdinalIgnoreCase)) continue;
            var cooldown = s.TryGetProperty("cooldown", out var cd) && cd.ValueKind == JsonValueKind.Array && cd.GetArrayLength() > 0 && cd[0].TryGetDouble(out var c) ? c : 0;
            list.Add(new SummonerSpellInfo(Str(s, "id") ?? prop.Name, Str(s, "name") ?? prop.Name, Str(s, "description") ?? "", cooldown,
                s.TryGetProperty("image", out var img) ? Str(img, "full") ?? prop.Name + ".png" : prop.Name + ".png", sr, aram));
        }
        // Client-like order: the most common picks first.
        string[] order = ["SummonerFlash", "SummonerTeleport", "SummonerDot", "SummonerSmite", "SummonerHeal", "SummonerExhaust", "SummonerBarrier", "SummonerHaste", "SummonerBoost", "SummonerMana", "SummonerSnowball"];
        return list.OrderBy(s => Array.IndexOf(order, s.Id) is var i && i >= 0 ? i : order.Length).ThenBy(s => s.Name).ToList();
    }

    public static ChampionAbilities? ParseAbilities(JsonElement root, string championId, string version)
    {
        if (!root.TryGetProperty("data", out var data)) return null;
        JsonElement champ = default;
        foreach (var prop in data.EnumerateObject())
        {
            champ = prop.Value;
            if (string.Equals(prop.Name, championId, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (champ.ValueKind != JsonValueKind.Object) return null;
        var abilities = new List<AbilityInfo>();
        if (champ.TryGetProperty("passive", out var passive) && passive.TryGetProperty("image", out var pimg))
            abilities.Add(new AbilityInfo('P', Str(passive, "name") ?? "Passive", Str(passive, "description") ?? "", AssetUrls.Passive(version, Str(pimg, "full") ?? ""), 0));
        if (champ.TryGetProperty("spells", out var spells))
        {
            var slots = new[] { 'Q', 'W', 'E', 'R' };
            var index = 0;
            foreach (var sp in spells.EnumerateArray())
            {
                if (index >= slots.Length) break;
                var img = sp.TryGetProperty("image", out var si) ? Str(si, "full") ?? "" : "";
                var maxRank = sp.TryGetProperty("maxrank", out var mr) && mr.TryGetInt32(out var r) ? r : slots[index] == 'R' ? 3 : 5;
                abilities.Add(new AbilityInfo(slots[index], Str(sp, "name") ?? slots[index].ToString(), Str(sp, "description") ?? "", AssetUrls.Spell(version, img), maxRank,
                    CooldownText(Str(sp, "cooldownBurn")), CostText(sp, Str(champ, "partype")), RangeText(Str(sp, "rangeBurn"))));
                index++;
            }
        }
        return new ChampionAbilities(championId, abilities);
    }

    internal static string CooldownText(string? burn) => string.IsNullOrWhiteSpace(burn) || burn.Trim() == "0" ? "" : burn.Trim() + "s";

    /// <summary>The cost line, e.g. "80/85/90/95/100 Mana". Data Dragon's <c>resource</c> is a template like "{{ cost }} {{ abilityresourcename }}".</summary>
    internal static string CostText(JsonElement spell, string? resourceName)
    {
        var burn = Str(spell, "costBurn")?.Trim() ?? "0";
        var resource = (Str(spell, "resource") ?? "").Trim();
        var name = string.IsNullOrWhiteSpace(resourceName) || resourceName == "None" ? "" : resourceName.Trim();
        if (resource.Length == 0) return burn == "0" ? "" : $"{burn} {name}".Trim();
        if (resource.Equals("No Cost", StringComparison.OrdinalIgnoreCase)) return "No cost";
        var text = resource.Replace("{{ cost }}", burn, StringComparison.Ordinal).Replace("{{ abilityresourcename }}", name, StringComparison.Ordinal).Trim();
        if (text.Contains("{{", StringComparison.Ordinal))
            text = burn == "0" ? "" : $"{burn} {name}".Trim(); // other placeholders (e.g. mana per second) aren't published
        return text == "0" || text.StartsWith("0 ", StringComparison.Ordinal) && burn == "0" ? "No cost" : text;
    }

    internal static string RangeText(string? burn)
    {
        if (string.IsNullOrWhiteSpace(burn) || burn.Trim() == "0") return "";
        var parts = burn.Trim().Split('/');
        return parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 20000) ? "Global" : burn.Trim();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> StrArray(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
