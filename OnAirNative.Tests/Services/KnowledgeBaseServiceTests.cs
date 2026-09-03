using OnAirNative.Models;
using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Covers KnowledgeBaseService's chunking, tokenization, and TF-IDF-lite relevance scoring —
/// always against real temp files (never the real user's documents), matching ConfigServiceTests'
/// isolated-temp-directory pattern.
/// </summary>
public class KnowledgeBaseServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "OnAirNativeTests_KB_" + Guid.NewGuid());

    public KnowledgeBaseServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static AppConfig MakeConfig(params string[] files) => new()
    {
        KnowledgeBaseFiles = [.. files],
    };

    [Fact]
    public void BuildContextForQuestion_NoFilesConfigured_ReturnsEmpty()
    {
        var kb = new KnowledgeBaseService();
        var result = kb.BuildContextForQuestion("What is the price?", new AppConfig());
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildContextForQuestion_FileDoesNotExist_ReturnsEmpty()
    {
        var kb = new KnowledgeBaseService();
        var cfg = MakeConfig(Path.Combine(_tempDir, "does-not-exist.txt"));
        var result = kb.BuildContextForQuestion("What is the price?", cfg);
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildContextForQuestion_NoOverlapWithQuestion_ReturnsEmpty()
    {
        var path = WriteFile("doc.txt", "Our office hours are Monday through Friday, nine to five.");
        var kb = new KnowledgeBaseService();
        var cfg = MakeConfig(path);

        // Completely unrelated question — no shared meaningful terms with the document at all.
        var result = kb.BuildContextForQuestion("banana spaceship xylophone", cfg);
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildContextForQuestion_RelevantQuestion_ReturnsMatchingChunkOnly()
    {
        var path = WriteFile("doc.txt",
            "Contoso Widget Pro costs $499 per year and includes premium support.\n\n" +
            "Our office hours are Monday through Friday, nine to five, closed on public holidays.");
        var kb = new KnowledgeBaseService();
        var cfg = MakeConfig(path);

        var result = kb.BuildContextForQuestion("How much does the Widget Pro cost?", cfg);

        Assert.Contains("499", result);
        Assert.DoesNotContain("office hours", result);
    }

    [Fact]
    public void BuildContextForQuestion_RanksMoreRelevantChunkFirst()
    {
        var path = WriteFile("doc.txt",
            "The pricing plan mentions a discount briefly.\n\n" +
            "Pricing: the Enterprise plan is $999/month, billed annually, with a 20 percent discount for nonprofits.");
        var kb = new KnowledgeBaseService();
        var cfg = MakeConfig(path);

        var result = kb.BuildContextForQuestion("What is the Enterprise plan discount?", cfg, maxChunks: 2);

        // Both chunks share some terms, but the fuller pricing chunk should score higher and
        // therefore appear first in the joined result.
        var firstChunkPos = result.IndexOf("Enterprise plan is $999", StringComparison.Ordinal);
        var secondChunkPos = result.IndexOf("mentions a discount briefly", StringComparison.Ordinal);
        Assert.True(firstChunkPos >= 0);
        Assert.True(secondChunkPos < 0 || firstChunkPos < secondChunkPos);
    }

    [Fact]
    public void BuildContextForQuestion_RespectsMaxChunksLimit()
    {
        var path = WriteFile("doc.txt", string.Join("\n\n", Enumerable.Range(1, 6)
            .Select(i => $"Widget model {i} supports the widget feature and costs some widget amount.")));
        var kb = new KnowledgeBaseService();
        var cfg = MakeConfig(path);

        var result = kb.BuildContextForQuestion("Tell me about the widget feature", cfg, maxChunks: 2, maxTotalChars: 5000);

        var separatorCount = result.Split("\n\n---\n\n").Length - 1;
        Assert.True(separatorCount <= 1); // at most 2 chunks joined => at most 1 separator
    }

    [Fact]
    public void BuildContextForQuestion_RespectsMaxTotalCharsBudget()
    {
        var longChunk = "widget " + new string('x', 2000) + " widget feature";
        var path = WriteFile("doc.txt", longChunk);
        var kb = new KnowledgeBaseService();
        var cfg = MakeConfig(path);

        var result = kb.BuildContextForQuestion("widget feature", cfg, maxChunks: 3, maxTotalChars: 200);

        Assert.True(result.Length <= 210); // small slack for the "…" truncation marker
    }

    [Fact]
    public void BuildContextForQuestion_FileModifiedSinceLastCall_PicksUpNewContent()
    {
        var path = WriteFile("doc.txt", "The gadget costs ten dollars.");
        var kb = new KnowledgeBaseService();
        var cfg = MakeConfig(path);

        var first = kb.BuildContextForQuestion("How much does the gadget cost?", cfg);
        Assert.Contains("ten dollars", first);

        // Simulate an external edit — rewrite with different content and a distinctly later
        // LastWriteTimeUtc so the cache-invalidation check reliably sees a change.
        File.WriteAllText(path, "The gadget costs one hundred dollars now.");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var second = kb.BuildContextForQuestion("How much does the gadget cost?", cfg);
        Assert.Contains("one hundred dollars", second);
        Assert.DoesNotContain("ten dollars", second);
    }

    [Fact]
    public void BuildContextForQuestion_UnreadableOrLockedFile_SkipsSilentlyWithoutThrowing()
    {
        var goodPath = WriteFile("good.txt", "The widget feature is available on all plans.");
        var lockedPath = WriteFile("locked.txt", "This content should never be seen.");

        var kb = new KnowledgeBaseService();
        using (var stream = File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var cfg = MakeConfig(goodPath, lockedPath);
            var result = kb.BuildContextForQuestion("Tell me about the widget feature", cfg);
            Assert.Contains("widget feature", result);
        }
    }

    [Fact]
    public void ChunkText_SplitsOnBlankLines()
    {
        var chunks = KnowledgeBaseService.ChunkText("First paragraph.\n\nSecond paragraph.\n\n\nThird paragraph.");
        Assert.Equal(3, chunks.Count);
        Assert.Equal("First paragraph.", chunks[0]);
        Assert.Equal("Second paragraph.", chunks[1]);
        Assert.Equal("Third paragraph.", chunks[2]);
    }

    [Fact]
    public void ChunkText_IgnoresBlankParagraphs()
    {
        var chunks = KnowledgeBaseService.ChunkText("\n\n\nOnly real paragraph.\n\n   \n\n");
        Assert.Single(chunks);
        Assert.Equal("Only real paragraph.", chunks[0]);
    }

    [Fact]
    public void ChunkText_SplitsOversizedParagraphOnSentenceBoundaries()
    {
        var sentence = "This is one sentence about widgets and gadgets. ";
        var bigParagraph = string.Concat(Enumerable.Repeat(sentence, 40)); // ~1960 chars, one paragraph

        var chunks = KnowledgeBaseService.ChunkText(bigParagraph, maxChunkChars: 500);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 520)); // small slack for boundary rounding
    }

    [Fact]
    public void ChunkText_HardCutsASingleSentenceLongerThanTheCap()
    {
        var noPunctuation = new string('a', 3000); // one giant "sentence" with no punctuation at all
        var chunks = KnowledgeBaseService.ChunkText(noPunctuation, maxChunkChars: 900);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 900));
    }

    [Fact]
    public void Tokenize_LowercasesAndStripsPunctuationAndMarkdown()
    {
        var tokens = KnowledgeBaseService.Tokenize("**Widget-Pro** costs #499 (approx.)");
        Assert.Contains("widget", tokens);
        Assert.Contains("pro", tokens);
        Assert.Contains("499", tokens);
        Assert.DoesNotContain("**widget-pro**", tokens);
    }

    [Fact]
    public void Tokenize_RemovesCommonEnglishAndPortugueseStopwords()
    {
        var tokens = KnowledgeBaseService.Tokenize("the price of the product is not the same as before");
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("of", tokens);
        Assert.DoesNotContain("is", tokens);
        Assert.DoesNotContain("not", tokens);
        Assert.Contains("price", tokens);
        Assert.Contains("product", tokens);

        var ptTokens = KnowledgeBaseService.Tokenize("o preço do produto não é o mesmo de antes");
        Assert.DoesNotContain("o", ptTokens);
        Assert.DoesNotContain("do", ptTokens);
        Assert.DoesNotContain("não", ptTokens);
        Assert.Contains("preço", ptTokens);
        Assert.Contains("produto", ptTokens);
    }
}
