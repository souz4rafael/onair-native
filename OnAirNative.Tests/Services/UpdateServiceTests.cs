using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Covers UpdateService's pure version-compare logic (IsNewer/NormalizeForParse) in isolation
/// from the real GitHub Releases API call in CheckForUpdateAsync.
/// </summary>
public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.2.0", "1.2.1", true)]
    [InlineData("1.2.1", "1.2.0", false)]
    [InlineData("1.2.0", "1.2.0", false)] // equal is not "newer"
    [InlineData("1.1.0", "1.2.0", true)]
    [InlineData("2.0.0", "1.9.9", false)]
    public void IsNewer_ComparesSemanticVersionsCorrectly(string current, string latest, bool expectedNewer)
    {
        Assert.Equal(expectedNewer, UpdateService.IsNewer(current, latest));
    }

    [Fact]
    public void IsNewer_BareMajorVersion_IsPaddedAndComparedCorrectly()
    {
        // NormalizeForParse pads a bare "1" to "1.0" since Version.Parse needs Major.Minor.
        Assert.True(UpdateService.IsNewer("1", "2"));
        Assert.False(UpdateService.IsNewer("1", "1"));
    }

    [Fact]
    public void IsNewer_PreReleaseSuffix_IsStrippedBeforeComparing()
    {
        // "1.2.1-beta" and "1.2.1" must compare as equal (suffix stripped), not fail to parse.
        Assert.False(UpdateService.IsNewer("1.2.1-beta", "1.2.1"));
        Assert.True(UpdateService.IsNewer("1.2.0", "1.2.1-beta"));
    }

    [Theory]
    [InlineData("not-a-version", "1.2.0")]
    [InlineData("1.2.0", "not-a-version")]
    [InlineData("", "1.2.0")]
    public void IsNewer_UnparsableVersion_ReturnsFalseRatherThanThrowing(string current, string latest)
    {
        Assert.False(UpdateService.IsNewer(current, latest));
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1.2", "1.2")]
    [InlineData("1.2.0-beta", "1.2.0")]
    [InlineData("1.2.0-rc.1", "1.2.0")]
    public void NormalizeForParse_ProducesVersionParseableStrings(string input, string expected)
    {
        Assert.Equal(expected, UpdateService.NormalizeForParse(input));
    }
}
