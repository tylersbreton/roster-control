using System.Text.Json;
using System.Text.Json.Serialization;
using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>
/// A roster shared as a file (<c>.rcroster</c>): its groups and champions, optionally the ratings and the builds of those
/// champions. Plain JSON with a format version, so files from older versions keep importing; unknown fields are ignored.
/// Ids are Data Dragon ids, so a file works on any patch (champions missing from the current patch are skipped).
/// </summary>
public sealed class SharedRosterFile
{
    public const string Extension = "rcroster";
    public const string Kind = "roster";
    public const int CurrentFormat = 1;
    public const long MaxFileBytes = 4 * 1024 * 1024;

    public string Application { get; set; } = "Roster Control";
    /// <summary>"roster"; empty in anything that isn't a roster file.</summary>
    public string FileKind { get; set; } = "";
    public int Format { get; set; } = CurrentFormat;
    public string? AppVersion { get; set; }
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? GameVersion { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Whether proficiency and priority were shared (otherwise placements import unrated).</summary>
    public bool IncludesRatings { get; set; }
    public List<SharedGroup> Groups { get; set; } = [];
    public List<Build> Builds { get; set; } = [];

    public sealed class SharedGroup
    {
        public string Name { get; set; } = "";
        public Position? Role { get; set; }
        public string? Color { get; set; }
        public List<SharedEntry> Entries { get; set; } = [];
    }

    public sealed class SharedEntry
    {
        public string ChampionId { get; set; } = "";
        public int? Proficiency { get; set; }
        public ChampionPriority? Priority { get; set; }
    }

    public int ChampionCount => Groups.SelectMany(g => g.Entries).Select(e => e.ChampionId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public bool IsFromNewerVersion => Format > CurrentFormat;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="includeRatings">Share proficiency and priority.</param>
    /// <param name="builds">Builds to include (only those of champions in the roster are kept).</param>
    public static SharedRosterFile Create(string name, IReadOnlyList<RosterGroup> groups, bool includeRatings, IEnumerable<Build>? builds, string? appVersion, string? gameVersion)
    {
        var file = new SharedRosterFile
        {
            FileKind = Kind,
            Name = name,
            AppVersion = appVersion,
            GameVersion = gameVersion,
            IncludesRatings = includeRatings,
            Groups = groups.Select(g => new SharedGroup
            {
                Name = g.Name,
                Role = g.Role,
                Color = g.Color,
                Entries = g.Entries.Select(e => new SharedEntry
                {
                    ChampionId = e.ChampionId,
                    Proficiency = includeRatings ? e.Proficiency : null,
                    Priority = includeRatings ? e.Priority : null,
                }).ToList(),
            }).ToList(),
        };
        if (builds is not null)
        {
            var champions = file.Groups.SelectMany(g => g.Entries).Select(e => e.ChampionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            file.Builds = builds.Where(b => champions.Contains(b.ChampionId)).Select(b => new Build
            {
                ChampionId = b.ChampionId,
                Name = b.Name,
                Role = b.Role,
                GameVersion = b.GameVersion,
                Runes = b.Runes.Clone(),
                Items = b.Items.Clone(),
                SummonerSpells = [.. b.SummonerSpells],
                SkillOrder = b.SkillOrder,
                Notes = "", // notes may be personal: never shared
            }).ToList();
        }
        return file;
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads and cleans a file (names trimmed, counts and ratings clamped). False with a reason when it isn't a roster file.</summary>
    public static bool TryParse(string text, out SharedRosterFile file, out string? error)
    {
        file = new SharedRosterFile();
        error = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<SharedRosterFile>(text, Json);
            if (parsed is null || !string.Equals(parsed.FileKind, Kind, StringComparison.OrdinalIgnoreCase) || parsed.Groups is null)
            {
                error = "That isn't a Roster Control roster file.";
                return false;
            }
            parsed.Name = Clean(parsed.Name, 60, "Shared roster");
            parsed.Groups = parsed.Groups.Where(g => g is not null).Take(30).Select(g => new SharedGroup
            {
                Name = Clean(g.Name, 80, "Group"),
                Role = g.Role is { } r && Enum.IsDefined(r) ? r : null,
                Color = g.Color is { Length: 7 or 9 } c && c.StartsWith('#') ? c : null,
                Entries = (g.Entries ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.ChampionId)).Take(200).Select(e => new SharedEntry
                {
                    ChampionId = e.ChampionId.Trim(),
                    Proficiency = e.Proficiency is { } p ? Math.Clamp(p, 0, 5) : null,
                    Priority = e.Priority is { } pr && Enum.IsDefined(pr) ? pr : null,
                }).ToList(),
            }).ToList();
            parsed.Builds = (parsed.Builds ?? []).Where(b => b is not null && !string.IsNullOrWhiteSpace(b.ChampionId)).Take(500).Select(b =>
            {
                b.Id = 0;
                b.IsDefault = false;
                b.ChampionId = b.ChampionId.Trim();
                b.Name = Clean(b.Name, 80, "Shared build");
                b.Runes ??= new RunePageData();
                b.Items ??= new ItemSetData();
                b.SummonerSpells = [.. (b.SummonerSpells ?? []).Concat(["", ""]).Take(2).Select(s => s ?? "")];
                b.SkillOrder = new string((b.SkillOrder ?? "").Where(ch => ch is 'Q' or 'W' or 'E' or 'R' or '-').Take(18).ToArray());
                b.Notes = "";
                return b;
            }).ToList();
            file = parsed;
            return true;
        }
        catch (JsonException)
        {
            error = "That roster file is damaged or isn't a roster file.";
            return false;
        }
    }

    private static string Clean(string? value, int max, string fallback)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return fallback;
        return v.Length > max ? v[..max] : v;
    }
}
