using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>Direct coverage of WavReader's RIFF chunk walking, separate from PacingAnalyzerTests'
/// higher-level behavior — in particular the "extra/out-of-order chunks" tolerance, since
/// PacingAnalyzerTests' own WAV builder always writes the canonical fmt-then-data order.</summary>
public class WavReaderTests
{
    private static byte[] BuildWav(short channels, int sampleRate, short bitsPerSample, byte[] data, string? extraChunkId = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF".ToCharArray());

        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate   = sampleRate * blockAlign;

        var bodyLength = 4 /*WAVE*/
            + 8 + 16 /*fmt chunk*/
            + (extraChunkId is null ? 0 : 8 + 4) /*optional extra 4-byte chunk*/
            + 8 + data.Length + (data.Length % 2);
        bw.Write(bodyLength);
        bw.Write("WAVE".ToCharArray());

        bw.Write("fmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        if (extraChunkId is not null)
        {
            bw.Write(extraChunkId.ToCharArray());
            bw.Write(4);
            bw.Write(0xDEADBEEF);
        }

        bw.Write("data".ToCharArray());
        bw.Write(data.Length);
        bw.Write(data);
        if (data.Length % 2 == 1) bw.Write((byte)0); // word-align padding

        return ms.ToArray();
    }

    [Fact]
    public void Read_StandardMonoPcmWav_ParsesFormatCorrectly()
    {
        var data = new byte[] { 1, 0, 2, 0, 3, 0 }; // three 16-bit samples
        var wav = BuildWav(channels: 1, sampleRate: 16000, bitsPerSample: 16, data);

        var result = WavReader.Read(wav);

        Assert.Equal(16000, result.SampleRate);
        Assert.Equal(16, result.BitsPerSample);
        Assert.Equal(1, result.Channels);
        Assert.False(result.IsIeeeFloat);
        Assert.Equal(data, result.Samples);
    }

    [Fact]
    public void Read_WavWithExtraChunkBeforeData_SkipsItAndStillFindsData()
    {
        var data = new byte[] { 5, 0, 6, 0 };
        var wav = BuildWav(channels: 2, sampleRate: 44100, bitsPerSample: 16, data, extraChunkId: "LIST");

        var result = WavReader.Read(wav);

        Assert.Equal(44100, result.SampleRate);
        Assert.Equal(2, result.Channels);
        Assert.Equal(data, result.Samples);
    }

    [Fact]
    public void Read_NotARiffFile_ThrowsInvalidDataException()
    {
        var notWav = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B };
        Assert.Throws<InvalidDataException>(() => WavReader.Read(notWav));
    }

    [Fact]
    public void Read_MissingDataChunk_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF".ToCharArray());
        bw.Write(28);
        bw.Write("WAVE".ToCharArray());
        bw.Write("fmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(16000);
        bw.Write(32000);
        bw.Write((short)2);
        bw.Write((short)16);
        // No "data" chunk at all.

        Assert.Throws<InvalidDataException>(() => WavReader.Read(ms.ToArray()));
    }
}
