using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using RosterControl.Core.Abstractions;
using RosterControl.Core.Model;
using RosterControl.Data;

namespace RosterControl.Tests;

/// <summary>Several rosters (Solo/Duo, Flex…): groups belong to one roster; builds and matchups are shared.</summary>
public class RosterProfileTests
{
    private string _root = "";
    private AppPaths _paths = null!;
    private Factory _factory = null!;
    private RosterRepository _repo = null!;

    private sealed class Factory(string path) : IDbContextFactory<RosterDbContext>
    {
        public RosterDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<RosterDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options);
    }

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rostercontrol-rosters-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _factory = new Factory(_paths.DatabasePath);
        _repo = new RosterRepository(_factory, NullLogger<RosterRepository>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Test]
    public async Task A_new_database_has_one_roster_with_the_role_groups()
    {
        await _repo.InitializeAsync();
        var rosters = await _repo.GetRostersAsync();
        Assert.That(rosters.Select(r => r.Name), Is.EqualTo(new[] { "My roster" }));
        Assert.That((await _repo.GetGroupsAsync()).Select(g => g.Name), Is.EqualTo(new[] { "Top", "Jungle", "Middle", "Bottom", "Support" }));
    }

    [Test]
    public async Task Upgrading_from_1_2_moves_existing_groups_into_the_first_roster()
    {
        // A 1.2 database (migrated up to BuildDefault) with a custom group and a placement.
        await using (var db = _factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261125212436_BuildDefault");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO RosterGroups (Id, Name, Role, SortOrder, Color, CreatedAt) VALUES (7, 'Pocket picks', NULL, 0, NULL, '2026-11-01 00:00:00+00:00')");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO RosterEntries (GroupId, ChampionId, SortOrder, Proficiency, Priority, AddedAt) VALUES (7, 'Ahri', 0, 4, 0, '2026-11-01 00:00:00+00:00')");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Preferences (Key, ValueJson) VALUES ('data.seeded', 'true')");
        }
        await _repo.InitializeAsync();
        var rosters = await _repo.GetRostersAsync();
        Assert.That(rosters.Select(r => r.Name), Is.EqualTo(new[] { "My roster" }));
        var groups = await _repo.GetGroupsAsync();
        Assert.That(groups.Select(g => g.Name), Is.EqualTo(new[] { "Pocket picks" }));
        Assert.That(groups[0].RosterId, Is.EqualTo(rosters[0].Id));
        Assert.That(groups[0].Entries.Single().ChampionId, Is.EqualTo("Ahri"));
    }

    [Test]
    public async Task Groups_belong_to_the_active_roster()
    {
        await _repo.InitializeAsync();
        var solo = (await _repo.GetRostersAsync()).Single();
        var aram = await _repo.AddRosterAsync("ARAM", RosterTemplate.Empty);
        var flex = await _repo.AddRosterAsync("Flex", RosterTemplate.Roles);

        _repo.ActiveRosterId = aram.Id;
        Assert.That(await _repo.GetGroupsAsync(), Is.Empty, "empty template");
        var fun = await _repo.AddGroupAsync("Fun picks", null, null);
        await _repo.AddEntryAsync(fun.Id, "Teemo", 5, ChampionPriority.Main);
        Assert.That((await _repo.GetGroupsAsync()).Select(g => g.Name), Is.EqualTo(new[] { "Fun picks" }));

        _repo.ActiveRosterId = flex.Id;
        Assert.That((await _repo.GetGroupsAsync()).Select(g => g.Name), Is.EqualTo(new[] { "Top", "Jungle", "Middle", "Bottom", "Support" }));

        _repo.ActiveRosterId = solo.Id;
        Assert.That((await _repo.GetGroupsAsync()).SelectMany(g => g.Entries), Is.Empty, "Teemo stays in ARAM");
        Assert.That(await _repo.GetGroupsAsync(aram.Id), Has.Count.EqualTo(1), "a roster can be read without switching");

        await _repo.RenameRosterAsync(aram.Id, "  Howling Abyss  ");
        Assert.That((await _repo.GetRostersAsync()).Select(r => r.Name), Is.EqualTo(new[] { "My roster", "Howling Abyss", "Flex" }));
    }

    [Test]
    public async Task A_roster_can_be_copied_and_deleted_but_not_the_last_one()
    {
        await _repo.InitializeAsync();
        var groups = await _repo.GetGroupsAsync();
        await _repo.AddEntryAsync(groups[2].Id, "Ahri", 5, ChampionPriority.Main);
        await _repo.AddEntryAsync(groups[2].Id, "Syndra", 2, ChampionPriority.Learning);
        var first = (await _repo.GetRostersAsync()).Single();

        var copy = await _repo.AddRosterAsync("Clash", RosterTemplate.Copy, first.Id);
        var copied = await _repo.GetGroupsAsync(copy.Id);
        Assert.That(copied.Select(g => g.Name), Is.EqualTo(groups.Select(g => g.Name)));
        Assert.That(copied[2].Entries.Select(e => (e.ChampionId, e.Proficiency, e.Priority)),
            Is.EqualTo(new[] { ("Ahri", 5, ChampionPriority.Main), ("Syndra", 2, ChampionPriority.Learning) }));

        // Editing the copy leaves the original alone.
        await _repo.DeleteEntryAsync(copied[2].Entries[0].Id);
        Assert.That((await _repo.GetGroupsAsync(first.Id))[2].Entries, Has.Count.EqualTo(2));

        _repo.ActiveRosterId = copy.Id;
        Assert.That(await _repo.DeleteRosterAsync(copy.Id), Is.True);
        Assert.That(await _repo.GetActiveRosterIdAsync(), Is.EqualTo(first.Id), "falls back to the first roster");
        Assert.That(await _repo.DeleteRosterAsync(first.Id), Is.False, "the last roster stays");
        Assert.That((await _repo.GetGroupsAsync())[2].Entries, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Backups_keep_every_roster_and_old_backups_import_as_one()
    {
        await _repo.InitializeAsync();
        var groups = await _repo.GetGroupsAsync();
        await _repo.AddEntryAsync(groups[0].Id, "Darius", 5, ChampionPriority.Main);
        var aram = await _repo.AddRosterAsync("ARAM", RosterTemplate.Empty);
        _repo.ActiveRosterId = aram.Id;
        var fun = await _repo.AddGroupAsync("Fun", null, null);
        await _repo.AddEntryAsync(fun.Id, "Teemo", 3, ChampionPriority.Pocket);

        var backup = await _repo.ExportAsync("16.23.1");
        Assert.That(backup.Rosters!.Select(r => r.Name), Is.EqualTo(new[] { "My roster", "ARAM" }));
        Assert.That(backup.Groups.Select(g => g.Name), Is.EqualTo(new[] { "Fun" }), "older versions import the active roster");

        await _repo.ImportAsync(backup);
        var rosters = await _repo.GetRostersAsync();
        Assert.That(rosters.Select(r => r.Name), Is.EqualTo(new[] { "My roster", "ARAM" }));
        Assert.That(_repo.ActiveRosterId, Is.Null, "the first roster is active after a restore");
        Assert.That((await _repo.GetGroupsAsync())[0].Entries.Single().ChampionId, Is.EqualTo("Darius"));
        Assert.That((await _repo.GetGroupsAsync(rosters[1].Id)).Single().Entries.Single().ChampionId, Is.EqualTo("Teemo"));

        // A 1.2 backup has no rosters: its groups become one roster.
        var old = JsonSerializer.Deserialize<RosterBackup>(JsonSerializer.Serialize(backup))!;
        old.Rosters = null;
        await _repo.ImportAsync(old);
        Assert.That((await _repo.GetRostersAsync()).Select(r => r.Name), Is.EqualTo(new[] { "My roster" }));
        Assert.That((await _repo.GetGroupsAsync()).Select(g => g.Name), Is.EqualTo(new[] { "Fun" }));
    }

    [Test]
    public async Task Writes_raise_the_written_event()
    {
        await _repo.InitializeAsync();
        var count = 0;
        _repo.Written += (_, _) => Interlocked.Increment(ref count);
        await _repo.AddGroupAsync("X", null, null);
        await _repo.GetGroupsAsync();
        Assert.That(count, Is.EqualTo(1), "reads don't count");
    }
}
