namespace OnAirNative.Models;

/// <summary>One inline run of text within a paragraph, carrying its own formatting flags —
/// a paragraph is a sequence of these rather than one plain string so a single line can mix
/// plain, **bold**, and *italic* segments.</summary>
public sealed record ScriptRun(string Text, bool Bold, bool Italic);

/// <summary>Base type for a block-level element of a parsed script. Every line of the raw
/// script text becomes exactly one block (see <see cref="Services.ScriptParser"/>), so block
/// order always matches source line order.</summary>
public abstract record ScriptBlock;

/// <summary>A chapter/section marker — from a line starting with "# " (Level 1) or "## "
/// (Level 2). Renders as a distinctive divider on the TP and becomes a jump target in the
/// Controller's chapter list.</summary>
public sealed record HeadingBlock(string Title, int Level) : ScriptBlock;

/// <summary>A regular script line, already split into formatted runs. An empty <see cref="Runs"/>
/// list represents a blank source line — still rendered (as a spacer) to preserve the vertical
/// rhythm of the original text.</summary>
public sealed record ParagraphBlock(IReadOnlyList<ScriptRun> Runs) : ScriptBlock;

/// <summary>One entry in the chapter navigation list. <see cref="BlockIndex"/> is the position
/// of the corresponding <see cref="HeadingBlock"/> within <see cref="ScriptDocument.Blocks"/> —
/// the same index the rendered View keeps a matching UIElement for, used to compute the jump
/// target's on-screen position (see OverlayWindow.JumpToBlock).</summary>
public sealed record ChapterInfo(string Title, int Level, int BlockIndex);

/// <summary>The parsed representation of a loaded script: an ordered list of blocks, plus a
/// convenience view of just the heading blocks for chapter navigation. Recomputed by
/// <see cref="Services.ScriptParser"/> every time <c>OverlayViewModel.ScriptText</c> changes —
/// never persisted itself, since the raw text remains the single source of truth (also what
/// MCP's onair_get_script_text / onair_load_script and the Stream Deck plugin see).</summary>
public sealed class ScriptDocument
{
    public IReadOnlyList<ScriptBlock> Blocks { get; }
    public IReadOnlyList<ChapterInfo> Chapters { get; }

    public ScriptDocument(IReadOnlyList<ScriptBlock> blocks)
    {
        Blocks = blocks;

        var chapters = new List<ChapterInfo>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is HeadingBlock heading)
                chapters.Add(new ChapterInfo(heading.Title, heading.Level, i));
        }
        Chapters = chapters;
    }

    public static readonly ScriptDocument Empty = new([]);
}
