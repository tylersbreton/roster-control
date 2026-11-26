using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RosterControl.Core.Model;

namespace RosterControl.Data;

/// <summary>
/// SQLite database (single file, <c>data\roster.db</c> next to the executable). Nested build data (rune page, item
/// set, spells) is stored as JSON columns so the schema stays small and patch changes never need migrations.
/// </summary>
public sealed class RosterDbContext(DbContextOptions<RosterDbContext> options) : DbContext(options)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    public DbSet<Roster> Rosters => Set<Roster>();
    public DbSet<RosterGroup> Groups => Set<RosterGroup>();
    public DbSet<RosterEntry> Entries => Set<RosterEntry>();
    public DbSet<ChampionProfile> Profiles => Set<ChampionProfile>();
    public DbSet<Matchup> Matchups => Set<Matchup>();
    public DbSet<Build> Builds => Set<Build>();
    public DbSet<Preference> Preferences => Set<Preference>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Roster>(e =>
        {
            e.ToTable("Rosters");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(60).IsRequired();
            e.HasMany<RosterGroup>().WithOne().HasForeignKey(x => x.RosterId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RosterGroup>(e =>
        {
            e.ToTable("RosterGroups");
            e.HasIndex(x => x.RosterId);
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.Color).HasMaxLength(9);
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RosterEntry>(e =>
        {
            e.ToTable("RosterEntries");
            e.HasKey(x => x.Id);
            e.Property(x => x.ChampionId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.GroupId, x.ChampionId }).IsUnique();
            e.HasIndex(x => x.ChampionId);
        });

        b.Entity<ChampionProfile>(e =>
        {
            e.ToTable("ChampionProfiles");
            e.HasKey(x => x.ChampionId);
            e.Property(x => x.ChampionId).HasMaxLength(64);
            e.Property(x => x.Tags).HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
        });

        b.Entity<Matchup>(e =>
        {
            e.ToTable("Matchups");
            e.HasKey(x => x.Id);
            e.Property(x => x.ChampionId).HasMaxLength(64).IsRequired();
            e.Property(x => x.OpponentId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.ChampionId, x.OpponentId, x.Role }).IsUnique(); // one per role (null = all roles)
            e.HasIndex(x => x.OpponentId);
        });

        b.Entity<Build>(e =>
        {
            e.ToTable("Builds");
            e.HasKey(x => x.Id);
            e.Property(x => x.ChampionId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.SkillOrder).HasMaxLength(18);
            e.Property(x => x.Runes).HasColumnName("RunesJson").HasConversion(JsonConverter<RunePageData>(), JsonComparer<RunePageData>());
            e.Property(x => x.Items).HasColumnName("ItemsJson").HasConversion(JsonConverter<ItemSetData>(), JsonComparer<ItemSetData>());
            e.Property(x => x.SummonerSpells).HasColumnName("SummonerSpellsJson").HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
            e.HasIndex(x => x.ChampionId);
        });

        b.Entity<Preference>(e =>
        {
            e.ToTable("Preferences");
            e.HasKey(x => x.Key);
        });
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : new() => new(
        v => JsonSerializer.Serialize(v, Json),
        v => string.IsNullOrEmpty(v) ? new T() : JsonSerializer.Deserialize<T>(v, Json) ?? new T());

    private static ValueComparer<T> JsonComparer<T>() where T : new() => new(
        (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
        v => JsonSerializer.Serialize(v, Json).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json) ?? new T());
}

/// <summary>Used only by <c>dotnet ef migrations</c>.</summary>
public sealed class DesignTimeRosterDbContextFactory : IDesignTimeDbContextFactory<RosterDbContext>
{
    public RosterDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RosterDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
