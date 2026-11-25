using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Ctrl+K: jump to any champion, page or common action by typing.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private const int MaxChampions = 7;
    private readonly MainWindowViewModel _main;

    public CommandPaletteViewModel(MainWindowViewModel main) => _main = main;

    public ObservableCollection<PaletteItem> Results { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private int _selectedIndex;

    [ObservableProperty]
    private bool _hasResults;

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnSelectedIndexChanged(int value)
    {
        for (var i = 0; i < Results.Count; i++) Results[i].IsSelected = i == value;
    }

    public void Open()
    {
        Query = "";
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = (SelectedIndex + delta + Results.Count) % Results.Count;
    }

    [RelayCommand]
    private void Execute(PaletteItem? item)
    {
        item ??= SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;
        if (item is null) return;
        Close();
        item.Run();
    }

    private void Refresh()
    {
        Results.Clear();
        var game = _main.Game;
        var query = ChampionInfo.Normalize(Query.Trim());

        if (query.Length == 0)
        {
            foreach (var id in _main.RecentChampions)
                if (game.Champion(id) is { } c) Results.Add(ChampionItem(game, c, "Recent"));
            foreach (var item in Actions()) Results.Add(item);
        }
        else
        {
            var champions = game.Champions
                .Where(c => c.Matches(Query))
                .OrderBy(c => !ChampionInfo.Normalize(c.Name).StartsWith(query, StringComparison.Ordinal))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxChampions);
            foreach (var c in champions) Results.Add(ChampionItem(game, c, null));
            foreach (var item in Actions().Where(a => ChampionInfo.Normalize(a.Title + " " + a.Subtitle).Contains(query, StringComparison.Ordinal)))
                Results.Add(item);
        }
        HasResults = Results.Count > 0;
        SelectedIndex = 0;
        OnSelectedIndexChanged(0);
    }

    private PaletteItem ChampionItem(GameDataSnapshot game, ChampionInfo champion, string? prefix)
    {
        var groups = _main.Rosters?.Groups.Where(g => g.Entries.Any(e => string.Equals(e.ChampionId, champion.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(g => g.Name).ToList() ?? [];
        var subtitle = groups.Count > 0 ? "In " + string.Join(", ", groups) : champion.Title;
        if (prefix is not null) subtitle = prefix + " · " + subtitle;
        return new PaletteItem(champion.Name, subtitle, AssetUrls.ChampionSquare(game.Version, champion.ImageFile), null, () => _main.OpenChampion(champion.Id));
    }

    private IEnumerable<PaletteItem> Actions()
    {
        yield return new PaletteItem("Rosters", "Page · Ctrl+1", null, "layout-grid", () => _ = _main.NavigateAsync(Section.Rosters));
        yield return new PaletteItem("Champions", "Page · Ctrl+2", null, "library-big", () => _ = _main.NavigateAsync(Section.Champions));
        yield return new PaletteItem("Counter Picks", "Page · Ctrl+3", null, "swords", () => _ = _main.NavigateAsync(Section.Counters));
        yield return new PaletteItem("Settings", "Page · Ctrl+4", null, "settings", () => _ = _main.NavigateAsync(Section.Settings));
        yield return new PaletteItem("New roster group", "Action", null, "plus", async () =>
        {
            await _main.NavigateAsync(Section.Rosters);
            _main.Rosters?.AddGroupCommand.Execute(null);
        });
        yield return new PaletteItem("Toggle compact roster cards", "Action", null, "list", () =>
        {
            if (_main.Rosters is { } r) r.Compact = !r.Compact;
        });
        yield return new PaletteItem("Check for a new patch", "Action · game data", null, "refresh-cw", () => _ = _main.ReloadGameDataAsync(force: false));
        yield return new PaletteItem("Export backup…", "Action · Settings", null, "download", () => _main.Settings?.ExportCommand.Execute(null));
        yield return new PaletteItem("Support development", "GitHub Sponsors", null, "heart", () => SettingsViewModel.OpenUrl(SettingsViewModel.SponsorUrl));
        yield return new PaletteItem("Zoom in", "Action · Ctrl+=", null, "plus", () => _main.ZoomInCommand.Execute(null));
        yield return new PaletteItem("Zoom out", "Action · Ctrl+-", null, "minus", () => _main.ZoomOutCommand.Execute(null));
        yield return new PaletteItem("Reset zoom", "Action · Ctrl+0", null, "rotate-ccw", () => _main.ResetZoomCommand.Execute(null));
    }
}

public sealed partial class PaletteItem(string title, string subtitle, string? iconUrl, string? iconKind, Action run) : ObservableObject
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string? IconUrl { get; } = iconUrl;
    public string? IconKind { get; } = iconKind;
    public bool HasIconUrl => IconUrl is not null;
    public bool HasIconKind => IconKind is not null;
    public void Run() => run();

    [ObservableProperty]
    private bool _isSelected;
}
