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

    private readonly IDbContextFactory<RosterDbContext> _factory;
    private readonly ILogger<RosterRepository> _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private bool _initialized;

    public RosterRepository(IDbContextFactory<RosterDbContext> factory, ILogger<RosterRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>Applies migrations and, on first run, creates one roster group per role.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            if (!await db.Preferences.AnyAsync(p => p.Key == SeededPreferenceKey, ct).ConfigureAwait(false))
            {
                if (!await db.Groups.AnyAsync(ct).ConfigureAwait(false))
                {
                    var order = 0;
                    foreach (var (name, role, color) in DefaultGroups)
                        db.Groups.Add(new RosterGroup { Name = name, Role = role, Color = color, SortOrder = order++ });
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

    // ── Groups & entries ────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RosterGroup>> GetGroupsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var groups = await db.Groups.AsNoTracking().Include(g => g.Entries).ToListAsync(ct).ConfigureAwait(false);
        foreach (var g in groups) g.Entries = g.Entries.OrderBy(e => e.SortOrder).ThenBy(e => e.Id).ToList();
        return groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToList();
    }

    public Task<RosterGroup> AddGroupAsync(string name, Position? role, string? color, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var max = await db.Groups.Select(g => (int?)g.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
        var group = new RosterGroup { Name = Clean(name, 80, "New group"), Role = role, Color = color, SortOrder = max + 1 };
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
        return new RosterBackup
        {
            SchemaVersion = RosterBackup.CurrentSchemaVersion,
            ExportedAt = DateTimeOffset.Now,
            GameVersion = gameVersion,
            Groups = groups.Select(g => new BackupGroup
            {
                Name = g.Name,
                Role = g.Role,
                Color = g.Color,
                Entries = g.Entries.Select(e => new BackupEntry { ChampionId = e.ChampionId, Proficiency = e.Proficiency, Priority = e.Priority, AddedAt = e.AddedAt }).ToList(),
            }).ToList(),
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
        await db.Profiles.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Matchups.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Builds.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var groupOrder = 0;
        foreach (var g in backup.Groups)
        {
            var entryOrder = 0;
            db.Groups.Add(new RosterGroup
            {
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

    private async Task<T> WriteAsync<T>(Func<RosterDbContext, Task<T>> action, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await action(db).ConfigureAwait(false);
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
    public List<BackupGroup> Groups { get; set; } = [];
    public List<ChampionProfile> Profiles { get; set; } = [];
    public List<Matchup> Matchups { get; set; } = [];
    public List<Build> Builds { get; set; } = [];
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
