namespace RosterControl.Core.Rules;

/// <param name="Body">The section's text (bullets with <c>- </c> and <c>**bold**</c>, see <see cref="NoteFormat"/>).</param>
public sealed record ChangelogEntry(Version Version, string Body);

/// <summary>Reads CHANGELOG.md: one <c>## x.y.z</c> section per version, newest first.</summary>
public static class Changelog
{
    public static IReadOnlyList<ChangelogEntry> Parse(string? text)
    {
        var entries = new List<ChangelogEntry>();
        Version? current = null;
        var body = new List<string>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                current = TryParseVersion(raw[3..].Trim().Split(' ', '—', '-')[0], out var v) ? v : null;
                continue;
            }
            if (raw.StartsWith("# ", StringComparison.Ordinal)) continue;
            if (current is not null) body.Add(raw);
        }
        Flush();
        return entries.OrderByDescending(e => e.Version).ToList();

        void Flush()
        {
            if (current is not null) entries.Add(new ChangelogEntry(current, string.Join('\n', body).Trim()));
            body.Clear();
        }
    }

    /// <summary>Entries newer than <paramref name="after"/> (all when null) up to and including <paramref name="upTo"/>.</summary>
    public static IReadOnlyList<ChangelogEntry> Between(IReadOnlyList<ChangelogEntry> entries, Version? after, Version upTo) =>
        entries.Where(e => (after is null || e.Version > after) && e.Version <= upTo).ToList();

    /// <summary>Parses "1.3.0", "v1.3.0" or "1.3.0-beta+abc" (the pre-release part is ignored).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }
}
