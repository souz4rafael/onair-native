namespace OnAirNative.Services;

/// <summary>Result of a pacing analysis — null from <see cref="PacingAnalyzer.Analyze"/> itself
/// means "not enough data for a meaningful estimate" (too few words, too little detected
/// speaking time, or an unparseable/empty recording), never a wrong or misleading number.</summary>
public sealed record PacingResult(int WordCount, double SpeakingSeconds, double WordsPerMinute, string Feedback, PacingLevel Level);

/// <summary>Coarse pacing classification mirroring <see cref="PacingAnalyzer"/>'s WPM thresholds —
/// exposed separately from the free-text Feedback so callers (RemoteState / Stream Deck) can
/// render a simple color-coded status without parsing English sentences. There's no "no data"
/// member here on purpose: that case is represented by <see cref="PacingResult"/> itself being
/// null, not by a level value.</summary>
public enum PacingLevel { Slow, Good, Fast }

/// <summary>
/// Estimates the presenter's speaking pace (words per minute) for a completed Q&amp;A recording,
/// using <see cref="VoiceActivityDetector"/> to measure actual SPEAKING time (excluding detected
/// silence/pauses) rather than the recording's total wall-clock duration — a presenter who
/// pauses to think shouldn't be penalized as "speaking slowly" just because the clock kept
/// running through the pause.
///
/// Deliberately scoped to per-recording feedback meant for the Controller's Q&amp;A tab, NOT the
/// TP: pacing coaching is presenter-facing self-improvement info, not client-facing content, and
/// (per this session's own established lesson from reverting the TP conversation-history
/// display — see plan history) the TP should stay uncluttered with only the current Q+A pair and
/// follow-up suggestions.
///
/// The WPM thresholds below are a general, widely-cited "comfortable spoken pace" guide, not a
/// precise or authoritative rule — presented to the user as a gentle nudge, never as a strict
/// pass/fail judgment.
/// </summary>
public static class PacingAnalyzer
{
    private const double SlowWpmThreshold = 110;
    private const double FastWpmThreshold = 170;

    // Below these, a pace estimate would be noise, not signal — e.g. a one-word "yes" answer
    // captured in half a second of audio could otherwise "compute" to an absurd wpm figure.
    private const double MinSpeakingSecondsForFeedback = 3.0;
    private const int    MinWordsForFeedback           = 5;

    private const int FrameMs = 30; // standard-ish VAD frame size

    /// <param name="wavData">The just-completed recording's raw WAV bytes (as returned by
    /// AudioService.StopRecordingAsync) — any format AudioService's own capture paths produce
    /// (16-bit PCM or 32-bit IEEE float; mono or multi-channel).</param>
    /// <param name="transcriptText">The transcript Whisper produced for this same recording.</param>
    /// <param name="voiceThreshold">The user's configured AppConfig.Appearance.VoiceRmsThreshold
    /// — reused as-is so "how sensitive is speech detection" means the same thing here as it
    /// does for live Voice-activated scroll.</param>
    public static PacingResult? Analyze(byte[] wavData, string transcriptText, float voiceThreshold)
    {
        if (wavData.Length == 0 || string.IsNullOrWhiteSpace(transcriptText))
            return null;

        var wordCount = CountWords(transcriptText);
        if (wordCount < MinWordsForFeedback)
            return null;

        WavReader.WavData wav;
        try
        {
            wav = WavReader.Read(wavData);
        }
        catch (InvalidDataException)
        {
            return null; // unparseable/corrupt audio — never let a pacing estimate crash the Q&A flow
        }

        var speakingSeconds = MeasureSpeakingSeconds(wav, voiceThreshold);
        if (speakingSeconds < MinSpeakingSecondsForFeedback)
            return null;

        var wpm = wordCount / (speakingSeconds / 60.0);
        var level = wpm switch
        {
            < SlowWpmThreshold => PacingLevel.Slow,
            > FastWpmThreshold => PacingLevel.Fast,
            _                  => PacingLevel.Good,
        };
        var feedback = level switch
        {
            PacingLevel.Slow => "a bit slow — consider picking it up slightly",
            PacingLevel.Fast => "a bit fast — consider slowing down for clarity",
            _                => "good",
        };

        return new PacingResult(wordCount, speakingSeconds, wpm, feedback, level);
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Replays the recording frame-by-frame (FrameMs chunks) through a fresh
    /// VoiceActivityDetector, summing the duration of frames classified as speech — this is what
    /// makes the pacing estimate ignore silence/pauses rather than counting the whole clip.</summary>
    private static double MeasureSpeakingSeconds(WavReader.WavData wav, float voiceThreshold)
    {
        var bytesPerSample = wav.BitsPerSample / 8;
        if (bytesPerSample == 0) return 0;

        var frameBytes = Math.Max(bytesPerSample * wav.Channels,
            wav.SampleRate * FrameMs / 1000 * bytesPerSample * wav.Channels);

        var vad = new VoiceActivityDetector();
        double speakingMs = 0;

        for (var offset = 0; offset < wav.Samples.Length; offset += frameBytes)
        {
            var length = Math.Min(frameBytes, wav.Samples.Length - offset);
            var rms = AudioLevel.CalculateRms(SliceInto(wav.Samples, offset, length), length, wav.BitsPerSample, wav.IsIeeeFloat);
            if (vad.Process(rms, voiceThreshold, FrameMs))
                speakingMs += FrameMs;
        }

        return speakingMs / 1000.0;
    }

    /// <summary>AudioLevel.CalculateRms takes a buffer + a byte count starting at index 0 (it
    /// mirrors NAudio's own callback signature, which always hands over a fresh buffer) — since
    /// we're iterating in-place over one big already-in-memory array instead, copy just the
    /// current frame out rather than changing that shared method's contract for this one caller.</summary>
    private static byte[] SliceInto(byte[] source, int offset, int length)
    {
        var slice = new byte[length];
        Array.Copy(source, offset, slice, 0, length);
        return slice;
    }
}
