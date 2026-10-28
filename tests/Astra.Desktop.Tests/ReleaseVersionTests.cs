using Astra.Desktop.Core;

namespace Astra.Desktop.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.10.0", "0.10.0-preview.2", true)]
    [InlineData("0.10.0-preview.2", "0.10.0-preview.1", true)]
    [InlineData("0.10.0-preview.1", "0.9.5", true)]
    [InlineData("0.10.0-preview.10", "0.10.0-preview.9", true)]
    [InlineData("0.13.0", "0.12.0", true)]
    [InlineData("v1.0.0", "0.99.99", true)]
    [InlineData("0.10.0+build5", "0.10.0", false)]
    [InlineData("0.10.0-preview.1", "0.10.0", false)]
    [InlineData("0.10.0", "0.10.0", false)]
    [InlineData("0.9.5", "0.10.0-preview.1", false)]
    [InlineData("0.10.0-preview", "0.10.0-preview.1", false)]
    [InlineData("0.10.0-alpha", "0.10.0-1", true)]
    [InlineData("garbage", "0.1.0", false)]
    [InlineData(null, "0.1.0", false)]
    public void IsNewer(string? candidate, string current, bool expected)
        => Assert.Equal(expected, ReleaseVersion.IsNewer(candidate, current));

    [Fact]
    public void Compare_is_null_for_unparsable_input()
    {
        Assert.Null(ReleaseVersion.Compare("x", "1.0.0"));
        Assert.Equal(0, ReleaseVersion.Compare("1.2", "1.2.0"));
    }

    [Theory]
    [InlineData("1.0", 1)]
    [InlineData("1", 1)]
    [InlineData(" 2.4", 2)]
    [InlineData("v1", null)]
    [InlineData("1x", null)]
    [InlineData("", null)]
    public void MajorOf(string version, int? expected) => Assert.Equal(expected, ReleaseVersion.MajorOf(version));

    [Fact]
    public void Api_version_compatibility_follows_the_expected_major()
    {
        Assert.True(ReleaseVersion.IsApiVersionCompatible("1.7"));
        Assert.False(ReleaseVersion.IsApiVersionCompatible("2.0"));
        Assert.False(ReleaseVersion.IsApiVersionCompatible(null));
    }
}
