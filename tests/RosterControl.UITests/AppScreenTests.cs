using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Core.Rules;
using RosterControl.Data;
using RosterControl.Desktop.Controls;
using RosterControl.Desktop.Services;
using RosterControl.Desktop.ViewModels;
using RosterControl.Desktop.Views;

// The screens are captured in a deliberate order on one shared session; [Order] is deprecated in NUnit 5 but still works.
#pragma warning disable CS0618

namespace RosterControl.UITests;

/// <summary>
/// Drives the real application through every page with realistic data and writes screenshots to artifacts/screenshots
/// (used for visual validation). Requires network access on first run (Data Dragon); cached afterwards.
/// </summary>
[NonParallelizable]
public class AppScreenTests
{
    private static MainWindow? _window;
    private static string? _windowTest;
    private static bool _initialized;
    private static bool _seeded;

    private static MainWindowViewModel Vm => UiTestHost.Services.GetRequiredService<MainWindowViewModel>();
    private static RosterRepository Repo => UiTestHost.Services.GetRequiredService<RosterRepository>();
    private static GameDataSnapshot Game => Vm.Game;

    /// <summary>
    /// A new window per test (the headless test runner closes windows between tests); the view model, database and
    /// caches are shared, like a real session.
    /// </summary>
    private static MainWindow Window(int width = 1600, int height = 960)
    {
        if (!_initialized)
        {
            _initialized = true;
            RosterControl.Desktop.App.InitializeServices(UiTestHost.Services);
            UiTestHost.Run(() => Vm.InitializeAsync());
            Assert.That(Vm.LoadError, Is.Null, Vm.LoadError);
        }
        var test = TestContext.CurrentContext.Test.ID;
        if (_window is null || _windowTest != test || Math.Abs(_window.Width - width) > 0.5 || Math.Abs(_window.Height - height) > 0.5)
        {
            _window?.Close();
            _window = new MainWindow { DataContext = Vm, Width = width, Height = height };
            _windowTest = test;
            _window.Show();
        }
        if (!_seeded)
        {
            _seeded = true;
            UiTestHost.Run(SeedAsync);
            UiTestHost.Run(() => Vm.Rosters!.ReloadAsync());
            WarmImages();
        }
        UiTestHost.Settle();
        return _window;
    }

    private static void WarmImages()
    {
        var images = UiTestHost.Services.GetRequiredService<AssetImageService>();
        var urls = Game.Champions.Select(c => AssetUrls.ChampionSquare(Game.Version, c.ImageFile))
            .Concat(Game.ShopItems.Select(i => AssetUrls.Item(Game.Version, i.Id)))
            .Concat(Game.SummonerSpells.Select(s => AssetUrls.Spell(Game.Version, s.ImageFile)))
            .Concat(PositionInfo.All.Select(AssetUrls.PositionIcon))
            .ToList();
        UiTestHost.Run(() => images.WarmAsync(urls, 96, TimeSpan.FromSeconds(150)), 200);
        var runeUrls = Game.RuneTrees.SelectMany(t => t.Rows.SelectMany(r => r).Select(r => AssetUrls.Rune(r.IconPath)).Append(AssetUrls.Rune(t.IconPath)))
            .Concat(Game.Shards.Values.Select(s => s.IconUrl));
        UiTestHost.Run(() => Task.WhenAll(runeUrls.Distinct().SelectMany(u => new[] { images.GetAsync(u, 96, false), images.GetAsync(u, 96, true) })), 120);
        UiTestHost.Run(() => images.WarmAsync([AssetUrls.ChampionCentered("Ahri")], 1600, TimeSpan.FromSeconds(60)), 90);
    }

    private static void WaitForImages(int rounds = 30)
    {
        for (var i = 0; i < rounds; i++)
        {
            UiTestHost.Settle(2);
            Thread.Sleep(40);
        }
    }

    private static string Capture(string name)
    {
        WaitForImages();
        var frame = _window!.CaptureRenderedFrame();
        var path = Path.Combine(UiTestHost.ScreenshotDirectory, name + ".png");
        frame?.Save(path);
        TestContext.Out.WriteLine(path);
        return path;
    }

    // ── Seed data ──────────────────────────────────────────────────────────────────────────────────

    private static async Task SeedAsync()
    {
        var groups = await Repo.GetGroupsAsync();
        int Group(string name) => groups.First(g => g.Name == name).Id;
        var placements = new (string Group, string Champion, ChampionPriority Priority, int Proficiency)[]
        {
            ("Top", "Darius", ChampionPriority.Main, 5), ("Top", "Garen", ChampionPriority.Secondary, 4), ("Top", "Sett", ChampionPriority.Pocket, 3),
            ("Top", "Malphite", ChampionPriority.Pocket, 3),
            ("Jungle", "LeeSin", ChampionPriority.Main, 4), ("Jungle", "Vi", ChampionPriority.Secondary, 3), ("Jungle", "Amumu", ChampionPriority.Learning, 2),
            ("Middle", "Ahri", ChampionPriority.Main, 5), ("Middle", "Orianna", ChampionPriority.Secondary, 4), ("Middle", "Syndra", ChampionPriority.Learning, 2),
            ("Middle", "Malphite", ChampionPriority.Pocket, 3),
            ("Bottom", "Jinx", ChampionPriority.Main, 4), ("Bottom", "Caitlyn", ChampionPriority.Secondary, 3),
            ("Support", "Thresh", ChampionPriority.Main, 5), ("Support", "Lulu", ChampionPriority.Secondary, 4), ("Support", "Nautilus", ChampionPriority.Pocket, 3),
        };
        foreach (var p in placements) await Repo.AddEntryAsync(Group(p.Group), p.Champion, p.Proficiency, p.Priority);

        // A complete Ahri build.
        var runes = new RunePageData();
        var domination = Game.RuneTrees.First(t => t.Key == "Domination");
        var sorcery = Game.RuneTrees.First(t => t.Key == "Sorcery");
        RunePageRules.SelectPrimaryTree(runes, domination.Id);
        RuneInfo Pick(RuneTreeInfo tree, int row, string key) => tree.Rows[row].FirstOrDefault(r => r.Key.Contains(key, StringComparison.OrdinalIgnoreCase)) ?? tree.Rows[row][0];
        RunePageRules.SelectPrimaryRune(runes, Game, Pick(domination, 0, "Electrocute").Id);
        RunePageRules.SelectPrimaryRune(runes, Game, Pick(domination, 1, "TasteOfBlood").Id);
        RunePageRules.SelectPrimaryRune(runes, Game, Pick(domination, 2, "Grisly").Id);
        RunePageRules.SelectPrimaryRune(runes, Game, Pick(domination, 3, "Ultimate").Id);
        RunePageRules.SelectSubTree(runes, sorcery.Id);
        RunePageRules.SelectSubRune(runes, Game, Pick(sorcery, 1, "Manaflow").Id);
        RunePageRules.SelectSubRune(runes, Game, Pick(sorcery, 3, "Scorch").Id);
        for (var i = 0; i < Game.ShardRows.Count; i++)
            RunePageRules.SelectShard(runes, Game, i, Game.ShardRows[i].Options[i == 0 ? 0 : i == 1 ? 0 : 0].Id);

        string Item(string name) => Game.ShopItems.FirstOrDefault(i => i.Name == name)?.Id ?? Game.ShopItems[0].Id;
        var build = new Build
        {
            ChampionId = "Ahri",
            Name = "Standard",
            Role = Position.Middle,
            GameVersion = Game.Version,
            Runes = runes,
            Items = new ItemSetData
            {
                Blocks =
                [
                    new ItemBlockData { Name = "Starting Items", Items = [Item("Doran's Ring"), Item("Health Potion"), Item("Health Potion")] },
                    new ItemBlockData { Name = "Core Build", Items = [Item("Luden's Companion"), Item("Sorcerer's Shoes"), Item("Shadowflame"), Item("Rabadon's Deathcap"), Item("Zhonya's Hourglass")] },
                    new ItemBlockData { Name = "Situational", Items = [Item("Banshee's Veil"), Item("Void Staff"), Item("Morellonomicon")] },
                ],
            },
            SummonerSpells = ["SummonerFlash", "SummonerDot"],
            SkillOrder = "QWEQQRQWQWRWWEEREE",
            Notes = "Level 2 all-in with E → Q when they walk up for a minion.",
        };
        await Repo.SaveBuildAsync(build);
        // Saved on an older patch: items that changed since are flagged in the item editor.
        await Repo.SaveBuildAsync(new Build
        {
            ChampionId = "Ahri", Name = "vs Assassins", Role = Position.Middle, GameVersion = "16.10.1", Runes = runes.Clone(),
            SummonerSpells = ["SummonerFlash", "SummonerBarrier"],
            Items = new ItemSetData
            {
                Blocks =
                [
                    new ItemBlockData { Name = "Starting Items", Items = [Item("Doran's Ring"), Item("Health Potion")] },
                    new ItemBlockData { Name = "Core Build", Items = [Item("Luden's Companion"), Item("Zhonya's Hourglass"), Item("Rabadon's Deathcap"), Item("Shadowflame"), Item("Banshee's Veil")] },
                ],
            },
        });

        await Repo.SetMatchupAsync("Ahri", "Veigar", MatchupKind.GoodAgainst, "Charm through the cage edge; he can't trade back early.");
        await Repo.SetMatchupAsync("Ahri", "Xerath", MatchupKind.GoodAgainst, "Dash in after he uses E.");
        await Repo.SetMatchupAsync("Ahri", "Lux", MatchupKind.GoodAgainst, "");
        await Repo.SetMatchupAsync("Ahri", "Fizz", MatchupKind.WeakAgainst, "Hold R for his E. Rush Zhonya's.");
        await Repo.SetMatchupAsync("Ahri", "Kassadin", MatchupKind.WeakAgainst, "Push and roam before 6.");
        await Repo.SetMatchupAsync("Darius", "Garen", MatchupKind.GoodAgainst, "Win short trades; keep 5 stacks.");
        await Repo.SetMatchupAsync("Darius", "Vayne", MatchupKind.WeakAgainst, "Ghost + flash or don't fight.");
        await Repo.SetMatchupAsync("Sett", "Garen", MatchupKind.GoodAgainst, "");
        await Repo.SetMatchupAsync("Malphite", "Vayne", MatchupKind.GoodAgainst, "Armor stack, ult onto her.");
        await Repo.SetMatchupAsync("Malphite", "Garen", MatchupKind.GoodAgainst, "");
        await Repo.SetMatchupAsync("Malphite", "Garen", MatchupKind.WeakAgainst, "Top only: he out-sustains you before Sunfire.", Position.Top);
        await Repo.SetMatchupAsync("Veigar", "Syndra", MatchupKind.GoodAgainst, "");
        await Repo.SetMatchupAsync("Thresh", "Vayne", MatchupKind.GoodAgainst, "Hook on her Q dash end.");
        await Repo.SetMatchupAsync("Jinx", "Vayne", MatchupKind.WeakAgainst, "");
        await Repo.SaveProfileAsync("Ahri", "Look for picks with charm from fog. Hold R to dodge key abilities.", ["Pick", "Roam", "Blind pick"]);
    }

    // ── Pages ──────────────────────────────────────────────────────────────────────────────────────


    [AvaloniaTest, Order(1)]
    public void Rosters_page()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        var groups = Vm.Rosters!.Groups;
        Assert.That(groups.Select(g => g.Name), Is.EqualTo(new[] { "Top", "Jungle", "Middle", "Bottom", "Support" }));
        Assert.That(groups.First(g => g.Name == "Middle").Entries.Select(e => e.ChampionId), Does.Contain("Ahri"));
        Capture("01-rosters-1600x960");
    }

    [AvaloniaTest, Order(2)]
    public void Rosters_page_full_hd()
    {
        Window(1920, 1080);
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        Capture("01b-rosters-1920x1080");
        Window();
    }

    [AvaloniaTest, Order(3)]
    public void Dragging_a_champion_from_the_pool_adds_it_to_a_group()
    {
        var window = Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        UiTestHost.Settle();

        var tile = window.GetVisualDescendants().OfType<Border>().First(b => Dnd.GetPayload(b) is ChampionDragData d && d.Champion.Id == "Akali" && d.Origin is null);
        var middle = window.GetVisualDescendants().OfType<Border>().First(b => Dnd.GetTarget(b) is RosterGroupViewModel g && g.Name == "Middle");
        var start = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2), window)!.Value;
        var end = middle.TranslatePoint(new Point(middle.Bounds.Width / 2, middle.Bounds.Height - 60), window)!.Value;

        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        try
        {
            for (var i = 1; i <= 12; i++)
            {
                window.MouseMove(new Point(start.X + (end.X - start.X) * i / 12, start.Y + (end.Y - start.Y) * i / 12), RawInputModifiers.LeftMouseButton);
                UiTestHost.Settle(1);
            }
            Assert.That(Dnd.IsDragging, Is.True, "drag should have started: " + string.Join(" | ", Dnd.Diagnostics));
            Capture("02-drag-in-progress");
        }
        finally
        {
            window.MouseUp(end, MouseButton.Left);
            UiTestHost.Settle(20);
        }

        var group = Vm.Rosters!.Groups.First(g => g.Name == "Middle");
        Assert.That(group.Entries.Select(e => e.ChampionId), Does.Contain("Akali"));
        var stored = UiTestHost.Run(() => Repo.GetGroupsAsync());
        Assert.That(stored.First(g => g.Name == "Middle").Entries.Select(e => e.ChampionId), Does.Contain("Akali"));
    }

    [AvaloniaTest, Order(4)]
    public void Champion_page_runes()
    {
        Window();
        Vm.OpenChampion("Ahri");
        UiTestHost.Settle(30);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        Assert.That(page.Builds, Has.Count.EqualTo(2));
        Assert.That(page.SelectedBuild!.Runes.IsComplete, Is.True);
        Capture("03-champion-runes");
    }

    [AvaloniaTest, Order(5)]
    public void Champion_page_items()
    {
        Window();
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        page.SelectedBuild!.EditorTab = 1;
        Assert.That(page.SelectedBuild.Items.ItemCount, Is.EqualTo(11));
        Capture("04-champion-items");
    }

    [AvaloniaTest, Order(5)]
    public void Build_from_an_older_patch_flags_changed_items()
    {
        Window();
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        var old = page.Builds.First(b => b.Name == "vs Assassins");
        Assert.That(old.IsFromOlderPatch, Is.True);
        page.SelectedBuild = old;
        old.EditorTab = 1;
        var items = old.Items;
        for (var i = 0; i < 200 && !items.HasPatchChanges; i++) UiTestHost.Settle(1);
        TestContext.Out.WriteLine(items.PatchChangeSummary);
        Assert.That(items.HasPatchChanges, Is.True, "some of these items changed between 16.10 and the current patch");
        Capture("04b-items-changed-since-patch");
        page.SelectedBuild = page.Builds[0];
    }

    [AvaloniaTest, Order(6)]
    public void Champion_page_spells_and_skills()
    {
        Window();
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        page.SelectedBuild!.EditorTab = 2;
        UiTestHost.Settle(20);
        Assert.That(page.SelectedBuild.SpellsSkills.MaxOrderText, Does.StartWith("Q"));
        Capture("05-champion-spells-skills");
    }

    [AvaloniaTest, Order(7)]
    public void Champion_page_matchups()
    {
        Window();
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        page.SelectedTab = 1;
        UiTestHost.Settle(10);
        Assert.That(page.Matchups.Good.Items, Has.Count.EqualTo(3));
        Assert.That(page.Matchups.Weak.Items, Has.Count.EqualTo(2));
        Capture("06-champion-matchups");
    }

    [AvaloniaTest, Order(8)]
    public void Champion_page_notes()
    {
        Window();
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        page.SelectedTab = 2;
        Assert.That(page.UserTags, Has.Count.EqualTo(3));
        Capture("07-champion-notes");
    }

    [AvaloniaTest, Order(12)]
    public void Full_screen_toggles_with_F11_and_exits_with_Escape()
    {
        var window = Window();
        window.WindowState = WindowState.Maximized;
        UiTestHost.Settle(3);
        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
        UiTestHost.Settle(3);
        Assert.That(window.WindowState, Is.EqualTo(WindowState.FullScreen));
        Assert.That(window.FindControl<Button>("ExitFullScreenButton")!.IsVisible, Is.True, "a visible way out of full screen");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        UiTestHost.Settle(3);
        Assert.That(window.WindowState, Is.EqualTo(WindowState.Maximized), "restores the previous state");
        Assert.That(window.FindControl<Button>("ExitFullScreenButton")!.IsVisible, Is.False);
        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
        UiTestHost.Settle(3);
        Assert.That(window.WindowState, Is.EqualTo(WindowState.Maximized), "F11 toggles back");
    }

    [AvaloniaTest, Order(13)]
    public void Matchups_per_role()
    {
        Window();
        Vm.OpenChampion("Malphite", ChampionTab.Matchups);
        UiTestHost.Settle(30);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        var matchups = page.Matchups;
        Assert.That(matchups.Good.Items.Select(i => i.OpponentId), Does.Contain("Garen"), "all-roles view lists every matchup");
        matchups.SelectRoleCommand.Execute(matchups.RoleOptions.First(o => Equals(o.Value, Position.Top)));
        UiTestHost.Settle(10);
        Assert.That(matchups.Good.Items.Select(i => i.OpponentId), Does.Not.Contain("Garen"), "Top-only matchup replaces it in Top");
        Assert.That(matchups.Weak.Items.Single(i => i.OpponentId == "Garen").Role, Is.EqualTo(Position.Top));
        Capture("06b-matchups-role-top");
    }

    [AvaloniaTest, Order(14)]
    public void Item_tooltip_shows_build_path()
    {
        RosterControl.Desktop.App.InitializeServices(UiTestHost.Services);
        Window();
        var ie = Game.ShopItems.First(i => i.Name == "Infinity Edge");
        var images = UiTestHost.Services.GetRequiredService<AssetImageService>();
        var details = new ItemDetailsViewModel(Game, ie);
        UiTestHost.Run(() => images.WarmAsync(ie.From.Concat(ie.Into).Append(ie.Id).Concat(Game.Items.Values.SelectMany(i => i.From)).Distinct()
            .Select(id => AssetUrls.Item(Game.Version, id)), 96, TimeSpan.FromSeconds(60)), 90);
        Assert.That(details.HasRecipe, Is.True);
        var window = new Window
        {
            Width = 460, Height = 520, Background = Avalonia.Media.Brushes.Transparent,
            Content = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F0101823")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#785A28")),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10),
                Margin = new Thickness(10), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Child = new ItemTooltipView { DataContext = details },
            },
        };
        window.Show();
        for (var i = 0; i < 40; i++) UiTestHost.Settle(2);
        window.CaptureRenderedFrame()?.Save(Path.Combine(UiTestHost.ScreenshotDirectory, "04c-item-tooltip-build-path.png"));
        window.Close();
    }

    [AvaloniaTest, Order(9)]
    public void Counter_picks()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Counters));
        var counters = Vm.Counters!;
        foreach (var id in new[] { "Garen", "Vayne", "Veigar" }) counters.AddEnemyCommand.Execute(counters.Picker.Find(id)!);
        UiTestHost.Settle();
        Assert.That(counters.Strong.Select(s => s.Champion.Id), Does.Contain("Malphite").And.Contain("Ahri"));
        Assert.That(counters.Strong.Single(s => s.Champion.Id == "Malphite").Candidate.Role, Is.EqualTo(Position.Middle));
        Assert.That(counters.Risky.Select(s => (s.Champion.Id, s.Candidate.Role)), Does.Contain(("Malphite", (Position?)Position.Top)), "Top-only matchup applies to his Top placement");
        Assert.That(counters.Risky.Select(s => s.Champion.Id), Does.Contain("Jinx"));
        Assert.That(counters.Bans.First().Champion.Id, Is.EqualTo("Vayne"), "counters two Mains");
        Capture("08-counter-picks");
    }

    [AvaloniaTest, Order(10)]
    public void Library()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Champions));
        Capture("09-champions-library");
    }

    [AvaloniaTest, Order(11)]
    public void Settings()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Settings));
        Capture("10-settings");
    }

    [AvaloniaTest, Order(12)]
    public void Small_window()
    {
        Window(1280, 760);
        Vm.OpenChampion("Ahri");
        UiTestHost.Settle(30);
        Capture("11-champion-1280x760");
        Window();
    }
}
