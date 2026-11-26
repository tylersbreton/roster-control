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

    private static string CaptureWindow(Window window, string name)
    {
        WaitForImages();
        var frame = window.CaptureRenderedFrame();
        var path = Path.Combine(UiTestHost.ScreenshotDirectory, name + ".png");
        frame?.Save(path);
        TestContext.Out.WriteLine(path);
        return path;
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
            Assert.That(tile.IsSet(InputElement.CursorProperty) && window.IsSet(InputElement.CursorProperty), Is.True, "the cursor is hidden while dragging");
            Capture("02-drag-in-progress");
        }
        finally
        {
            window.MouseUp(end, MouseButton.Left);
            UiTestHost.Settle(20);
        }

        Assert.That(window.IsSet(InputElement.CursorProperty), Is.False, "the cursor is restored after the drop");
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
        var core = page.SelectedBuild.Items.Blocks.Single(b => b.IsCore);
        Assert.That(core.Name, Is.EqualTo("Core Build"), "the core block carries the totals");
        Assert.That(core.CoreStats.Select(st => st.Name), Does.Contain("Ability Power"));
        Assert.That(core.CoreTotals!.Gold, Is.EqualTo(core.Items.Sum(i => i.Item!.TotalGold)));
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

    // ── Quality of life ────────────────────────────────────────────────────────────────────────────

    [AvaloniaTest, Order(15)]
    public void Command_palette_opens_with_ctrl_k_and_enter_opens_the_champion()
    {
        var window = Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
        UiTestHost.Settle(3);
        Assert.That(Vm.Palette.IsOpen, Is.True);
        Vm.Palette.Query = "thre";
        UiTestHost.Settle(3);
        Assert.That(Vm.Palette.Results.First().Title, Is.EqualTo("Thresh"));
        Capture("12-command-palette");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiTestHost.Settle(30);
        Assert.That(Vm.Palette.IsOpen, Is.False);
        Assert.That((Vm.CurrentPage as ChampionDetailViewModel)?.Champion.Id, Is.EqualTo("Thresh"));
        window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
        UiTestHost.Settle(3);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        UiTestHost.Settle(3);
        Assert.That(Vm.Palette.IsOpen, Is.False, "Esc closes the palette");
    }

    [AvaloniaTest, Order(16)]
    public void Champion_opened_from_a_group_steps_through_the_group()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        var middle = Vm.Rosters!.Groups.First(g => g.Name == "Middle");
        var ids = middle.Entries.Select(e => e.ChampionId).ToList();
        middle.Entries[0].OpenCommand.Execute(null);
        UiTestHost.Settle(30);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        Assert.That(page.HasSequence, Is.True);
        Assert.That(page.SequenceText, Does.StartWith($"1 / {ids.Count}").And.Contain("Middle"));
        Capture("13-champion-in-sequence");
        Assert.That(Vm.HandleShortcut(Key.Right, KeyModifiers.Control, typing: false), Is.True);
        UiTestHost.Settle(30);
        Assert.That(((ChampionDetailViewModel)Vm.CurrentPage!).Champion.Id, Is.EqualTo(ids[1]));
        ((ChampionDetailViewModel)Vm.CurrentPage!).PreviousCommand.Execute(null);
        UiTestHost.Settle(30);
        Assert.That(((ChampionDetailViewModel)Vm.CurrentPage!).Champion.Id, Is.EqualTo(ids[0]));
    }

    [AvaloniaTest, Order(17)]
    public void Hovered_card_keys_rate_and_remove_and_ctrl_z_undoes()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        var rosters = Vm.Rosters!;
        Assert.That(Vm.HandleShortcut(Key.D2, KeyModifiers.None, typing: false), Is.False, "no hovered card: digits do nothing");
        var support = rosters.Groups.First(g => g.Name == "Support");
        var lulu = support.Entries.First(e => e.ChampionId == "Lulu");
        rosters.HoveredEntry = lulu;
        Assert.That(Vm.HandleShortcut(Key.D2, KeyModifiers.None, typing: false), Is.True);
        Assert.That(lulu.Proficiency, Is.EqualTo(2));
        Assert.That(Vm.HandleShortcut(Key.D2, KeyModifiers.None, typing: true), Is.False, "ignored while typing");

        rosters.HoveredEntry = lulu;
        Vm.HandleShortcut(Key.Delete, KeyModifiers.None, typing: false);
        UiTestHost.Settle(10);
        Assert.That(support.Entries.Select(e => e.ChampionId), Does.Not.Contain("Lulu"));
        Assert.That(Vm.Toasts.Any(t => t.ActionText == "Undo"), Is.True, "removal offers Undo");

        Assert.That(Vm.HandleShortcut(Key.Z, KeyModifiers.Control, typing: false), Is.True);
        UiTestHost.Settle(20);
        support = Vm.Rosters!.Groups.First(g => g.Name == "Support");
        Assert.That(support.Entries.Select(e => e.ChampionId), Does.Contain("Lulu"), "Ctrl+Z restores the card");
        var stored = UiTestHost.Run(() => Repo.GetGroupsAsync());
        Assert.That(stored.First(g => g.Name == "Support").Entries.Select(e => e.ChampionId), Does.Contain("Lulu"));
    }

    [AvaloniaTest, Order(18)]
    public void Right_click_add_and_compact_cards_and_empty_filters()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        var rosters = Vm.Rosters!;
        var top = rosters.Groups.First(g => g.Name == "Top");
        rosters.AddToGroup(Game.Champion("Ornn")!, top);
        UiTestHost.Settle(10);
        Assert.That(top.Entries.Select(e => e.ChampionId), Does.Contain("Ornn"));

        rosters.Compact = true;
        UiTestHost.Settle(5);
        Capture("01c-rosters-compact");
        rosters.Compact = false;

        rosters.Pool.Search = "zzzz";
        UiTestHost.Settle(3);
        Assert.That(rosters.Pool.IsEmpty, Is.True);
        rosters.Pool.ClearFiltersCommand.Execute(null);
        UiTestHost.Settle(3);
        Assert.That(rosters.Pool.IsEmpty, Is.False);
        Assert.That(rosters.Pool.Search, Is.Empty);
    }

    [AvaloniaTest, Order(19)]
    public void Zoom_scales_the_whole_window()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        Assert.That(Vm.HandleShortcut(Key.OemPlus, KeyModifiers.Control, typing: false), Is.True);
        Assert.That(Vm.UiScale, Is.EqualTo(1.1).Within(0.001));
        Vm.UiScale = 1.25;
        UiTestHost.Settle(5);
        Capture("01d-rosters-zoom-125");
        Vm.HandleShortcut(Key.D0, KeyModifiers.Control, typing: false);
        Assert.That(Vm.UiScale, Is.EqualTo(1.0));
    }

    [AvaloniaTest, Order(20)]
    public void Build_can_be_copied_to_another_champion()
    {
        Window();
        Vm.OpenChampion("Ahri");
        UiTestHost.Settle(30);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        var source = page.SelectedBuild!;
        page.CopyQuery = "lux";
        Assert.That(page.CopyTargets.First().Champion.Id, Is.EqualTo("Lux"));
        UiTestHost.Run(() => page.CopyBuildToCommand.ExecuteAsync(page.CopyTargets.First()));
        var copies = UiTestHost.Run(() => Repo.GetBuildsAsync("Lux"));
        var copy = copies.Single(b => b.Name == source.Name);
        Assert.That(copy.SkillOrder, Is.Empty, "skill order is champion-specific");
        Assert.That(copy.Runes.PrimaryPerks, Is.EqualTo(source.Model.Runes.PrimaryPerks));
        Assert.That(Vm.UndoLast(), Is.False, "the Open action of the copy toast is not an undo");
    }

    // ── Builds, notes and search ───────────────────────────────────────────────────────────────────

    [AvaloniaTest, Order(21)]
    public void Notes_show_formatting_and_checkboxes_tick_without_editing()
    {
        var window = Window();
        Vm.OpenChampion("Ahri", ChampionTab.Notes);
        UiTestHost.Settle(30);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        page.Notes = "**Laning**\n- Look for picks with charm from fog\n- Hold R to dodge key abilities\n  - Zed R, Malzahar R\n\n**Checklist**\n[x] Buy control ward\n[ ] Ward river at 2:45\n\nRoam bot after first back.";
        UiTestHost.Settle(10);
        var editor = window.GetVisualDescendants().OfType<NotesEditor>().First(e => e.IsEffectivelyVisible);
        Assert.That(editor.IsEditing, Is.False, "shows the formatted view");
        Capture("07b-notes-formatted");

        var unticked = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Tag, "notecheck") && Equals(ToolTip.GetTip(b), "Mark as done"));
        var point = unticked.TranslatePoint(new Point(unticked.Bounds.Width / 2, unticked.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        UiTestHost.Settle(5);
        Assert.That(page.Notes, Does.Contain("[x] Ward river at 2:45"));
        Assert.That(editor.IsEditing, Is.False, "ticking doesn't open the editor");
    }

    [AvaloniaTest, Order(22)]
    public void Default_build_opens_first_and_builds_reorder_by_dragging()
    {
        Window();
        Vm.OpenChampion("Ahri", ChampionTab.Builds);
        UiTestHost.Settle(30);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        var assassins = page.Builds.First(b => b.Name == "vs Assassins");
        Assert.That(page.SelectedBuild!.Name, Is.EqualTo("Standard"), "no default yet: the first build");
        UiTestHost.Run(() => page.ToggleDefaultCommand.ExecuteAsync(assassins));
        Assert.That(assassins.IsDefault, Is.True);

        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        Vm.Rosters!.Groups.First(g => g.Name == "Middle").Entries.First(e => e.ChampionId == "Ahri").OpenCommand.Execute(null);
        UiTestHost.Settle(30);
        page = (ChampionDetailViewModel)Vm.CurrentPage!;
        Assert.That(page.SelectedBuild!.Name, Is.EqualTo("vs Assassins"), "the default build opens first");
        page.SelectedTab = (int)ChampionTab.Builds;
        Capture("03b-default-build");

        var first = page.Builds[0];
        page.BuildDrop.Drop(page.Builds[1].DragData, first, after: false, copy: false);
        UiTestHost.Settle(10);
        Assert.That(page.Builds.Select(b => b.Name), Is.EqualTo(new[] { "vs Assassins", "Standard" }));
        var stored = UiTestHost.Run(() => Repo.GetBuildsAsync("Ahri"));
        Assert.That(stored.Select(b => b.Name), Is.EqualTo(new[] { "vs Assassins", "Standard" }), "order saved");
        Assert.That(stored.Single(b => b.IsDefault).Name, Is.EqualTo("vs Assassins"));
    }

    [AvaloniaTest, Order(23)]
    public void Share_code_copies_and_pastes_a_build()
    {
        Window();
        Vm.OpenChampion("Ahri", ChampionTab.Builds);
        UiTestHost.Settle(30);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        var source = page.Builds.First(b => b.Name == "Standard");
        UiTestHost.Run(() => page.CopyShareCodeCommand.ExecuteAsync(source));
        UiTestHost.Settle(3);
        Assert.That(Vm.Toasts.Select(t => t.Message), Has.Some.StartsWith("Build code for \"Standard\" copied"));
        var count = page.Builds.Count;
        UiTestHost.Run(() => page.PasteShareCodeCommand.ExecuteAsync(null));
        UiTestHost.Settle(5);
        Assert.That(page.Builds, Has.Count.EqualTo(count + 1));
        var pasted = page.Builds.Last();
        Assert.That(pasted.Name, Is.EqualTo("Standard"));
        Assert.That(pasted.Model.Runes.PrimaryPerks, Is.EqualTo(source.Model.Runes.PrimaryPerks));
        Assert.That(pasted.Model.Items.Blocks.SelectMany(b => b.Items), Is.EqualTo(source.Model.Items.Blocks.SelectMany(b => b.Items)));
        Assert.That(pasted.IsDefault, Is.False);
        Assert.That(page.SelectedBuild, Is.SameAs(pasted));
    }

    [AvaloniaTest, Order(24)]
    public void Quick_search_finds_text_in_notes()
    {
        var window = Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        Vm.Palette.Open();
        Vm.Palette.Query = "charm";
        for (var i = 0; i < 100 && !Vm.Palette.Results.Any(r => r.Subtitle.Contains("notes")); i++) UiTestHost.Settle(2);
        var hit = Vm.Palette.Results.FirstOrDefault(r => r.Subtitle == "Ahri · champion notes");
        Assert.That(hit, Is.Not.Null, string.Join(" | ", Vm.Palette.Results.Select(r => r.Title + " / " + r.Subtitle)));
        Assert.That(hit!.Title, Does.Contain("charm"));
        Capture("12b-search-notes");
        Vm.Palette.ExecuteCommand.Execute(hit);
        UiTestHost.Settle(30);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        Assert.That(page.Champion.Id, Is.EqualTo("Ahri"));
        Assert.That(page.IsNotesTab, Is.True);
    }

    // ── 1.3: rosters, selection, sorting, image, compare, what's new, diagnostics ─────────────────

    private static RostersView RostersView(Window window) => window.GetVisualDescendants().OfType<RostersView>().First(v => v.IsEffectivelyVisible);

    [AvaloniaTest, Order(25)]
    public void Rosters_can_be_created_and_switched()
    {
        var window = Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        var rosters = Vm.Rosters!;
        Assert.That(rosters.Rosters.Select(r => r.Name), Is.EqualTo(new[] { "My roster" }));
        var original = rosters.Groups.Select(g => g.Name).ToList();

        rosters.NewRosterName = "ARAM";
        UiTestHost.Run(() => rosters.NewRosterCommand.ExecuteAsync("Empty"));
        UiTestHost.Settle(10);
        Assert.That(rosters.RosterName, Is.EqualTo("ARAM"));
        Assert.That(rosters.Groups, Is.Empty);
        Assert.That(rosters.Rosters.Single(r => r.IsActive).Name, Is.EqualTo("ARAM"));

        var switcher = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "RosterSwitcher");
        switcher.Flyout!.ShowAt(switcher);
        UiTestHost.Settle(5);
        Capture("01e-roster-switcher");
        switcher.Flyout.Hide();

        UiTestHost.Run(() => rosters.SwitchRosterCommand.ExecuteAsync(rosters.Rosters.First(r => r.Name == "My roster")));
        UiTestHost.Settle(10);
        Assert.That(rosters.Groups.Select(g => g.Name), Is.EqualTo(original), "back to the first roster");
        Assert.That(UiTestHost.Run(() => Repo.GetRostersAsync()), Has.Count.EqualTo(2));

        // Delete the empty one again (without the confirmation dialog: straight through the repository).
        var aram = rosters.Rosters.First(r => r.Name == "ARAM");
        Assert.That(UiTestHost.Run(() => Repo.DeleteRosterAsync(aram.Id)), Is.True);
        UiTestHost.Run(() => rosters.ReloadAsync());
        Assert.That(rosters.Rosters, Has.Count.EqualTo(1));
    }

    [AvaloniaTest, Order(26)]
    public void Ctrl_click_selects_cards_and_the_selection_moves_together()
    {
        var window = Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        UiTestHost.Settle(5);
        var rosters = Vm.Rosters!;
        rosters.ClearSelection();
        var cards = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("rostercard") && b.DataContext is RosterEntryViewModel { Group.Name: "Support" } && b.IsEffectivelyVisible)
            .ToList();
        Assert.That(cards, Has.Count.GreaterThanOrEqualTo(2));
        foreach (var card in cards.Take(2))
        {
            var point = card.TranslatePoint(new Point(card.Bounds.Width / 2, 12), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
            UiTestHost.Settle(2);
        }
        Assert.That(rosters.SelectionCount, Is.EqualTo(2), "Ctrl+click selects instead of opening");
        Assert.That(Vm.CurrentSection, Is.EqualTo(Section.Rosters));
        Capture("01f-multi-select");

        var picked = rosters.SelectedEntries.Select(e => e.ChampionId).ToList();
        var bottom = rosters.Groups.First(g => g.Name == "Bottom");
        UiTestHost.Run(() => rosters.CopySelectionCommand.ExecuteAsync(bottom));
        UiTestHost.Settle(10);
        Assert.That(bottom.Entries.Select(e => e.ChampionId), Is.SupersetOf(picked), "copied together");
        Assert.That(rosters.Groups.First(g => g.Name == "Support").Entries.Select(e => e.ChampionId), Is.SupersetOf(picked), "copy keeps the originals");
        Assert.That(rosters.HasSelection, Is.False);

        // Select the copies and remove them in one go (Delete key), then undo.
        foreach (var e in bottom.Entries.Where(e => picked.Contains(e.ChampionId))) e.IsSelected = true;
        rosters.UpdateSelection();
        Assert.That(Vm.HandleShortcut(Key.Delete, KeyModifiers.None, typing: false), Is.True);
        UiTestHost.Settle(10);
        Assert.That(bottom.Entries.Select(e => e.ChampionId), Has.None.AnyOf(picked.ToArray()));
        Assert.That(Vm.HandleShortcut(Key.Z, KeyModifiers.Control, typing: false), Is.True);
        UiTestHost.Settle(20);
        Assert.That(Vm.Rosters!.Groups.First(g => g.Name == "Bottom").Entries.Select(e => e.ChampionId), Is.SupersetOf(picked), "undo restores all of them");
        // Leave Bottom as it was for later tests.
        foreach (var e in Vm.Rosters!.Groups.First(g => g.Name == "Bottom").Entries.Where(e => picked.Contains(e.ChampionId))) e.IsSelected = true;
        Vm.Rosters!.UpdateSelection();
        UiTestHost.Run(() => Vm.Rosters!.RemoveSelectionCommand.ExecuteAsync(null));
        UiTestHost.Settle(5);
    }

    [AvaloniaTest, Order(27)]
    public void Pool_sorts_by_newest_and_the_roster_saves_as_an_image()
    {
        var window = Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        var pool = Vm.Rosters!.Pool;
        pool.SetSortCommand.Execute("Newest");
        Assert.That(pool.Tiles[0].Champion.Key, Is.EqualTo(Game.Champions.Max(c => c.Key)));
        pool.SetSortCommand.Execute("Class");
        Assert.That(pool.Tiles[0].Champion.Tags.First(), Is.EqualTo("Assassin"));
        pool.SetSortCommand.Execute("A–Z");
        Assert.That(pool.Tiles[0].Name, Is.EqualTo("Aatrox"));

        using var bitmap = UiTestHost.Run(() => RostersView(window).RenderImageAsync(), 30);
        Assert.That(bitmap.PixelSize.Width, Is.GreaterThan(1000));
        bitmap.Save(Path.Combine(UiTestHost.ScreenshotDirectory, "14-roster-image.png"));
    }

    [AvaloniaTest, Order(28)]
    public void Compare_shows_two_champions_and_their_head_to_head()
    {
        Window();
        Vm.OpenCompare("Ahri", "Veigar");
        UiTestHost.Settle(40);
        var page = (CompareViewModel)Vm.CurrentPage!;
        Assert.That(page.Left.Champion!.Id, Is.EqualTo("Ahri"));
        Assert.That(page.Right.Champion!.Id, Is.EqualTo("Veigar"));
        Assert.That(page.Left.HasBuild, Is.True);
        Assert.That(page.Left.Blocks, Is.Not.Empty);
        Assert.That(page.HeadToHead.Select(h => h.Text), Has.Some.StartsWith("Ahri is good against Veigar"));
        Capture("15-compare");
        UiTestHost.Run(() => page.SwapCommand.ExecuteAsync(null));
        Assert.That(page.Left.Champion!.Id, Is.EqualTo("Veigar"));
    }

    [AvaloniaTest, Order(29)]
    public void Whats_new_diagnostics_saved_indicator_and_reduce_motion()
    {
        var window = Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Settings));
        Vm.ShowWhatsNew();
        UiTestHost.Settle(5);
        Assert.That(Vm.WhatsNew, Is.Not.Null);
        Assert.That(Vm.WhatsNew!.Entries.First().Version, Is.EqualTo("1.3.0"));
        Capture("16-whats-new");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        UiTestHost.Settle(3);
        Assert.That(Vm.WhatsNew, Is.Null, "Esc closes it");

        var diagnostics = UiTestHost.Run(() => Vm.Settings!.BuildDiagnosticsAsync());
        Assert.That(diagnostics, Does.StartWith("Roster Control diagnostics"));
        Assert.That(diagnostics, Does.Contain("Version:").And.Contain("roster(s)"));
        Assert.That(diagnostics, Does.Not.Contain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "the Windows user name is hidden");

        UiTestHost.Run(() => Repo.SaveProfileAsync("Thresh", "Hook from fog.", []));
        UiTestHost.Settle(3);
        Assert.That(Vm.ShowSaved, Is.True, "Saved indicator after a write");

        Vm.ReduceMotion = true;
        UiTestHost.Settle(2);
        Assert.That(window.Classes, Does.Contain("reducedmotion"));
        Vm.ReduceMotion = false;
        Assert.That(window.Classes, Does.Not.Contain("reducedmotion"));
    }

    // ── Hover preview, patch review, edge scrolling ───────────────────────────────────────────────

    [AvaloniaTest, Order(30)]
    public void Hovering_a_roster_card_previews_its_default_build()
    {
        var window = Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        UiTestHost.Settle(5);
        var ahri = Vm.Rosters!.Groups.First(g => g.Name == "Middle").Entries.First(e => e.ChampionId == "Ahri");
        UiTestHost.Run(() => ahri.LoadPreviewAsync());
        var preview = ahri.Preview!;
        Assert.That(preview.HasBuild, Is.True);
        Assert.That(preview.BuildName, Is.EqualTo("vs Assassins"), "the default build for Middle (set in an earlier test)");
        Assert.That(preview.IsDefault, Is.True);
        Assert.That(preview.Items, Is.Not.Empty);
        Assert.That(preview.Spells, Has.Count.EqualTo(2));

        var amumu = Vm.Rosters!.Groups.First(g => g.Name == "Jungle").Entries.First(e => e.ChampionId == "Amumu");
        UiTestHost.Run(() => amumu.LoadPreviewAsync());
        Assert.That(amumu.Preview!.HasBuild, Is.False);

        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("rostercard") && b.DataContext == ahri);
        ToolTip.SetIsOpen(card, true);
        UiTestHost.Settle(5);
        Capture("01g-card-preview");
        ToolTip.SetIsOpen(card, false);
    }

    [AvaloniaTest, Order(31)]
    public void Builds_changed_by_a_patch_are_listed_for_review()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        UiTestHost.Run(() => Vm.RefreshPatchReviewAsync(), 60);
        var review = Vm.Review;
        var entry = review.Entries.FirstOrDefault(e => e.ChampionName == "Ahri" && e.BuildName == "vs Assassins");
        Assert.That(entry, Is.Not.Null, "saved on 16.10 with items that changed since");
        Assert.That(entry!.Changes, Is.Not.Empty);
        Assert.That(review.ShowBanner, Is.True);
        Capture("01h-patch-review-banner");

        Vm.OpenPatchReview();
        UiTestHost.Settle(30);
        var page = (PatchReviewViewModel)Vm.CurrentPage!;
        Capture("17-patch-review");

        var before = review.Count;
        UiTestHost.Run(() => page.MarkReviewedCommand.ExecuteAsync(entry));
        UiTestHost.Settle(3);
        Assert.That(review.Count, Is.EqualTo(before - 1));
        Assert.That(UiTestHost.Run(() => Repo.GetBuildsAsync("Ahri")).Single(b => b.Name == "vs Assassins").GameVersion, Is.EqualTo(Game.Version));

        Assert.That(Vm.HandleShortcut(Key.Z, KeyModifiers.Control, typing: false), Is.True, "Ctrl+Z undoes it");
        UiTestHost.Settle(30);
        Assert.That(review.Entries.Select(e => e.BuildName), Does.Contain("vs Assassins"));
        Assert.That(UiTestHost.Run(() => Repo.GetBuildsAsync("Ahri")).Single(b => b.Name == "vs Assassins").GameVersion, Is.EqualTo("16.10.1"));
    }

    [AvaloniaTest, Order(32)]
    public void Dragging_past_the_edge_scrolls_the_board()
    {
        Assert.That(Dnd.Speed(200, 400), Is.Zero, "middle: no scrolling");
        Assert.That(Dnd.Speed(40, 400), Is.LessThan(0).And.GreaterThan(-10), "inside the edge zone: slow");
        Assert.That(Dnd.Speed(-200, 400), Is.EqualTo(-38), "far past the edge: fastest");
        Assert.That(Dnd.Speed(399, 400), Is.GreaterThan(Dnd.Speed(360, 400)), "faster nearer the edge");

        var window = Window(1280, 760);
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
        UiTestHost.Settle(10);
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "GroupsScroller");
        Assert.That(scroller.Extent.Width, Is.GreaterThan(scroller.Viewport.Width), "the groups overflow at this size");
        scroller.Offset = new Vector(0, 0);
        var tile = window.GetVisualDescendants().OfType<Border>().First(b => Dnd.GetPayload(b) is ChampionDragData { Origin: null, Champion.Id: "Annie" });
        var start = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2), window)!.Value;
        var outside = new Point(window.Bounds.Width + 60, 400); // past the window's right edge
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        try
        {
            for (var i = 1; i <= 10; i++)
            {
                window.MouseMove(new Point(start.X + (outside.X - start.X) * i / 10, start.Y + (outside.Y - start.Y) * i / 10), RawInputModifiers.LeftMouseButton);
                UiTestHost.Settle(1);
            }
            for (var i = 0; i < 25 && scroller.Offset.X <= 0; i++)
            {
                Thread.Sleep(45);
                UiTestHost.Settle(2);
            }
            Assert.That(scroller.Offset.X, Is.GreaterThan(0), "kept scrolling with the pointer outside the window");
        }
        finally
        {
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); // cancel the drag
            window.MouseUp(outside, MouseButton.Left);
            UiTestHost.Settle(5);
        }
        Assert.That(Vm.Rosters!.Groups.SelectMany(g => g.Entries).Count(e => e.ChampionId == "Annie"), Is.Zero, "cancelled, nothing added");
        Window();
    }

    [AvaloniaTest, Order(33)]
    public void Not_in_roster_filter_shows_unplaced_champions()
    {
        Window();
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Champions));
        var library = Vm.Library!;
        var placed = Vm.Rosters!.Groups.SelectMany(g => g.Entries).Select(e => e.ChampionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.That(placed, Is.Not.Empty);
        try
        {
            library.OnlyRoster = true;
            library.NotInRoster = true;
            Assert.That(library.OnlyRoster, Is.False, "the two roster filters exclude each other");
            UiTestHost.Settle(5);
            var shown = library.Browser.Tiles.Select(t => t.Id).ToList();
            Assert.That(shown, Has.Count.EqualTo(Game.Champions.Count - placed.Count));
            Assert.That(shown.Intersect(placed, StringComparer.OrdinalIgnoreCase), Is.Empty);
            Assert.That(library.Browser.HasActiveFilters, Is.True);
            Capture("33-not-in-roster");
            library.Browser.ClearFiltersCommand.Execute(null);
            Assert.That(library.NotInRoster, Is.False, "Clear filters resets it");
        }
        finally
        {
            library.OnlyRoster = false;
            library.NotInRoster = false;
        }
    }

    [AvaloniaTest, Order(34)]
    public void Abilities_tab_shows_numbers_from_the_game_files()
    {
        Window();
        Vm.OpenChampion("Ahri", ChampionTab.Abilities);
        UiTestHost.Settle(5);
        var page = (ChampionDetailViewModel)Vm.CurrentPage!;
        Assert.That(page.IsAbilitiesTab, Is.True);
        var abilities = page.Abilities;
        for (var i = 0; i < 300 && !abilities.Abilities.Any(a => a.HasValues); i++)
        {
            Thread.Sleep(30);
            UiTestHost.Settle(1);
        }
        Assert.That(abilities.Abilities.Select(a => a.Slot), Is.EqualTo(new[] { 'P', 'Q', 'W', 'E', 'R' }));
        var q = abilities.Abilities.Single(a => a.Slot == 'Q');
        Assert.That(q.Stats.Select(st => st.Label), Is.EqualTo(new[] { "Cooldown", "Cost", "Range" }));
        Assert.That(q.Values.Select(v => v.Text), Has.Some.Contains("% AP"), "damage with its AP ratio");
        Assert.That(abilities.Note, Does.Contain("game files"));
        Capture("34-champion-abilities");

        // The same numbers show in the skill order tooltips.
        page.SelectedTab = (int)ChampionTab.Builds;
        page.SelectedBuild!.EditorTab = 2;
        var spells = page.SelectedBuild.SpellsSkills;
        for (var i = 0; i < 200 && spells.SkillRows[0].Detail?.HasValues != true; i++)
        {
            Thread.Sleep(20);
            UiTestHost.Settle(1);
        }
        Assert.That(spells.SkillRows[0].Detail!.Values, Is.Not.Empty);
        page.SelectedBuild.EditorTab = 0;
        page.SelectedTab = (int)ChampionTab.Builds;
    }

    [AvaloniaTest, Order(35)]
    public void Quick_look_window_shows_the_default_build()
    {
        var window = Window();
        Vm.OpenQuickLook("Ahri", Position.Middle);
        UiTestHost.Settle(10);
        var quick = Vm.QuickLook;
        for (var i = 0; i < 200 && quick.SelectedBuild is null; i++)
        {
            Thread.Sleep(20);
            UiTestHost.Settle(1);
        }
        var ql = window.QuickLookWindow!;
        Assert.That(ql.IsVisible, Is.True);
        Assert.That(ql.Topmost, Is.True, "always on top by default");
        Assert.That(quick.ChampionName, Is.EqualTo("Ahri"));
        var expected = BuildSelection.PickDefault(UiTestHost.Run(() => Repo.GetBuildsAsync("Ahri")), Position.Middle)!;
        Assert.That(quick.SelectedBuild!.Build.Id, Is.EqualTo(expected.Id), "the Mid default, like opening Ahri from the Mid group");
        Assert.That(quick.Builds, Has.Count.GreaterThan(1), "build switcher");

        // Switch to the full build (the seeded default "vs Assassins" has no skill order).
        quick.SelectBuildCommand.Execute(quick.Builds.First(b => b.Build.Name == "Standard"));
        Assert.That(quick.SelectedBuild!.Build.Name, Is.EqualTo("Standard"));
        for (var i = 0; i < 200 && !quick.MaxOrder.Any(m => m.HasAbility); i++)
        {
            Thread.Sleep(20);
            UiTestHost.Settle(1);
        }
        Assert.That(quick.Blocks.Single(b => b.IsCore).Name, Is.EqualTo("Core Build"));
        Assert.That(quick.CoreTotals, Does.Contain("gold"));
        Assert.That(quick.SkillCells, Has.Count.EqualTo(18));
        Assert.That(quick.MaxOrder.First().Slot, Is.EqualTo('Q'));
        Assert.That(quick.MaxOrder.First().Ability!.Values, Is.Not.Empty, "hover shows the ability's numbers");
        UiTestHost.Settle(5);
        CaptureWindow(ql, "35-quick-look");

        quick.Search = "lux";
        Assert.That(quick.Results.First().Name, Is.EqualTo("Lux"));
        UiTestHost.Run(() => quick.PickFirstCommand.ExecuteAsync(null));
        Assert.That(quick.ChampionName, Is.EqualTo("Lux"));
        Assert.That(quick.Builds, Has.Count.EqualTo(UiTestHost.Run(() => Repo.GetBuildsAsync("Lux")).Count));
        Assert.That(quick.Search, Is.Empty);
        ql.Hide();
        Window();
    }

    [AvaloniaTest, Order(36)]
    public void Quick_look_shortcut_can_be_changed()
    {
        Window();
        Assert.That(Vm.QuickLookHotkey, Is.EqualTo(MainWindowViewModel.DefaultQuickLookHotkey));
        Vm.RecordHotkeyCommand.Execute(null);
        Assert.That(Vm.IsRecordingHotkey, Is.True);
        Vm.RecordHotkeyKey(Key.LeftCtrl, KeyModifiers.Control);
        Assert.That(Vm.IsRecordingHotkey, Is.True, "modifiers alone: still waiting");
        Vm.RecordHotkeyKey(Key.Q, KeyModifiers.None);
        Assert.That(Vm.HotkeyStatus, Does.Contain("Ctrl, Alt or Win"), "a shortcut needs a modifier");
        Vm.RecordHotkeyKey(Key.F9, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.That(Vm.IsRecordingHotkey, Is.False);
        Assert.That(Vm.QuickLookHotkey, Is.EqualTo("Ctrl+Alt+F9"));
        Assert.That(Vm.Preferences.Get("quicklook.hotkey", ""), Is.EqualTo("Ctrl+Alt+F9"));
        Vm.RecordHotkeyCommand.Execute(null);
        Vm.RecordHotkeyKey(Key.Escape, KeyModifiers.None);
        Assert.That(Vm.QuickLookHotkey, Is.EqualTo("Ctrl+Alt+F9"), "Esc cancels");
        Vm.ResetHotkeyCommand.Execute(null);
        Assert.That(Vm.QuickLookHotkey, Is.EqualTo("Ctrl+Shift+Space"));

        Assert.That(GlobalHotkey.TryParse("Ctrl+Shift+Space", out var gesture), Is.True);
        Assert.That(GlobalHotkey.Format(gesture), Is.EqualTo("Ctrl+Shift+Space"));
        Assert.That(GlobalHotkey.TryParse("Q", out _), Is.False);
        Assert.That(GlobalHotkey.TryParse("nonsense+", out _), Is.False);
        Assert.That(GlobalHotkey.VirtualKey(Key.A), Is.EqualTo(0x41u));
        Assert.That(GlobalHotkey.VirtualKey(Key.F1), Is.EqualTo(0x70u));
        Assert.That(GlobalHotkey.VirtualKey(Key.D5), Is.EqualTo(0x35u));

        UiTestHost.Run(() => Vm.NavigateAsync(Section.Settings));
        Capture("36-settings-quick-look");
        UiTestHost.Run(() => Vm.NavigateAsync(Section.Rosters));
    }
}
