using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RosterControl.Core.Model;

namespace RosterControl.Core.Rules;

/// <summary>
/// Short text codes for sharing a build (e.g. in Discord): <c>RC1:</c> followed by base64url-encoded, deflated JSON.
/// Codes are found anywhere in pasted text, so a whole chat message can be pasted. Ids are Data Dragon ids, so a code
/// works on any patch (removed items show up as missing, like in an older saved build).
/// </summary>
public static partial class BuildShareCode
{
    public const string Prefix = "RC1:";
    private const int MaxDecodedBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    [GeneratedRegex(@"RC1:(?<data>[A-Za-z0-9_\-]{8,})")]
    private static partial Regex CodeRegex();

    public static string Encode(Build build)
    {
        var dto = new SharedBuild
        {
            C = build.ChampionId,
            N = build.Name,
            R = build.Role,
            V = build.GameVersion,
            P = build.Runes.PrimaryStyleId,
            S = build.Runes.SubStyleId,
            Pp = build.Runes.PrimaryPerks,
            Sp = build.Runes.SubPerks,
            Sh = build.Runes.Shards,
            B = build.Items.Blocks.Select(b => new SharedBlock { N = b.Name, I = b.Items }).ToList(),
            Ss = build.SummonerSpells,
            K = string.IsNullOrEmpty(build.SkillOrder) ? null : build.SkillOrder,
            T = string.IsNullOrEmpty(build.Notes) ? null : build.Notes,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);
        return Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>True when the text contains something that looks like a share code.</summary>
    public static bool ContainsCode(string? text) => text is not null && CodeRegex().IsMatch(text);

    /// <summary>Decodes the first code found in <paramref name="text"/>. The returned build is new (Id 0, not default).</summary>
    public static bool TryDecode(string? text, out Build build, out string? error)
    {
        build = new Build();
        error = null;
        var match = text is null ? Match.Empty : CodeRegex().Match(text);
        if (!match.Success)
        {
            error = "No build code found. Codes start with RC1:";
            return false;
        }
        try
        {
            var data = match.Groups["data"].Value.Replace('-', '+').Replace('_', '/');
            data = data.PadRight(data.Length + (4 - data.Length % 4) % 4, '=');
            var compressed = Convert.FromBase64String(data);
            using var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var json = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                json.Write(buffer, 0, read);
                if (json.Length > MaxDecodedBytes) throw new InvalidDataException("too large");
            }
            var dto = JsonSerializer.Deserialize<SharedBuild>(json.ToArray(), Json) ?? throw new InvalidDataException("empty");
            if (string.IsNullOrWhiteSpace(dto.C)) throw new InvalidDataException("no champion");
            build = new Build
            {
                ChampionId = dto.C.Trim(),
                Name = Trim(dto.N, 80) is { Length: > 0 } name ? name : "Shared build",
                Role = dto.R,
                GameVersion = dto.V,
                Runes = new RunePageData
                {
                    PrimaryStyleId = dto.P,
                    SubStyleId = dto.S,
                    PrimaryPerks = Fixed(dto.Pp, 4),
                    SubPerks = (dto.Sp ?? []).Take(2).ToList(),
                    Shards = Fixed(dto.Sh, 3),
                },
                Items = new ItemSetData
                {
                    Blocks = (dto.B ?? []).Take(12).Select(b => new ItemBlockData
                    {
                        Name = Trim(b.N, 60),
                        Items = (b.I ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Take(30).Select(i => i.Trim()).ToList(),
                    }).ToList(),
                },
                SummonerSpells = [.. (dto.Ss ?? []).Concat(["", ""]).Take(2).Select(s => s ?? "")],
                SkillOrder = new string((dto.K ?? "").Where(ch => ch is 'Q' or 'W' or 'E' or 'R' or '-').Take(18).ToArray()),
                Notes = Trim(dto.T, 4000),
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException or IOException)
        {
            error = "That build code is damaged or incomplete.";
            return false;
        }
    }

    private static string Trim(string? value, int max)
    {
        var v = (value ?? "").Trim();
        return v.Length > max ? v[..max] : v;
    }

    private static List<int> Fixed(List<int>? values, int count) => [.. (values ?? []).Concat(Enumerable.Repeat(0, count)).Take(count)];

    // Short property names keep codes small.
    private sealed class SharedBuild
    {
        public string C { get; set; } = "";
        public string? N { get; set; }
        public Position? R { get; set; }
        public string? V { get; set; }
        public int? P { get; set; }
        public int? S { get; set; }
        public List<int>? Pp { get; set; }
        public List<int>? Sp { get; set; }
        public List<int>? Sh { get; set; }
        public List<SharedBlock>? B { get; set; }
        public List<string>? Ss { get; set; }
        public string? K { get; set; }
        public string? T { get; set; }
    }

    private sealed class SharedBlock
    {
        public string? N { get; set; }
        public List<string>? I { get; set; }
    }
}
