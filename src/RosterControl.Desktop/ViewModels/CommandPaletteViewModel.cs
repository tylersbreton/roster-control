using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Rules;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Ctrl+K: jump to any champion, page or common action by typing.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private const int MaxChampions = 7;
    private const int MaxItems = 4;
    private const int MaxRunes = 3;
    private const int MaxNotes = 6;
    private const int MinNoteQuery = 3;
    private readonly MainWindowViewModel _main;
    private IReadOnlyList<NoteEntry> _notes = [];
    private int _notesVersion;

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
        _ = LoadNotesAsync();
    }

    /// <summary>Champion, build and matchup notes, loaded each time the palette opens (they change while editing).</summary>
    private async Task LoadNotesAsync()
    {
        var version = ++_notesVersion;
        try
        {
            await _main.Saves.FlushAsync();
            var repo = _main.Repository;
            var profiles = await repo.GetProfilesAsync();
            var builds = await repo.GetAllBuildsAsync();
            var matchups = await repo.GetMatchupsAsync();
            var entries = new List<NoteEntry>();
            entries.AddRange(profiles.Where(p => !string.IsNullOrWhiteSpace(p.Notes))
                .Select(p => new NoteEntry(p.ChampionId, NoteKind.Champion, p.Notes, null, null)));
            entries.AddRange(builds.Where(b => !string.IsNullOrWhiteSpace(b.Notes))
                .Select(b => new NoteEntry(b.ChampionId, NoteKind.Build, b.Notes, b.Name, b.Id)));
            entries.AddRange(matchups.Where(m => !string.IsNullOrWhiteSpace(m.Note))
                .Select(m => new NoteEntry(m.ChampionId, NoteKind.Matchup, m.Note, m.OpponentId, null)));
            if (version != _notesVersion) return;
            _notes = entries;
            if (IsOpen && Query.Trim().Length >= MinNoteQuery) Refresh();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not load notes for search");
        }
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
            if (query.Length >= 3)
            {
                var items = game.ShopItems
                    .Where(i => i.SearchKey.Contains(query, StringComparison.Ordinal))
                    .OrderBy(i => !i.SearchKey.StartsWith(query, StringComparison.Ordinal))
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxItems);
                foreach (var i in items)
                {
                    var id = i.Id;
                    Results.Add(new PaletteItem(i.Name, $"Item · {i.TotalGold:N0} gold", AssetUrls.Item(game.Version, id), null, () => _ = _main.OpenItemAsync(id)));
                }
                var runes = game.Runes.Values
                    .Where(r => ChampionInfo.Normalize(r.Name).Contains(query, StringComparison.Ordinal))
                    .OrderBy(r => !ChampionInfo.Normalize(r.Name).StartsWith(query, StringComparison.Ordinal))
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRunes);
                foreach (var r in runes)
                {
                    var id = r.Id;
                    var path = game.Tree(r.TreeId)?.Name ?? "Rune";
                    Results.Add(new PaletteItem(r.Name, $"Rune · {path}{(r.IsKeystone ? " keystone" : "")}", AssetUrls.Rune(r.IconPath), null, () => _ = _main.OpenRuneAsync(id)));
                }
            }
            foreach (var item in NoteResults(game, Query.Trim())) Results.Add(item);
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

    private IEnumerable<PaletteItem> NoteResults(GameDataSnapshot game, string query)
    {
        if (query.Length < MinNoteQuery) yield break;
        var count = 0;
        foreach (var note in _notes)
        {
            if (NoteFormat.Snippet(note.Text, query) is not { } snippet) continue;
            if (game.Champion(note.ChampionId) is not { } champion) continue;
            var icon = AssetUrls.ChampionSquare(game.Version, champion.ImageFile);
            var (where, run) = note.Kind switch
            {
                NoteKind.Build => ($"{champion.Name} · {note.Context} build notes", (Action)(() =>
                {
                    _main.Preferences.Set("build.editorTab", 3); // the build's Notes tab
                    _main.OpenChampion(champion.Id, ChampionTab.Builds, buildId: note.BuildId);
                })),
                NoteKind.Matchup => ($"{champion.Name} vs {game.Champion(note.Context ?? "")?.Name ?? note.Context} · matchup note",
                    () => _main.OpenChampion(champion.Id, ChampionTab.Matchups)),
                _ => ($"{champion.Name} · champion notes", (Action)(() => _main.OpenChampion(champion.Id, ChampionTab.Notes))),
            };
            yield return new PaletteItem(snippet, where, icon, null, run);
            if (++count >= MaxNotes) yield break;
        }
    }

    private IEnumerable<PaletteItem> Actions()
    {
        yield return new PaletteItem("Rosters", "Page · Ctrl+1", null, "layout-grid", () => _ = _main.NavigateAsync(Section.Rosters));
        yield return new PaletteItem("Champions", "Page · Ctrl+2", null, "library-big", () => _ = _main.NavigateAsync(Section.Champions));
        yield return new PaletteItem("Counter Picks", "Page · Ctrl+3", null, "swords", () => _ = _main.NavigateAsync(Section.Counters));
        yield return new PaletteItem("Items", "Page · Ctrl+5 · gold efficiency and your builds per item", null, "shopping-bag", () => _ = _main.NavigateAsync(Section.Items));
        yield return new PaletteItem("Runes", "Page · Ctrl+6 · formulas and your builds per rune", null, "sparkles", () => _ = _main.NavigateAsync(Section.Runes));
        yield return new PaletteItem("Champion stats", "Page · Ctrl+7 · base stats of every champion at a level", null, "table", () => _ = _main.NavigateAsync(Section.Stats));
        yield return new PaletteItem("Settings", "Page · Ctrl+4", null, "settings", () => _ = _main.NavigateAsync(Section.Settings));
        yield return new PaletteItem("New roster group", "Action", null, "plus", async () =>
        {
            await _main.NavigateAsync(Section.Rosters);
            _main.Rosters?.AddGroupCommand.Execute(null);
        });
        foreach (var roster in _main.Rosters?.Rosters.Where(r => !r.IsActive) ?? [])
            yield return new PaletteItem($"Switch to {roster.Name}", $"Roster · {roster.Detail}", null, "layout-grid", () => _ = _main.SwitchRosterAsync(roster.Id));
        yield return new PaletteItem("Patch review", _main.Review.Count == 0 ? "Page · builds affected by patch changes" : $"Page · {_main.Review.Count} build(s) to review",
            null, "triangle-alert", () => _main.OpenPatchReview());
        yield return new PaletteItem("Compare champions", "Page · side by side", null, "columns-2", () => _main.OpenCompare(
            (_main.CurrentPage as ChampionDetailViewModel)?.Champion.Id, null));
        yield return new PaletteItem("Quick look", (_main.QuickLookHotkeyEnabled ? _main.QuickLookHotkey + " · " : "") + "always-on-top build window", null, "eye",
            () => _main.OpenQuickLook((_main.CurrentPage as ChampionDetailViewModel)?.Champion.Id,
                buildId: (_main.CurrentPage as ChampionDetailViewModel)?.SelectedBuild?.Model.Id));
        yield return new PaletteItem("What's new", "Changes in recent versions", null, "sparkles", () => _main.ShowWhatsNew());
        yield return new PaletteItem("Toggle compact roster cards", "Action", null, "list", () =>
        {
            if (_main.Rosters is { } r) r.Compact = !r.Compact;
        });
        yield return new PaletteItem("Check for a new patch", "Action · game data", null, "refresh-cw", () => _ = _main.ReloadGameDataAsync(force: false));
        yield return new PaletteItem("Export backup…", "Action · Settings", null, "download", () => _main.Settings?.ExportCommand.Execute(null));
        yield return new PaletteItem("Paste build code", "Action · add a shared build from the clipboard", null, "clipboard-paste", async () =>
        {
            if (await _main.ImportSharedBuildAsync(null) is { } build)
            {
                _main.OpenChampion(build.ChampionId, ChampionTab.Builds, buildId: build.Id);
                _main.ShowToast($"Added build \"{build.Name}\"", ToastKind.Success);
            }
        });
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

internal enum NoteKind
{
    Champion,
    Build,
    Matchup,
}

/// <param name="Context">Build name, or the opponent's id for a matchup.</param>
internal sealed record NoteEntry(string ChampionId, NoteKind Kind, string Text, string? Context, int? BuildId);
