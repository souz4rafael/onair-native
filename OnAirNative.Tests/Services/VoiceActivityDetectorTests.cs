using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Covers VoiceActivityDetector's attack/release hysteresis — the behavior that actually
/// distinguishes it from a naive <c>rms &gt; threshold</c> instantaneous compare.
/// </summary>
public class VoiceActivityDetectorTests
{
    private const float Threshold = 10f;

    [Fact]
    public void Process_LevelBelowThreshold_StaysInactive()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);

        var active = vad.Process(5f, Threshold, 100);

        Assert.False(active);
        Assert.False(vad.IsActive);
    }

    [Fact]
    public void Process_LevelAboveThresholdBriefly_DoesNotActivateBeforeAttackDurationElapses()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);

        // Only 30ms above threshold so far — less than the 60ms attack requirement.
        var active = vad.Process(20f, Threshold, 30);

        Assert.False(active);
    }

    [Fact]
    public void Process_LevelAboveThresholdForAttackDuration_BecomesActive()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);

        vad.Process(20f, Threshold, 30);
        var active = vad.Process(20f, Threshold, 30); // cumulative 60ms >= attackMs

        Assert.True(active);
        Assert.True(vad.IsActive);
    }

    [Fact]
    public void Process_SingleLoudTransientShorterThanAttack_NeverActivates()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);

        vad.Process(20f, Threshold, 20);  // above, but brief
        var afterTransient = vad.Process(2f, Threshold, 200); // drops back to quiet

        Assert.False(afterTransient);
        Assert.False(vad.IsActive);
    }

    [Fact]
    public void Process_BriefDipBelowThresholdDuringSpeech_StaysActiveWithinReleaseDuration()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);

        // Ramp up to active.
        vad.Process(20f, Threshold, 30);
        vad.Process(20f, Threshold, 30);
        Assert.True(vad.IsActive);

        // A short natural pause between words (100ms) — well under the 300ms release window.
        var duringPause = vad.Process(2f, Threshold, 100);

        Assert.True(duringPause, "A brief pause shorter than the release window should not drop activity.");
    }

    [Fact]
    public void Process_SustainedSilenceBeyondReleaseDuration_BecomesInactive()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);

        vad.Process(20f, Threshold, 30);
        vad.Process(20f, Threshold, 30);
        Assert.True(vad.IsActive);

        vad.Process(2f, Threshold, 150);
        var stillActive = vad.Process(2f, Threshold, 150); // cumulative 300ms silence >= releaseMs

        Assert.False(stillActive);
        Assert.False(vad.IsActive);
    }

    [Fact]
    public void Process_ResumedSpeechDuringReleaseWindow_ResetsSilenceCounterAndStaysActive()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);
        vad.Process(20f, Threshold, 30);
        vad.Process(20f, Threshold, 30);
        Assert.True(vad.IsActive);

        vad.Process(2f, Threshold, 200);  // 200ms of silence — not yet enough to release
        var resumed = vad.Process(20f, Threshold, 30); // speech resumes

        Assert.True(resumed);

        // A further 200ms of silence should NOT be enough to release, since the silence
        // counter should have reset when speech resumed above.
        var stillActiveAfterResume = vad.Process(2f, Threshold, 200);
        Assert.True(stillActiveAfterResume);
    }

    [Fact]
    public void Reset_ClearsActiveStateAndTimers()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);
        vad.Process(20f, Threshold, 30);
        vad.Process(20f, Threshold, 30);
        Assert.True(vad.IsActive);

        vad.Reset();

        Assert.False(vad.IsActive);
        // After reset, activation should again require the full attack duration from scratch.
        var active = vad.Process(20f, Threshold, 30);
        Assert.False(active);
    }

    [Fact]
    public void Process_ExactlyAtThreshold_IsNotConsideredAboveThreshold()
    {
        var vad = new VoiceActivityDetector(attackMs: 60, releaseMs: 300);

        // rms == threshold should behave like "below" (strict greater-than), matching the
        // pre-existing rms > threshold convention this replaces.
        vad.Process(Threshold, Threshold, 100);
        vad.Process(Threshold, Threshold, 100);

        Assert.False(vad.IsActive);
    }
}
