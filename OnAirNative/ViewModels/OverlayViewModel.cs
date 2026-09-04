using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;
using OnAirNative.Models;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

public enum OverlayMode { Script, QA }
public enum ScrollMode  { Manual, Auto, Voice }

/// <summary>
/// ViewModel for the Overlay window.
/// Owns the script text, mode, recording state, Q&A state, and scroll behaviour.
/// All hotkey handlers in App.xaml.cs delegate here.
/// </summary>
public partial class OverlayViewModel : ObservableObject
{
    private readonly ConfigService    _config;
    private readonly AudioService     _audio;
    private readonly WhisperService   _whisper;
    private readonly AiChatService    _ai;
    private readonly DispatcherQueue  _uiQueue;

    // Injected from the View after window creation
    public IntPtr Hwnd { get; set; }

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private OverlayMode _currentMode  = OverlayMode.Script;
    [ObservableProperty] private ScrollMode  _scrollMode   = ScrollMode.Manual;
    [ObservableProperty] private bool        _isMoveModeActive = true;
    [ObservableProperty] private bool        _isClickThrough   = false;

    private const string DefaultScriptPlaceholder = "Load a script to begin.\n\nUse Ctrl+Alt+O or drag a .txt file here.";

    [ObservableProperty] private string _scriptText     = DefaultScriptPlaceholder;
    [ObservableProperty] private string _loadedFileName = "";
    [ObservableProperty] private double _scrollOffset   = 0;

    // Parsed representation of ScriptText (blocks + chapter headings) — recomputed by
    // OnScriptTextChanged below every time ScriptText changes. Field initializer parses the
    // SAME placeholder constant directly (rather than relying on OnScriptTextChanged firing
    // for it) because C# field initializers bypass generated property setters, so
    // OnScriptTextChanged never actually runs for this default value.
    [ObservableProperty] private ScriptDocument _scriptDocument = ScriptParser.Parse(DefaultScriptPlaceholder);

    [ObservableProperty] private bool   _isRecording  = false;
    [ObservableProperty] private bool   _isBusy       = false;
    [ObservableProperty] private string _qaQuestion   = "";
    [ObservableProperty] private string _qaAnswer     = "";
    [ObservableProperty] private string _qaStatus     = "";

    // Block 5 pacing coach — words-per-minute estimate for the most recently completed
    // recording, using VoiceActivityDetector to measure actual speaking time (excluding
    // silence/pauses). Presenter-facing only (Controller Q&A tab), never shown on the TP — see
    // PacingAnalyzer's own doc comment for the full reasoning. Always computed (no separate
    // opt-in toggle needed): it costs nothing beyond a bit of local CPU crunching on bytes
    // already in memory, unlike follow-up suggestions' real extra AI-call cost.
    [ObservableProperty] private string _pacingSummary = "No pacing data yet.";
    /// <summary>"None" | "Slow" | "Good" | "Fast" — mirrors <see cref="PacingLevel"/> exactly
    /// (or "None" when there's no estimate yet/not enough data). Kept separate from
    /// <see cref="PacingSummary"/>'s free English text so machine consumers (RemoteState /
    /// Stream Deck's color-coded pacing tile) don't need to parse a sentence.</summary>
    [ObservableProperty] private string _pacingLevel  = "None";

    // Conversation memory across consecutive Q&A recordings — each successful answer appends
    // a turn here, and it's passed back into every subsequent GetAnswerAsync call so follow-up
    // questions ("and what about pricing?") resolve correctly instead of being answered in
    // isolation. Deliberately NOT rendered on the TP (an earlier version showed a collapsed
    // history of prior questions there — the user found it cluttered the screen and asked for
    // it to be removed; the TP now only ever shows the CURRENT question/answer, same as before
    // this whole side-experiment). ObservableCollection (not a plain List) purely so
    // ConversationTurnCount below can stay a simple computed property backed by .Count — nothing
    // currently reacts to CollectionChanged on this specific collection. Capped at
    // MaxConversationTurns to bound token cost on a long-running stream; oldest turn drops first
    // once the cap is hit (simple FIFO, no smarter summarization).
    public ObservableCollection<ChatTurn> ConversationTurns { get; } = [];
    private const int MaxConversationTurns = 6;

    /// <summary>How many Q&amp;A turns are currently remembered — surfaced so the Controller can
    /// show e.g. "3 turns remembered" next to the Clear Conversation button.</summary>
    public int ConversationTurnCount => ConversationTurns.Count;

    /// <summary>Forgets all prior Q&amp;A turns — the next question starts a fresh context. Does
    /// NOT clear whatever's currently shown on the TP (QaQuestion/QaAnswer); those are a
    /// separate concern (what's currently displayed vs. what the AI remembers).</summary>
    [RelayCommand]
    public void ClearConversation()
    {
        ConversationTurns.Clear();
        OnPropertyChanged(nameof(ConversationTurnCount));
    }
    // ── Follow-up question suggestions (Block 2) ─────────────────────────────
    // Populated after a successful answer, only when AppConfig.ShowFollowUpSuggestions is on
    // (see AskAndAnswerAsync). An ObservableCollection so OverlayWindow can react to
    // CollectionChanged the same way ScrollTabViewModel.Chapters already does for the chapter
    // list — no separate "has suggestions" bool needed, an empty collection IS "none to show".
    //
    //
    // These are questions the PRESENTER can ask THEIR CLIENT next, to keep the conversation
    // flowing (a sales/conversation-flow aid — see AiChatService.GetFollowUpSuggestionsAsync's
    // doc comment). Deliberately NOT clickable/actionable — rendered as plain text on the TP
    // (see OverlayWindow.PopulateFollowUpSuggestions): the TP is frequently click-through/locked
    // during live use, and the presenter reads/says these aloud themselves — there's nothing to
    // "activate" (an earlier version of this feature wrongly made these clickable buttons that
    // asked the AI the suggested question, which was backwards on both counts).
    public ObservableCollection<string> FollowUpSuggestions { get; } = [];

    // ── Q&A session recording (Block 2) ──────────────────────────────────────
    // See QaSessionService's own doc comment for the full design rationale (never automatic,
    // always a brand-new file, no in-app history/browser).
    private readonly QaSessionService _qaSession = new();

    // ── Knowledge base (Block 3) ─────────────────────────────────────────────
    // See KnowledgeBaseService's own doc comment for the search approach. Stateless w.r.t.
    // config (reads cfg.KnowledgeBaseFiles fresh on every call, same as AiChatService reads cfg
    // fresh) — only caches file CONTENT internally, so it's safe to keep one long-lived instance
    // here for the whole overlay's lifetime.
    private readonly KnowledgeBaseService _knowledgeBase = new();

    // ── Remote monitoring + Copilot insights (Block 6) ────────────────────────
    // QaTurnCount is a pure change-detection heartbeat for an EXTERNAL agent polling
    // RemoteState/onair_get_last_qa_turn over MCP/WebSocket — it increments once per
    // successfully completed Q&A round (see AskAndAnswerAsync), completely independent of
    // ConversationTurnCount (which is capped at 6 and reset by "Clear conversation" — that's
    // the AI's OWN memory retention, a different concern from "did a new turn just happen").
    // A monitoring agent remembers the last QaTurnCount it saw and treats a higher value as
    // "there's a new question+answer to look at" — cheaper and more robust than string-diffing
    // QaQuestion/QaAnswer (which could coincidentally repeat verbatim on two different turns).
    [ObservableProperty] private int _qaTurnCount;

    // Free text an EXTERNAL agent (connected via MCP — see App.ShowInsightRemote /
    // onair_show_insight) can push to appear in a small footer on the TP, visible in BOTH
    // Script and Q&A modes (see OverlayWindow.xaml's footer row, a sibling of both mode panels,
    // not inside either one — this is deliberately NOT part of the AI's own Q&A answer, so the
    // presenter can always tell "what I should tell the client" (QaAnswer) apart from "a private
    // heads-up from my copilot" (this). Never persisted to AppConfig — a live, ephemeral signal
    // from an external agent, not a setting. Use SetInsight/ClearInsight, not this property's
    // raw setter directly, so the length cap below is never bypassed.
    [ObservableProperty] private string _insightText = "";

    /// <summary>Max characters shown in the TP's insight footer — long enough for a genuinely
    /// useful one-or-two-sentence coaching note, short enough that it can never grow into a
    /// second wall of text competing with the actual Q&A answer for the presenter's attention.</summary>
    private const int MaxInsightLength = 280;

    /// <summary>Sets the Copilot insight footer text — truncates to <see cref="MaxInsightLength"/>
    /// (with a trailing "…") rather than rejecting an overlong value outright, so a slightly
    /// verbose external agent still gets SOMETHING useful shown instead of an opaque failure.
    /// Blank/whitespace-only input is treated as <see cref="ClearInsight"/> instead of leaving a
    /// stale value in place.</summary>
    public void SetInsight(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { ClearInsight(); return; }
        InsightText = text.Length > MaxInsightLength ? text[..MaxInsightLength] + "…" : text;
    }

    /// <summary>Clears the Copilot insight footer — collapses it entirely on the TP (see
    /// OverlayWindow's InsightText PropertyChanged handler).</summary>
    public void ClearInsight() => InsightText = "";

    /// <summary>Whether a Q&amp;A session is currently recording — lets the Controller
    /// enable/disable the "Close session" button appropriately (nothing to close when no
    /// session is active).</summary>
    public bool IsQaSessionActive => _qaSession.IsActive;

    /// <summary>Human-readable status for the Controller's Q&amp;A session card.</summary>
    public string QaSessionStatusText => _qaSession.IsActive
        ? $"Recording to: {_qaSession.CurrentFileName} ({_qaSession.TurnCount} turn{(_qaSession.TurnCount == 1 ? "" : "s")})"
        : "No active session.";

    /// <summary>Where session .md files live — the Controller's "Open sessions folder" button
    /// reads this rather than duplicating the path logic.</summary>
    public string QaSessionsFolderPath => _qaSession.SessionsDirectory;

    /// <summary>Starts a brand-new Q&amp;A session — always a fresh file, and (per explicit user
    /// decision: "another conversation/session, another client — nothing can be inherited from
    /// the previous one") also clears the Block 1 conversation memory, so the AI starts with a
    /// completely clean slate exactly when the session does.</summary>
    [RelayCommand]
    public void StartNewQaSession(string? label)
    {
        _qaSession.StartNewSession(label);
        ClearConversation();
        OnPropertyChanged(nameof(QaSessionStatusText));
        OnPropertyChanged(nameof(IsQaSessionActive));
    }

    /// <summary>Ends the active Q&amp;A session (if any) WITHOUT starting a new one — the only
    /// way to stop recording besides immediately starting a different session (there was
    /// previously no way to just "stop" — the user had to either start a new session or close
    /// the whole app). The already-written file needs no special finalization (every turn was
    /// flushed to disk as it happened) — this just detaches further turns from being appended.
    /// Deliberately does NOT clear the Block 1 conversation memory (unlike StartNewQaSession) —
    /// closing a session doesn't imply the presenter wants to lose AI context mid-conversation,
    /// only that they've stopped recording it to a file.</summary>
    [RelayCommand]
    public void CloseQaSession()
    {
        _qaSession.CloseSession();
        OnPropertyChanged(nameof(QaSessionStatusText));
        OnPropertyChanged(nameof(IsQaSessionActive));
    }

    // Rolling, best-effort transcript shown live *while* recording — only active when a
    // local Whisper model is loaded (re-transcribing on a timer against a cloud API would
    // spam paid/rate-limited requests). Cleared the moment recording stops, right before
    // the final full-buffer transcription (QaQuestion) replaces it. See StartLivePreview.
    [ObservableProperty] private string _livePreviewText = "";

    [ObservableProperty] private double _opacity   = 0.75;
    [ObservableProperty] private int    _fontSize  = 22;
    [ObservableProperty] private string _fontColor = "#F0F0F0";
    [ObservableProperty] private string _fontFamily = "Segoe UI";

    // Voice-scroll indicators
    [ObservableProperty] private bool   _isVoiceActive = false;
    [ObservableProperty] private double _micLevel      = 0;  // 0-100, for UI feedback

    // Raised so the View can apply WS_EX_TRANSPARENT via WindowService
    public event EventHandler<bool>?   ClickThroughChanged;

    public OverlayViewModel(
        ConfigService config, AudioService audio,
        WhisperService whisper, AiChatService ai)
    {
        _config  = config;
        _audio   = audio;
        _whisper = whisper;
        _ai      = ai;

        // Capture UI thread dispatcher for cross-thread scroll calls (voice mode)
        _uiQueue = DispatcherQueue.GetForCurrentThread();

        var a = config.Current.Appearance;
        Opacity    = a.Opacity / 100.0;
        FontSize   = a.FontSize;
        FontColor  = a.FontColor;
        FontFamily = a.FontFamily;
    }

    // ── Scroll mode changes ───────────────────────────────────────────────────

    partial void OnScrollModeChanged(ScrollMode value)
    {
        StopAutoScroll();
        StopVoiceScrollTimer();
        _audio.StopVoiceMonitor();
        IsVoiceActive = false;
        MicLevel      = 0; // clear any stale reading — otherwise switching Voice mode off then
                            // back on (possibly with a different recording source configured in
                            // between) could leave the last real level frozen on screen if the
                            // new source stays silent for a while (same staleness bug as the
                            // Settings mic test, same root cause: nothing overwrites a stale
                            // value if the new capture just doesn't deliver data yet)

        switch (value)
        {
            case ScrollMode.Auto:  StartAutoScroll();  break;
            case ScrollMode.Voice: StartVoiceScroll(); break;
        }
    }

    // ── Auto-scroll (timer-based) ─────────────────────────────────────────────

    private DispatcherTimer? _autoTimer;

    private void StartAutoScroll()
    {
        _autoTimer = new DispatcherTimer
        {
            // Fixed 50ms tick (~20fps); amount per tick comes from ScrollSpeed config
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _autoTimer.Tick += AutoScrollTick;
        _autoTimer.Start();
    }

    private void StopAutoScroll()
    {
        if (_autoTimer is null) return;
        _autoTimer.Stop();
        _autoTimer.Tick -= AutoScrollTick;
        _autoTimer = null;
    }

    private void AutoScrollTick(object? sender, object e)
    {
        // speed 1-100 → 1–10 pixels per tick at 20fps = 20–200 px/s
        var pxPerTick = Math.Max(1, _config.Current.Appearance.ScrollSpeed / 10);
        Scroll(pxPerTick);
    }

    // ── Voice scroll (RMS microphone monitoring) ──────────────────────────────
    //
    // Continuous timer, same shape as Auto mode, but gated on IsVoiceActive and
    // driven by its own VoiceScrollSpeed setting instead of ScrollSpeed. Previously
    // this scrolled directly from the audio DataAvailable callback with a "1 scroll
    // every 3 callbacks" debounce baked in — that throttle plus sharing Auto's speed
    // value meant Voice mode topped out at a fraction of Auto's max speed even with
    // the slider maxed out. Decoupling the speed AND removing the debounce (voice
    // detection now only toggles IsVoiceActive; the timer does the actual scrolling
    // every tick while active) fixes both problems.

    private DispatcherTimer? _voiceTimer;

    // Block 5: real VAD (attack/release hysteresis) replacing a naive rms > threshold compare —
    // see VoiceActivityDetector's own doc comment for why. One long-lived instance for the
    // overlay's whole lifetime, explicitly Reset() at the start of each voice-scroll session so
    // stale hysteresis state from a previous session never leaks into a new one.
    private readonly VoiceActivityDetector _voiceVad = new();
    private long? _lastVoiceRmsTickMs;

    private void StartVoiceScroll()
    {
        _voiceVad.Reset();
        _lastVoiceRmsTickMs = null;

        _voiceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _voiceTimer.Tick += VoiceScrollTick;
        _voiceTimer.Start();

        // Pass the configured recording source + device IDs so Voice mode actually monitors
        // whatever the user selected (mic / system loopback / both, and which specific
        // input/output device) instead of always the physical microphone / default playback
        // device regardless of those settings.
        _audio.StartVoiceMonitor(_config.Current.AudioRecordingSource, OnVoiceRms,
            _config.Current.AudioDeviceId, _config.Current.AudioOutputDeviceId);
    }

    private void StopVoiceScrollTimer()
    {
        if (_voiceTimer is null) return;
        _voiceTimer.Stop();
        _voiceTimer.Tick -= VoiceScrollTick;
        _voiceTimer = null;
    }

    private void VoiceScrollTick(object? sender, object e)
    {
        if (!IsVoiceActive) return;
        var pxPerTick = Math.Max(1, _config.Current.Appearance.VoiceScrollSpeed / 10);
        Scroll(pxPerTick);
    }

    private void OnVoiceRms(float rms)
    {
        var threshold = (float)_config.Current.Appearance.VoiceRmsThreshold;

        // Audio device callbacks don't arrive on a fixed schedule (buffer size varies by
        // device/driver), so measure the REAL elapsed time between callbacks rather than
        // assuming a fixed tick rate — VoiceActivityDetector's attack/release durations are
        // only meaningful against real wall-clock time.
        var nowMs = Environment.TickCount64;
        var elapsedMs = _lastVoiceRmsTickMs is null ? 30 : Math.Max(1, nowMs - _lastVoiceRmsTickMs.Value);
        _lastVoiceRmsTickMs = nowMs;

        bool active = _voiceVad.Process(rms, threshold, elapsedMs);

        if (active != IsVoiceActive || MicLevel != Math.Round(rms, 1))
            _uiQueue.TryEnqueue(() => { IsVoiceActive = active; MicLevel = Math.Round(rms, 1); });
    }

    // ── Live transcript preview (local Whisper only, while recording) ────────
    //
    // "Poor man's streaming": every tick, re-transcribe everything captured so far from
    // scratch (AudioService.PeekRecordedAudio doesn't stop capture) and show the result as
    // a live, still-growing preview in the Box. True incremental/VAD-based streaming
    // (à la whisper.cpp's stream.cpp) would avoid the repeated work, but is meaningfully
    // more engineering for this app's use case (short Q&A questions, not long dictation).
    //
    // An earlier version windowed this to a trailing N seconds to keep re-transcription
    // roughly constant-time — but re-transcribing a disjoint slice from scratch every tick
    // meant the preview jumped between unrelated fragments and erased whatever was shown a
    // moment before, instead of reading like a transcript that grows as you talk. Given a
    // reasonably fast (non-medium/large) local model and this feature's target of short
    // clips, re-transcribing the whole buffer each tick is worth the simpler, more
    // intuitive result — see AudioService.PeekRecordedAudio for the same trade-off from
    // the audio side. A late-arriving result is still discarded once recording has already
    // stopped (see the IsRecording check in LivePreviewTick below).
    private const int LivePreviewIntervalMs = 2500;

    private DispatcherTimer? _livePreviewTimer;
    private bool             _livePreviewInFlight;

    private void StartLivePreview()
    {
        if (!_whisper.IsLocalModelLoaded) return; // cloud API: don't spam paid requests

        _livePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LivePreviewIntervalMs),
        };
        _livePreviewTimer.Tick += LivePreviewTick;
        _livePreviewTimer.Start();
    }

    private void StopLivePreview()
    {
        if (_livePreviewTimer is not null)
        {
            _livePreviewTimer.Stop();
            _livePreviewTimer.Tick -= LivePreviewTick;
            _livePreviewTimer = null;
        }
        LivePreviewText = "";
    }

    private async void LivePreviewTick(object? sender, object e)
    {
        // Skip if the previous tick's transcription is still running — cheaper than
        // queuing up overlapping Whisper calls if a slow tick falls behind the timer.
        if (_livePreviewInFlight || !IsRecording) return;
        _livePreviewInFlight = true;
        try
        {
            var wav = _audio.PeekRecordedAudio();
            if (wav.Length == 0) return;

            var result = await _whisper.TranscribeAsync(wav, _config.Current);
            if (result.Success && IsRecording) // still recording when the result comes back?
                LivePreviewText = result.Text;
        }
        catch (Exception ex)
        {
            // Best-effort feature — never let a live-preview failure take down the whole
            // app (this is an `async void` timer handler, so an uncaught exception here
            // would otherwise be unrecoverable). Logged for diagnosis; the final,
            // full-buffer transcription after Stop is unaffected either way.
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "onAIr");
            try { File.AppendAllText(Path.Combine(logDir, "live-preview.log"), $"{DateTime.Now:HH:mm:ss.fff} {ex}\n"); }
            catch { /* logging is best-effort too */ }
        }
        finally
        {
            _livePreviewInFlight = false;
        }
    }

    // ── Move mode (Ctrl+Alt+Home) ─────────────────────────────────────────────

    public void ToggleMoveMode() => SetMoveMode(!IsMoveModeActive);

    public void SetMoveMode(bool movable)
    {
        IsMoveModeActive = movable;
        IsClickThrough   = !movable;
        ClickThroughChanged?.Invoke(this, IsClickThrough);
    }

    // ── Script loading ────────────────────────────────────────────────────────

    public async Task LoadScriptAsync(string filePath)
    {
        try
        {
            ScriptText     = await File.ReadAllTextAsync(filePath);
            LoadedFileName = Path.GetFileName(filePath);
            ScrollOffset   = 0;
            CurrentMode    = OverlayMode.Script;
        }
        catch (Exception ex)
        {
            ScriptText = $"⚠ Could not load file:\n{ex.Message}";
        }
    }

    public async Task OpenFilePickerAsync(Window ownerWindow)
    {
        // Win32FileDialog (classic in-process IFileOpenDialog COM interface) instead of
        // Windows.Storage.Pickers.FileOpenPicker — the WinRT picker hangs forever with no
        // dialog ever appearing in this unpackaged app under some session types (its broker
        // process, PickerHost.exe, spawns but never creates a window). See Win32FileDialog's
        // own doc comment for the full root-cause writeup.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(ownerWindow);
        var path = Win32.Win32FileDialog.PickSingleFile(hwnd, "Text files", "txt");
        if (path is not null) await LoadScriptAsync(path);
    }

    // Re-parses into ScriptDocument every time the raw text changes (new file loaded, remote
    // "set script text", etc.) — ScriptText itself stays the single source of truth (also
    // what MCP/Stream Deck see), ScriptDocument is purely a derived, recomputed view of it.
    partial void OnScriptTextChanged(string value) => ScriptDocument = ScriptParser.Parse(value);

    // ── Scroll ────────────────────────────────────────────────────────────────

    public void Scroll(int delta) =>
        ScrollOffset = Math.Max(0, ScrollOffset + delta);

    /// <summary>Requests the View scroll so the block at <paramref name="blockIndex"/> (an
    /// index into ScriptDocument.Blocks — see ChapterInfo.BlockIndex) becomes visible at the
    /// top of the TP. The View (OverlayWindow) owns the actual pixel-position computation
    /// since only it has the rendered UIElements; this just relays the request, the same
    /// pattern already used for ClickThroughChanged/OpacityChanged (View-only concerns).</summary>
    public event EventHandler<int>? JumpToBlockRequested;

    public void JumpToBlock(int blockIndex) => JumpToBlockRequested?.Invoke(this, blockIndex);

    // ── Q&A / Recording (Ctrl+Alt+R) ─────────────────────────────────────────

    [RelayCommand]
    public async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            // Stop recording → transcribe → AI answer
            IsRecording = false;
            StopLivePreview(); // no more ticks once the real, full-buffer transcription starts
            IsBusy      = true;
            QaStatus    = "Transcribing…";

            var wavData = await _audio.StopRecordingAsync();
            var tx      = await _whisper.TranscribeAsync(wavData, _config.Current);

            if (!tx.Success)
            {
                QaStatus = $"Transcription failed: {tx.Error}";
                IsBusy   = false;
                return;
            }

            QaQuestion = tx.Text;
            QaStatus   = "Getting answer…";
            UpdatePacingSummary(wavData, tx.Text);
            await AskAndAnswerAsync(tx.Text);
        }
        else
        {
            // Stop voice scroll if active (can't monitor + record simultaneously)
            if (ScrollMode == ScrollMode.Voice)
            {
                StopVoiceScrollTimer();
                _audio.StopVoiceMonitor();
                IsVoiceActive = false;
            }

            CurrentMode = OverlayMode.QA;
            QaQuestion  = "";
            QaAnswer    = "";
            FollowUpSuggestions.Clear();
            QaStatus    = "Recording… (Ctrl+Alt+R to stop)";
            IsRecording = true;
            await _audio.StartRecordingAsync(_config.Current.AudioRecordingSource,
                _config.Current.AudioDeviceId, _config.Current.AudioOutputDeviceId);
            StartLivePreview();
        }
    }

    /// <summary>Computes and updates the Block 5 pacing-coach summary for the recording that was
    /// just transcribed — see PacingAnalyzer's own doc comment for the full approach/reasoning.
    /// A null result (not enough words/speaking time, or an unparseable clip) shows a neutral
    /// message rather than a wrong or misleading number.</summary>
    private void UpdatePacingSummary(byte[] wavData, string transcriptText)
    {
        var threshold = (float)_config.Current.Appearance.VoiceRmsThreshold;
        var pacing = PacingAnalyzer.Analyze(wavData, transcriptText, threshold);
        PacingSummary = pacing is null
            ? "Not enough data for a pace estimate."
            : $"{pacing.WordsPerMinute:F0} words/min — {pacing.Feedback} ({pacing.WordCount} words over {pacing.SpeakingSeconds:F0}s of speech)";
        PacingLevel = pacing?.Level.ToString() ?? "None";
    }

    /// <summary>Shared "call the AI, update state" tail for a completed Q&amp;A turn — extracted
    /// from ToggleRecordingAsync's stop-branch for clarity. Updates the Block 1 conversation
    /// memory (used only for AI context, NOT rendered on the TP — see ConversationTurns' own
    /// doc comment for why), appends to the active Q&amp;A session file (if any, via
    /// QaSessionService — a no-op when no session is active), and — if enabled — fetches
    /// follow-up suggestions (plain informational text on the TP, not an interactive action) for
    /// the next round.</summary>
    private async Task AskAndAnswerAsync(string question)
    {
        var cfg = _config.Current;
        var kbContext = _knowledgeBase.BuildContextForQuestion(question, cfg);
        var ans = await _ai.GetAnswerAsync(question, cfg, ConversationTurns, kbContext);
        QaAnswer = ans.Success ? ans.Text : $"Error: {ans.Error}";
        QaStatus = ans.Success ? "" : "AI call failed";
        IsBusy   = false;

        if (!ans.Success) return;

        ConversationTurns.Add(new ChatTurn(question, ans.Text));
        while (ConversationTurns.Count > MaxConversationTurns)
            ConversationTurns.RemoveAt(0);
        OnPropertyChanged(nameof(ConversationTurnCount));

        _qaSession.AppendTurn(question, ans.Text);
        OnPropertyChanged(nameof(QaSessionStatusText));

        if (_config.Current.ShowFollowUpSuggestions)
            await FetchFollowUpSuggestionsAsync(question, ans.Text);

        // Incremented LAST, once every other field this turn touches (QaAnswer, PacingSummary,
        // FollowUpSuggestions) is fully settled — an external agent polling on QaTurnCount as its
        // "something new happened" signal (see this property's own doc comment) should never
        // observe a bumped counter alongside stale/still-populating follow-up suggestions.
        QaTurnCount++;
    }

    private async Task FetchFollowUpSuggestionsAsync(string question, string answer)
    {
        var suggestions = await _ai.GetFollowUpSuggestionsAsync(question, answer, _config.Current);
        FollowUpSuggestions.Clear();
        foreach (var s in suggestions) FollowUpSuggestions.Add(s);
    }

    // ── Appearance sync from Controller ──────────────────────────────────────

    public void ApplyAppearance(int fontSize, double opacity, string fontColor)
    {
        FontSize  = fontSize;
        Opacity   = opacity;
        FontColor = fontColor;

        _config.Current.Appearance.FontSize  = fontSize;
        _config.Current.Appearance.Opacity   = (int)(opacity * 100);
        _config.Current.Appearance.FontColor = fontColor;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Cleanup()
    {
        StopAutoScroll();
        StopVoiceScrollTimer();
        StopLivePreview();
        _audio.StopVoiceMonitor();
    }
}
