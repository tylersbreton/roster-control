using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RosterControl.Core.Model;

namespace RosterControl.Data;

/// <summary>
/// All reads and writes of user data. Every call uses its own short-lived context; writes are serialised so the UI can
/// fire them without coordinating. Returned objects are detached copies.
/// </summary>
public sealed class RosterRepository
{
    public const string SeededPreferenceKey = "data.seeded";

    private static readonly (string Name, Position Role, string Color)[] DefaultGroups =
    [
        ("Top", Position.Top, "#E0815A"),
        ("Jungle", Position.Jungle, "#6FCB74"),
        ("Middle", Position.Middle, "#6FA8F5"),
        ("Bottom", Position.Bottom, "#E5C35C"),
        ("Support", Position.Support, "#5FD3C9"),
    ];

    public const string DefaultRosterName = "My roster";

    private readonly IDbContextFactory<RosterDbContext> _factory;
    private readonly ILogger<RosterRepository> _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private bool _initialized;

    public RosterRepository(IDbContextFactory<RosterDbContext> factory, ILogger<RosterRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>Raised after every successful write (for the "Saved" indicator).</summary>
    public event EventHandler? Written;

    /// <summary>
    /// The roster that group and placement calls work on. Null (or a deleted roster) means the first roster.
    /// </summary>
    public int? ActiveRosterId { get; set; }

    /// <summary>Applies migrations and, on first run, creates one roster group per role.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            DatabaseFromNewerVersion = applied.Any(m => !known.Contains(m));
            if (DatabaseFromNewerVersion)
                _logger.LogWarning("The database was last used by a newer version of Roster Control (unknown migrations: {Migrations})",
                    string.Join(", ", applied.Where(m => !known.Contains(m))));
            // Upgrading existing data: keep an exact copy of the old database first.
            if (applied.Count > 0 && pending.Count > 0) UpgradeBackupPath = BackupBeforeUpgrade(db, applied[^1], pending[^1]);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            if (!await db.Rosters.AnyAsync(ct).ConfigureAwait(false))
            {
                db.Rosters.Add(new Roster { Name = DefaultRosterName });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            if (!await db.Preferences.AnyAsync(p => p.Key == SeededPreferenceKey, ct).ConfigureAwait(false))
            {
                if (!await db.Groups.AnyAsync(ct).ConfigureAwait(false))
                {
                    var rosterId = await db.Rosters.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).Select(r => r.Id).FirstAsync(ct).ConfigureAwait(false);
                    AddDefaultGroups(db, rosterId);
                }
                db.Preferences.Add(new Preference { Key = SeededPreferenceKey, ValueJson = "true" });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Created default role groups");
            }
            _initialized = true;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>The data was written by a newer version of the app than this one (e.g. an older copy was started).</summary>
    public bool DatabaseFromNewerVersion { get; private set; }

    /// <summary>Copy of the database taken before this start applied schema upgrades (null when none were needed).</summary>
    public string? UpgradeBackupPath { get; private set; }

    public const int UpgradeBackupsKept = 5;

    /// <summary>
    /// Copies the database (SQLite online backup, consistent even with a WAL) to
    /// <c>backups\before-upgrade-&lt;time&gt;.db</c> next to it. Failure is logged; the upgrade still runs, since each
    /// migration is transactional.
    /// </summary>
    private string? BackupBeforeUpgrade(RosterDbContext db, string fromMigration, string toMigration)
    {
        try
        {
            var source = db.Database.GetDbConnection().DataSource;
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) return null;
            var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "backups");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"before-upgrade-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
            using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
            {
                from.Open();
                from.BackupDatabase(to);
            }
            _logger.LogInformation("Upgrading the database from {From} to {To}; previous version saved to {Path}", fromMigration, toMigration, target);
            foreach (var old in new DirectoryInfo(folder).GetFiles("before-upgrade-*.db").OrderByDescending(f => f.Name).Skip(UpgradeBackupsKept))
                old.Delete();
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            _logger.LogError(ex, "Could not back up the database before upgrading it");
            return null;
        }
    }

    private static void AddDefaultGroups(RosterDbContext db, int rosterId)
    {
        var order = 0;
        foreach (var (name, role, color) in DefaultGroups)
            db.Groups.Add(new RosterGroup { RosterId = rosterId, Name = name, Role = role, Color = color, SortOrder = order++ });
    }

    // ── Rosters ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Roster>> GetRostersAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Rosters.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToList();
    }

    /// <summary>The active roster's id (the first roster when none is set or it was deleted).</summary>
    public async Task<int> GetActiveRosterIdAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ResolveRosterIdAsync(db, null, ct).ConfigureAwait(false);
    }

    private async Task<int> ResolveRosterIdAsync(RosterDbContext db, int? rosterId, CancellationToken ct)
    {
        var wanted = rosterId ?? ActiveRosterId;
        if (wanted is { } id && await db.Rosters.AnyAsync(r => r.Id == id, ct).ConfigureAwait(false)) return id;
        var first = await db.Rosters.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).Select(r => (int?)r.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (first is { } f) return f;
        var roster = new Roster { Name = DefaultRosterName };
        db.Rosters.Add(roster);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return roster.Id;
    }

    public Task<Roster> AddRosterAsync(string name, RosterTemplate template, int? copyFromRosterId = null, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var max = await db.Rosters.Select(r => (int?)r.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
        var roster = new Roster { Name = Clean(name, 60, "Roster"), SortOrder = max + 1 };
        db.Rosters.Add(roster);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        switch (template)
        {
            case RosterTemplate.Roles:
                AddDefaultGroups(db, roster.Id);
                break;
            case RosterTemplate.Copy:
                var sourceId = await ResolveRosterIdAsync(db, copyFromRosterId, ct).ConfigureAwait(false);
                var groups = await db.Groups.AsNoTracking().Include(g => g.Entries).Where(g => g.RosterId == sourceId).ToListAsync(ct).ConfigureAwait(false);
                foreach (var g in groups)
                    db.Groups.Add(new RosterGroup
                    {
                        RosterId = roster.Id,
                        Name = g.Name,
                        Role = g.Role,
                        Color = g.Color,
                        SortOrder = g.SortOrder,
                        Entries = g.Entries.Select(e => new RosterEntry
                        {
                            ChampionId = e.ChampionId,
                            Proficiency = e.Proficiency,
                            Priority = e.Priority,
                            SortOrder = e.SortOrder,
                            AddedAt = e.AddedAt,
                        }).ToList(),
                    });
                break;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return roster;
    }, ct);

    public Task RenameRosterAsync(int rosterId, string name, CancellationToken ct = default) => WriteAsync(async db =>
        await db.Rosters.Where(r => r.Id == rosterId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, Clean(name, 60, "Roster")), ct).ConfigureAwait(false), ct);

    /// <summary>Deletes a roster with its groups and placements. The last roster can't be deleted.</summary>
    public Task<bool> DeleteRosterAsync(int rosterId, CancellationToken ct = default) => WriteAsync(async db =>
    {
        if (await db.Rosters.CountAsync(ct).ConfigureAwait(false) <= 1) return false;
        var groupIds = await db.Groups.Where(g => g.RosterId == rosterId).Select(g => g.Id).ToListAsync(ct).ConfigureAwait(false);
        await db.Entries.Where(e => groupIds.Contains(e.GroupId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Groups.Where(g => g.RosterId == rosterId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return await db.Rosters.Where(r => r.Id == rosterId).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }, ct);

    // ── Groups & entries ────────────────────────────────────────────────────────────────────────────

    /// <summary>The groups of a roster (default: the active roster), with their entries.</summary>
    public async Task<IReadOnlyList<RosterGroup>> GetGroupsAsync(CancellationToken ct = default) => await GetGroupsAsync(null, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RosterGroup>> GetGroupsAsync(int? rosterId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = await ResolveRosterIdAsync(db, rosterId, ct).ConfigureAwait(false);
        var groups = await db.Groups.AsNoTracking().Include(g => g.Entries).Where(g => g.RosterId == id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var g in groups) g.Entries = g.Entries.OrderBy(e => e.SortOrder).ThenBy(e => e.Id).ToList();
        return groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToList();
    }

    public Task<RosterGroup> AddGroupAsync(string name, Position? role, string? color, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var rosterId = await ResolveRosterIdAsync(db, null, ct).ConfigureAwait(false);
        var max = await db.Groups.Where(g => g.RosterId == rosterId).Select(g => (int?)g.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
        var group = new RosterGroup { RosterId = rosterId, Name = Clean(name, 80, "New group"), Role = role, Color = color, SortOrder = max + 1 };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return group;
    }, ct);

    public Task UpdateGroupAsync(int groupId, string name, Position? role, string? color, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var group = await db.Groups.FindAsync([groupId], ct).ConfigureAwait(false);
        if (group is null) return 0;
        group.Name = Clean(name, 80, group.Name);
        group.Role = role;
        group.Color = color;
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }, ct);

    public Task DeleteGroupAsync(int groupId, CancellationToken ct = default) => WriteAsync(async db =>
        await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync(ct).ConfigureAwait(false), ct);

    public Task ReorderGroupsAsync(IReadOnlyList<int> groupIds, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var groups = await db.Groups.ToListAsync(ct).ConfigureAwait(false);
        foreach (var g in groups)
        {
            var index = groupIds.IndexOf(g.Id);
            if (index >= 0) g.SortOrder = index;
        }
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }, ct);

    public Task<RosterEntry> AddEntryAsync(int groupId, string championId, int proficiency, ChampionPriority priority, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var existing = await db.Entries.FirstOrDefaultAsync(e => e.GroupId == groupId && e.ChampionId == championId, ct).ConfigureAwait(false);
        if (existing is not null) return existing;
        var max = await db.Entries.Where(e => e.GroupId == groupId).Select(e => (int?)e.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
        var entry = new RosterEntry { GroupId = groupId, ChampionId = championId, Proficiency = Math.Clamp(proficiency, 0, 5), Priority = priority, SortOrder = max + 1 };
        db.Entries.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entry;
    }, ct);

    public Task UpdateEntryAsync(int entryId, int proficiency, ChampionPriority priority, CancellationToken ct = default) => WriteAsync(async db =>
        await db.Entries.Where(e => e.Id == entryId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Proficiency, Math.Clamp(proficiency, 0, 5))
            .SetProperty(e => e.Priority, priority), ct).ConfigureAwait(false), ct);

    public Task DeleteEntryAsync(int entryId, CancellationToken ct = default) => WriteAsync(async db =>
        await db.Entries.Where(e => e.Id == entryId).ExecuteDeleteAsync(ct).ConfigureAwait(false), ct);

    /// <summary>
    /// Sets the complete, ordered contents of a group: entries listed here are moved into the group (if needed) and
    /// re-numbered. Used for drag-and-drop reorders and moves between groups.
    /// </summary>
    public Task SetGroupOrderAsync(int groupId, IReadOnlyList<int> entryIds, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var entries = await db.Entries.Where(e => entryIds.Contains(e.Id) || e.GroupId == groupId).ToListAsync(ct).ConfigureAwait(false);
        foreach (var e in entries)
        {
            var index = entryIds.IndexOf(e.Id);
            if (index < 0) continue; // not listed: left alone (it is being moved elsewhere by another call)
            e.GroupId = groupId;
            e.SortOrder = index;
        }
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }, ct);

    // ── Profiles (notes & tags) ─────────────────────────────────────────────────────────────────────

    public async Task<ChampionProfile> GetProfileAsync(string championId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.ChampionId == championId, ct).ConfigureAwait(false)
               ?? new ChampionProfile { ChampionId = championId };
    }

    public async Task<IReadOnlyList<ChampionProfile>> GetProfilesAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Profiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    public Task SaveProfileAsync(string championId, string notes, IReadOnlyList<string> tags, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var profile = await db.Profiles.FindAsync([championId], ct).ConfigureAwait(false);
        if (profile is null)
        {
            profile = new ChampionProfile { ChampionId = championId };
            db.Profiles.Add(profile);
        }
        profile.Notes = notes;
        profile.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }, ct);

    // ── Matchups ────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Matchup>> GetMatchupsAsync(string? championId = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Matchups.AsNoTracking();
        if (championId is not null) query = query.Where(m => m.ChampionId == championId);
        return (await query.ToListAsync(ct).ConfigureAwait(false)).OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToList();
    }

    /// <summary>
    /// Creates or updates the matchup between two champions for a role (null = all roles). There is one record per
    /// champion, opponent and role.
    /// </summary>
    public Task<Matchup> SetMatchupAsync(string championId, string opponentId, MatchupKind kind, string? note = null, Position? role = null, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var m = await db.Matchups.FirstOrDefaultAsync(x => x.ChampionId == championId && x.OpponentId == opponentId && x.Role == role, ct).ConfigureAwait(false);
        if (m is null)
        {
            var max = await db.Matchups.Where(x => x.ChampionId == championId).Select(x => (int?)x.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
            m = new Matchup { ChampionId = championId, OpponentId = opponentId, Role = role, SortOrder = max + 1 };
            db.Matchups.Add(m);
        }
        m.Kind = kind;
        if (note is not null) m.Note = note;
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return m;
    }, ct);

    /// <summary>Moves a matchup to another role (null = all roles). Returns false when that role already has one for the pair.</summary>
    public Task<bool> SetMatchupRoleAsync(int matchupId, Position? role, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var m = await db.Matchups.FindAsync([matchupId], ct).ConfigureAwait(false);
        if (m is null) return false;
        if (m.Role == role) return true;
        var clash = await db.Matchups.AnyAsync(x => x.Id != matchupId && x.ChampionId == m.ChampionId && x.OpponentId == m.OpponentId && x.Role == role, ct).ConfigureAwait(false);
        if (clash) return false;
        m.Role = role;
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }, ct);

    public Task UpdateMatchupNoteAsync(int matchupId, string note, CancellationToken ct = default) => WriteAsync(async db =>
        await db.Matchups.Where(m => m.Id == matchupId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Note, note), ct).ConfigureAwait(false), ct);

    public Task ReorderMatchupsAsync(IReadOnlyList<int> matchupIds, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var matchups = await db.Matchups.Where(m => matchupIds.Contains(m.Id)).ToListAsync(ct).ConfigureAwait(false);
        foreach (var m in matchups) m.SortOrder = matchupIds.IndexOf(m.Id);
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }, ct);

    public Task DeleteMatchupAsync(int matchupId, CancellationToken ct = default) => WriteAsync(async db =>
        await db.Matchups.Where(m => m.Id == matchupId).ExecuteDeleteAsync(ct).ConfigureAwait(false), ct);

    // ── Builds ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Build>> GetBuildsAsync(string championId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Builds.AsNoTracking().Where(b => b.ChampionId == championId).ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToList();
    }

    /// <summary>Number of builds and matchups per champion (for badges on roster cards).</summary>
    public async Task<IReadOnlyDictionary<string, (int Builds, int Matchups)>> GetChampionCountsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var builds = await db.Builds.GroupBy(b => b.ChampionId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        var matchups = await db.Matchups.GroupBy(m => m.ChampionId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in builds) result[b.Key] = (b.Count, 0);
        foreach (var m in matchups) result[m.Key] = (result.TryGetValue(m.Key, out var v) ? v.Item1 : 0, m.Count);
        return result;
    }

    /// <summary>Inserts (Id = 0) or updates a build. Returns the stored id.</summary>
    public Task<int> SaveBuildAsync(Build build, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var copy = Copy(build);
        copy.Name = Clean(copy.Name, 80, "Build");
        copy.UpdatedAt = DateTimeOffset.UtcNow;
        if (copy.Id == 0)
        {
            var max = await db.Builds.Where(b => b.ChampionId == copy.ChampionId).Select(b => (int?)b.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
            copy.SortOrder = max + 1;
            db.Builds.Add(copy);
        }
        else if (await db.Builds.AnyAsync(b => b.Id == copy.Id, ct).ConfigureAwait(false))
        {
            db.Builds.Update(copy);
        }
        else
        {
            return 0; // deleted meanwhile
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return copy.Id;
    }, ct);

    /// <summary>All builds of all champions (for searching notes).</summary>
    public async Task<IReadOnlyList<Build>> GetAllBuildsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Builds.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a build as the default for its role (clearing any other default of the same champion and role), or
    /// clears it. Doesn't touch the build's patch or edit time.
    /// </summary>
    public Task SetDefaultBuildAsync(int buildId, bool isDefault, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var build = await db.Builds.FirstOrDefaultAsync(b => b.Id == buildId, ct).ConfigureAwait(false);
        if (build is null) return 0;
        if (isDefault)
        {
            var championId = build.ChampionId;
            var role = build.Role;
            await db.Builds.Where(b => b.ChampionId == championId && b.Role == role && b.Id != buildId && b.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDefault, false), ct).ConfigureAwait(false);
        }
        build.IsDefault = isDefault;
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }, ct);

    /// <summary>
    /// Sets the patch builds are considered up to date with (Patch review → Mark reviewed, and its Undo) without
    /// touching anything else.
    /// </summary>
    public Task SetBuildPatchesAsync(IReadOnlyDictionary<int, string?> patches, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var total = 0;
        foreach (var (id, version) in patches)
            total += await db.Builds.Where(b => b.Id == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.GameVersion, version), ct).ConfigureAwait(false);
        return total;
    }, ct);

    public Task DeleteBuildAsync(int buildId, CancellationToken ct = default) => WriteAsync(async db =>
        await db.Builds.Where(b => b.Id == buildId).ExecuteDeleteAsync(ct).ConfigureAwait(false), ct);

    public Task ReorderBuildsAsync(IReadOnlyList<int> buildIds, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var builds = await db.Builds.Where(b => buildIds.Contains(b.Id)).ToListAsync(ct).ConfigureAwait(false);
        foreach (var b in builds) b.SortOrder = buildIds.IndexOf(b.Id);
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }, ct);

    // ── Backup support ──────────────────────────────────────────────────────────────────────────────

    public async Task<RosterBackup> ExportAsync(string? gameVersion, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var groups = await GetGroupsAsync(ct).ConfigureAwait(false);
        var rosters = new List<BackupRoster>();
        foreach (var roster in await GetRostersAsync(ct).ConfigureAwait(false))
            rosters.Add(new BackupRoster { Name = roster.Name, Groups = (await GetGroupsAsync(roster.Id, ct).ConfigureAwait(false)).Select(ToBackup).ToList() });
        return new RosterBackup
        {
            SchemaVersion = RosterBackup.CurrentSchemaVersion,
            ExportedAt = DateTimeOffset.Now,
            GameVersion = gameVersion,
            Groups = groups.Select(ToBackup).ToList(),
            Rosters = rosters,
            Profiles = await db.Profiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            Matchups = (await db.Matchups.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).OrderBy(m => m.ChampionId).ThenBy(m => m.SortOrder).ToList(),
            Builds = (await db.Builds.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).OrderBy(b => b.ChampionId).ThenBy(b => b.SortOrder).ToList(),
        };
    }

    /// <summary>Replaces all user data with the backup's content (in one transaction).</summary>
    public Task ImportAsync(RosterBackup backup, CancellationToken ct = default) => WriteAsync(async db =>
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Entries.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Groups.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Rosters.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Profiles.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Matchups.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Builds.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var sourceRosters = backup.Rosters is { Count: > 0 }
            ? backup.Rosters
            : [new BackupRoster { Name = DefaultRosterName, Groups = backup.Groups }];
        var rosterOrder = 0;
        var rosters = new List<(Roster Roster, List<BackupGroup> Groups)>();
        foreach (var r in sourceRosters)
        {
            var roster = new Roster { Name = Clean(r.Name, 60, "Roster"), SortOrder = rosterOrder++ };
            db.Rosters.Add(roster);
            rosters.Add((roster, r.Groups ?? []));
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        ActiveRosterId = null;

        foreach (var (roster, groups) in rosters)
        {
        var groupOrder = 0;
        foreach (var g in groups)
        {
            var entryOrder = 0;
            db.Groups.Add(new RosterGroup
            {
                RosterId = roster.Id,
                Name = Clean(g.Name, 80, "Group"),
                Role = g.Role,
                Color = g.Color,
                SortOrder = groupOrder++,
                Entries = g.Entries.DistinctBy(e => e.ChampionId, StringComparer.OrdinalIgnoreCase).Select(e => new RosterEntry
                {
                    ChampionId = e.ChampionId,
                    Proficiency = Math.Clamp(e.Proficiency, 0, 5),
                    Priority = e.Priority,
                    AddedAt = e.AddedAt,
                    SortOrder = entryOrder++,
                }).ToList(),
            });
        }
        }
        foreach (var p in backup.Profiles.DistinctBy(p => p.ChampionId, StringComparer.OrdinalIgnoreCase))
            db.Profiles.Add(new ChampionProfile { ChampionId = p.ChampionId, Notes = p.Notes ?? "", Tags = p.Tags ?? [], UpdatedAt = p.UpdatedAt });
        foreach (var m in backup.Matchups.DistinctBy(m => (m.ChampionId.ToLowerInvariant(), m.OpponentId.ToLowerInvariant(), m.Role)))
            db.Matchups.Add(new Matchup { ChampionId = m.ChampionId, OpponentId = m.OpponentId, Role = m.Role, Kind = m.Kind, Note = m.Note ?? "", SortOrder = m.SortOrder, UpdatedAt = m.UpdatedAt });
        foreach (var b in backup.Builds)
        {
            var copy = Copy(b);
            copy.Id = 0;
            db.Builds.Add(copy);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return 0;
    }, ct);

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    // ── Shared roster files ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a shared roster, all or nothing: as a new roster named <paramref name="newName"/>, or merged into
    /// <paramref name="mergeIntoRosterId"/> (groups matched by name, then by role; champions already in a group are left
    /// alone). Builds are added next to existing ones (never replacing them); identical builds are skipped.
    /// </summary>
    /// <param name="canonicalChampion">Maps a champion id to the current patch's id; null = unknown champion (skipped).</param>
    public Task<RosterImportResult> ImportSharedRosterAsync(Core.Rules.SharedRosterFile file, int? mergeIntoRosterId, string newName, bool includeBuilds,
        Func<string, string?> canonicalChampion, CancellationToken ct = default) => WriteAsync(async db =>
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        int rosterId;
        if (mergeIntoRosterId is { } target && await db.Rosters.AnyAsync(r => r.Id == target, ct).ConfigureAwait(false))
        {
            rosterId = target;
        }
        else
        {
            var maxRoster = await db.Rosters.Select(r => (int?)r.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
            var roster = new Roster { Name = Clean(newName, 60, "Shared roster"), SortOrder = maxRoster + 1 };
            db.Rosters.Add(roster);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            rosterId = roster.Id;
        }

        var groups = await db.Groups.Include(g => g.Entries).Where(g => g.RosterId == rosterId).ToListAsync(ct).ConfigureAwait(false);
        var nextGroupOrder = groups.Count == 0 ? 0 : groups.Max(g => g.SortOrder) + 1;
        int groupsAdded = 0, placementsAdded = 0;
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shared in file.Groups)
        {
            var group = groups.FirstOrDefault(g => string.Equals(g.Name, shared.Name, StringComparison.OrdinalIgnoreCase))
                        ?? (shared.Role is { } role ? groups.FirstOrDefault(g => g.Role == role) : null);
            if (group is null)
            {
                group = new RosterGroup { RosterId = rosterId, Name = Clean(shared.Name, 80, "Group"), Role = shared.Role, Color = shared.Color, SortOrder = nextGroupOrder++ };
                db.Groups.Add(group);
                groups.Add(group);
                groupsAdded++;
            }
            var nextEntryOrder = group.Entries.Count == 0 ? 0 : group.Entries.Max(e => e.SortOrder) + 1;
            foreach (var entry in shared.Entries)
            {
                if (canonicalChampion(entry.ChampionId) is not { } championId)
                {
                    skipped.Add(entry.ChampionId);
                    continue;
                }
                if (group.Entries.Any(e => string.Equals(e.ChampionId, championId, StringComparison.OrdinalIgnoreCase))) continue;
                group.Entries.Add(new RosterEntry
                {
                    ChampionId = championId,
                    Proficiency = Math.Clamp(entry.Proficiency ?? 0, 0, 5),
                    Priority = entry.Priority ?? ChampionPriority.Secondary,
                    SortOrder = nextEntryOrder++,
                });
                placementsAdded++;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        int buildsAdded = 0, buildsExisting = 0;
        if (includeBuilds && file.Builds.Count > 0)
        {
            var champions = file.Builds.Select(b => canonicalChampion(b.ChampionId)).Where(c => c is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existing = (await db.Builds.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
                .Where(b => champions.Contains(b.ChampionId)).ToList();
            var signatures = existing.Select(BuildSignature).ToHashSet(StringComparer.Ordinal);
            var nextOrder = existing.GroupBy(b => b.ChampionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(b => b.SortOrder) + 1, StringComparer.OrdinalIgnoreCase);
            foreach (var shared in file.Builds)
            {
                if (canonicalChampion(shared.ChampionId) is not { } championId)
                {
                    skipped.Add(shared.ChampionId);
                    continue;
                }
                var build = Copy(shared);
                build.Id = 0;
                build.ChampionId = championId;
                build.IsDefault = false;
                build.Name = Clean(build.Name, 80, "Shared build");
                build.UpdatedAt = DateTimeOffset.UtcNow;
                if (!signatures.Add(BuildSignature(build)))
                {
                    buildsExisting++;
                    continue;
                }
                build.SortOrder = nextOrder.GetValueOrDefault(championId);
                nextOrder[championId] = build.SortOrder + 1;
                db.Builds.Add(build);
                buildsAdded++;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new RosterImportResult(rosterId, groupsAdded, placementsAdded, buildsAdded, buildsExisting, skipped.Count);
    }, ct);

    /// <summary>What makes two builds the same for import purposes (name and content, not notes or edit time).</summary>
    private static string BuildSignature(Build b) => string.Join("|",
        b.ChampionId.ToLowerInvariant(), b.Name.Trim(), b.Role?.ToString() ?? "",
        System.Text.Json.JsonSerializer.Serialize(b.Runes), System.Text.Json.JsonSerializer.Serialize(b.Items),
        string.Join(",", b.SummonerSpells), b.SkillOrder ?? "");

    private async Task<T> WriteAsync<T>(Func<RosterDbContext, Task<T>> action, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var result = await action(db).ConfigureAwait(false);
            Written?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            _logger.LogError(ex, "Database write failed");
            throw;
        }
        finally
        {
            _write.Release();
        }
    }

    private static string Clean(string? value, int max, string fallback)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) v = fallback;
        return v.Length > max ? v[..max] : v;
    }

    private static BackupGroup ToBackup(RosterGroup g) => new()
    {
        Name = g.Name,
        Role = g.Role,
        Color = g.Color,
        Entries = g.Entries.Select(e => new BackupEntry { ChampionId = e.ChampionId, Proficiency = e.Proficiency, Priority = e.Priority, AddedAt = e.AddedAt }).ToList(),
    };

    private static Build Copy(Build b) => new()
    {
        Id = b.Id,
        ChampionId = b.ChampionId,
        Name = b.Name,
        Role = b.Role,
        SortOrder = b.SortOrder,
        IsDefault = b.IsDefault,
        GameVersion = b.GameVersion,
        Runes = (b.Runes ?? new RunePageData()).Clone(),
        Items = (b.Items ?? new ItemSetData()).Clone(),
        SummonerSpells = [.. b.SummonerSpells ?? ["", ""]],
        SkillOrder = b.SkillOrder ?? "",
        Notes = b.Notes ?? "",
        UpdatedAt = b.UpdatedAt,
    };
}

internal static class ListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
            if (EqualityComparer<T>.Default.Equals(list[i], value)) return i;
        return -1;
    }
}

/// <summary>Portable JSON backup of all user data. Ids are not preserved (champions/items/runes use Data Dragon ids).</summary>
public sealed class RosterBackup
{
    public const int CurrentSchemaVersion = 1;
    public string Application { get; set; } = "Roster Control";
    public int SchemaVersion { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public string? GameVersion { get; set; }
    /// <summary>The active roster's groups (what versions before 1.3 import).</summary>
    public List<BackupGroup> Groups { get; set; } = [];
    /// <summary>Every roster with its groups (1.3+). When empty, <see cref="Groups"/> is imported as one roster.</summary>
    public List<BackupRoster>? Rosters { get; set; }
    public List<ChampionProfile> Profiles { get; set; } = [];
    public List<Matchup> Matchups { get; set; } = [];
    public List<Build> Builds { get; set; } = [];
}

public sealed class BackupRoster
{
    public string Name { get; set; } = "";
    public List<BackupGroup> Groups { get; set; } = [];
}

public sealed class BackupGroup
{
    public string Name { get; set; } = "";
    public Position? Role { get; set; }
    public string? Color { get; set; }
    public List<BackupEntry> Entries { get; set; } = [];
}

public sealed class BackupEntry
{
    public string ChampionId { get; set; } = "";
    public int Proficiency { get; set; }
    public ChampionPriority Priority { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>What importing a shared roster added.</summary>
/// <param name="ChampionsSkipped">Champions not in the current patch (left out).</param>
public sealed record RosterImportResult(int RosterId, int GroupsAdded, int PlacementsAdded, int BuildsAdded, int BuildsAlreadyThere, int ChampionsSkipped);
