using RosterControl.Core.GameData;

namespace RosterControl.Core.Rules;

public sealed record ItemStatCategory(string Key, string Name, IReadOnlyList<string> Tags);

public enum ItemMap
{
    SummonersRift,
    Aram,
}

/// <summary>Item shop categorisation and filtering (mirrors the League client's item set editor filters).</summary>
public static class ItemCatalog
{
    /// <summary>The stat filters shown in the client's item set editor, mapped to Data Dragon item tags.</summary>
    public static readonly IReadOnlyList<ItemStatCategory> StatCategories =
    [
        new("ad", "Attack Damage", ["Damage"]),
        new("crit", "Critical Strike", ["CriticalStrike"]),
        new("as", "Attack Speed", ["AttackSpeed"]),
        new("onhit", "On-Hit Effects", ["OnHit"]),
        new("arpen", "Armor Penetration", ["ArmorPenetration"]),
        new("ap", "Ability Power", ["SpellDamage"]),
        new("mana", "Mana & Regeneration", ["Mana", "ManaRegen"]),
        new("mpen", "Magic Penetration", ["MagicPenetration"]),
        new("health", "Health & Regeneration", ["Health", "HealthRegen"]),
        new("armor", "Armor", ["Armor"]),
        new("mr", "Magic Resistance", ["SpellBlock", "MagicResist"]),
        new("ah", "Ability Haste", ["AbilityHaste", "CooldownReduction"]),
        new("move", "Movement", ["Boots", "NonbootsMovement"]),
        new("vamp", "Life Steal & Vamp", ["LifeSteal", "SpellVamp", "Omnivamp"]),
    ];

    public static readonly IReadOnlyList<ItemTier> TierOrder =
        [ItemTier.Starter, ItemTier.Consumable, ItemTier.Trinket, ItemTier.Boots, ItemTier.Basic, ItemTier.Epic, ItemTier.Legendary];

    public static string DisplayName(this ItemTier tier) => tier switch
    {
        ItemTier.Starter => "Starter Items",
        ItemTier.Boots => "Boots",
        ItemTier.Basic => "Basic Items",
        ItemTier.Epic => "Epic Items",
        ItemTier.Legendary => "Legendary Items",
        ItemTier.Consumable => "Consumables",
        ItemTier.Trinket => "Trinkets",
        _ => tier.ToString(),
    };

    public static ItemTier Classify(IReadOnlyCollection<string> tags, IReadOnlyCollection<string> from, IReadOnlyCollection<string> into)
    {
        bool Has(string t) => tags.Contains(t, StringComparer.OrdinalIgnoreCase);
        if (Has("Trinket")) return ItemTier.Trinket;
        if (Has("Consumable")) return ItemTier.Consumable;
        if (Has("Boots")) return ItemTier.Boots;
        // Support quest items (World Atlas and its upgrades) are bought at the start.
        if (Has("GoldPer") && Has("Lane") && into.Count == 0) return ItemTier.Starter;
        if ((Has("Lane") || Has("Jungle")) && from.Count == 0 && into.Count <= 1) return ItemTier.Starter;
        if (from.Count == 0) return ItemTier.Basic;
        return into.Count > 0 ? ItemTier.Epic : ItemTier.Legendary;
    }

    public static bool MatchesCategories(ItemInfo item, IReadOnlyCollection<string> categoryKeys)
    {
        if (categoryKeys.Count == 0) return true;
        foreach (var key in categoryKeys)
        {
            var cat = StatCategories.FirstOrDefault(c => c.Key == key);
            if (cat is null) continue;
            if (!cat.Tags.Any(t => item.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    public static IEnumerable<ItemInfo> Filter(IEnumerable<ItemInfo> items, string? search, IReadOnlyCollection<string> categoryKeys, ItemMap map)
    {
        var query = string.IsNullOrWhiteSpace(search) ? null : ChampionInfo.Normalize(search);
        return items.Where(i =>
            (map == ItemMap.SummonersRift ? i.OnSummonersRift : i.OnAram)
            && (query is null || i.SearchKey.Contains(query, StringComparison.Ordinal) || Aliases(i).Any(a => a.Contains(query, StringComparison.Ordinal)))
            && MatchesCategories(i, categoryKeys));
    }

    public static IReadOnlyList<(ItemTier Tier, IReadOnlyList<ItemInfo> Items)> GroupByTier(IEnumerable<ItemInfo> items) =>
        items.GroupBy(i => i.Tier)
            .OrderBy(g => TierOrder.ToList().IndexOf(g.Key))
            .Select(g => (g.Key, (IReadOnlyList<ItemInfo>)g.OrderBy(i => i.TotalGold).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

    /// <summary>The item's recipe tree: its components, their components, and so on (like the client's shop).</summary>
    public static RecipeNode BuildRecipe(ItemInfo item, IReadOnlyDictionary<string, ItemInfo> items, int maxDepth = 4)
    {
        var components = maxDepth <= 1
            ? []
            : item.From.Select(id => items.GetValueOrDefault(id)).Where(i => i is not null).Select(i => BuildRecipe(i!, items, maxDepth - 1)).ToList();
        return new RecipeNode(item, components);
    }

    /// <summary>Items this item builds into, restricted to purchasable shop items (mode variants removed), cheapest first.</summary>
    public static IReadOnlyList<ItemInfo> BuildsInto(ItemInfo item, IReadOnlyDictionary<string, ItemInfo> items, IReadOnlySet<string> shopIds) =>
        item.Into.Select(id => items.GetValueOrDefault(id))
            .Where(i => i is not null && shopIds.Contains(i.Id))
            .Cast<ItemInfo>()
            .DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i.TotalGold).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// What changed about an item between two patches ("Cost 3,000 → 3,100; stats or effects changed"), or null when it
    /// is unchanged or there is nothing to compare with.
    /// </summary>
    public static string? DescribeChange(ItemInfo? before, ItemInfo? after)
    {
        if (before is null || after is null) return null;
        var changes = new List<string>();
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal)) changes.Add($"renamed from {before.Name}");
        if (before.TotalGold != after.TotalGold)
            changes.Add($"cost {before.TotalGold.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} → {after.TotalGold.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}");
        if (!string.Equals(Collapse(before.Description), Collapse(after.Description), StringComparison.Ordinal)) changes.Add("stats or effects changed");
        if (!before.From.Order(StringComparer.Ordinal).SequenceEqual(after.From.Order(StringComparer.Ordinal))) changes.Add("recipe changed");
        if (changes.Count == 0) return null;
        var text = string.Join("; ", changes);
        return char.ToUpperInvariant(text[0]) + text[1..];

        static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Well-known community nicknames that are not simple initials.</summary>
    private static readonly Dictionary<string, string[]> Nicknames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Blade of The Ruined King"] = ["bork", "botrk"],
        ["Rabadon's Deathcap"] = ["dcap", "deathcap"],
        ["Zhonya's Hourglass"] = ["hg", "zhonyas"],
        ["Lord Dominik's Regards"] = ["ldr"],
        ["Mercurial Scimitar"] = ["qss"],
        ["Quicksilver Sash"] = ["qss"],
        ["The Collector"] = ["collector"],
        ["Banshee's Veil"] = ["banshee"],
        ["Rapid Firecannon"] = ["rfc"],
        ["Runaan's Hurricane"] = ["runaans"],
        ["Morellonomicon"] = ["morello"],
        ["Mejai's Soulstealer"] = ["mejais"],
        ["Liandry's Torment"] = ["liandrys"],
        ["Wit's End"] = ["wits"],
    };

    /// <summary>Initials ("ie", "bt", "ga", "tf") and community nicknames ("bork", "dcap") so search feels familiar.</summary>
    private static IEnumerable<string> Aliases(ItemInfo item)
    {
        var all = item.Name.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        var significant = all.Where(w => !string.Equals(w, "of", StringComparison.OrdinalIgnoreCase) && !string.Equals(w, "the", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var words in new[] { significant, all.ToList() })
        {
            var initials = new string(words.Select(w => char.ToLowerInvariant(w[0])).ToArray());
            if (initials.Length > 1) yield return initials;
        }
        if (Nicknames.TryGetValue(item.Name, out var nicknames))
            foreach (var n in nicknames) yield return n;
    }
}
