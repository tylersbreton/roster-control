using RosterControl.Core.Model;
using RosterControl.Core.Rules;

namespace RosterControl.Tests;

public class NoteFormatTests
{
    [Test]
    public void Lines_are_parsed_into_bullets_checkboxes_and_text()
    {
        var lines = NoteFormat.Parse("Laning\n- poke with Q\n  * dodge **Zed R**\n[ ] ward river\n[x] buy control ward\n\nplain\n\n");
        Assert.That(lines.Select(l => l.Kind), Is.EqualTo(new[]
        {
            NoteLineKind.Text, NoteLineKind.Bullet, NoteLineKind.Bullet, NoteLineKind.Check, NoteLineKind.Check, NoteLineKind.Blank, NoteLineKind.Text,
        }), "trailing blank lines are dropped");
        Assert.That(lines[2].Indent, Is.EqualTo(1));
        Assert.That(lines[2].Content, Is.EqualTo("dodge **Zed R**"));
        Assert.That(lines[3].Checked, Is.False);
        Assert.That(lines[4].Checked, Is.True);
        Assert.That(lines[4].Content, Is.EqualTo("buy control ward"));
    }

    [Test]
    public void Windows_line_endings_are_handled()
    {
        Assert.That(NoteFormat.Parse("- a\r\n- b").Select(l => l.Content), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Bold_spans_are_parsed_and_unmatched_markers_kept()
    {
        Assert.That(NoteFormat.ParseInline("all-in at **6** with **ignite**"), Is.EqualTo(new[]
        {
            new NoteSpan("all-in at ", false), new NoteSpan("6", true), new NoteSpan(" with ", false), new NoteSpan("ignite", true),
        }));
        Assert.That(string.Concat(NoteFormat.ParseInline("a **b** c **d").Select(s => s.Text)), Is.EqualTo("a b c **d"));
        Assert.That(NoteFormat.ParseInline("no markers"), Is.EqualTo(new[] { new NoteSpan("no markers", false) }));
    }

    [Test]
    public void Checkboxes_toggle_in_place()
    {
        var text = "intro\n[ ] ward\n  [x] recall";
        Assert.That(NoteFormat.ToggleCheck(text, 1), Is.EqualTo("intro\n[x] ward\n  [x] recall"));
        Assert.That(NoteFormat.ToggleCheck(text, 2), Is.EqualTo("intro\n[ ] ward\n  [ ] recall"));
        Assert.That(NoteFormat.ToggleCheck(text, 0), Is.EqualTo(text), "not a checkbox line");
    }

    [Test]
    public void List_markers_toggle_on_every_selected_line()
    {
        var edit = NoteFormat.ToggleList("one\ntwo\nthree", 1, 6, NoteLineKind.Bullet);
        Assert.That(edit.Text, Is.EqualTo("- one\n- two\nthree"));
        var back = NoteFormat.ToggleList(edit.Text, edit.SelectionStart, edit.SelectionEnd, NoteLineKind.Bullet);
        Assert.That(back.Text, Is.EqualTo("one\ntwo\nthree"));
        Assert.That(NoteFormat.ToggleList("- one", 3, 3, NoteLineKind.Check).Text, Is.EqualTo("[ ] one"), "switches the marker kind");
    }

    [Test]
    public void Bold_wraps_and_unwraps_the_selection()
    {
        var edit = NoteFormat.ToggleBold("dodge Zed R", 6, 11);
        Assert.That(edit.Text, Is.EqualTo("dodge **Zed R**"));
        Assert.That((edit.SelectionStart, edit.SelectionEnd), Is.EqualTo((8, 13)));
        Assert.That(NoteFormat.ToggleBold(edit.Text, edit.SelectionStart, edit.SelectionEnd).Text, Is.EqualTo("dodge Zed R"));
        var empty = NoteFormat.ToggleBold("ab", 1, 1);
        Assert.That(empty.Text, Is.EqualTo("a****b"));
        Assert.That(empty.SelectionStart, Is.EqualTo(3), "caret between the markers");
    }

    [Test]
    public void Enter_continues_and_ends_lists()
    {
        var next = NoteFormat.ContinueList("- poke", 6);
        Assert.That(next!.Value.Text, Is.EqualTo("- poke\n- "));
        Assert.That(next.Value.SelectionStart, Is.EqualTo(9));
        Assert.That(NoteFormat.ContinueList("  [x] done", 10)!.Value.Text, Is.EqualTo("  [x] done\n  [ ] "), "new checkbox is unticked, same indent");
        Assert.That(NoteFormat.ContinueList("- poke\n- ", 9)!.Value.Text, Is.EqualTo("- poke\n"), "Enter on an empty item ends the list");
        Assert.That(NoteFormat.ContinueList("plain", 5), Is.Null);
        Assert.That(NoteFormat.ContinueList("- poke hard", 6), Is.Null, "splitting a line is a normal Enter");
    }

    [Test]
    public void Snippets_strip_markers_and_mark_cuts()
    {
        var note = "- Laning: **respect** level 2. Look for picks with charm from fog after first back, then roam bot.";
        var snippet = NoteFormat.Snippet(note, "CHARM", radius: 12)!;
        Assert.That(snippet, Does.Contain("charm"));
        Assert.That(snippet, Does.StartWith("…").And.EndWith("…"));
        Assert.That(NoteFormat.Snippet(note, "respect"), Does.Not.Contain("**"));
        Assert.That(NoteFormat.Snippet(note, "zed"), Is.Null);
    }
}

public class BuildShareCodeTests
{
    private static Build Sample() => new()
    {
        Id = 42,
        ChampionId = "Ahri",
        Name = "Standard",
        Role = Position.Middle,
        IsDefault = true,
        GameVersion = "16.23.1",
        Runes = new RunePageData { PrimaryStyleId = 8100, SubStyleId = 8200, PrimaryPerks = [8112, 8139, 8138, 8135], SubPerks = [8226, 8210], Shards = [5008, 5008, 5001] },
        Items = new ItemSetData
        {
            Blocks =
            [
                new ItemBlockData { Name = "Starting Items", Items = ["1056", "2003", "2003"] },
                new ItemBlockData { Name = "Core Build", Items = ["3020", "6655", "4645"] },
            ],
        },
        SummonerSpells = ["SummonerFlash", "SummonerDot"],
        SkillOrder = "QWEQQRQWQWRWWEEREE",
        Notes = "- poke with Q\n[ ] ward",
    };

    [Test]
    public void A_build_round_trips_through_a_code()
    {
        var code = BuildShareCode.Encode(Sample());
        Assert.That(code, Does.StartWith("RC1:"));
        Assert.That(code.Length, Is.LessThan(500), "short enough to paste in chat");
        Assert.That(BuildShareCode.TryDecode(code, out var build, out var error), Is.True, error);
        var expected = Sample();
        Assert.That(build.Id, Is.Zero, "a new build");
        Assert.That(build.IsDefault, Is.False, "never default on import");
        Assert.That(build.ChampionId, Is.EqualTo(expected.ChampionId));
        Assert.That(build.Name, Is.EqualTo(expected.Name));
        Assert.That(build.Role, Is.EqualTo(Position.Middle));
        Assert.That(build.GameVersion, Is.EqualTo("16.23.1"));
        Assert.That(build.Runes.PrimaryPerks, Is.EqualTo(expected.Runes.PrimaryPerks));
        Assert.That(build.Runes.SubPerks, Is.EqualTo(expected.Runes.SubPerks));
        Assert.That(build.Runes.Shards, Is.EqualTo(expected.Runes.Shards));
        Assert.That(build.Items.Blocks.Select(b => b.Name), Is.EqualTo(new[] { "Starting Items", "Core Build" }));
        Assert.That(build.Items.Blocks[0].Items, Is.EqualTo(new[] { "1056", "2003", "2003" }));
        Assert.That(build.SummonerSpells, Is.EqualTo(expected.SummonerSpells));
        Assert.That(build.SkillOrder, Is.EqualTo(expected.SkillOrder));
        Assert.That(build.Notes, Is.EqualTo(expected.Notes));
    }

    [Test]
    public void A_code_is_found_inside_a_pasted_message()
    {
        var message = $"try this vs assassins:\nAhri · Standard — Roster Control build: {BuildShareCode.Encode(Sample())} gl!";
        Assert.That(BuildShareCode.ContainsCode(message), Is.True);
        Assert.That(BuildShareCode.TryDecode(message, out var build, out _), Is.True);
        Assert.That(build.Name, Is.EqualTo("Standard"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("just some text")]
    [TestCase("RC1:not-a-real-code-at-all")]
    [TestCase("RC1:AAAAAAAAAAAAAAAA")]
    public void Bad_input_fails_with_a_message(string? text)
    {
        Assert.That(BuildShareCode.TryDecode(text, out _, out var error), Is.False);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void Decoded_values_are_sanitised()
    {
        var build = Sample();
        build.SkillOrder = "QWEZZZ";
        build.Name = new string('x', 200);
        Assert.That(BuildShareCode.TryDecode(BuildShareCode.Encode(build), out var decoded, out _), Is.True);
        Assert.That(decoded.SkillOrder, Is.EqualTo("QWE"));
        Assert.That(decoded.Name, Has.Length.EqualTo(80));
    }
}
