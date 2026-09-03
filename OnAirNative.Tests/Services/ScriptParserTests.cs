using OnAirNative.Models;
using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Covers ScriptParser's tiny Markdown-lite syntax (# / ## headings, **bold**/*italic* spans)
/// and — just as importantly — its graceful-degradation behavior for anything that ISN'T that
/// syntax, since a plain .txt script with zero markup must render identically to how it did
/// before the chapters/formatting feature existed (see ControllerWindow's CHAPTERS card and
/// OverlayWindow.RenderScriptDocument, which both depend on that guarantee).
/// </summary>
public class ScriptParserTests
{
    [Fact]
    public void Parse_EmptyString_ReturnsEmptyDocument()
    {
        var doc = ScriptParser.Parse("");

        Assert.Empty(doc.Blocks);
        Assert.Empty(doc.Chapters);
    }

    [Fact]
    public void Parse_PlainLineWithNoMarkup_YieldsOneParagraphBlockWithASingleRun()
    {
        var doc = ScriptParser.Parse("Just a plain line.");

        var block = Assert.Single(doc.Blocks);
        var paragraph = Assert.IsType<ParagraphBlock>(block);
        var run = Assert.Single(paragraph.Runs);
        Assert.Equal("Just a plain line.", run.Text);
        Assert.False(run.Bold);
        Assert.False(run.Italic);
    }

    [Fact]
    public void Parse_BlankLine_YieldsParagraphBlockWithNoRuns()
    {
        var doc = ScriptParser.Parse("\n");

        // "\n" splits into two lines: "" before it and "" after it.
        Assert.All(doc.Blocks, b => Assert.Empty(Assert.IsType<ParagraphBlock>(b).Runs));
    }

    [Theory]
    [InlineData("# Chapter One", "Chapter One", 1)]
    [InlineData("## Section Two", "Section Two", 2)]
    [InlineData("#    Extra   spaces  ", "Extra   spaces", 1)]
    public void Parse_HeadingLine_YieldsHeadingBlockWithCorrectTitleAndLevel(
        string line, string expectedTitle, int expectedLevel)
    {
        var doc = ScriptParser.Parse(line);

        var block = Assert.Single(doc.Blocks);
        var heading = Assert.IsType<HeadingBlock>(block);
        Assert.Equal(expectedTitle, heading.Title);
        Assert.Equal(expectedLevel, heading.Level);
    }

    [Theory]
    [InlineData("###Too many hashes, no space")]
    [InlineData("###### way too deep")]
    [InlineData("#nospace")]
    [InlineData("A line that just happens to contain a # character")]
    public void Parse_InvalidOrNonHeadingHash_FallsThroughAsPlainParagraph(string line)
    {
        var doc = ScriptParser.Parse(line);

        var block = Assert.Single(doc.Blocks);
        Assert.IsType<ParagraphBlock>(block);
        Assert.Empty(doc.Chapters);
    }

    [Fact]
    public void Parse_ThreeHashHeading_FallsThroughAsPlainParagraph_NotHeadingLevel3()
    {
        // Only # (level 1) and ## (level 2) are supported headings — "### x" must NOT become
        // a level-3 heading (out of scope by design), and must not silently drop the leading
        // "###" either — the raw text should survive unchanged in the paragraph's run text.
        var doc = ScriptParser.Parse("### Not a real heading");

        var block = Assert.Single(doc.Blocks);
        var paragraph = Assert.IsType<ParagraphBlock>(block);
        Assert.Equal("### Not a real heading", Assert.Single(paragraph.Runs).Text);
    }

    [Fact]
    public void Parse_BoldSpan_YieldsBoldRunWithDelimitersStripped()
    {
        var doc = ScriptParser.Parse("This is **bold** text.");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        Assert.Collection(paragraph.Runs,
            r => { Assert.Equal("This is ", r.Text); Assert.False(r.Bold); Assert.False(r.Italic); },
            r => { Assert.Equal("bold", r.Text);     Assert.True(r.Bold);  Assert.False(r.Italic); },
            r => { Assert.Equal(" text.", r.Text);   Assert.False(r.Bold); Assert.False(r.Italic); });
    }

    [Fact]
    public void Parse_ItalicSpan_YieldsItalicRunWithDelimitersStripped()
    {
        var doc = ScriptParser.Parse("This is *italic* text.");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        Assert.Collection(paragraph.Runs,
            r => { Assert.Equal("This is ", r.Text); Assert.False(r.Bold); Assert.False(r.Italic); },
            r => { Assert.Equal("italic", r.Text);   Assert.False(r.Bold); Assert.True(r.Italic); },
            r => { Assert.Equal(" text.", r.Text);   Assert.False(r.Bold); Assert.False(r.Italic); });
    }

    [Fact]
    public void Parse_BoldAndItalicInSameLine_YieldsBothCorrectly()
    {
        var doc = ScriptParser.Parse("**Bold** and *italic* together.");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        Assert.Collection(paragraph.Runs,
            r => { Assert.Equal("Bold", r.Text);       Assert.True(r.Bold);  Assert.False(r.Italic); },
            r => { Assert.Equal(" and ", r.Text);      Assert.False(r.Bold); Assert.False(r.Italic); },
            r => { Assert.Equal("italic", r.Text);     Assert.False(r.Bold); Assert.True(r.Italic); },
            r => { Assert.Equal(" together.", r.Text); Assert.False(r.Bold); Assert.False(r.Italic); });
    }

    [Fact]
    public void Parse_DoubleAsterisks_PreferBoldOverTwoAdjacentItalics()
    {
        // Regression guard for the exact ambiguity the parser's doc comment calls out:
        // "**x**" must be consumed as ONE bold run, never as italic("*") + literal + italic("*").
        var doc = ScriptParser.Parse("**word**");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        var run = Assert.Single(paragraph.Runs);
        Assert.Equal("word", run.Text);
        Assert.True(run.Bold);
        Assert.False(run.Italic);
    }

    [Fact]
    public void Parse_UnbalancedAsterisk_DegradesGracefullyToLiteralText()
    {
        // No closing delimiter — must render as plain literal text (asterisk included),
        // never throw, never silently eat the rest of the line.
        var doc = ScriptParser.Parse("An unbalanced * asterisk with no closing pair.");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        var run = Assert.Single(paragraph.Runs);
        Assert.Equal("An unbalanced * asterisk with no closing pair.", run.Text);
        Assert.False(run.Bold);
        Assert.False(run.Italic);
    }

    [Fact]
    public void Parse_MultiLineScriptWithHeadingsAndFormatting_ProducesCorrectChapterList()
    {
        var raw = "# Introduction\n" +
                  "Welcome to the **demo**.\n" +
                  "\n" +
                  "## Agenda\n" +
                  "Some *plain* talk.\n" +
                  "# Live Demo\n";

        var doc = ScriptParser.Parse(raw);

        Assert.Equal(3, doc.Chapters.Count);
        Assert.Equal(("Introduction", 1), (doc.Chapters[0].Title, doc.Chapters[0].Level));
        Assert.Equal(("Agenda", 2),       (doc.Chapters[1].Title, doc.Chapters[1].Level));
        Assert.Equal(("Live Demo", 1),    (doc.Chapters[2].Title, doc.Chapters[2].Level));

        // BlockIndex must point at the actual HeadingBlock's position in Blocks — this is what
        // OverlayWindow.JumpToBlock relies on to find the right on-screen element.
        foreach (var chapter in doc.Chapters)
            Assert.IsType<HeadingBlock>(doc.Blocks[chapter.BlockIndex]);
    }

    [Fact]
    public void Parse_PlainScriptWithNoMarkupAtAll_HasNoChapters()
    {
        // The core backward-compatibility guarantee: any pre-existing .txt script with zero
        // #/##/**/* usage must produce zero HeadingBlocks — the Controller's CHAPTERS card
        // must stay hidden for it, exactly like before this feature existed.
        var raw = "Line one.\nLine two.\n\nLine four after a blank line.\n";

        var doc = ScriptParser.Parse(raw);

        Assert.Empty(doc.Chapters);
        Assert.All(doc.Blocks, b => Assert.IsType<ParagraphBlock>(b));
    }
}
