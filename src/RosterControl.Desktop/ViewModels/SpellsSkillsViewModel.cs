using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Summoner spells (D/F like the client) and an 18-level skill order grid.</summary>
public sealed partial class SpellsSkillsViewModel : ObservableObject
{
    private readonly GameDataSnapshot _game;
    private readonly Build _build;
    private readonly Action _changed;
    private int _basicMaxRank = 5;
    private int _ultimateMaxRank = 3;

    public SpellsSkillsViewModel(GameDataSnapshot game, Build build, Action changed)
    {
        _game = game;
        _build = build;
        _changed = changed;
        while (build.SummonerSpells.Count < 2) build.SummonerSpells.Add("");
        Slots = [new SpellSlotViewModel(0, "D"), new SpellSlotViewModel(1, "F")];
        Options = game.SummonerSpells.Select(s => new SpellOptionViewModel(s, game.Version)).ToList();
        Levels = Enumerable.Range(1, SkillOrderRules.MaxLevel).ToList();
        SkillRows = SkillOrderRules.Slots.Select(s => new SkillRowViewModel(this, s)).ToList();
        FillOptions = ["Q › W › E", "Q › E › W", "W › Q › E", "W › E › Q", "E › Q › W", "E › W › Q"];
        ActiveSlot = Slots[0];
        RefreshSpells();
        RefreshSkills();
    }

    public IReadOnlyList<SpellSlotViewModel> Slots { get; }
    public IReadOnlyList<SpellOptionViewModel> Options { get; }
    public IReadOnlyList<int> Levels { get; }
    public IReadOnlyList<SkillRowViewModel> SkillRows { get; }
    public IReadOnlyList<string> FillOptions { get; }

    [ObservableProperty]
    private SpellSlotViewModel _activeSlot;

    [ObservableProperty]
    private AbilityInfo? _passive;

    [ObservableProperty]
    private string _maxOrderText = "";

    [ObservableProperty]
    private bool _hasInvalid;

    [ObservableProperty]
    private string _summaryText = "";

    public string? PassiveIconUrl => Passive?.ImageUrl;
    public bool HasPassive => Passive is not null;

    partial void OnActiveSlotChanged(SpellSlotViewModel? oldValue, SpellSlotViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        newValue.IsActive = true;
    }

    partial void OnPassiveChanged(AbilityInfo? value)
    {
        OnPropertyChanged(nameof(PassiveIconUrl));
        OnPropertyChanged(nameof(HasPassive));
    }

    /// <summary>Loads the champion's ability names and icons (downloaded on demand).</summary>
    public void SetAbilities(ChampionAbilities? abilities)
    {
        if (abilities is null) return;
        Passive = abilities['P'];
        foreach (var row in SkillRows) row.Ability = abilities[row.Slot];
        _basicMaxRank = abilities['Q']?.MaxRank is > 0 and var q ? q : 5;
        _ultimateMaxRank = abilities['R']?.MaxRank is > 0 and var r ? r : 3;
        RefreshSkills();
    }

    [RelayCommand]
    private void SelectSlot(SpellSlotViewModel slot) => ActiveSlot = slot;

    [RelayCommand]
    private void ChooseSpell(SpellOptionViewModel option)
    {
        var spells = _build.SummonerSpells;
        var active = ActiveSlot.Index;
        var other = 1 - active;
        if (spells[other] == option.Spell.Id)
        {
            // Already in the other slot: swap, like the client.
            spells[other] = spells[active];
        }
        spells[active] = option.Spell.Id;
        ActiveSlot = Slots[other];
        RefreshSpells();
        _changed();
    }

    [RelayCommand]
    private void SwapSpells()
    {
        (_build.SummonerSpells[0], _build.SummonerSpells[1]) = (_build.SummonerSpells[1], _build.SummonerSpells[0]);
        RefreshSpells();
        _changed();
    }

    internal void ToggleSkill(char slot, int level)
    {
        _build.SkillOrder = SkillOrderRules.Set(_build.SkillOrder, level, slot);
        RefreshSkills();
        _changed();
    }

    [RelayCommand]
    private void ClearSkills()
    {
        _build.SkillOrder = "";
        RefreshSkills();
        _changed();
    }

    /// <summary>Fills a standard order: one point in each basic at levels 1–3, R at 6/11/16, then max by priority.</summary>
    [RelayCommand]
    private void FillSkills(string order)
    {
        var priority = order.Where(c => c is 'Q' or 'W' or 'E').ToList();
        if (priority.Count != 3) return;
        var chars = new char[SkillOrderRules.MaxLevel];
        var ranks = new Dictionary<char, int> { ['Q'] = 0, ['W'] = 0, ['E'] = 0, ['R'] = 0 };
        for (var level = 1; level <= SkillOrderRules.MaxLevel; level++)
        {
            var pick = level switch
            {
                6 or 11 or 16 => 'R',
                <= 3 => priority[level - 1],
                _ => priority.FirstOrDefault(c => ranks[c] < _basicMaxRank && ranks[c] < (level + 1) / 2, priority[0]),
            };
            ranks[pick]++;
            chars[level - 1] = pick;
        }
        _build.SkillOrder = new string(chars);
        RefreshSkills();
        _changed();
    }

    private void RefreshSpells()
    {
        for (var i = 0; i < 2; i++)
        {
            var info = _game.Spell(_build.SummonerSpells[i]);
            Slots[i].Set(info, info is null ? null : AssetUrls.Spell(_game.Version, info.ImageFile));
        }
        foreach (var o in Options) o.IsSelected = _build.SummonerSpells.Contains(o.Spell.Id);
    }

    private void RefreshSkills()
    {
        var invalid = SkillOrderRules.InvalidLevels(_build.SkillOrder, _basicMaxRank, _ultimateMaxRank);
        foreach (var row in SkillRows)
        {
            var max = row.Slot == 'R' ? _ultimateMaxRank : _basicMaxRank;
            row.Update(_build.SkillOrder, invalid, max);
        }
        HasInvalid = invalid.Count > 0;
        var maxOrder = SkillOrderRules.MaxOrder(_build.SkillOrder, _basicMaxRank);
        MaxOrderText = maxOrder.Count == 0 ? "" : string.Join("  ›  ", maxOrder);
        var assigned = (_build.SkillOrder ?? "").Count(c => c != '-');
        SummaryText = assigned == 0 ? "No skill order yet" : $"{assigned} / 18 levels";
    }
}

public sealed partial class SpellSlotViewModel(int index, string hotkey) : ObservableObject
{
    public int Index { get; } = index;
    public string Hotkey { get; } = hotkey;

    [ObservableProperty]
    private string? _iconUrl;

    [ObservableProperty]
    private string _name = "Empty";

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _hasSpell;

    internal void Set(SummonerSpellInfo? info, string? url)
    {
        IconUrl = url;
        HasSpell = info is not null;
        Name = info?.Name ?? "Empty";
        Description = info?.Description ?? "Choose a summoner spell below.";
    }
}

public sealed partial class SpellOptionViewModel(SummonerSpellInfo spell, string version) : ObservableObject
{
    public SummonerSpellInfo Spell { get; } = spell;
    public string IconUrl { get; } = AssetUrls.Spell(version, spell.ImageFile);
    public string Name => Spell.Name;
    public string Description => Spell.Description;
    public string CooldownText => Spell.Cooldown > 0 ? $"{Spell.Cooldown.ToString("0", CultureInfo.InvariantCulture)}s cooldown" : "";
    public string ModeText => Spell.OnSummonersRift && Spell.OnAram ? "Summoner's Rift · ARAM" : Spell.OnSummonersRift ? "Summoner's Rift" : "ARAM";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class SkillRowViewModel : ObservableObject
{
    private readonly SpellsSkillsViewModel _owner;

    public SkillRowViewModel(SpellsSkillsViewModel owner, char slot)
    {
        _owner = owner;
        Slot = slot;
        Accent = Palette.Hex(slot switch { 'Q' => "#6FA8F5", 'W' => "#6FCB74", 'E' => "#E5C35C", _ => "#EE5A6F" });
        Cells = Enumerable.Range(1, SkillOrderRules.MaxLevel).Select(l => new SkillCellViewModel(this, l)).ToList();
    }

    public char Slot { get; }
    public string Key => Slot.ToString();
    public IBrush Accent { get; }
    public IReadOnlyList<SkillCellViewModel> Cells { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconUrl), nameof(AbilityName), nameof(AbilityDescription), nameof(HasIcon))]
    private AbilityInfo? _ability;

    [ObservableProperty]
    private string _rankText = "";

    public string? IconUrl => Ability?.ImageUrl;
    public bool HasIcon => Ability is not null;
    public string AbilityName => Ability?.Name ?? Key;
    public string AbilityDescription => Ability?.Description ?? "";

    internal void Toggle(int level) => _owner.ToggleSkill(Slot, level);

    internal void Update(string? order, IReadOnlySet<int> invalid, int maxRank)
    {
        foreach (var cell in Cells)
        {
            cell.IsSet = SkillOrderRules.At(order, cell.Level) == Slot;
            cell.IsInvalid = cell.IsSet && invalid.Contains(cell.Level);
        }
        RankText = $"{SkillOrderRules.RankAt(order, Slot, SkillOrderRules.MaxLevel)}/{maxRank}";
    }
}

public sealed partial class SkillCellViewModel(SkillRowViewModel row, int level) : ObservableObject
{
    public SkillRowViewModel Row { get; } = row;
    public int Level { get; } = level;
    public string LevelText => Level.ToString(CultureInfo.InvariantCulture);
    public IBrush Accent => Row.Accent;

    [ObservableProperty]
    private bool _isSet;

    [ObservableProperty]
    private bool _isInvalid;

    public string Tooltip => IsInvalid ? $"Level {Level}: {Row.Key} — unusual for this level (check rank limits)" : $"Level {Level}: {Row.Key}";

    [RelayCommand]
    private void Toggle() => Row.Toggle(Level);
}

/// <summary>A build in the champion page's build list, with lazily created editors.</summary>
public sealed partial class BuildViewModel : ObservableObject
{
    private readonly IShell _shell;
    private RunePageViewModel? _runes;
    private ItemSetViewModel? _items;
    private SpellsSkillsViewModel? _spells;
    private bool _loading = true;

    public BuildViewModel(IShell shell, Build model)
    {
        _shell = shell;
        Model = model;
        _name = model.Name;
        _role = model.Role;
        _notes = model.Notes;
        SavedOnPatch = model.GameVersion is { } v && v != shell.Game.Version ? v : null;
        _loading = false;
        RefreshSummary();
    }

    public Build Model { get; }
    public GameDataSnapshot Game => _shell.Game;
    public IReadOnlyList<RoleChoice> RoleChoices { get; } =
        new[] { new RoleChoice(null, "Any role") }.Concat(PositionInfo.All.Select(p => new RoleChoice(p, p.DisplayName()))).ToList();

    public RunePageViewModel Runes => _runes ??= new RunePageViewModel(Game, Model.Runes, () => { RefreshSummary(); Changed(); });
    public ItemSetViewModel Items
    {
        get
        {
            if (_items is not null) return _items;
            _items = new ItemSetViewModel(Game, Model.Items, Model.GameVersion, () => { RefreshSummary(); Changed(); });
            var items = _items;
            if (SavedOnPatch is { } patch) _ = LoadPatchBaselineAsync(items, patch);
            return items;
        }
    }

    /// <summary>The patch the build was last saved on, when that isn't the current patch (captured before any edit).</summary>
    public string? SavedOnPatch { get; private set; }
    public bool IsFromOlderPatch => SavedOnPatch is not null;
    public string SavedOnPatchText => SavedOnPatch is null ? "" : $"Saved on patch {SavedOnPatch}";

    private async Task LoadPatchBaselineAsync(ItemSetViewModel items, string patch)
    {
        var baseline = await _shell.GameData.GetItemsForVersionAsync(patch, CancellationToken.None);
        if (baseline is not null) items.ApplyBaseline(patch, baseline);
    }
    public SpellsSkillsViewModel SpellsSkills
    {
        get
        {
            if (_spells is not null) return _spells;
            _spells = new SpellsSkillsViewModel(Game, Model, () => { RefreshSummary(); Changed(); });
            var spells = _spells;
            _ = LoadAbilitiesAsync(spells);
            return spells;
        }
    }

    private async Task LoadAbilitiesAsync(SpellsSkillsViewModel spells)
    {
        var abilities = await _shell.GameData.GetAbilitiesAsync(Model.ChampionId, CancellationToken.None);
        spells.SetAbilities(abilities);
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private Position? _role;

    [ObservableProperty]
    private string _notes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunesTab), nameof(IsItemsTab), nameof(IsSpellsTab), nameof(IsNotesTab))]
    private int _editorTab;

    public bool IsRunesTab { get => EditorTab == 0; set { if (value) EditorTab = 0; } }
    public bool IsItemsTab { get => EditorTab == 1; set { if (value) EditorTab = 1; } }
    public bool IsSpellsTab { get => EditorTab == 2; set { if (value) EditorTab = 2; } }
    public bool IsNotesTab { get => EditorTab == 3; set { if (value) EditorTab = 3; } }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string? _keystoneIconUrl;

    [ObservableProperty]
    private string? _subTreeIconUrl;

    [ObservableProperty]
    private string _runeSummary = "";

    [ObservableProperty]
    private IReadOnlyList<string> _summaryItems = [];

    [ObservableProperty]
    private IReadOnlyList<string> _summarySpells = [];

    [ObservableProperty]
    private string _skillSummary = "";

    public RoleChoice SelectedRole
    {
        get => RoleChoices.First(r => r.Role == Role);
        set
        {
            if (value is null || value.Role == Role) return;
            Role = value.Role;
        }
    }

    public string? RoleIconUrl => Role is { } r ? AssetUrls.PositionIcon(r) : null;
    public bool HasRole => Role is not null;

    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        Changed();
    }

    partial void OnRoleChanged(Position? value)
    {
        Model.Role = value;
        OnPropertyChanged(nameof(SelectedRole));
        OnPropertyChanged(nameof(RoleIconUrl));
        OnPropertyChanged(nameof(HasRole));
        Changed();
    }

    partial void OnNotesChanged(string value)
    {
        Model.Notes = value;
        Changed();
    }

    private void Changed()
    {
        if (_loading) return;
        Model.GameVersion = Game.Version;
        _shell.Saves.Schedule(this, SaveAsync);
        _shell.MarkDirty();
    }

    public async Task SaveAsync()
    {
        var id = await _shell.Repository.SaveBuildAsync(Model);
        if (Model.Id == 0 && id != 0) Model.Id = id;
    }

    private void RefreshSummary()
    {
        var page = Model.Runes;
        var keystone = Game.Rune(page.PrimaryPerks.ElementAtOrDefault(0));
        KeystoneIconUrl = keystone is not null ? AssetUrls.Rune(keystone.IconPath) : Game.Tree(page.PrimaryStyleId) is { } pt ? AssetUrls.Rune(pt.IconPath) : null;
        SubTreeIconUrl = Game.Tree(page.SubStyleId) is { } st ? AssetUrls.Rune(st.IconPath) : null;
        RuneSummary = keystone?.Name ?? Game.Tree(page.PrimaryStyleId)?.Name ?? "No runes";
        var core = Model.Items.Blocks.FirstOrDefault(b => b.Name.Contains("core", StringComparison.OrdinalIgnoreCase) && b.Items.Count > 0)
                   ?? Model.Items.Blocks.FirstOrDefault(b => b.Items.Count > 0);
        SummaryItems = (core?.Items ?? []).Take(6).Select(id => AssetUrls.Item(Game.Item(id) is null ? Model.GameVersion ?? Game.Version : Game.Version, id)).ToList();
        SummarySpells = Model.SummonerSpells.Select(id => Game.Spell(id)).Where(s => s is not null).Select(s => AssetUrls.Spell(Game.Version, s!.ImageFile)).ToList();
        var order = SkillOrderRules.MaxOrder(Model.SkillOrder);
        SkillSummary = order.Count == 0 ? "" : string.Join(" › ", order);
    }
}

public sealed record RoleChoice(Position? Role, string Label)
{
    public string? IconUrl => Role is { } r ? AssetUrls.PositionIcon(r) : null;
    public bool HasIcon => Role is not null;
    public override string ToString() => Label;
}
