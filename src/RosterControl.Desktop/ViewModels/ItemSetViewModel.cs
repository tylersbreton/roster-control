using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Item set editor in the style of the League client: named blocks on the left, the item shop on the right.</summary>
public sealed partial class ItemSetViewModel : ObservableObject, IDropTarget
{
    private readonly GameDataSnapshot _game;
    private readonly ItemSetData _data;
    private readonly Action _changed;
    private readonly string _buildVersion;

    public ItemSetViewModel(GameDataSnapshot game, ItemSetData data, string? buildVersion, Action changed)
    {
        _game = game;
        _data = data;
        _changed = changed;
        _buildVersion = buildVersion ?? game.Version;
        if (data.Blocks.Count == 0) data.Blocks.AddRange(ItemSetData.CreateDefault().Blocks);
        foreach (var b in data.Blocks) Blocks.Add(new ItemBlockViewModel(this, b.Name, b.Items));
        SelectedBlock = Blocks.FirstOrDefault(b => b.Items.Count == 0) ?? Blocks.FirstOrDefault();
        Shop = new ItemShopViewModel(game, this);
    }

    public GameDataSnapshot Game => _game;
    public ObservableCollection<ItemBlockViewModel> Blocks { get; } = [];
    public ItemShopViewModel Shop { get; }
    public int ItemCount => Blocks.Sum(b => b.Items.Count);
    public bool HasMissingItems => Blocks.Any(b => b.Items.Any(i => i.IsMissing));

    // ── Patch changes: items are often reworked between patches ─────────────────────────────────────

    private IReadOnlyDictionary<string, ItemInfo>? _baselineItems;
    private string? _baselineVersion;

    /// <summary>
    /// Compares the build's items with the patch the build was last saved on and marks the ones whose cost, stats or
    /// recipe changed since then (and the ones removed from the game).
    /// </summary>
    public void ApplyBaseline(string version, IReadOnlyDictionary<string, ItemInfo> items)
    {
        if (version == _game.Version) return;
        _baselineVersion = version;
        _baselineItems = items;
        foreach (var slot in Blocks.SelectMany(b => b.Items)) slot.ChangeNote = DescribeChange(slot.ItemId);
        NotifyChanges();
    }

    private string? DescribeChange(string itemId)
    {
        if (_baselineItems is null) return null;
        var change = ItemCatalog.DescribeChange(_baselineItems.GetValueOrDefault(itemId), _game.Item(itemId));
        return change is null ? null : $"Changed since patch {_baselineVersion}: {change}";
    }

    public int ChangedCount => Blocks.Sum(b => b.Items.Where(i => i.HasChange).Select(i => i.ItemId).Distinct().Count());
    public int RemovedCount => Blocks.Sum(b => b.Items.Count(i => i.IsMissing));
    public bool HasPatchChanges => ChangedCount > 0 || RemovedCount > 0;

    public string PatchChangeSummary
    {
        get
        {
            var parts = new List<string>();
            if (ChangedCount > 0) parts.Add($"{ChangedCount} item{(ChangedCount == 1 ? "" : "s")} changed");
            if (RemovedCount > 0) parts.Add($"{RemovedCount} removed from the game");
            var since = _baselineVersion is null ? "" : $" since this build was saved on patch {_baselineVersion}";
            return string.Join(", ", parts) + since + ". Hover the marked items for details.";
        }
    }

    private void NotifyChanges()
    {
        OnPropertyChanged(nameof(ChangedCount));
        OnPropertyChanged(nameof(RemovedCount));
        OnPropertyChanged(nameof(HasPatchChanges));
        OnPropertyChanged(nameof(PatchChangeSummary));
    }

    [ObservableProperty]
    private ItemBlockViewModel? _selectedBlock;

    partial void OnSelectedBlockChanged(ItemBlockViewModel? oldValue, ItemBlockViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    internal ItemSlotViewModel CreateSlot(ItemBlockViewModel block, string itemId)
    {
        var item = _game.Item(itemId);
        // Items removed since the build was saved are drawn with the build's own patch icon.
        var url = AssetUrls.Item(item is null ? _buildVersion : _game.Version, itemId);
        return new ItemSlotViewModel(block, itemId, item, url, _game) { ChangeNote = DescribeChange(itemId) };
    }

    /// <summary>Writes the editor state back into the build and schedules a save.</summary>
    internal void Commit()
    {
        _data.Blocks = Blocks.Select(b => new ItemBlockData { Name = b.Name.Trim().Length == 0 ? "Block" : b.Name.Trim(), Items = b.Items.Select(i => i.ItemId).ToList() }).ToList();
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(HasMissingItems));
        NotifyChanges();
        _changed();
    }

    public void AddToSelected(ShopItemViewModel item)
    {
        var block = SelectedBlock ?? Blocks.FirstOrDefault();
        if (block is null)
        {
            AddBlock();
            block = Blocks[^1];
        }
        block.Items.Add(CreateSlot(block, item.Item.Id));
        Commit();
    }

    [RelayCommand]
    private void AddBlock()
    {
        var block = new ItemBlockViewModel(this, Blocks.Count switch { 0 => "Starting Items", 1 => "Core Build", 2 => "Situational", _ => "New Block" }, []);
        Blocks.Add(block);
        SelectedBlock = block;
        Commit();
    }

    [RelayCommand]
    private void RemoveBlock(ItemBlockViewModel block)
    {
        var index = Blocks.IndexOf(block);
        Blocks.Remove(block);
        if (ReferenceEquals(SelectedBlock, block)) SelectedBlock = Blocks.ElementAtOrDefault(Math.Max(0, index - 1));
        Commit();
    }

    [RelayCommand]
    private void MoveBlockUp(ItemBlockViewModel block)
    {
        var i = Blocks.IndexOf(block);
        if (i <= 0) return;
        Blocks.Move(i, i - 1);
        Commit();
    }

    [RelayCommand]
    private void MoveBlockDown(ItemBlockViewModel block)
    {
        var i = Blocks.IndexOf(block);
        if (i < 0 || i >= Blocks.Count - 1) return;
        Blocks.Move(i, i + 1);
        Commit();
    }

    [RelayCommand]
    private void SelectBlock(ItemBlockViewModel block) => SelectedBlock = block;

    // ── The shop is a drop target: dropping an item from a block removes it ───────────────────────

    public bool CanAccept(IDragPayload payload) => payload is ItemDragData { Origin: ItemSlotViewModel };
    public string? Describe(IDragPayload payload, bool copy) => "Remove from item set";

    public void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy)
    {
        if (payload is ItemDragData { Origin: ItemSlotViewModel slot })
        {
            slot.Block.Items.Remove(slot);
            Commit();
        }
    }
}

public sealed partial class ItemBlockViewModel : ObservableObject, IDropTarget
{
    private readonly ItemSetViewModel _owner;

    public ItemBlockViewModel(ItemSetViewModel owner, string name, IEnumerable<string> itemIds)
    {
        _owner = owner;
        _name = name;
        foreach (var id in itemIds) Items.Add(owner.CreateSlot(this, id));
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TotalGoldText));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public ItemSetViewModel Owner => _owner;
    public ObservableCollection<ItemSlotViewModel> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public string TotalGoldText => Items.Sum(i => i.Item?.TotalGold ?? 0).ToString("N0", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnNameChanged(string value) => _owner.Commit();

    [RelayCommand]
    private void RemoveItem(ItemSlotViewModel slot)
    {
        Items.Remove(slot);
        _owner.Commit();
    }

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        _owner.Commit();
    }

    public bool CanAccept(IDragPayload payload) => payload is ItemDragData;

    public string? Describe(IDragPayload payload, bool copy) => payload switch
    {
        ItemDragData { Origin: ItemSlotViewModel s } when ReferenceEquals(s.Block, this) => copy ? $"Duplicate in {Name}" : "Reorder",
        ItemDragData { Origin: ItemSlotViewModel } => copy ? $"Copy to {Name}" : $"Move to {Name}",
        _ => $"Add to {Name}",
    };

    public void Drop(IDragPayload payload, object? relativeTo, bool after, bool copy)
    {
        if (payload is not ItemDragData data) return;
        var relative = relativeTo as ItemSlotViewModel;
        var index = relative is not null && Items.Contains(relative) ? Items.IndexOf(relative) + (after ? 1 : 0) : Items.Count;
        if (data.Origin is ItemSlotViewModel source && !copy)
        {
            if (ReferenceEquals(source.Block, this))
            {
                var old = Items.IndexOf(source);
                if (index > old) index--;
                if (old < 0 || old == index) return;
                Items.Move(old, index);
            }
            else
            {
                source.Block.Items.Remove(source);
                Items.Insert(index, _owner.CreateSlot(this, source.ItemId));
            }
        }
        else
        {
            Items.Insert(index, _owner.CreateSlot(this, data.ItemId));
        }
        _owner.SelectedBlock = this;
        _owner.Commit();
    }
}

public sealed partial class ItemSlotViewModel : ObservableObject
{
    /// <summary>What changed about this item since the build's patch (null = unchanged or unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChange))]
    private string? _changeNote;

    public bool HasChange => ChangeNote is not null;

    public ItemSlotViewModel(ItemBlockViewModel block, string itemId, ItemInfo? item, string iconUrl, GameDataSnapshot game)
    {
        _details = item is null ? null : new Lazy<ItemDetailsViewModel>(() => new ItemDetailsViewModel(game, item));
        Block = block;
        ItemId = itemId;
        Item = item;
        IconUrl = iconUrl;
        DragData = new ItemDragData(itemId, item, iconUrl, this);
    }

    public ItemBlockViewModel Block { get; }
    public string ItemId { get; }
    public ItemInfo? Item { get; }
    public string IconUrl { get; }
    public ItemDragData DragData { get; }
    private readonly Lazy<ItemDetailsViewModel>? _details;
    /// <summary>Recipe tree and "builds into" for the tooltip (built on first hover).</summary>
    public ItemDetailsViewModel? Details => _details?.Value;
    public bool HasDetails => _details is not null;
    public bool IsMissing => Item is null;
    public string Name => Item?.Name ?? $"Removed item ({ItemId})";
    public string Description => Item?.Description ?? "This item is not available in the current patch.";
    public string CostText => Item is null ? "" : Item.TotalGold.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed class ShopItemViewModel
{
    public ShopItemViewModel(ItemInfo item, GameDataSnapshot game)
    {
        var version = game.Version;
        _details = new Lazy<ItemDetailsViewModel>(() => new ItemDetailsViewModel(game, item));
        Item = item;
        IconUrl = AssetUrls.Item(version, item.Id);
        DragData = new ItemDragData(item.Id, item, IconUrl, null);
    }

    private readonly Lazy<ItemDetailsViewModel> _details;
    public ItemDetailsViewModel Details => _details.Value;
    public ItemInfo Item { get; }
    public string IconUrl { get; }
    public ItemDragData DragData { get; }
    public string Name => Item.Name;
    public string Description => Item.Description;
    public string PlainText => Item.PlainText;
    public bool HasPlainText => Item.PlainText.Length > 0;
    public string CostText => Item.TotalGold.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed class ShopSectionViewModel(string title, IReadOnlyList<ShopItemViewModel> items)
{
    public string Title { get; } = title.ToUpperInvariant();
    public IReadOnlyList<ShopItemViewModel> Items { get; } = items;
}

public sealed partial class CategoryOptionViewModel(ItemStatCategory category, Action changed) : ObservableObject
{
    public ItemStatCategory Category { get; } = category;
    public string Name => Category.Name;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

public sealed partial class ItemShopViewModel : ObservableObject
{
    private readonly ItemSetViewModel _owner;
    private readonly IReadOnlyList<ShopItemViewModel> _all;

    public ItemShopViewModel(GameDataSnapshot game, ItemSetViewModel owner)
    {
        _owner = owner;
        _all = game.ShopItems.Select(i => new ShopItemViewModel(i, game)).ToList();
        Categories = ItemCatalog.StatCategories.Select(c => new CategoryOptionViewModel(c, Refilter)).ToList();
        Refilter();
    }

    public IReadOnlyList<CategoryOptionViewModel> Categories { get; }

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRift))]
    private bool _isAram;

    public bool IsRift
    {
        get => !IsAram;
        set => IsAram = !value;
    }

    [ObservableProperty]
    private IReadOnlyList<ShopSectionViewModel> _sections = [];

    [ObservableProperty]
    private bool _isEmpty;

    public bool HasFilters => Categories.Any(c => c.IsChecked) || Search.Length > 0;

    partial void OnSearchChanged(string value) => Refilter();
    partial void OnIsAramChanged(bool value) => Refilter();

    [RelayCommand]
    private void ClearFilters()
    {
        foreach (var c in Categories) c.IsChecked = false;
        Search = "";
        Refilter();
    }

    [RelayCommand]
    private void Add(ShopItemViewModel item) => _owner.AddToSelected(item);

    private void Refilter()
    {
        var keys = Categories.Where(c => c.IsChecked).Select(c => c.Category.Key).ToList();
        var matching = ItemCatalog.Filter(_all.Select(a => a.Item), Search, keys, IsAram ? ItemMap.Aram : ItemMap.SummonersRift)
            .Select(i => i.Id).ToHashSet();
        Sections = ItemCatalog.GroupByTier(_all.Where(a => matching.Contains(a.Item.Id)).Select(a => a.Item))
            .Select(g => new ShopSectionViewModel(g.Tier.DisplayName(), g.Items.Select(i => _all.First(a => a.Item.Id == i.Id)).ToList()))
            .ToList();
        IsEmpty = Sections.Count == 0;
        OnPropertyChanged(nameof(HasFilters));
    }
}

/// <summary>Everything shown in an item tooltip: stats, recipe tree (build path) and what it builds into.</summary>
public sealed class ItemDetailsViewModel
{
    public ItemDetailsViewModel(GameDataSnapshot game, ItemInfo item)
    {
        Item = item;
        IconUrl = AssetUrls.Item(game.Version, item.Id);
        var recipe = ItemCatalog.BuildRecipe(item, game.Items);
        Recipe = new RecipeNodeViewModel(recipe, game.Version, isRoot: true, isFirst: true, isLast: true);
        BuildsInto = ItemCatalog.BuildsInto(item, game.Items, game.ShopItemIds).Select(i => new IntoItemViewModel(i, game.Version)).ToList();
    }

    public ItemInfo Item { get; }
    public string IconUrl { get; }
    public string Name => Item.Name;
    public string Description => Item.Description;
    public string PlainText => Item.PlainText;
    public bool HasPlainText => Item.PlainText.Length > 0;
    public string CostText => Item.TotalGold.ToString("N0", CultureInfo.InvariantCulture);
    public RecipeNodeViewModel Recipe { get; }
    public bool HasRecipe => Recipe.HasChildren;
    public string CombineText => $"Combine cost {Item.CombineCost.ToString("N0", CultureInfo.InvariantCulture)}";
    public IReadOnlyList<IntoItemViewModel> BuildsInto { get; }
    public bool HasBuildsInto => BuildsInto.Count > 0;
}

/// <summary>A node of the build path tree. First/last flags draw the connector lines like the client's shop.</summary>
public sealed class RecipeNodeViewModel
{
    public RecipeNodeViewModel(RecipeNode node, string version, bool isRoot, bool isFirst, bool isLast)
    {
        Item = node.Item;
        IconUrl = AssetUrls.Item(version, node.Item.Id);
        IsRoot = isRoot;
        ShowLeftLine = !isRoot && !isFirst;
        ShowRightLine = !isRoot && !isLast;
        Children = node.Components.Select((c, i) => new RecipeNodeViewModel(c, version, false, i == 0, i == node.Components.Count - 1)).ToList();
    }

    public ItemInfo Item { get; }
    public string IconUrl { get; }
    public string Name => Item.Name;
    public string CostText => Item.TotalGold.ToString("N0", CultureInfo.InvariantCulture);
    public bool IsRoot { get; }
    public bool ShowLeftLine { get; }
    public bool ShowRightLine { get; }
    public IReadOnlyList<RecipeNodeViewModel> Children { get; }
    public bool HasChildren => Children.Count > 0;
    public double IconSize => IsRoot ? 34 : 28;
}

public sealed class IntoItemViewModel(ItemInfo item, string version)
{
    public string IconUrl { get; } = AssetUrls.Item(version, item.Id);
    public string Name => item.Name;
    public string CostText => item.TotalGold.ToString("N0", CultureInfo.InvariantCulture);
}
