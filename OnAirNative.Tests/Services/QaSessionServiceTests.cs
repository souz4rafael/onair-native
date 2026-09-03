using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Verifies QaSessionService's file lifecycle — always against an isolated temp directory (via
/// the sessionsDirectory constructor parameter added specifically for tests), never the real
/// Documents\onAIr\Sessions\ (same isolation discipline as ConfigServiceTests).
/// </summary>
public class QaSessionServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "OnAirNativeTests_Sessions_" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_DoesNotCreateTheDirectoryUntilASessionStarts()
    {
        // Sessions are never created automatically (explicit user requirement) — not even the
        // directory itself should appear just from constructing the service.
        _ = new QaSessionService(_tempDir);

        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public void IsActive_BeforeAnySessionStarted_IsFalse()
    {
        var service = new QaSessionService(_tempDir);
        Assert.False(service.IsActive);
        Assert.Null(service.CurrentFileName);
    }

    [Fact]
    public void StartNewSession_NoLabel_CreatesTimestampedMarkdownFile()
    {
        var service = new QaSessionService(_tempDir);

        var fileName = service.StartNewSession();

        Assert.True(service.IsActive);
        Assert.Equal(fileName, service.CurrentFileName);
        Assert.StartsWith("onair-qa-session-", fileName);
        Assert.EndsWith(".md", fileName);
        Assert.True(File.Exists(Path.Combine(_tempDir, fileName)));
    }

    [Fact]
    public void StartNewSession_WithLabel_IncludesSanitizedLabelInFileName()
    {
        var service = new QaSessionService(_tempDir);

        var fileName = service.StartNewSession("Contoso: Q3 review");

        Assert.Contains("Contoso-Q3-review", fileName);
    }

    [Fact]
    public void StartNewSession_WithLabel_IncludesLabelInFileHeader()
    {
        var service = new QaSessionService(_tempDir);

        var fileName = service.StartNewSession("Contoso");

        var content = File.ReadAllText(Path.Combine(_tempDir, fileName));
        Assert.Contains("# Q&A Session", content);
        Assert.Contains("(Contoso)", content);
    }

    [Fact]
    public void StartNewSession_NoLabel_HeaderHasNoParenthesesSuffix()
    {
        var service = new QaSessionService(_tempDir);

        var fileName = service.StartNewSession();

        var content = File.ReadAllText(Path.Combine(_tempDir, fileName));
        Assert.Contains("# Q&A Session", content);
        Assert.DoesNotContain("(", content);
    }

    [Fact]
    public void AppendTurn_NoActiveSession_IsNoOp()
    {
        var service = new QaSessionService(_tempDir);

        service.AppendTurn("question", "answer"); // must not throw

        Assert.Equal(0, service.TurnCount);
        Assert.False(Directory.Exists(_tempDir)); // still never created anything
    }

    [Fact]
    public void AppendTurn_ActiveSession_AppendsFormattedEntryAndIncrementsTurnCount()
    {
        var service = new QaSessionService(_tempDir);
        var fileName = service.StartNewSession();

        service.AppendTurn("What's the pricing?", "It depends on usage tier.");

        Assert.Equal(1, service.TurnCount);
        var content = File.ReadAllText(Path.Combine(_tempDir, fileName));
        Assert.Contains("**Q:** What's the pricing?", content);
        Assert.Contains("**A:** It depends on usage tier.", content);
    }

    [Fact]
    public void AppendTurn_MultipleTurns_AllPresentInFileAndCountedInOrder()
    {
        var service = new QaSessionService(_tempDir);
        service.StartNewSession();

        service.AppendTurn("q1", "a1");
        service.AppendTurn("q2", "a2");
        service.AppendTurn("q3", "a3");

        Assert.Equal(3, service.TurnCount);
        var content = File.ReadAllText(Path.Combine(_tempDir, service.CurrentFileName!));
        Assert.Contains("**Q:** q1", content);
        Assert.Contains("**Q:** q2", content);
        Assert.Contains("**Q:** q3", content);
        // q1 must appear before q2, which must appear before q3 (append order preserved)
        Assert.True(content.IndexOf("q1") < content.IndexOf("q2"));
        Assert.True(content.IndexOf("q2") < content.IndexOf("q3"));
    }

    [Fact]
    public void StartNewSession_CalledAgain_CreatesADifferentFileAndResetsTurnCount()
    {
        var service = new QaSessionService(_tempDir);
        var firstFile = service.StartNewSession("Client A");
        service.AppendTurn("q1", "a1");
        service.AppendTurn("q2", "a2");

        var secondFile = service.StartNewSession("Client B");

        Assert.NotEqual(firstFile, secondFile);
        Assert.Equal(0, service.TurnCount); // nothing inherited from the previous session
        // Both files still exist on disk — starting a new session doesn't delete the old one.
        Assert.True(File.Exists(Path.Combine(_tempDir, firstFile)));
        Assert.True(File.Exists(Path.Combine(_tempDir, secondFile)));
    }

    [Fact]
    public void StartNewSession_SecondSession_DoesNotContainFirstSessionsTurns()
    {
        var service = new QaSessionService(_tempDir);
        service.StartNewSession("Client A");
        service.AppendTurn("secret client A question", "secret client A answer");

        var secondFile = service.StartNewSession("Client B");
        service.AppendTurn("client B question", "client B answer");

        var secondContent = File.ReadAllText(Path.Combine(_tempDir, secondFile));
        Assert.DoesNotContain("secret client A question", secondContent);
    }

    [Fact]
    public void CloseSession_ActiveSession_MakesIsActiveFalseAndResetsTurnCount()
    {
        var service = new QaSessionService(_tempDir);
        service.StartNewSession();
        service.AppendTurn("q1", "a1");

        service.CloseSession();

        Assert.False(service.IsActive);
        Assert.Null(service.CurrentFileName);
        Assert.Equal(0, service.TurnCount);
    }

    [Fact]
    public void CloseSession_NoActiveSession_IsNoOp()
    {
        var service = new QaSessionService(_tempDir);

        service.CloseSession(); // must not throw

        Assert.False(service.IsActive);
    }

    [Fact]
    public void CloseSession_ThenAppendTurn_IsNoOpJustLikeBeforeAnySessionEver()
    {
        var service = new QaSessionService(_tempDir);
        var fileName = service.StartNewSession();
        service.CloseSession();

        service.AppendTurn("should not be recorded", "should not be recorded");

        Assert.Equal(0, service.TurnCount);
        // The file from the closed session is untouched — closing doesn't delete/truncate it,
        // it just stops further turns from being appended to it.
        var content = File.ReadAllText(Path.Combine(_tempDir, fileName));
        Assert.DoesNotContain("should not be recorded", content);
    }

    [Fact]
    public void CloseSession_ThenStartNewSession_WorksNormally()
    {
        var service = new QaSessionService(_tempDir);
        service.StartNewSession("Client A");
        service.CloseSession();

        var fileName = service.StartNewSession("Client B");
        service.AppendTurn("q1", "a1");

        Assert.True(service.IsActive);
        Assert.Equal(1, service.TurnCount);
        Assert.Contains("Client-B", fileName); // spaces are sanitized to hyphens in filenames
    }

    [Fact]
    public void SessionsDirectory_ExposesTheConfiguredPath()
    {
        var service = new QaSessionService(_tempDir);
        Assert.Equal(_tempDir, service.SessionsDirectory);
    }
}
