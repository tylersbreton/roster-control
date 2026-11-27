using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RosterControl.Core.Abstractions;
using RosterControl.Core.Model;
using RosterControl.Data;

namespace RosterControl.Tests;

/// <summary>Runs against a real SQLite file (migrations included) in a temporary folder.</summary>
public class RepositoryTests
{
    private string _root = "";
    private RosterRepository _repo = null!;
    private AppPaths _paths = null!;

    private sealed class Factory(string path) : IDbContextFactory<RosterDbContext>
    {
        public RosterDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<RosterDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options);
    }

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rostercontrol-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _repo = new RosterRepository(new Factory(_paths.DatabasePath), NullLogger<RosterRepository>.Instance);
        await _repo.InitializeAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Test]
    public async Task First_run_creates_one_group_per_role()
    {
        var groups = await _repo.GetGroupsAsync();
        Assert.That(groups.Select(g => g.Name), Is.EqualTo(new[] { "Top", "Jungle", "Middle", "Bottom", "Support" }));
        Assert.That(groups.Select(g => g.Role), Is.EqualTo(PositionInfo.All.Cast<Position?>()));
    }

    [Test]
    public async Task Default_groups_are_not_recreated_after_the_user_deletes_them()
    {
        foreach (var g in await _repo.GetGroupsAsync()) await _repo.DeleteGroupAsync(g.Id);
        var again = new RosterRepository(new Factory(_paths.DatabasePath), NullLogger<RosterRepository>.Instance);
        await again.InitializeAsync();
        Assert.That(await again.GetGroupsAsync(), Is.Empty);
    }

    [Test]
    public async Task Entries_are_unique_per_group_and_can_move_between_groups()
    {
        var groups = await _repo.GetGroupsAsync();
        var top = groups[0];
        var mid = groups[2];
        var darius = await _repo.AddEntryAsync(top.Id, "Darius", 5, ChampionPriority.Main);
        var garen = await _repo.AddEntryAsync(top.Id, "Garen", 3, ChampionPriority.Secondary);
        var duplicate = await _repo.AddEntryAsync(top.Id, "Darius", 1, ChampionPriority.Learning);
        Assert.That(duplicate.Id, Is.EqualTo(darius.Id));

        // Move Garen to Middle (drag & drop) and reorder Top.
        await _repo.SetGroupOrderAsync(mid.Id, [garen.Id]);
        await _repo.SetGroupOrderAsync(top.Id, [darius.Id]);
        groups = await _repo.GetGroupsAsync();
        Assert.That(groups[0].Entries.Select(e => e.ChampionId), Is.EqualTo(new[] { "Darius" }));
        Assert.That(groups[2].Entries.Select(e => e.ChampionId), Is.EqualTo(new[] { "Garen" }));

        await _repo.UpdateEntryAsync(darius.Id, 9, ChampionPriority.Pocket);
        var updated = (await _repo.GetGroupsAsync())[0].Entries.Single();
        Assert.That(updated.Proficiency, Is.EqualTo(5), "clamped to 0-5");
        Assert.That(updated.Priority, Is.EqualTo(ChampionPriority.Pocket));
    }

    [Test]
    public async Task Deleting_a_group_removes_its_entries_but_keeps_builds()
    {
        var top = (await _repo.GetGroupsAsync())[0];
        await _repo.AddEntryAsync(top.Id, "Darius", 5, ChampionPriority.Main);
        await _repo.SaveBuildAsync(new Build { ChampionId = "Darius", Name = "Standard" });
        await _repo.DeleteGroupAsync(top.Id);
        Assert.That((await _repo.GetGroupsAsync()).SelectMany(g => g.Entries), Is.Empty);
        Assert.That(await _repo.GetBuildsAsync("Darius"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Builds_round_trip_runes_items_spells_and_skills()
    {
        var build = new Build
        {
            ChampionId = "Ahri",
            Name = "Standard",
            Role = Position.Middle,
            GameVersion = "16.23.1",
            Runes = new RunePageData { PrimaryStyleId = 8100, SubStyleId = 8200, PrimaryPerks = [8112, 8139, 8140, 8135], SubPerks = [8226, 8237], Shards = [5008, 5008, 5011] },
            Items = new ItemSetData { Blocks = [new ItemBlockData { Name = "Start", Items = ["1056", "2003", "2003"] }] },
            SummonerSpells = ["SummonerFlash", "SummonerDot"],
            SkillOrder = "QWEQQRQWQWRWWEEREE",
            Notes = "notes",
        };
        build.Id = await _repo.SaveBuildAsync(build);
        Assert.That(build.Id, Is.GreaterThan(0));

        build.Name = "Renamed";
        build.Items.Blocks[0].Items.Add("1001");
        await _repo.SaveBuildAsync(build);

        var loaded = (await _repo.GetBuildsAsync("Ahri")).Single();
        Assert.That(loaded.Name, Is.EqualTo("Renamed"));
        Assert.That(loaded.Runes.PrimaryPerks, Is.EqualTo(build.Runes.PrimaryPerks));
        Assert.That(loaded.Runes.SubPerks, Is.EqualTo(build.Runes.SubPerks));
        Assert.That(loaded.Runes.Shards, Is.EqualTo(build.Runes.Shards));
        Assert.That(loaded.Items.Blocks.Single().Items, Is.EqualTo(new[] { "1056", "2003", "2003", "1001" }));
        Assert.That(loaded.SummonerSpells, Is.EqualTo(build.SummonerSpells));
        Assert.That(loaded.SkillOrder, Is.EqualTo(build.SkillOrder));
        Assert.That(loaded.Role, Is.EqualTo(Position.Middle));
    }

    [Test]
    public async Task One_matchup_per_pair_is_kept_and_counts_are_reported()
    {
        var first = await _repo.SetMatchupAsync("Ahri", "Fizz", MatchupKind.GoodAgainst, "first");
        var second = await _repo.SetMatchupAsync("Ahri", "Fizz", MatchupKind.WeakAgainst);
        Assert.That(second.Id, Is.EqualTo(first.Id));
        var stored = (await _repo.GetMatchupsAsync("Ahri")).Single();
        Assert.That(stored.Kind, Is.EqualTo(MatchupKind.WeakAgainst));
        Assert.That(stored.Note, Is.EqualTo("first"), "note kept when not supplied");

        await _repo.SaveBuildAsync(new Build { ChampionId = "Ahri" });
        var counts = await _repo.GetChampionCountsAsync();
        Assert.That(counts["Ahri"], Is.EqualTo((1, 1)));
    }

    [Test]
    public async Task Backup_export_and_import_round_trip()
    {
        var groups = await _repo.GetGroupsAsync();
        await _repo.AddEntryAsync(groups[2].Id, "Ahri", 5, ChampionPriority.Main);
        await _repo.SaveBuildAsync(new Build { ChampionId = "Ahri", Name = "Standard", SkillOrder = "QWE" });
        await _repo.SetMatchupAsync("Ahri", "Veigar", MatchupKind.GoodAgainst, "cage");
        await _repo.SaveProfileAsync("Ahri", "charm from fog", ["Pick", "Roam", "pick"]);

        var service = new BackupService(_repo, _paths, NullLogger<BackupService>.Instance);
        using var ms = new MemoryStream();
        await service.ExportAsync(ms, "16.23.1");

        // Wipe and change things, then import.
        foreach (var g in await _repo.GetGroupsAsync()) await _repo.DeleteGroupAsync(g.Id);
        await _repo.SetMatchupAsync("Zed", "Lux", MatchupKind.GoodAgainst);

        ms.Position = 0;
        var backup = await BackupService.ReadAsync(ms);
        await service.ImportAsync(backup, "16.23.1");

        var restored = await _repo.GetGroupsAsync();
        Assert.That(restored.Select(g => g.Name), Is.EqualTo(new[] { "Top", "Jungle", "Middle", "Bottom", "Support" }));
        Assert.That(restored[2].Entries.Single().ChampionId, Is.EqualTo("Ahri"));
        Assert.That((await _repo.GetBuildsAsync("Ahri")).Single().SkillOrder, Is.EqualTo("QWE"));
        Assert.That((await _repo.GetMatchupsAsync()).Select(m => m.OpponentId), Is.EqualTo(new[] { "Veigar" }));
        var profile = await _repo.GetProfileAsync("Ahri");
        Assert.That(profile.Tags, Is.EqualTo(new[] { "Pick", "Roam" }), "tags de-duplicated case-insensitively");
        Assert.That(Directory.EnumerateFiles(_paths.BackupDirectory, "before-import-*.json"), Is.Not.Empty, "safety backup written");
    }

    [Test]
    public async Task One_default_build_per_champion_and_role()
    {
        var mid1 = await _repo.SaveBuildAsync(new Build { ChampionId = "Ahri", Name = "Mid A", Role = Position.Middle });
        var mid2 = await _repo.SaveBuildAsync(new Build { ChampionId = "Ahri", Name = "Mid B", Role = Position.Middle });
        var any = await _repo.SaveBuildAsync(new Build { ChampionId = "Ahri", Name = "Any", Role = null });
        var lux = await _repo.SaveBuildAsync(new Build { ChampionId = "Lux", Name = "Lux mid", Role = Position.Middle });
        await _repo.SetDefaultBuildAsync(mid1, true);
        await _repo.SetDefaultBuildAsync(any, true);
        await _repo.SetDefaultBuildAsync(lux, true);
        await _repo.SetDefaultBuildAsync(mid2, true);

        var ahri = (await _repo.GetBuildsAsync("Ahri")).ToDictionary(b => b.Id);
        Assert.That(ahri[mid1].IsDefault, Is.False, "replaced by Mid B");
        Assert.That(ahri[mid2].IsDefault, Is.True);
        Assert.That(ahri[any].IsDefault, Is.True, "the any-role default is separate");
        Assert.That((await _repo.GetBuildsAsync("Lux")).Single().IsDefault, Is.True, "other champions are untouched");

        await _repo.SetDefaultBuildAsync(mid2, false);
        Assert.That((await _repo.GetBuildsAsync("Ahri")).Count(b => b.IsDefault), Is.EqualTo(1));
        Assert.That(await _repo.GetAllBuildsAsync(), Has.Count.EqualTo(4));

        var backup = await _repo.ExportAsync("16.23.1");
        await _repo.ImportAsync(backup);
        Assert.That((await _repo.GetBuildsAsync("Ahri")).Single(b => b.IsDefault).Name, Is.EqualTo("Any"), "kept through backup and restore");
    }

    [Test]
    public async Task Matchups_are_kept_per_role()
    {
        var any = await _repo.SetMatchupAsync("Pantheon", "Darius", MatchupKind.GoodAgainst, "general");
        var top = await _repo.SetMatchupAsync("Pantheon", "Darius", MatchupKind.WeakAgainst, "top", Position.Top);
        Assert.That(top.Id, Is.Not.EqualTo(any.Id));
        var again = await _repo.SetMatchupAsync("Pantheon", "Darius", MatchupKind.WeakAgainst, null, Position.Top);
        Assert.That(again.Id, Is.EqualTo(top.Id));
        Assert.That((await _repo.GetMatchupsAsync("Pantheon")).Select(m => (m.Role, m.Note)), Is.EquivalentTo(new (Position?, string)[] { (null, "general"), (Position.Top, "top") }));

        Assert.That(await _repo.SetMatchupRoleAsync(top.Id, null), Is.False, "all-roles matchup already exists");
        Assert.That(await _repo.SetMatchupRoleAsync(top.Id, Position.Support), Is.True);
        Assert.That((await _repo.GetMatchupsAsync("Pantheon")).Single(m => m.Id == top.Id).Role, Is.EqualTo(Position.Support));
    }

    [Test]
    public async Task Reading_a_file_that_is_not_a_backup_fails_clearly()
    {
        using var ms = new MemoryStream("{\"hello\": 1}"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadAsync(ms));
        using var bad = new MemoryStream("not json"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadAsync(bad));
    }

    [Test]
    public async Task Automatic_backups_run_at_most_daily()
    {
        var service = new BackupService(_repo, _paths, NullLogger<BackupService>.Instance);
        Assert.That(await service.RunAutomaticBackupAsync("16.23.1"), Is.Not.Null);
        Assert.That(await service.RunAutomaticBackupAsync("16.23.1"), Is.Null);
    }

    [Test]
    public async Task Shared_roster_imports_as_a_new_roster_or_merges_without_duplicates()
    {
        // Mine: Ahri in Middle with a build.
        var groups = await _repo.GetGroupsAsync();
        await _repo.AddEntryAsync(groups.Single(g => g.Role == Position.Middle).Id, "Ahri", 3, ChampionPriority.Secondary);
        await _repo.SaveBuildAsync(new Build { ChampionId = "Ahri", Name = "Standard", SkillOrder = "QWE" });

        var shared = new Core.Rules.SharedRosterFile
        {
            FileKind = Core.Rules.SharedRosterFile.Kind,
            Name = "Clash",
            IncludesRatings = true,
            Groups =
            [
                new() { Name = "Middle", Role = Position.Middle, Entries = [new() { ChampionId = "ahri", Proficiency = 5, Priority = ChampionPriority.Main }, new() { ChampionId = "Syndra", Proficiency = 4 }] },
                new() { Name = "Fun picks", Entries = [new() { ChampionId = "Teemo" }, new() { ChampionId = "NotAChampion" }] },
            ],
            Builds =
            [
                new Build { ChampionId = "Ahri", Name = "Standard", SkillOrder = "QWE" }, // same as mine
                new Build { ChampionId = "Ahri", Name = "vs AD", SkillOrder = "QEW" },
            ],
        };
        var known = new[] { "Ahri", "Syndra", "Teemo" };
        string? Canonical(string id) => known.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));

        // As a new roster.
        var result = await _repo.ImportSharedRosterAsync(shared, null, "Clash", includeBuilds: true, Canonical);
        Assert.That((await _repo.GetRostersAsync()).Select(r => r.Name), Is.EqualTo(new[] { RosterRepository.DefaultRosterName, "Clash" }));
        var clash = await _repo.GetGroupsAsync(result.RosterId);
        Assert.That(clash.Select(g => g.Name), Is.EqualTo(new[] { "Middle", "Fun picks" }));
        var ahri = clash[0].Entries.First();
        Assert.That((ahri.ChampionId, ahri.Proficiency, ahri.Priority), Is.EqualTo(("Ahri", 5, ChampionPriority.Main)), "ids fixed to the patch's spelling; ratings kept");
        Assert.That(clash[0].Entries[1].Priority, Is.EqualTo(ChampionPriority.Secondary), "missing priority: Secondary");
        Assert.That(result.ChampionsSkipped, Is.EqualTo(1));
        Assert.That(result.BuildsAdded, Is.EqualTo(1));
        Assert.That(result.BuildsAlreadyThere, Is.EqualTo(1));
        var builds = await _repo.GetBuildsAsync("Ahri");
        Assert.That(builds.Select(b => b.Name), Is.EqualTo(new[] { "Standard", "vs AD" }));
        Assert.That(builds.Any(b => b.IsDefault), Is.False);

        // Merged into my roster: Middle gets Syndra (Ahri is already there), "Fun picks" is new; builds aren't added twice.
        var mine = (await _repo.GetRostersAsync())[0].Id;
        var merged = await _repo.ImportSharedRosterAsync(shared, mine, "ignored", includeBuilds: true, Canonical);
        Assert.That(merged.RosterId, Is.EqualTo(mine));
        Assert.That(merged.PlacementsAdded, Is.EqualTo(2), "Syndra and Teemo");
        Assert.That(merged.GroupsAdded, Is.EqualTo(1));
        Assert.That(merged.BuildsAdded, Is.EqualTo(0));
        var middle = (await _repo.GetGroupsAsync(mine)).Single(g => g.Name == "Middle");
        Assert.That(middle.Entries.Select(e => e.ChampionId), Is.EqualTo(new[] { "Ahri", "Syndra" }));
        Assert.That(middle.Entries[0].Proficiency, Is.EqualTo(3), "my existing placement isn't changed");
        Assert.That((await _repo.GetRostersAsync()).Count, Is.EqualTo(2), "no new roster when merging");
    }
}
