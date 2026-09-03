using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>Direct coverage of the shared RMS calculation used by both AudioService (live device
/// callbacks) and PacingAnalyzer (replaying a recorded WAV) — see AudioLevel's own doc comment
/// for why this was extracted rather than duplicated.</summary>
public class AudioLevelTests
{
    [Fact]
    public void CalculateRms_Silence16Bit_ReturnsZero()
    {
        var buffer = new byte[] { 0, 0, 0, 0 };
        var rms = AudioLevel.CalculateRms(buffer, buffer.Length, bitsPerSample: 16, isIeeeFloat: false);
        Assert.Equal(0f, rms);
    }

    [Fact]
    public void CalculateRms_FullScale16Bit_ReturnsApproximately100()
    {
        var sample = BitConverter.GetBytes((short)32767);
        var buffer = sample.Concat(sample).ToArray();
        var rms = AudioLevel.CalculateRms(buffer, buffer.Length, bitsPerSample: 16, isIeeeFloat: false);
        Assert.InRange(rms, 99f, 100f);
    }

    [Fact]
    public void CalculateRms_HalfScale16Bit_ReturnsApproximately50()
    {
        var sample = BitConverter.GetBytes((short)16384); // half of 32768
        var buffer = sample.Concat(sample).ToArray();
        var rms = AudioLevel.CalculateRms(buffer, buffer.Length, bitsPerSample: 16, isIeeeFloat: false);
        Assert.InRange(rms, 49f, 51f);
    }

    [Fact]
    public void CalculateRms_IeeeFloat32_ScalesCorrectly()
    {
        var sample = BitConverter.GetBytes(0.5f);
        var buffer = sample.Concat(sample).ToArray();
        var rms = AudioLevel.CalculateRms(buffer, buffer.Length, bitsPerSample: 32, isIeeeFloat: true);
        Assert.InRange(rms, 49f, 51f);
    }

    [Fact]
    public void CalculateRms_EmptyBuffer_ReturnsZeroWithoutThrowing()
    {
        var rms = AudioLevel.CalculateRms([], 0, bitsPerSample: 16, isIeeeFloat: false);
        Assert.Equal(0f, rms);
    }

    [Fact]
    public void CalculateRms_UnrecognizedFormat_ReturnsZeroRatherThanThrowing()
    {
        var buffer = new byte[] { 1, 2, 3 }; // 8-bit, not supported
        var rms = AudioLevel.CalculateRms(buffer, buffer.Length, bitsPerSample: 8, isIeeeFloat: false);
        Assert.Equal(0f, rms);
    }
}
