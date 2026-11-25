using System.Text;
using System.Text.RegularExpressions;

namespace RosterControl.Core.Rules;

public enum NoteLineKind
{
    Text,
    Bullet,
    Check,
    Blank,
}

/// <summary>One line of a note. <see cref="Content"/> is the text after the list marker.</summary>
public sealed record NoteLine(int Index, NoteLineKind Kind, int Indent, bool Checked, string Content);

/// <summary>A run of inline text; <see cref="Bold"/> for text between <c>**</c> markers.</summary>
public sealed record NoteSpan(string Text, bool Bold);

/// <summary>An edit result: the new text and the selection to restore.</summary>
public readonly record struct NoteEdit(string Text, int SelectionStart, int SelectionEnd);

/// <summary>
/// Light formatting for notes, stored as plain text so notes stay readable anywhere (backups, share codes, search):
/// <c>- item</c> (or <c>*</c>, <c>•</c>) is a bullet, <c>[ ] task</c> / <c>[x] done</c> is a checkbox and <c>**text**</c> is bold.
/// Leading spaces indent bullets and checkboxes.
/// </summary>
public static partial class NoteFormat
{
    public const string BulletPrefix = "- ";
    public const string CheckPrefix = "[ ] ";

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:[-*•])\s+(?<content>.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)\[(?<mark>[ xX])\]\s?(?<content>.*)$")]
    private static partial Regex CheckRegex();

    public static IReadOnlyList<NoteLine> Parse(string? text)
    {
        var lines = SplitLines(text ?? "");
        var result = new List<NoteLine>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                result.Add(new NoteLine(i, NoteLineKind.Blank, 0, false, ""));
                continue;
            }
            var check = CheckRegex().Match(line);
            if (check.Success)
            {
                result.Add(new NoteLine(i, NoteLineKind.Check, IndentOf(check.Groups["indent"].Value), check.Groups["mark"].Value != " ", check.Groups["content"].Value));
                continue;
            }
            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                result.Add(new NoteLine(i, NoteLineKind.Bullet, IndentOf(bullet.Groups["indent"].Value), false, bullet.Groups["content"].Value));
                continue;
            }
            result.Add(new NoteLine(i, NoteLineKind.Text, 0, false, line));
        }
        // Trailing blank lines add nothing to the rendered view.
        while (result.Count > 0 && result[^1].Kind == NoteLineKind.Blank) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>Splits on <c>**</c>: odd segments are bold. An unmatched <c>**</c> is kept as text.</summary>
    public static IReadOnlyList<NoteSpan> ParseInline(string? text)
    {
        text ??= "";
        var parts = text.Split("**");
        if (parts.Length < 3) return [new NoteSpan(text, false)];
        var spans = new List<NoteSpan>();
        var closed = parts.Length % 2 == 1; // an even number of markers
        for (var i = 0; i < parts.Length; i++)
        {
            var bold = i % 2 == 1;
            var segment = parts[i];
            if (!closed && i == parts.Length - 1 && bold)
            {
                // Unmatched trailing marker: show it literally.
                spans.Add(new NoteSpan("**" + segment, false));
                continue;
            }
            if (segment.Length > 0) spans.Add(new NoteSpan(segment, bold));
        }
        return spans;
    }

    /// <summary>Text without formatting markers (for search and previews).</summary>
    public static string ToPlainText(string? text)
    {
        var sb = new StringBuilder();
        foreach (var line in Parse(text))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Content.Replace("**", ""));
        }
        return sb.ToString();
    }

    /// <summary>Ticks or unticks the checkbox on line <paramref name="lineIndex"/>.</summary>
    public static string ToggleCheck(string text, int lineIndex)
    {
        var lines = SplitLines(text);
        if (lineIndex < 0 || lineIndex >= lines.Length) return text;
        var m = CheckRegex().Match(lines[lineIndex]);
        if (!m.Success) return text;
        var mark = m.Groups["mark"];
        var line = lines[lineIndex];
        lines[lineIndex] = line[..mark.Index] + (mark.Value == " " ? "x" : " ") + line[(mark.Index + 1)..];
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Toggles a list marker on every line touched by the selection: adds it when the first line doesn't have it
    /// (replacing the other kind of marker), removes it otherwise.
    /// </summary>
    public static NoteEdit ToggleList(string text, int selectionStart, int selectionEnd, NoteLineKind kind)
    {
        if (kind is not (NoteLineKind.Bullet or NoteLineKind.Check)) throw new ArgumentOutOfRangeException(nameof(kind));
        text = Normalize(text);
        var (start, end) = Order(text, selectionStart, selectionEnd);
        var firstLineStart = LineStart(text, start);
        var lastLineEnd = LineEnd(text, end > start && end > 0 && text[end - 1] == '\n' ? end - 1 : end);
        var block = text[firstLineStart..lastLineEnd].Split('\n');
        var add = KindOf(block[0]) != kind;
        var prefix = kind == NoteLineKind.Bullet ? BulletPrefix : CheckPrefix;
        var delta = 0;
        var firstDelta = 0;
        for (var i = 0; i < block.Length; i++)
        {
            var before = block[i].Length;
            var (indent, body) = StripMarker(block[i]);
            block[i] = add ? indent + prefix + body : indent + body;
            var change = block[i].Length - before;
            if (i == 0) firstDelta = change;
            delta += change;
        }
        var newText = text[..firstLineStart] + string.Join('\n', block) + text[lastLineEnd..];
        var newStart = Math.Max(firstLineStart, start + firstDelta);
        var newEnd = Math.Max(newStart, end + delta);
        return new NoteEdit(newText, Math.Min(newStart, newText.Length), Math.Min(newEnd, newText.Length));
    }

    /// <summary>Wraps the selection in <c>**</c> (or unwraps it); with no selection inserts a pair with the caret inside.</summary>
    public static NoteEdit ToggleBold(string text, int selectionStart, int selectionEnd)
    {
        text = Normalize(text);
        var (start, end) = Order(text, selectionStart, selectionEnd);
        if (start >= 2 && end + 2 <= text.Length && text.Substring(start - 2, 2) == "**" && text.Substring(end, 2) == "**")
        {
            var unwrapped = text[..(start - 2)] + text[start..end] + text[(end + 2)..];
            return new NoteEdit(unwrapped, start - 2, end - 2);
        }
        var selected = text[start..end];
        if (selected.Length >= 4 && selected.StartsWith("**", StringComparison.Ordinal) && selected.EndsWith("**", StringComparison.Ordinal))
        {
            var inner = selected[2..^2];
            return new NoteEdit(text[..start] + inner + text[end..], start, start + inner.Length);
        }
        var wrapped = text[..start] + "**" + selected + "**" + text[end..];
        return new NoteEdit(wrapped, start + 2, end + 2);
    }

    /// <summary>
    /// Enter at the end of a bullet or checkbox line continues the list; Enter on an empty item ends it.
    /// Returns null when Enter should behave normally.
    /// </summary>
    public static NoteEdit? ContinueList(string text, int caret)
    {
        text = Normalize(text);
        caret = Math.Clamp(caret, 0, text.Length);
        var lineStart = LineStart(text, caret);
        var lineEnd = LineEnd(text, caret);
        var line = text[lineStart..lineEnd];
        var kind = KindOf(line);
        if (kind is not (NoteLineKind.Bullet or NoteLineKind.Check)) return null;
        var (indent, body) = StripMarker(line);
        if (body.Trim().Length == 0)
        {
            // Empty item: remove the marker and stop the list.
            var cleared = text[..lineStart] + indent + text[lineEnd..];
            var pos = lineStart + indent.Length;
            return new NoteEdit(cleared, pos, pos);
        }
        if (caret < lineEnd && text[caret..lineEnd].Trim().Length > 0) return null; // splitting a line: plain newline
        var insert = "\n" + indent + (kind == NoteLineKind.Bullet ? BulletPrefix : CheckPrefix);
        var newText = text[..caret] + insert + text[caret..];
        return new NoteEdit(newText, caret + insert.Length, caret + insert.Length);
    }

    /// <summary>A short excerpt around the first match of <paramref name="query"/> (case-insensitive), or null.</summary>
    public static string? Snippet(string? text, string query, int radius = 36)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query)) return null;
        var plain = string.Join(" · ", ToPlainText(text).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var index = plain.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var from = Math.Max(0, index - radius);
        var to = Math.Min(plain.Length, index + query.Trim().Length + radius);
        // Snap to word boundaries.
        if (from > 0)
        {
            var space = plain.IndexOf(' ', from);
            if (space >= 0 && space < index) from = space + 1;
        }
        if (to < plain.Length)
        {
            var space = plain.LastIndexOf(' ', to);
            if (space > index + query.Trim().Length) to = space;
        }
        return (from > 0 ? "…" : "") + plain[from..to].Trim() + (to < plain.Length ? "…" : "");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static string[] SplitLines(string text) => Normalize(text).Split('\n');

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static int IndentOf(string whitespace) => whitespace.Replace("\t", "    ").Length / 2;

    private static NoteLineKind KindOf(string line) =>
        CheckRegex().IsMatch(line) ? NoteLineKind.Check : BulletRegex().IsMatch(line) ? NoteLineKind.Bullet : NoteLineKind.Text;

    private static (string Indent, string Body) StripMarker(string line)
    {
        var check = CheckRegex().Match(line);
        if (check.Success) return (check.Groups["indent"].Value, check.Groups["content"].Value);
        var bullet = BulletRegex().Match(line);
        if (bullet.Success) return (bullet.Groups["indent"].Value, bullet.Groups["content"].Value);
        var trimmed = line.TrimStart(' ', '\t');
        return (line[..(line.Length - trimmed.Length)], trimmed);
    }

    private static (int Start, int End) Order(string text, int a, int b)
    {
        a = Math.Clamp(a, 0, text.Length);
        b = Math.Clamp(b, 0, text.Length);
        return a <= b ? (a, b) : (b, a);
    }

    private static int LineStart(string text, int index) => index <= 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;

    private static int LineEnd(string text, int index)
    {
        var next = text.IndexOf('\n', Math.Clamp(index, 0, text.Length));
        return next < 0 ? text.Length : next;
    }
}
