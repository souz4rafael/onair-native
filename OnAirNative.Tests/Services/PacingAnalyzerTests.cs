using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Covers PacingAnalyzer's words-per-minute estimation, including the key behavior that
/// distinguishes it from a naive "words / total clip duration" calculation: only SPEECH time
/// (as classified by VoiceActivityDetector) counts, not silence/pauses. Also exercises WavReader
/// indirectly (every test here round-trips a synthetic WAV byte array end-to-end).
/// </summary>
public class PacingAnalyzerTests
{
    private const int SampleRate = 16000;
    private const float VoiceThreshold = 10f;

    // A constant-amplitude 16-bit PCM signal's RMS (scaled 0-100) is simply amplitude/32768*100,
    // since every sample is identical. ~30 comfortably clears VoiceThreshold=10; 0 is silence.
    private const short LoudAmplitude = 9830; // ≈ RMS 30

    /// <summary>Builds a minimal valid mono 16-bit PCM WAV byte array from a sequence of
    /// (duration, amplitude) segments — e.g. [(2.0, Loud), (1.0, 0), (2.0, Loud)] for
    /// speech-pause-speech.</summary>
    private static byte[] BuildWav(params (double seconds, short amplitude)[] segments)
    {
        var samples = new List<short>();
        foreach (var (seconds, amplitude) in segments)
        {
            var count = (int)(seconds * SampleRate);
            for (var i = 0; i < count; i++) samples.Add(amplitude);
        }

        var dataBytes = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(dataBytes, i * 2);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF".ToCharArray());
        bw.Write(36 + dataBytes.Length);
        bw.Write("WAVE".ToCharArray());
        bw.Write("fmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data".ToCharArray());
        bw.Write(dataBytes.Length);
        bw.Write(dataBytes);
        return ms.ToArray();
    }

    private static string Words(int count) => string.Join(" ", Enumerable.Repeat("word", count));

    [Fact]
    public void Analyze_EmptyWavData_ReturnsNull()
    {
        var result = PacingAnalyzer.Analyze([], Words(20), VoiceThreshold);
        Assert.Null(result);
    }

    [Fact]
    public void Analyze_EmptyTranscript_ReturnsNull()
    {
        var wav = BuildWav((5.0, LoudAmplitude));
        var result = PacingAnalyzer.Analyze(wav, "", VoiceThreshold);
        Assert.Null(result);
    }

    [Fact]
    public void Analyze_TooFewWords_ReturnsNull()
    {
        var wav = BuildWav((5.0, LoudAmplitude));
        var result = PacingAnalyzer.Analyze(wav, "just one word", VoiceThreshold);
        Assert.Null(result);
    }

    [Fact]
    public void Analyze_AllSilence_ReturnsNull()
    {
        var wav = BuildWav((5.0, 0));
        var result = PacingAnalyzer.Analyze(wav, Words(20), VoiceThreshold);
        Assert.Null(result);
    }

    [Fact]
    public void Analyze_CorruptWavData_ReturnsNullRatherThanThrowing()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var result = PacingAnalyzer.Analyze(garbage, Words(20), VoiceThreshold);
        Assert.Null(result);
    }

    [Fact]
    public void Analyze_NormalPace_ReturnsGoodFeedback()
    {
        // 10 seconds of speech, 20 words => 120 wpm, inside the "good" band (110-170).
        var wav = BuildWav((10.0, LoudAmplitude));
        var result = PacingAnalyzer.Analyze(wav, Words(20), VoiceThreshold);

        Assert.NotNull(result);
        Assert.Equal(20, result!.WordCount);
        Assert.InRange(result.SpeakingSeconds, 9.0, 10.0);
        Assert.InRange(result.WordsPerMinute, 110, 130);
        Assert.Equal("good", result.Feedback);
    }

    [Fact]
    public void Analyze_SlowPace_ReturnsSlowFeedback()
    {
        // 10 seconds of speech, 10 words => 60 wpm, below the slow threshold (110).
        var wav = BuildWav((10.0, LoudAmplitude));
        var result = PacingAnalyzer.Analyze(wav, Words(10), VoiceThreshold);

        Assert.NotNull(result);
        Assert.True(result!.WordsPerMinute < 110);
        Assert.Contains("slow", result.Feedback);
    }

    [Fact]
    public void Analyze_FastPace_ReturnsFastFeedback()
    {
        // 10 seconds of speech, 40 words => 240 wpm, above the fast threshold (170).
        var wav = BuildWav((10.0, LoudAmplitude));
        var result = PacingAnalyzer.Analyze(wav, Words(40), VoiceThreshold);

        Assert.NotNull(result);
        Assert.True(result!.WordsPerMinute > 170);
        Assert.Contains("fast", result.Feedback);
    }

    [Fact]
    public void Analyze_MixOfSpeechAndSilence_OnlyCountsSpeechTimeNotTotalDuration()
    {
        // 5s speech + 5s silence + 5s speech = 15s total clip, but only ~10s of actual speech.
        // 20 words over the TRUE speaking time (10s) => 120 wpm ("good"); over the total wall
        // clock (15s) it would compute to 80 wpm ("a bit slow") — this test fails if pacing
        // analysis ever regresses to using total duration instead of VAD-measured speech time.
        var wav = BuildWav((5.0, LoudAmplitude), (5.0, 0), (5.0, LoudAmplitude));
        var result = PacingAnalyzer.Analyze(wav, Words(20), VoiceThreshold);

        Assert.NotNull(result);
        Assert.InRange(result!.SpeakingSeconds, 9.0, 10.5);
        Assert.Equal("good", result.Feedback);
    }

    [Fact]
    public void Analyze_StereoWav_DoesNotThrowAndStillProducesAResult()
    {
        // Build a stereo variant manually (channels=2) to confirm PacingAnalyzer/WavReader
        // don't assume mono — interleave the same amplitude on both channels.
        var count = (int)(6.0 * SampleRate);
        var dataBytes = new byte[count * 2 * 2]; // 2 channels * 2 bytes/sample
        for (var i = 0; i < count; i++)
        {
            BitConverter.GetBytes(LoudAmplitude).CopyTo(dataBytes, i * 4);
            BitConverter.GetBytes(LoudAmplitude).CopyTo(dataBytes, i * 4 + 2);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF".ToCharArray());
        bw.Write(36 + dataBytes.Length);
        bw.Write("WAVE".ToCharArray());
        bw.Write("fmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)2); // stereo
        bw.Write(SampleRate);
        bw.Write(SampleRate * 4);
        bw.Write((short)4);
        bw.Write((short)16);
        bw.Write("data".ToCharArray());
        bw.Write(dataBytes.Length);
        bw.Write(dataBytes);

        var result = PacingAnalyzer.Analyze(ms.ToArray(), Words(20), VoiceThreshold);

        Assert.NotNull(result);
        Assert.True(result!.SpeakingSeconds > 0);
    }
}
