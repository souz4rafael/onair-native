namespace OnAirNative.Services;

/// <summary>
/// A small stateful voice-activity-detection state machine — takes a stream of RMS amplitude
/// readings (0-100, same scale as <see cref="AudioLevel.CalculateRms"/>) over time and decides
/// whether the input currently counts as "speech". This replaces a naive instantaneous
/// <c>rms &gt; threshold</c> compare (what Voice-activated scroll used before Block 5) with real
/// hysteresis:
///
///   - ATTACK: requires the level to stay above threshold for a short minimum duration before
///     flipping to "active" — a single loud transient (a click, a cough, a stray noise) shouldn't
///     immediately trigger.
///   - RELEASE (hangover): once active, stays active for a short duration after the level drops
///     back below threshold — natural pauses between words/sentences shouldn't immediately flip
///     back to inactive, which is exactly what made the old naive compare feel choppy/flickery
///     for voice-activated scroll in practice.
///
/// Deliberately does NOT auto-learn/adapt the threshold itself (no noise-floor estimation): the
/// existing user-configured <c>AppConfig.Appearance.VoiceRmsThreshold</c> stays the single,
/// predictable, already-exposed control (Settings slider, hotkeys, Stream Deck dial) — this
/// class only adds the missing hysteresis smoothing on top of that existing threshold. A
/// self-learning noise floor would be a materially larger, riskier change (e.g. it could be
/// poisoned by speech happening before any "quiet" calibration window) for a use case this app
/// doesn't clearly need — hysteresis alone is what actually distinguishes a real VAD state
/// machine from a bare greater-than compare, and is the lower-risk, well-understood improvement.
/// </summary>
public sealed class VoiceActivityDetector
{
    private readonly float _attackMs;
    private readonly float _releaseMs;

    private bool   _isActive;
    private double _msAboveThreshold; // consecutive ms currently above threshold (drives attack)
    private double _msBelowThreshold; // consecutive ms currently at/below threshold (drives release)

    /// <param name="attackMs">Minimum consecutive time above threshold before flipping to active.
    /// Default 60ms — roughly 2-3 audio callbacks at typical buffer sizes, enough to reject a
    /// single-callback transient without adding noticeable delay to real speech onset.</param>
    /// <param name="releaseMs">Minimum consecutive time at/below threshold before flipping back
    /// to inactive. Default 300ms — comfortably longer than a natural pause between words, short
    /// enough to still notice when the presenter has genuinely stopped talking.</param>
    public VoiceActivityDetector(float attackMs = 60, float releaseMs = 300)
    {
        _attackMs  = attackMs;
        _releaseMs = releaseMs;
    }

    /// <summary>Whether the detector currently considers the input "speech".</summary>
    public bool IsActive => _isActive;

    /// <summary>Feeds one new level reading. <paramref name="elapsedMs"/> is the time since the
    /// previous reading, so this works regardless of the caller's actual tick rate — a live
    /// 50ms UI timer, or a fixed frame size when replaying an already-recorded WAV after the
    /// fact. Returns the updated <see cref="IsActive"/> state.</summary>
    public bool Process(float rms, float threshold, double elapsedMs)
    {
        if (rms > threshold)
        {
            _msAboveThreshold += elapsedMs;
            _msBelowThreshold  = 0;
            if (!_isActive && _msAboveThreshold >= _attackMs)
                _isActive = true;
        }
        else
        {
            _msAboveThreshold  = 0;
            _msBelowThreshold += elapsedMs;
            if (_isActive && _msBelowThreshold >= _releaseMs)
                _isActive = false;
        }
        return _isActive;
    }

    /// <summary>Resets to a fresh, inactive state — used when starting a new monitoring session
    /// (a new voice-scroll session, or analyzing a new recording) so stale state from a previous
    /// session/recording never leaks in.</summary>
    public void Reset()
    {
        _isActive         = false;
        _msAboveThreshold = 0;
        _msBelowThreshold = 0;
    }
}
