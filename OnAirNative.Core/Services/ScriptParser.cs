using System.Text.RegularExpressions;
using OnAirNative.Models;

namespace OnAirNative.Services;

/// <summary>
/// Parses a script's raw text into a <see cref="ScriptDocument"/> — a list of blocks the TP
/// renders one UIElement per block, and the Controller's chapter list navigates by index.
///
/// Deliberately hand-rolled rather than pulling in a full Markdown/CommonMark library: the
/// supported syntax is intentionally tiny (heading lines + **bold**/*italic* spans, nothing
/// else — no lists, links, tables, nesting), so a general-purpose parser would be solving a
/// much bigger problem than the one we actually have, at the cost of a new dependency.
///
/// Every source line becomes exactly one block (never grouped into multi-line paragraphs) —
/// this exactly mirrors how the original single-TextBlock rendering worked (each "\n" in the
/// raw text was already a visual line break; word-wrap within a line was the TextBlock's own
/// job, still is per-block here), so a script with no markup at all renders identically to
/// before this feature existed.
/// </summary>
public static class ScriptParser
{
    // "# Title" or "## Title" — requires the space, so "#hashtag" or "### too-deep" (which
    // this regex simply won't match, since backtracking #{1,2} still leaves a stray '#'
    // immediately before where \s+ needs to start) fall through and render as plain text.
    private static readonly Regex HeadingRegex =
        new(@"^(#{1,2})\s+(.+)$", RegexOptions.Compiled);

    // Alternation order matters: **bold** is tried before *italic* at each position, so
    // "**x**" is never partially consumed as "*" + italic "*x*" + stray "*".
    private static readonly Regex InlineRegex =
        new(@"\*\*(.+?)\*\*|\*(.+?)\*", RegexOptions.Compiled);

    public static ScriptDocument Parse(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return ScriptDocument.Empty;

        var lines  = rawText.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<ScriptBlock>(lines.Length);

        foreach (var line in lines)
        {
            var headingMatch = HeadingRegex.Match(line);
            blocks.Add(headingMatch.Success
                ? new HeadingBlock(headingMatch.Groups[2].Value.Trim(), headingMatch.Groups[1].Value.Length)
                : new ParagraphBlock(ParseInline(line)));
        }

        return new ScriptDocument(blocks);
    }

    /// <summary>Splits one line into runs at **bold**/*italic* boundaries. A blank line (or a
    /// line that's only whitespace) yields an empty run list — the renderer treats that as a
    /// spacer rather than trying to render zero-width text.</summary>
    private static IReadOnlyList<ScriptRun> ParseInline(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];

        var runs = new List<ScriptRun>();
        int pos  = 0;

        foreach (Match m in InlineRegex.Matches(line))
        {
            if (m.Index > pos)
                runs.Add(new ScriptRun(line[pos..m.Index], Bold: false, Italic: false));

            if (m.Groups[1].Success) // **bold**
                runs.Add(new ScriptRun(m.Groups[1].Value, Bold: true, Italic: false));
            else                     // *italic*
                runs.Add(new ScriptRun(m.Groups[2].Value, Bold: false, Italic: true));

            pos = m.Index + m.Length;
        }

        if (pos < line.Length)
            runs.Add(new ScriptRun(line[pos..], Bold: false, Italic: false));

        return runs;
    }
}
