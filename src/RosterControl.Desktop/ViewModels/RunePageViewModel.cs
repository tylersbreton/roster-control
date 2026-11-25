using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Rune page editor laid out like the League client: primary path on the left, secondary path and shards on the right.</summary>
public sealed partial class RunePageViewModel : ObservableObject
{
    private readonly GameDataSnapshot _game;
    private readonly RunePageData _page;
    private readonly Action _changed;
    private int? _builtPrimary = -1;
    private int? _builtSub = -1;

    public RunePageViewModel(GameDataSnapshot game, RunePageData page, Action changed)
    {
        _game = game;
        _page = page;
        _changed = changed;
        RunePageRules.Normalize(page);
        Issues = RunePageRules.Sanitize(page, game);
        PrimaryTrees = game.RuneTrees.Select(t => new TreeOptionViewModel(t)).ToList();
        ShardRows = game.ShardRows.Select((row, i) => new ShardRowViewModel(i, row)).ToList();
        Refresh();
    }

    public IReadOnlyList<string> Issues { get; }
    public bool HasIssues => Issues.Count > 0;
    public string IssuesText => "Updated for this patch: " + string.Join("; ", Issues);

    public IReadOnlyList<TreeOptionViewModel> PrimaryTrees { get; }
    public ObservableCollection<TreeOptionViewModel> SecondaryTrees { get; } = [];
    public ObservableCollection<RuneRowViewModel> PrimaryRows { get; } = [];
    public ObservableCollection<RuneRowViewModel> SecondaryRows { get; } = [];
    public IReadOnlyList<ShardRowViewModel> ShardRows { get; }
    public bool HasShards => ShardRows.Count > 0;

    public RuneTreeInfo? PrimaryTree => _game.Tree(_page.PrimaryStyleId);
    public RuneTreeInfo? SubTree => _game.Tree(_page.SubStyleId);
    public bool HasPrimary => PrimaryTree is not null;
    public bool HasSecondary => SubTree is not null;
    public string PrimaryName => PrimaryTree?.Name.ToUpperInvariant() ?? "CHOOSE A PATH";
    public string PrimaryTagline => PrimaryTree?.Tagline ?? "Select your primary path to begin.";
    public string? PrimaryIconUrl => PrimaryTree is { } t ? AssetUrls.Rune(t.IconPath) : null;
    public IBrush PrimaryAccent => Palette.Hex(PrimaryTree?.Color ?? Palette.Gold);
    public IBrush PrimaryAccentSoft => Palette.Soft(PrimaryTree?.Color ?? Palette.Gold, 0x40);
    public string SecondaryName => SubTree?.Name.ToUpperInvariant() ?? "SECONDARY";
    public string SecondaryTagline => SubTree?.Tagline ?? (HasPrimary ? "Pick a secondary path." : "");
    public string? SecondaryIconUrl => SubTree is { } t ? AssetUrls.Rune(t.IconPath) : null;
    public IBrush SecondaryAccent => Palette.Hex(SubTree?.Color ?? Palette.Gold);
    public string? BackgroundUrl => PrimaryTree?.BackgroundUrl(_page.SubStyleId, _page.PrimaryPerks.ElementAtOrDefault(0));

    public int SelectedCount => RunePageRules.SelectedCount(_page);
    public bool IsComplete => RunePageRules.IsComplete(_page);
    public string ProgressText => IsComplete ? "Complete" : $"{SelectedCount} / 9 selected";
    public string? KeystoneIconUrl => _game.Rune(_page.PrimaryPerks.ElementAtOrDefault(0)) is { } r ? AssetUrls.Rune(r.IconPath) : PrimaryIconUrl;
    public string KeystoneName => _game.Rune(_page.PrimaryPerks.ElementAtOrDefault(0))?.Name ?? PrimaryTree?.Name ?? "No runes";

    [RelayCommand]
    private void SelectPrimaryTree(TreeOptionViewModel option)
    {
        if (RunePageRules.SelectPrimaryTree(_page, option.Tree.Id)) Commit();
    }

    [RelayCommand]
    private void SelectSecondaryTree(TreeOptionViewModel option)
    {
        if (RunePageRules.SelectSubTree(_page, option.Tree.Id)) Commit();
    }

    [RelayCommand]
    private void SelectRune(RuneOptionViewModel option)
    {
        var changed = option.IsPrimary
            ? RunePageRules.SelectPrimaryRune(_page, _game, option.Rune.Id)
            : RunePageRules.SelectSubRune(_page, _game, option.Rune.Id);
        if (changed) Commit();
    }

    [RelayCommand]
    private void SelectShard(ShardOptionViewModel option)
    {
        if (RunePageRules.SelectShard(_page, _game, option.Row, option.Shard.Id)) Commit();
    }

    [RelayCommand]
    private void Reset()
    {
        _page.PrimaryStyleId = null;
        _page.SubStyleId = null;
        _page.PrimaryPerks = [0, 0, 0, 0];
        _page.SubPerks = [];
        _page.Shards = [0, 0, 0];
        Commit();
    }

    private void Commit()
    {
        Refresh();
        _changed();
    }

    private void Refresh()
    {
        if (_builtPrimary != _page.PrimaryStyleId)
        {
            _builtPrimary = _page.PrimaryStyleId;
            PrimaryRows.Clear();
            if (PrimaryTree is { } tree)
            {
                for (var i = 0; i < tree.Rows.Count && i < 4; i++)
                    PrimaryRows.Add(new RuneRowViewModel(i, tree, tree.Rows[i], isPrimary: true));
            }
            SecondaryTrees.Clear();
            foreach (var t in _game.RuneTrees.Where(t => t.Id != _page.PrimaryStyleId)) SecondaryTrees.Add(new TreeOptionViewModel(t));
            _builtSub = -1;
        }
        if (_builtSub != _page.SubStyleId)
        {
            _builtSub = _page.SubStyleId;
            SecondaryRows.Clear();
            if (SubTree is { } tree)
            {
                for (var i = 1; i < tree.Rows.Count; i++)
                    SecondaryRows.Add(new RuneRowViewModel(i, tree, tree.Rows[i], isPrimary: false));
            }
        }

        foreach (var t in PrimaryTrees) t.IsSelected = t.Tree.Id == _page.PrimaryStyleId;
        foreach (var t in SecondaryTrees) t.IsSelected = t.Tree.Id == _page.SubStyleId;
        foreach (var row in PrimaryRows) row.Update(id => _page.PrimaryPerks.ElementAtOrDefault(row.Row) == id);
        foreach (var row in SecondaryRows) row.Update(id => _page.SubPerks.Contains(id));
        foreach (var row in ShardRows) row.Update(_page.Shards.ElementAtOrDefault(row.Row));

        OnPropertyChanged(string.Empty); // all derived summary properties
    }
}

public sealed partial class TreeOptionViewModel : ObservableObject
{
    public TreeOptionViewModel(RuneTreeInfo tree)
    {
        Tree = tree;
        IconUrl = AssetUrls.Rune(tree.IconPath);
        Accent = Palette.Hex(tree.Color);
        AccentSoft = Palette.Soft(tree.Color, 0x55);
    }

    public RuneTreeInfo Tree { get; }
    public string Name => Tree.Name;
    public string Tooltip => $"{Tree.Name} — {Tree.Tagline}";
    public string IconUrl { get; }
    public IBrush Accent { get; }
    public IBrush AccentSoft { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class RuneRowViewModel : ObservableObject
{
    public RuneRowViewModel(int row, RuneTreeInfo tree, IReadOnlyList<RuneInfo> runes, bool isPrimary)
    {
        Row = row;
        IsKeystoneRow = row == 0;
        Options = runes.Select(r => new RuneOptionViewModel(r, tree, isPrimary)).ToList();
        Accent = Palette.Hex(tree.Color);
    }

    public int Row { get; }
    public bool IsKeystoneRow { get; }
    public IReadOnlyList<RuneOptionViewModel> Options { get; }
    public IBrush Accent { get; }

    [ObservableProperty]
    private string _selectedName = "";

    [ObservableProperty]
    private string _selectedDescription = "";

    [ObservableProperty]
    private bool _hasSelection;

    internal void Update(Func<int, bool> isSelected)
    {
        RuneOptionViewModel? selected = null;
        foreach (var o in Options)
        {
            o.IsSelected = isSelected(o.Rune.Id);
            if (o.IsSelected) selected = o;
        }
        HasSelection = selected is not null;
        SelectedName = selected?.Rune.Name ?? "";
        SelectedDescription = selected?.Rune.ShortDesc ?? "";
    }
}

public sealed partial class RuneOptionViewModel : ObservableObject
{
    public RuneOptionViewModel(RuneInfo rune, RuneTreeInfo tree, bool isPrimary)
    {
        Rune = rune;
        IsPrimary = isPrimary;
        IconUrl = AssetUrls.Rune(rune.IconPath);
        Accent = Palette.Hex(tree.Color);
        Glow = BoxShadows.Parse($"0 0 16 1 #99{tree.Color.TrimStart('#')}");
        Size = rune.IsKeystone && isPrimary ? 58 : 40;
    }

    public RuneInfo Rune { get; }
    public bool IsPrimary { get; }
    public string IconUrl { get; }
    public IBrush Accent { get; }
    public BoxShadows Glow { get; }
    public double Size { get; }
    public double RingSize => Size + 8;
    public string Name => Rune.Name;
    public string LongDesc => Rune.LongDesc;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class ShardRowViewModel : ObservableObject
{
    public ShardRowViewModel(int row, StatShardRow data)
    {
        Row = row;
        Label = data.Label.ToUpperInvariant();
        Options = data.Options.Select(o => new ShardOptionViewModel(row, o)).ToList();
    }

    public int Row { get; }
    public string Label { get; }
    public IReadOnlyList<ShardOptionViewModel> Options { get; }

    [ObservableProperty]
    private string _selectedName = "";

    internal void Update(int selectedId)
    {
        foreach (var o in Options) o.IsSelected = o.Shard.Id == selectedId;
        SelectedName = Options.FirstOrDefault(o => o.IsSelected) is { } s ? Controls.RiotText.ToPlainText(s.Shard.ShortDesc) : "";
    }
}

public sealed partial class ShardOptionViewModel : ObservableObject
{
    public ShardOptionViewModel(int row, StatShardInfo shard)
    {
        Row = row;
        Shard = shard;
    }

    public int Row { get; }
    public StatShardInfo Shard { get; }
    public string IconUrl => Shard.IconUrl;
    public string Name => Shard.Name;
    public string Description => Shard.ShortDesc;

    [ObservableProperty]
    private bool _isSelected;
}
