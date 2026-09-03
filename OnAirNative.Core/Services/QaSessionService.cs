namespace OnAirNative.Services;

/// <summary>
/// Manages the lifecycle of an optional Q&amp;A "session" recording — a single Markdown file
/// that live-appends every successful Q&amp;A turn while a session is active.
///
/// Deliberately simple by design (per explicit user decisions):
///   - Sessions are NEVER created automatically — only <see cref="StartNewSession"/>, which the
///     user must actively trigger, creates a file. No implicit background logging.
///   - Starting a new session always creates a brand-new file — nothing is inherited from a
///     prior session (different conversation, different client: a clean slate every time).
///   - <see cref="CloseSession"/> lets the user explicitly end a session (e.g. a meeting wrapped
///     up) WITHOUT immediately starting a new one — the file itself needs no special "footer" or
///     finalization step (every turn was already flushed to disk as it happened), this just stops
///     further turns from being appended to it until another session starts.
///   - No in-app session history/browser — <see cref="SessionsDirectory"/> is exposed so the UI
///     can offer an "open folder" action (Explorer), not an in-app viewer.
///   - Every turn is appended to disk as it happens (not batched/buffered until some later
///     "export" step) — so nothing is lost if the app closes unexpectedly mid-session.
/// </summary>
public sealed class QaSessionService
{
    private readonly string _sessionsDirectory;
    private string? _currentFilePath;

    public bool IsActive => _currentFilePath is not null;
    public string? CurrentFileName => _currentFilePath is null ? null : Path.GetFileName(_currentFilePath);
    public int TurnCount { get; private set; }

    /// <summary>Where session .md files are written — exposed so the UI can offer an
    /// "open sessions folder" action without this service needing to know about Explorer/UI.</summary>
    public string SessionsDirectory => _sessionsDirectory;

    /// <param name="sessionsDirectory">Overrides where session files are written — used by
    /// OnAirNative.Tests for isolation. Defaults to Documents\onAIr\Sessions\ (a visible,
    /// easy-to-find location — deliberately NOT %LocalAppData%, which is meant for internal app
    /// state, not user-facing exported content the user may want to browse or share).</param>
    public QaSessionService(string? sessionsDirectory = null)
    {
        _sessionsDirectory = sessionsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "onAIr", "Sessions");
    }

    /// <summary>Starts a brand-new session — always creates a fresh file, immediately writing a
    /// header (closing out whatever was active before, if anything; nothing is inherited between
    /// sessions by design). Returns the new file's display name.</summary>
    /// <param name="label">Optional free-text label (e.g. a client/meeting name) — included in
    /// both the filename and the file's header when provided.</param>
    public string StartNewSession(string? label = null)
    {
        Directory.CreateDirectory(_sessionsDirectory);

        var timestamp = DateTime.Now;
        var cleanLabel = SanitizeForFileName(label);
        var fileSuffix = cleanLabel.Length == 0 ? "" : $"-{cleanLabel}";
        var fileName = $"onair-qa-session-{timestamp:yyyy-MM-dd-HHmm}{fileSuffix}.md";

        _currentFilePath = Path.Combine(_sessionsDirectory, fileName);
        TurnCount = 0;

        var header = string.IsNullOrWhiteSpace(label)
            ? $"# Q&A Session — {timestamp:yyyy-MM-dd HH:mm}\n\n"
            : $"# Q&A Session — {timestamp:yyyy-MM-dd HH:mm} ({label.Trim()})\n\n";
        File.WriteAllText(_currentFilePath, header);

        return fileName;
    }

    /// <summary>Ends the active session (if any) without starting a new one — the only other way
    /// to stop recording besides immediately starting a different session. No-op (safe to call
    /// unconditionally) if no session is active. The file itself needs no closing/footer write:
    /// every turn was already flushed to disk by <see cref="AppendTurn"/> as it happened.</summary>
    public void CloseSession()
    {
        _currentFilePath = null;
        TurnCount = 0;
    }

    /// <summary>Appends one Q&amp;A turn to the active session file. No-op if no session is
    /// active — safe to call unconditionally after every successful answer, regardless of
    /// whether the user has ever started a session.</summary>
    public void AppendTurn(string question, string answer)
    {
        if (_currentFilePath is null) return;

        var entry = $"### {DateTime.Now:HH:mm:ss}\n**Q:** {question}\n**A:** {answer}\n\n";
        File.AppendAllText(_currentFilePath, entry);
        TurnCount++;
    }

    /// <summary>Strips characters that aren't valid in a Windows filename and collapses
    /// whitespace to hyphens — e.g. "Contoso: Q3 review" → "Contoso-Q3-review". Empty/whitespace
    /// input yields an empty string (no label suffix).</summary>
    private static string SanitizeForFileName(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. label.Where(c => !invalid.Contains(c) && c != ':')]);
        var collapsed = string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim('-');
    }
}
