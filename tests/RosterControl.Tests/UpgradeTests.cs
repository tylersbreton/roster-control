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

/// <summary>
/// Saved data must survive every app update. Each released version's database (built with that version's schema and
/// the JSON shapes it wrote) is upgraded by the current code and checked field by field; old JSON backups must import.
/// When adding a migration: add it to <see cref="Migrations"/> and, once released, a <see cref="Releases"/> case.
/// </summary>
public class UpgradeTests
{
    /// <summary>Every migration, in order. A new migration fails <see cref="Every_migration_is_known"/> until listed here.</summary>
    private static readonly string[] Migrations =
    [
        "20261125155821_InitialCreate",
        "20261125171225_MatchupRole",
        "20261125212436_BuildDefault",
        "20261126145204_Rosters",
    ];

    /// <summary>Released versions and the last migration they shipped with.</summary>
    private static readonly object[] Releases =
    [
        new object[] { "1.0.0", "20261125171225_MatchupRole" },
        new object[] { "1.1.0", "20261125171225_MatchupRole" },
        new object[] { "1.2.0", "20261125212436_BuildDefault" },
    ];

    private string _root = "";
    private AppPaths _paths = null!;
    private Factory _factory = null!;

    private sealed class Factory(string path) : IDbContextFactory<RosterDbContext>
    {
        public RosterDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<RosterDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options);
    }

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rostercontrol-upgrade-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _factory = new Factory(_paths.DatabasePath);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private RosterRepository Repo() => new(_factory, NullLogger<RosterRepository>.Instance);

    [Test]
    public void Every_migration_is_known()
    {
        using var db = _factory.CreateDbContext();
        Assert.That(db.Database.GetMigrations(), Is.EqualTo(Migrations),
            "A migration was added or renamed: list it in UpgradeTests.Migrations, and after releasing, add the release to UpgradeTests.Releases so its data is upgrade-tested.");
    }

    // ── A database as a released version left it ───────────────────────────────────────────────────

    /// <summary>Creates the schema of a released version and fills it the way that version's code wrote rows.</summary>
    private async Task CreateReleasedDatabaseAsync(string lastMigration)
    {
        await using var db = _factory.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(lastMigration);
        var hasIsDefault = string.CompareOrdinal(lastMigration, "20261125212436_BuildDefault") >= 0;
        string[] sql =
        [
            // Groups: one custom group and one role group, in a custom order and colours.
            "INSERT INTO RosterGroups (Id, Name, Role, SortOrder, Color, CreatedAt) VALUES (3, 'Middle', 2, 1, '#6FA8F5', '2026-11-25 10:00:00+00:00')",
            "INSERT INTO RosterGroups (Id, Name, Role, SortOrder, Color, CreatedAt) VALUES (9, 'Pocket picks', NULL, 0, '#B98CFF', '2026-11-25 10:00:00+00:00')",
            "INSERT INTO RosterEntries (Id, GroupId, ChampionId, SortOrder, Proficiency, Priority, AddedAt) VALUES (1, 3, 'Ahri', 0, 5, 0, '2026-11-25 10:00:00+00:00')",
            "INSERT INTO RosterEntries (Id, GroupId, ChampionId, SortOrder, Proficiency, Priority, AddedAt) VALUES (2, 3, 'Syndra', 1, 2, 3, '2026-11-25 10:00:00+00:00')",
            "INSERT INTO RosterEntries (Id, GroupId, ChampionId, SortOrder, Proficiency, Priority, AddedAt) VALUES (3, 9, 'Teemo', 0, 4, 2, '2026-11-25 10:00:00+00:00')",
            // A build with the JSON shapes written by RunePageData / ItemSetData (PascalCase, System.Text.Json defaults).
            $"INSERT INTO Builds (Id, ChampionId, Name, Role, SortOrder, GameVersion, RunesJson, ItemsJson, SummonerSpellsJson, SkillOrder, Notes, UpdatedAt{(hasIsDefault ? ", IsDefault" : "")}) VALUES (" +
            "5, 'Ahri', 'Standard', 2, 0, '16.10.1', " +
            "'{\"PrimaryStyleId\":8100,\"SubStyleId\":8200,\"PrimaryPerks\":[8112,8139,8138,8135],\"SubPerks\":[8226,8210],\"Shards\":[5008,5008,5001]}', " +
            "'{\"Blocks\":[{\"Name\":\"Starting Items\",\"Items\":[\"1056\",\"2003\",\"2003\"]},{\"Name\":\"Core Build\",\"Items\":[\"3020\",\"6655\"]}]}', " +
            "'[\"SummonerFlash\",\"SummonerDot\"]', 'QWEQQRQWQW', '- poke with Q\n[x] ward river', '2026-11-25 10:00:00+00:00'" +
            $"{(hasIsDefault ? ", 1" : "")})",
            "INSERT INTO Builds (Id, ChampionId, Name, Role, SortOrder, GameVersion, RunesJson, ItemsJson, SummonerSpellsJson, SkillOrder, Notes, UpdatedAt" +
            $"{(hasIsDefault ? ", IsDefault" : "")}) VALUES (6, 'Ahri', 'vs AP', NULL, 1, NULL, '{{}}', '{{}}', '[\"\",\"\"]', '', '', '2026-11-25 10:00:00+00:00'{(hasIsDefault ? ", 0" : "")})",
            // Matchups, including a role-specific one (1.0 already had roles).
            "INSERT INTO Matchups (Id, ChampionId, OpponentId, Role, Kind, Note, SortOrder, UpdatedAt) VALUES (1, 'Ahri', 'Veigar', NULL, 0, 'Charm through the cage edge', 0, '2026-11-25 10:00:00+00:00')",
            "INSERT INTO Matchups (Id, ChampionId, OpponentId, Role, Kind, Note, SortOrder, UpdatedAt) VALUES (2, 'Ahri', 'Fizz', 2, 1, 'Hold R for his E', 0, '2026-11-25 10:00:00+00:00')",
            "INSERT INTO ChampionProfiles (ChampionId, Notes, Tags, UpdatedAt) VALUES ('Ahri', 'Look for picks', '[\"Pick\",\"Roam\"]', '2026-11-25 10:00:00+00:00')",
            "INSERT INTO Preferences (Key, ValueJson) VALUES ('data.seeded', 'true')",
            "INSERT INTO Preferences (Key, ValueJson) VALUES ('ui.section', '0')",
            "INSERT INTO Preferences (Key, ValueJson) VALUES ('rosters.pool', '{\"Search\":\"ah\",\"Role\":2,\"Class\":\"Mage\"}')",
        ];
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        foreach (var statement in sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync();
        }
    }

    [TestCaseSource(nameof(Releases))]
    public async Task Data_from_a_released_version_survives_the_upgrade(string version, string lastMigration)
    {
        await CreateReleasedDatabaseAsync(lastMigration);
        var repo = Repo();
        await repo.InitializeAsync();

        // Schema is current, and the old database was kept.
        await using (var db = _factory.CreateDbContext())
            Assert.That(await db.Database.GetPendingMigrationsAsync(), Is.Empty);
        Assert.That(repo.UpgradeBackupPath, Is.Not.Null.And.Matches<string>(File.Exists), "a copy of the old database is saved before upgrading");
        Assert.That(repo.DatabaseFromNewerVersion, Is.False);

        // Groups and placements, in order, inside the first roster.
        Assert.That((await repo.GetRostersAsync()).Select(r => r.Name), Is.EqualTo(new[] { "My roster" }));
        var groups = await repo.GetGroupsAsync();
        Assert.That(groups.Select(g => (g.Name, g.Role, g.Color)), Is.EqualTo(new[]
        {
            ("Pocket picks", (Position?)null, "#B98CFF"),
            ("Middle", Position.Middle, "#6FA8F5"),
        }), "custom groups kept, no default groups added");
        Assert.That(groups[1].Entries.Select(e => (e.ChampionId, e.Proficiency, e.Priority)), Is.EqualTo(new[]
        {
            ("Ahri", 5, ChampionPriority.Main),
            ("Syndra", 2, ChampionPriority.Learning),
        }));
        Assert.That(groups[0].Entries.Single().ChampionId, Is.EqualTo("Teemo"));

        // Builds, with every nested field.
        var builds = await repo.GetBuildsAsync("Ahri");
        Assert.That(builds.Select(b => b.Name), Is.EqualTo(new[] { "Standard", "vs AP" }));
        var standard = builds[0];
        Assert.That(standard.Role, Is.EqualTo(Position.Middle));
        Assert.That(standard.GameVersion, Is.EqualTo("16.10.1"), "the patch it was saved on (drives the changed-items warning)");
        Assert.That(standard.Runes.PrimaryStyleId, Is.EqualTo(8100));
        Assert.That(standard.Runes.PrimaryPerks, Is.EqualTo(new[] { 8112, 8139, 8138, 8135 }));
        Assert.That(standard.Runes.SubPerks, Is.EqualTo(new[] { 8226, 8210 }));
        Assert.That(standard.Runes.Shards, Is.EqualTo(new[] { 5008, 5008, 5001 }));
        Assert.That(standard.Items.Blocks.Select(b => b.Name), Is.EqualTo(new[] { "Starting Items", "Core Build" }));
        Assert.That(standard.Items.Blocks[0].Items, Is.EqualTo(new[] { "1056", "2003", "2003" }));
        Assert.That(standard.SummonerSpells, Is.EqualTo(new[] { "SummonerFlash", "SummonerDot" }));
        Assert.That(standard.SkillOrder, Is.EqualTo("QWEQQRQWQW"));
        Assert.That(standard.Notes, Is.EqualTo("- poke with Q\n[x] ward river"));
        Assert.That(standard.IsDefault, Is.EqualTo(version == "1.2.0"), "the default flag is kept where it existed");
        Assert.That(builds[1].Runes.PrimaryPerks, Is.EqualTo(new[] { 0, 0, 0, 0 }), "empty JSON gets the defaults");

        // Matchups, notes, tags and preferences.
        var matchups = await repo.GetMatchupsAsync("Ahri");
        Assert.That(matchups.Select(m => (m.OpponentId, m.Kind, m.Role, m.Note)), Is.EquivalentTo(new[]
        {
            ("Veigar", MatchupKind.GoodAgainst, (Position?)null, "Charm through the cage edge"),
            ("Fizz", MatchupKind.WeakAgainst, (Position?)Position.Middle, "Hold R for his E"),
        }));
        var profile = await repo.GetProfileAsync("Ahri");
        Assert.That(profile.Notes, Is.EqualTo("Look for picks"));
        Assert.That(profile.Tags, Is.EqualTo(new[] { "Pick", "Roam" }));
        var prefs = new PreferencesStore(_factory, repo, NullLogger<PreferencesStore>.Instance);
        Assert.That(prefs.Get("ui.section", -1), Is.EqualTo(0));
        Assert.That(prefs.Get<JsonElement?>("rosters.pool", null)?.GetProperty("Class").GetString(), Is.EqualTo("Mage"), "old preference shapes still read");

        // Restarting doesn't upgrade (or back up) again.
        var again = Repo();
        await again.InitializeAsync();
        Assert.That(again.UpgradeBackupPath, Is.Null);
    }

    [Test]
    public async Task The_pre_upgrade_copy_is_the_old_database()
    {
        await CreateReleasedDatabaseAsync("20261125212436_BuildDefault");
        var repo = Repo();
        await repo.InitializeAsync();
        await using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = repo.UpgradeBackupPath, Pooling = false }.ToString());
        await copy.OpenAsync();
        await using var cmd = copy.CreateCommand();
        cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        Assert.That(await cmd.ExecuteScalarAsync(), Is.EqualTo("20261125212436_BuildDefault"), "restorable as-is by the 1.2 app");
        cmd.CommandText = "SELECT COUNT(*) FROM RosterEntries";
        Assert.That(await cmd.ExecuteScalarAsync(), Is.EqualTo(3L));
    }

    [Test]
    public async Task A_fresh_install_needs_no_upgrade_copy()
    {
        var repo = Repo();
        await repo.InitializeAsync();
        Assert.That(repo.UpgradeBackupPath, Is.Null);
        Assert.That(Directory.Exists(_paths.BackupDirectory) ? Directory.GetFiles(_paths.BackupDirectory, "before-upgrade-*") : [], Is.Empty);
    }

    [Test]
    public async Task Only_the_newest_upgrade_copies_are_kept()
    {
        Directory.CreateDirectory(_paths.BackupDirectory);
        for (var i = 0; i < 7; i++) File.WriteAllText(Path.Combine(_paths.BackupDirectory, $"before-upgrade-2020010{i}-000000-000.db"), "");
        await CreateReleasedDatabaseAsync("20261125212436_BuildDefault");
        await Repo().InitializeAsync();
        Assert.That(Directory.GetFiles(_paths.BackupDirectory, "before-upgrade-*.db"), Has.Length.EqualTo(RosterRepository.UpgradeBackupsKept));
    }

    [Test]
    public async Task A_database_from_a_newer_version_is_detected_not_broken()
    {
        await Repo().InitializeAsync();
        await using (var db = _factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231000000_FromTheFuture', '99.0')");
        var repo = Repo();
        await repo.InitializeAsync();
        Assert.That(repo.DatabaseFromNewerVersion, Is.True);
        Assert.That(await repo.GetGroupsAsync(), Is.Not.Empty, "still usable");
    }

    // ── JSON backups written by earlier versions ───────────────────────────────────────────────────

    /// <summary>The backup format written by 1.0–1.2: no rosters, no default-build flag.</summary>
    private const string Backup12 = """
        {
          "Application": "Roster Control", "SchemaVersion": 1, "ExportedAt": "2026-11-25T12:00:00+00:00", "GameVersion": "16.23.1",
          "Groups": [
            { "Name": "Top", "Role": 0, "Color": "#E0815A", "Entries": [ { "ChampionId": "Darius", "Proficiency": 5, "Priority": 0, "AddedAt": "2026-11-25T10:00:00+00:00" } ] },
            { "Name": "Fun", "Role": null, "Color": null, "Entries": [] }
          ],
          "Profiles": [ { "ChampionId": "Darius", "Notes": "Five stacks", "Tags": ["Lane bully"], "UpdatedAt": "2026-11-25T10:00:00+00:00" } ],
          "Matchups": [ { "Id": 4, "ChampionId": "Darius", "OpponentId": "Vayne", "Role": 0, "Kind": 1, "Note": "Ghost", "SortOrder": 0, "UpdatedAt": "2026-11-25T10:00:00+00:00" } ],
          "Builds": [ { "Id": 9, "ChampionId": "Darius", "Name": "Standard", "Role": 0, "SortOrder": 0, "GameVersion": "16.22.1",
                        "Runes": { "PrimaryStyleId": 8000, "SubStyleId": 8400, "PrimaryPerks": [8010, 9111, 9104, 8299], "SubPerks": [8444, 8242], "Shards": [5005, 5008, 5001] },
                        "Items": { "Blocks": [ { "Name": "Core", "Items": ["6631", "3047"] } ] },
                        "SummonerSpells": ["SummonerFlash", "SummonerGhost"], "SkillOrder": "QEWQQRQE", "Notes": "", "UpdatedAt": "2026-11-25T10:00:00+00:00" } ]
        }
        """;

    [Test]
    public async Task A_backup_from_1_2_imports_completely()
    {
        var repo = Repo();
        await repo.InitializeAsync();
        var backup = await BackupService.ReadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Backup12)));
        await new BackupService(repo, _paths, NullLogger<BackupService>.Instance).ImportAsync(backup, "16.23.1");

        Assert.That((await repo.GetRostersAsync()).Select(r => r.Name), Is.EqualTo(new[] { "My roster" }));
        var groups = await repo.GetGroupsAsync();
        Assert.That(groups.Select(g => g.Name), Is.EqualTo(new[] { "Top", "Fun" }));
        Assert.That(groups[0].Entries.Single().Proficiency, Is.EqualTo(5));
        var build = (await repo.GetBuildsAsync("Darius")).Single();
        Assert.That(build.Runes.PrimaryPerks, Is.EqualTo(new[] { 8010, 9111, 9104, 8299 }));
        Assert.That(build.Items.Blocks.Single().Items, Is.EqualTo(new[] { "6631", "3047" }));
        Assert.That(build.GameVersion, Is.EqualTo("16.22.1"));
        Assert.That(build.IsDefault, Is.False);
        Assert.That((await repo.GetMatchupsAsync("Darius")).Single().Role, Is.EqualTo(Position.Top));
        Assert.That((await repo.GetProfileAsync("Darius")).Tags, Is.EqualTo(new[] { "Lane bully" }));
    }

    [Test]
    public async Task A_current_backup_still_reads_as_schema_1_for_older_versions()
    {
        var repo = Repo();
        await repo.InitializeAsync();
        var backup = await repo.ExportAsync("16.23.1");
        Assert.That(backup.SchemaVersion, Is.EqualTo(1), "1.0–1.2 reject higher schema versions; new data is additive");
        Assert.That(backup.Groups, Is.Not.Empty, "older versions import the active roster's groups");
    }
}
