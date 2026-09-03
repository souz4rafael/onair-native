namespace OnAirNative.Services;

/// <summary>
/// Minimal, self-contained RIFF/WAVE PCM reader — deliberately NOT a dependency on NAudio's own
/// WaveFileReader, to keep this project (OnAirNative.Core) free of any native/device-oriented
/// dependency, matching its established "pure, fully unit-testable, no WinUI/native concerns"
/// architecture boundary (NAudio itself is a managed library, but this project has stayed
/// dependency-light on principle since it was split out — see the testing-infrastructure work
/// that created it). Standard PCM WAV is a simple, stable, well-documented binary layout; this
/// only needs to read files THIS APP'S OWN AudioService already wrote (via NAudio's
/// WaveFileWriter), not arbitrary WAV files from the wild, so a general-purpose codec isn't
/// required — just a correct chunk walker for canonical PCM/IEEE-float WAV.
/// </summary>
internal static class WavReader
{
    public sealed record WavData(int SampleRate, int BitsPerSample, int Channels, bool IsIeeeFloat, byte[] Samples);

    /// <summary>Parses a WAV byte array into its format info + raw sample bytes. Walks RIFF
    /// chunks by tag (rather than assuming fixed offsets) so it tolerates chunks appearing in
    /// either order, or extra chunks NAudio may include — only "fmt " and "data" are actually
    /// needed. Throws InvalidDataException for anything that isn't a well-formed RIFF/WAVE
    /// file, or is missing either required chunk — callers (PacingAnalyzer) treat that as "can't
    /// analyze this recording" rather than propagating a parse error to the user.</summary>
    public static WavData Read(byte[] wav)
    {
        using var ms = new MemoryStream(wav);
        using var br = new BinaryReader(ms);

        if (ms.Length < 12 || new string(br.ReadChars(4)) != "RIFF")
            throw new InvalidDataException("Not a RIFF file.");
        br.ReadInt32(); // overall RIFF size, unused — chunk sizes below are authoritative
        if (new string(br.ReadChars(4)) != "WAVE")
            throw new InvalidDataException("Not a WAVE file.");

        int sampleRate = 0, bitsPerSample = 0, channels = 0, audioFormat = 0;
        byte[]? samples = null;

        while (ms.Position <= ms.Length - 8)
        {
            var chunkId   = new string(br.ReadChars(4));
            var chunkSize = br.ReadInt32();
            var chunkEnd  = ms.Position + chunkSize;

            if (chunkId == "fmt ")
            {
                audioFormat   = br.ReadInt16(); // 1 = PCM, 3 = IEEE float
                channels      = br.ReadInt16();
                sampleRate    = br.ReadInt32();
                br.ReadInt32();   // byte rate, unused
                br.ReadInt16();   // block align, unused
                bitsPerSample = br.ReadInt16();
            }
            else if (chunkId == "data")
            {
                var available = (int)Math.Min(chunkSize, ms.Length - ms.Position);
                samples = br.ReadBytes(available);
            }

            // Skip to the next chunk boundary regardless of how much of this chunk we actually
            // read above — RIFF chunks are word-aligned (padded to an even size).
            var seekTo = chunkEnd + (chunkSize % 2 == 1 ? 1 : 0);
            if (seekTo > ms.Length) break;
            ms.Position = seekTo;
        }

        if (samples is null || sampleRate == 0 || bitsPerSample == 0)
            throw new InvalidDataException("Missing fmt or data chunk.");

        return new WavData(sampleRate, bitsPerSample, channels == 0 ? 1 : channels, audioFormat == 3, samples);
    }
}
