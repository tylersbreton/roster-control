using System.Text.RegularExpressions;

namespace RosterControl.Tests;

/// <summary>The GitHub Pages site (docs/index.html) only shows screenshots that the release script keeps in docs/images.</summary>
public partial class WebsiteTests
{
    private static string Repo(params string[] parts) =>
        Path.Combine([TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", .. parts]);

    [GeneratedRegex(@"images/([\w.-]+)\.png")]
    private static partial Regex ImageRef();

    [Test]
    public void Website_images_are_kept_by_the_screenshot_script()
    {
        var html = File.ReadAllText(Repo("docs", "index.html"));
        var listed = File.ReadAllLines(Repo("tools", "readme-screenshots.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();
        var used = ImageRef().Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.That(used, Is.Not.Empty);
        Assert.That(used.Where(n => !listed.Contains(n)), Is.Empty, "tools/update-readme-screenshots.ps1 deletes unlisted images");
    }
}
