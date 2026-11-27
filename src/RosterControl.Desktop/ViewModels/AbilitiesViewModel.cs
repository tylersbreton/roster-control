using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// One ability with its numbers: name, description, cooldown/cost/range (Data Dragon) and damage etc. per rank (game
/// files, loaded separately). Used by the Abilities tab, skill order tooltips and the quick-look window. On the Abilities
/// tab the calculator adds each number's value for a level, ranks and items.
/// </summary>
public sealed partial class AbilityDetailViewModel : ObservableObject
{
    public AbilityDetailViewModel(AbilityInfo info)
    {
        Info = info;
        Accent = Palette.Hex(info.Slot switch { 'Q' => "#6FA8F5", 'W' => "#6FCB74", 'E' => "#E5C35C", 'R' => "#EE5A6F", _ => "#C8AA6E" });
        _stats = BaseStats();
    }

    public AbilityInfo Info { get; }
    public char Slot => Info.Slot;
    public string Key => Slot == 'P' ? "P" : Slot.ToString();
    public string KeyLabel => Slot == 'P' ? "PASSIVE" : Slot.ToString();
    public string Name => Info.Name;
    public string Description => Info.Description;
    public string IconUrl => Info.ImageUrl;
    public IBrush Accent { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    private IReadOnlyList<AbilityStat> _stats;

    public bool HasStats => Stats.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValues))]
    private IReadOnlyList<AbilityValue> _values = [];

    public bool HasValues => Values.Count > 0;

    /// <summary>The numbers as shown: calculated value (when the calculator is on) next to the per-rank text.</summary>
    [ObservableProperty]
    private IReadOnlyList<AbilityValueRow> _rows = [];

    /// <summary>"Rank 3 of 5 at level 9", "Not learned yet at level 2"; empty without the calculator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRankText))]
    private string _rankText = "";

    public bool HasRankText => RankText.Length > 0;

    [ObservableProperty]
    private bool _isNotLearned;

    partial void OnValuesChanged(IReadOnlyList<AbilityValue> value) => Rows = value.Select(v => new AbilityValueRow(v.Label, "", v.Text)).ToList();

    private List<AbilityStat> BaseStats() => new[]
    {
        new AbilityStat("Cooldown", Info.CooldownText),
        new AbilityStat("Cost", Info.CostText),
        new AbilityStat("Range", Info.RangeText),
    }.Where(s => s.Value.Length > 0).ToList();

    /// <summary>Shows the numbers for <paramref name="rank"/> at <paramref name="level"/> with <paramref name="stats"/>; null resets to per-rank text only.</summary>
    internal void Calculate(ChampionStatBlock? stats, int rank, int level)
    {
        if (stats is null)
        {
            RankText = "";
            IsNotLearned = false;
            Stats = BaseStats();
            Rows = Values.Select(v => new AbilityValueRow(v.Label, "", v.Text)).ToList();
            return;
        }
        var passive = Slot == 'P';
        IsNotLearned = !passive && rank <= 0;
        var maxRank = Math.Max(1, Info.MaxRank);
        RankText = passive ? $"Level {level}" : rank <= 0 ? $"Not learned yet at level {level}" : $"Rank {rank} of {maxRank} at level {level}";
        var shownRank = Math.Max(1, rank);
        Rows = Values.Select(v => new AbilityValueRow(v.Label,
            v.Formula is { } f && !IsNotLearned ? ChampionMath.Format(f, ChampionMath.Evaluate(f, shownRank, level, stats)) : "",
            v.Text)).ToList();

        var list = new List<AbilityStat>();
        var cooldowns = ChampionMath.ParseSeries(Info.CooldownText);
        if (cooldowns.Count > 0 && !passive)
        {
            var cd = cooldowns[Math.Min(shownRank, cooldowns.Count) - 1] * stats.CooldownMultiplier;
            var haste = stats.AbilityHaste > 0 ? $" with {ChampionMath.Number(stats.AbilityHaste, 0)} haste" : "";
            var series = cooldowns.Distinct().Count() > 1 || stats.AbilityHaste > 0 ? $"  ({Info.CooldownText})" : "";
            list.Add(new AbilityStat("Cooldown", IsNotLearned ? Info.CooldownText : $"{ChampionMath.Number(cd, cd >= 10 ? 1 : 2)}s{haste}{series}"));
        }
        else if (Info.CooldownText.Length > 0) list.Add(new AbilityStat("Cooldown", Info.CooldownText));
        if (Info.CostText.Length > 0) list.Add(new AbilityStat("Cost", Info.CostText));
        if (Info.RangeText.Length > 0) list.Add(new AbilityStat("Range", Info.RangeText));
        Stats = list;
    }
}

public sealed record AbilityStat(string Label, string Value);

/// <param name="Result">The calculated number ("412"); empty when not calculated.</param>
public sealed record AbilityValueRow(string Label, string Result, string Text)
{
    public bool HasResult => Result.Length > 0;
}

/// <summary>The champion page's Abilities tab: passive and Q/W/E/R with their numbers, plus the calculator.</summary>
public sealed partial class AbilitiesViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly string _championId;
    private Task? _loading;

    public AbilitiesViewModel(IShell shell, string championId, Func<IReadOnlyList<BuildViewModel>>? builds = null, Func<BuildViewModel?>? selectedBuild = null)
    {
        _shell = shell;
        _championId = championId;
        if (shell.Game.Champion(championId) is { Stats: not null } champion && builds is not null)
            Calculator = new AbilityCalculatorViewModel(shell, champion, this, builds, selectedBuild ?? (() => null));
    }

    /// <summary>Stats and numbers for a level and build; null when the champion's base stats are unknown.</summary>
    public AbilityCalculatorViewModel? Calculator { get; }
    public bool HasCalculator => Calculator is not null;

    [ObservableProperty]
    private IReadOnlyList<AbilityDetailViewModel> _abilities = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private bool _isUnavailable;

    internal RuneNumbers? RuneNumbers { get; private set; }

    /// <summary>AD per level from the champion's game file, used when Data Dragon's is 0 (it is for every champion since 16.22).</summary>
    internal double? AttackDamageGrowth { get; private set; }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    /// <summary>The tab is shown again: builds may have been edited meanwhile.</summary>
    public void Refresh() => Calculator?.RefreshBuilds();

    private async Task LoadAsync()
    {
        try
        {
            var abilities = await _shell.GameData.GetAbilitiesAsync(_championId, CancellationToken.None);
            if (abilities is null)
            {
                IsUnavailable = true;
                Note = "Abilities couldn't be downloaded. Check your connection and open the champion again.";
                return;
            }
            Abilities = abilities.Abilities.Select(a => new AbilityDetailViewModel(a)).ToList();
            IsLoading = false;
            Calculator?.RefreshBuilds();
            var numbers = await _shell.GameData.GetAbilityNumbersAsync(_championId, CancellationToken.None);
            AttackDamageGrowth = numbers?.AttackDamagePerLevel;
            Apply(Abilities, numbers);
            Note = numbers is null
                ? "Damage numbers couldn't be downloaded from the game files; cooldowns, costs and ranges are from Data Dragon."
                : $"Numbers from the game files (patch {numbers.Patch}, via Community Dragon). Ratios like +50% AP add that share of the stat; \"by level\" values grow from champion level 1 to 18.";
            Calculator?.Recalculate();
            if (Calculator is not null)
            {
                RuneNumbers = await _shell.GameData.GetRuneNumbersAsync(CancellationToken.None);
                Calculator.Recalculate();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal static void Apply(IEnumerable<AbilityDetailViewModel> abilities, AbilityNumbers? numbers)
    {
        if (numbers is null) return;
        foreach (var a in abilities) a.Values = numbers[a.Slot];
    }
}

/// <summary>A build the calculator can use ("No build" = base stats, every ability at max rank).</summary>
public sealed record CalculatorBuildChoice(int? BuildId, string Name, BuildViewModel? Build)
{
    public override string ToString() => Name;
}

/// <summary>An item of the build that can be counted in the calculator.</summary>
public sealed partial class CalculatorItem(string itemId, string name, string iconUrl, int gold, Action changed) : ObservableObject
{
    public string ItemId { get; } = itemId;
    public string Name { get; } = name;
    public string IconUrl { get; } = iconUrl;
    public string Tip => $"{Name} ({gold:N0} gold) — click to count it or not";

    [ObservableProperty]
    private bool _isOn;

    /// <summary>This item is the one being swapped in the calculator's item choices.</summary>
    [ObservableProperty]
    private bool _isSwapSource;

    partial void OnIsOnChanged(bool value) => changed();
}

/// <param name="Detail">e.g. "1,250 base + 600 bonus".</param>
public sealed record CalculatorStat(string Label, string Value, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>A rune of the build with its numbers at the chosen level (or why there are none).</summary>
public sealed record CalculatorRune(string Name, string IconUrl, bool IsKeystone, IReadOnlyList<AbilityValueRow> Rows, string Note)
{
    public bool HasRows => Rows.Count > 0;
    public bool HasNote => Note.Length > 0;
    public string KindText => IsKeystone ? "KEYSTONE" : "RUNE";
}

/// <summary>
/// The Abilities tab's calculator: champion stats at a level with the items you tick and the build's stat shards, ability
/// ranks from the build's skill order, and each number's value; plus the build's keystone and runes that have formulas.
/// </summary>
public sealed partial class AbilityCalculatorViewModel : ObservableObject
{
    internal const string PrefsKey = "abilities.calc";

    private readonly IShell _shell;
    private readonly ChampionInfo _champion;
    private readonly AbilitiesViewModel _owner;
    private readonly Func<IReadOnlyList<BuildViewModel>> _builds;
    private readonly Func<BuildViewModel?> _selectedBuild;
    private readonly CalculatorPrefs _prefs;
    private bool _suspend;

    internal AbilityCalculatorViewModel(IShell shell, ChampionInfo champion, AbilitiesViewModel owner, Func<IReadOnlyList<BuildViewModel>> builds, Func<BuildViewModel?> selectedBuild)
    {
        _shell = shell;
        _champion = champion;
        _owner = owner;
        _builds = builds;
        _selectedBuild = selectedBuild;
        _prefs = shell.Preferences.Get(PrefsKey, new CalculatorPrefs());
        _level = Math.Clamp(_prefs.Level, 1, ChampionMath.MaxLevel);
        _includeShards = _prefs.Shards;
    }

    public sealed class CalculatorPrefs
    {
        public int Level { get; set; } = 11;
        public bool Shards { get; set; } = true;
        /// <summary>Ticked items per build id (absent = the core block).</summary>
        public Dictionary<int, List<string>> Items { get; set; } = [];
        /// <summary>Level each item is finished, per build id and item id (absent = <see cref="ItemTimeline.DefaultLevels"/>).</summary>
        public Dictionary<int, Dictionary<string, int>> Timeline { get; set; } = [];
        /// <summary>The number best-next and swap rank items by (a <see cref="CalcMetric.Key"/>).</summary>
        public string? Focus { get; set; }
        /// <summary>The number the power curve shows.</summary>
        public string? Curve { get; set; }
    }

    /// <summary>The items of a build the calculator can count: every block's items except consumables and trinkets, once each.</summary>
    internal static IEnumerable<ItemInfo> CountableItems(Build build, GameDataSnapshot game) =>
        build.Items.Blocks.SelectMany(b => b.Items).Distinct().Select(game.Item).OfType<ItemInfo>()
            .Where(i => i.Tier is not (ItemTier.Consumable or ItemTier.Trinket));

    /// <summary>The items counted for a build: the ones ticked in the calculator, or the core block when never changed.</summary>
    internal static List<ItemInfo> CountedItems(Build build, GameDataSnapshot game, CalculatorPrefs prefs)
    {
        var saved = prefs.Items.TryGetValue(build.Id, out var list) ? list.ToHashSet() : null;
        var core = BuildSelection.CoreBlock(build)?.Items.ToHashSet() ?? [];
        return CountableItems(build, game).Where(i => saved?.Contains(i.Id) ?? core.Contains(i.Id)).ToList();
    }

    /// <summary>When each counted item is finished: saved levels, otherwise the default spread.</summary>
    internal static List<(ItemInfo Item, int Level)> TimelineFor(Build? build, IReadOnlyList<ItemInfo> items, CalculatorPrefs prefs)
    {
        var defaults = ItemTimeline.DefaultLevels(items);
        var saved = build is not null && prefs.Timeline.TryGetValue(build.Id, out var t) ? t : null;
        return items.Select((item, i) => (item, saved is not null && saved.TryGetValue(item.Id, out var level) ? Math.Clamp(level, 1, ChampionMath.MaxLevel) : defaults[i])).ToList();
    }

    [ObservableProperty]
    private IReadOnlyList<CalculatorBuildChoice> _buildChoices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuild))]
    private CalculatorBuildChoice? _selectedChoice;

    public bool HasBuild => SelectedChoice?.Build is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    private int _level;

    public string LevelText => $"Level {Level}";

    [ObservableProperty]
    private bool _includeShards;

    public ObservableCollection<CalculatorItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private int _itemCount;

    public bool HasItems => ItemCount > 0;

    [ObservableProperty]
    private string _itemsText = "";

    [ObservableProperty]
    private string _shardsText = "";

    [ObservableProperty]
    private bool _hasShards;

    [ObservableProperty]
    private string _ranksText = "";

    [ObservableProperty]
    private IReadOnlyList<CalculatorStat> _stats = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunes))]
    private IReadOnlyList<CalculatorRune> _runes = [];

    public bool HasRunes => Runes.Count > 0;

    [ObservableProperty]
    private string _footnote = "";

    partial void OnLevelChanged(int value)
    {
        _prefs.Level = value;
        SavePrefs();
        Recalculate();
    }

    partial void OnIncludeShardsChanged(bool value)
    {
        _prefs.Shards = value;
        SavePrefs();
        Recalculate();
    }

    partial void OnSelectedChoiceChanged(CalculatorBuildChoice? value)
    {
        if (_suspend) return;
        LoadItems();
        Recalculate();
    }

    [RelayCommand]
    private void LevelDown() => Level = Math.Max(1, Level - 1);

    [RelayCommand]
    private void LevelUp() => Level = Math.Min(ChampionMath.MaxLevel, Level + 1);

    [RelayCommand]
    private void SetLevel(string level)
    {
        if (int.TryParse(level, out var l)) Level = Math.Clamp(l, 1, ChampionMath.MaxLevel);
    }

    [RelayCommand]
    private void AllItems() => SetAll(true);

    [RelayCommand]
    private void NoItems() => SetAll(false);

    private void SetAll(bool on)
    {
        _suspend = true;
        foreach (var i in Items) i.IsOn = on;
        _suspend = false;
        ItemsChanged();
    }

    /// <summary>Re-reads the champion's builds (they may have been added or edited), keeping the chosen one.</summary>
    public void RefreshBuilds()
    {
        var builds = _builds();
        var keep = SelectedChoice?.BuildId;
        var choices = builds.Select(b => new CalculatorBuildChoice(b.Model.Id, string.IsNullOrWhiteSpace(b.Name) ? "Build" : b.Name, b)).ToList();
        choices.Add(new CalculatorBuildChoice(null, "No build (base stats)", null));
        _suspend = true;
        BuildChoices = choices;
        SelectedChoice = (keep is { } id ? choices.FirstOrDefault(c => c.BuildId == id) : null)
                         ?? (_selectedBuild() is { } sel ? choices.FirstOrDefault(c => c.BuildId == sel.Model.Id) : null)
                         ?? choices[0];
        _suspend = false;
        LoadItems();
        Recalculate();
    }

    private void LoadItems()
    {
        _suspend = true;
        Items.Clear();
        if (SelectedChoice?.Build?.Model is { } build)
        {
            var game = _shell.Game;
            var saved = _prefs.Items.TryGetValue(build.Id, out var list) ? list.ToHashSet() : null;
            var core = BuildSelection.CoreBlock(build)?.Items.ToHashSet() ?? [];
            foreach (var item in CountableItems(build, game))
            {
                Items.Add(new CalculatorItem(item.Id, item.Name, AssetUrls.Item(game.Version, item.Id), item.TotalGold, ItemsChanged)
                {
                    IsOn = saved?.Contains(item.Id) ?? core.Contains(item.Id),
                });
            }
        }
        ItemCount = Items.Count;
        SwapFrom = null;
        _suspend = false;
    }

    private void ItemsChanged()
    {
        if (_suspend) return;
        if (SelectedChoice?.Build?.Model is { } build)
        {
            _prefs.Items[build.Id] = Items.Where(i => i.IsOn).Select(i => i.ItemId).ToList();
            SavePrefs();
        }
        Recalculate();
    }

    private void SavePrefs() => _shell.Preferences.Set(PrefsKey, _prefs);

    /// <summary>Recomputes stats and every ability's numbers.</summary>
    public void Recalculate()
    {
        if (_champion.Stats is not { } baseStats) return;
        // The champion's game file is the most accurate source (Data Dragon reports 0 for everyone since 16.22).
        if (_owner.AttackDamageGrowth is { } growth && Math.Abs(growth - baseStats.AttackDamagePerLevel) > 1e-9) baseStats = baseStats with { AttackDamagePerLevel = growth, AttackDamageGrowthUnknown = false };
        var game = _shell.Game;
        var build = SelectedChoice?.Build?.Model;
        var ticked = Items.Where(i => i.IsOn).ToList();
        var bonuses = new List<StatTotal>();
        foreach (var i in ticked)
            if (game.Item(i.ItemId) is { } item) bonuses.AddRange(ItemStats.Parse(item.Description));

        var shards = build?.Runes.Shards.Where(id => id != 0).Select(game.Shard).ToList() ?? [];
        HasShards = shards.Any(s => s is not null);
        var shardStats = ChampionMath.ShardStats(shards, Level);
        ShardsText = HasShards
            ? "Stat shards: " + string.Join(", ", shardStats.Select(s => s.Text))
            : build is null ? "" : "This build has no stat shards chosen.";
        if (IncludeShards) bonuses.AddRange(shardStats);

        var stats = ChampionMath.Compute(baseStats, Level, bonuses);
        var gold = ticked.Sum(i => game.Item(i.ItemId)?.TotalGold ?? 0);
        ItemsText = ticked.Count == 0 ? "No items counted" : $"{ticked.Count} item{(ticked.Count == 1 ? "" : "s")} counted · {gold:N0} gold";

        // Ability ranks at this level from the build's skill order.
        var maxRanks = _owner.Abilities.Where(a => a.Slot != 'P').ToDictionary(a => a.Slot, a => Math.Max(1, a.Info.MaxRank));
        if (maxRanks.Count == 0) maxRanks = new Dictionary<char, int> { ['Q'] = 5, ['W'] = 5, ['E'] = 5, ['R'] = 3 };
        var order = build?.SkillOrder;
        var ranks = ChampionMath.RanksAt(order, Level, maxRanks);
        RanksText = SkillOrderRules.IsEmpty(order)
            ? build is null ? "Every ability at max rank (no build chosen)." : "This build has no skill order, so every ability is at max rank."
            : $"Ranks at level {Level} from the skill order: " + string.Join(" · ", ranks.Select(r => $"{r.Key} {r.Value}"));
        foreach (var a in _owner.Abilities)
            a.Calculate(stats, a.Slot == 'P' ? 1 : ranks.GetValueOrDefault(a.Slot), Level);

        Stats = StatRows(stats);
        Runes = RuneRows(build, stats);
        UpdatePlanning(baseStats, stats, ranks, maxRanks, shards, ticked.Select(i => game.Item(i.ItemId)).OfType<ItemInfo>().ToList());
        var notCounted = stats.NotCounted.Count > 0 ? $" Not counted: {string.Join(", ", stats.NotCounted)}." : "";
        Footnote = "Counts the stats listed on the ticked items and the stat shards. Item passives and buffs aren't included, and damage is before the target's armor or magic resist." + notCounted;
    }

    private static IReadOnlyList<CalculatorStat> StatRows(ChampionStatBlock s)
    {
        string N(double v, int digits = 0) => ChampionMath.Number(v, digits);
        string Split(double baseValue, double bonus) => bonus > 0.05 ? $"{N(baseValue)} base + {N(bonus)}" : "";
        var rows = new List<CalculatorStat>
        {
            new("Health", N(s.Health), Split(s.BaseHealth, s.BonusHealth)),
        };
        if (s.Resource.Length > 0 && s.Mana > 0) rows.Add(new(s.Resource, N(s.Mana), Split(s.BaseMana, s.BonusMana)));
        rows.Add(new("Attack damage", N(s.AttackDamage), Split(s.BaseAttackDamage, s.BonusAttackDamage)));
        rows.Add(new("Ability power", N(s.AbilityPower), ""));
        rows.Add(new("Armor", N(s.Armor), Split(s.BaseArmor, s.BonusArmor)));
        rows.Add(new("Magic resist", N(s.MagicResist), Split(s.BaseMagicResist, s.BonusMagicResist)));
        rows.Add(new("Attack speed", N(s.AttackSpeed, 3), s.BonusAttackSpeed > 0.0005 ? $"+{N(s.BonusAttackSpeed * 100, 1)}% bonus" : ""));
        rows.Add(new("Ability haste", N(s.AbilityHaste), s.AbilityHaste > 0 ? $"cooldowns −{N((1 - s.CooldownMultiplier) * 100, 1)}%" : ""));
        if (s.CritChance > 0) rows.Add(new("Crit chance", N(s.CritChance * 100) + "%", s.BonusCritDamage > 0 ? $"crits deal {N(s.CritDamage * 100)}%" : ""));
        rows.Add(new("Move speed", N(s.MoveSpeed), ""));
        if (s.Lethality > 0) rows.Add(new("Lethality", N(s.Lethality), ""));
        if (s.ArmorPenetration > 0) rows.Add(new("Armor pen", N(s.ArmorPenetration * 100) + "%", ""));
        if (s.MagicPenetration > 0 || s.PercentMagicPenetration > 0)
            rows.Add(new("Magic pen", string.Join(" + ", new[] { s.MagicPenetration > 0 ? N(s.MagicPenetration) : "", s.PercentMagicPenetration > 0 ? N(s.PercentMagicPenetration * 100) + "%" : "" }.Where(x => x.Length > 0)), ""));
        if (s.LifeSteal > 0) rows.Add(new("Life steal", N(s.LifeSteal * 100) + "%", ""));
        if (s.Omnivamp > 0) rows.Add(new("Omnivamp", N(s.Omnivamp * 100) + "%", ""));
        if (s.HealShieldPower > 0) rows.Add(new("Heal & shield power", N(s.HealShieldPower * 100) + "%", ""));
        rows.Add(new("Range", N(s.AttackRange), s.IsRanged ? "ranged" : "melee"));
        return rows;
    }

    private IReadOnlyList<CalculatorRune> RuneRows(Build? build, ChampionStatBlock stats)
    {
        if (build is null) return [];
        var game = _shell.Game;
        var numbers = _owner.RuneNumbers;
        var list = new List<CalculatorRune>();
        foreach (var id in build.Runes.PrimaryPerks.Concat(build.Runes.SubPerks).Where(id => id != 0).Distinct())
        {
            if (game.Rune(id) is not { } rune) continue;
            var values = numbers?[id] ?? [];
            var rows = values.Select(v => new AbilityValueRow(v.Label,
                v.Formula is { } f ? ChampionMath.Format(f, ChampionMath.Evaluate(f, 1, Level, stats)) : "", v.Text)).ToList();
            if (rows.Count == 0 && !rune.IsKeystone) continue;
            var note = rows.Count > 0 ? ""
                : numbers is null ? "Rune numbers couldn't be downloaded from the game files, so this keystone isn't calculated."
                : $"Not calculated: the game files have no formula for {rune.Name}. Its description: {RiotTextPlain(rune.LongDesc)}";
            list.Add(new CalculatorRune(rune.Name, AssetUrls.Rune(rune.IconPath), rune.IsKeystone, rows, note));
        }
        return list;
    }

    private static string RiotTextPlain(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html.Replace("<br>", " "), "<[^>]+>", "").Replace("  ", " ").Trim();
}
