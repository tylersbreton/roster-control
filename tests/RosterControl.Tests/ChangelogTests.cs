using RosterControl.Core.Rules;

namespace RosterControl.Tests;

public class ChangelogTests
{
    private const string Sample = """
        # Changelog

        Intro text that isn't a version.

        ## 1.3.0 — 2026-11-26

        - **Several rosters**
        - Compare

        ## 1.2.0
        - Share codes

        ## 1.1.0
        - Quick search
        """;

    [Test]
    public void Sections_are_parsed_newest_first()
    {
        var entries = Changelog.Parse(Sample);
        Assert.That(entries.Select(e => e.Version.ToString()), Is.EqualTo(new[] { "1.3.0", "1.2.0", "1.1.0" }));
        Assert.That(entries[0].Body, Is.EqualTo("- **Several rosters**\n- Compare"));
    }

    [Test]
    public void Only_versions_since_the_last_one_seen_are_shown()
    {
        var entries = Changelog.Parse(Sample);
        Assert.That(Changelog.Between(entries, new Version(1, 1, 0), new Version(1, 3, 0)).Select(e => e.Version.ToString()), Is.EqualTo(new[] { "1.3.0", "1.2.0" }));
        Assert.That(Changelog.Between(entries, new Version(1, 3, 0), new Version(1, 3, 0)), Is.Empty);
        Assert.That(Changelog.Between(entries, null, new Version(1, 2, 0)), Has.Count.EqualTo(2), "nothing newer than the running version");
    }

    [TestCase("1.3.0", "1.3.0")]
    [TestCase("v1.3", "1.3.0")]
    [TestCase("1.3.0-beta.1+abc", "1.3.0")]
    public void Versions_are_parsed_leniently(string text, string expected)
    {
        Assert.That(Changelog.TryParseVersion(text, out var v), Is.True);
        Assert.That(v.ToString(3), Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("dev")]
    [TestCase("")]
    public void Non_versions_are_rejected(string? text) => Assert.That(Changelog.TryParseVersion(text, out _), Is.False);

    [Test]
    public void The_repository_changelog_parses_and_covers_the_current_version()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "CHANGELOG.md");
        var entries = Changelog.Parse(File.ReadAllText(path));
        Assert.That(entries, Has.Count.GreaterThanOrEqualTo(4));
        Assert.That(entries.All(e => e.Body.Length > 0), Is.True);
        var props = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "Directory.Build.props"));
        var version = System.Text.RegularExpressions.Regex.Match(props, "<Version>(.*?)</Version>").Groups[1].Value;
        Assert.That(entries.Select(e => e.Version.ToString(3)), Does.Contain(version), "add a CHANGELOG section for the version in Directory.Build.props");
    }
}
