namespace OnAirNative.Services;

/// <summary>
/// Shared RMS (root-mean-square) amplitude calculation, scaled to 0-100 — the single source of
/// truth for "how loud is this chunk of PCM audio", used by both the live monitoring path
/// (AudioService.CalculateRms, WinUI project, real device callbacks) and the post-hoc pacing
/// analysis path (PacingAnalyzer, this project, replaying an already-recorded WAV's bytes).
/// Splitting this out (rather than duplicating the formula in two places) guarantees the SAME
/// AppConfig.Appearance.VoiceRmsThreshold value means the same thing whether it's being compared
/// against a live callback's level or a recorded file's level.
/// </summary>
public static class AudioLevel
{
    /// <summary>Returns RMS amplitude scaled to 0-100. Supports the two PCM shapes this app's
    /// audio pipeline actually produces: 16-bit signed integer PCM (the common case — the mixed
    /// "both sources" recording path always uses this, and most microphone devices' native
    /// format does too), and 32-bit IEEE float PCM (some devices' native/shared-mode format).
    /// Any other bit depth/encoding yields 0 rather than throwing — an unrecognized format should
    /// never crash a level meter or a pacing estimate, just report "no signal".</summary>
    public static float CalculateRms(byte[] buffer, int bytesRecorded, int bitsPerSample, bool isIeeeFloat)
    {
        if (bytesRecorded == 0) return 0f;

        double sumSq = 0;
        int count = 0;

        if (isIeeeFloat && bitsPerSample == 32)
        {
            for (int i = 0; i + 3 < bytesRecorded; i += 4)
            {
                float s = BitConverter.ToSingle(buffer, i);
                sumSq += s * s;
                count++;
            }
        }
        else if (bitsPerSample == 16)
        {
            for (int i = 0; i + 1 < bytesRecorded; i += 2)
            {
                float s = BitConverter.ToInt16(buffer, i) / 32768f;
                sumSq += s * s;
                count++;
            }
        }

        if (count == 0) return 0f;
        return (float)(Math.Sqrt(sumSq / count) * 100.0);
    }
}
