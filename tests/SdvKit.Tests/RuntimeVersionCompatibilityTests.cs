using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class RuntimeVersionCompatibilityTests
{
    [Theory]
    [InlineData("1.6.15", "1.6.15.24356", "4.5.0")]
    [InlineData("1.6.15", "1.6.15.24357", "4.5.2")]
    [InlineData("1.6.16", "1.6.16.25000", "4.6.1")]
    [InlineData("1.6.99", "1.6.99.65535", "4.99.0")]
    public void SupportedPatchVersionsAreAccepted(
        string gameVersion,
        string gameFileVersion,
        string smapiVersion)
    {
        Assert.True(
            RuntimeVersionCompatibility.TryValidate(
                gameVersion,
                gameFileVersion,
                smapiVersion,
                out string error),
            error);
    }

    [Theory]
    [InlineData("1.6.14", "1.6.15.24356", "4.5.2", "Stardew game")]
    [InlineData("1.6.15", "1.6.15.24355", "4.5.2", "Stardew file version")]
    [InlineData("1.6.15", "1.6.15.24356", "4.4.9", "SMAPI")]
    [InlineData("1.7.0", "1.6.15.24356", "4.5.2", "Stardew game")]
    [InlineData("1.6.15", "1.7.0.0", "4.5.2", "Stardew file version")]
    [InlineData("1.6.15", "1.6.15.24356", "5.0.0", "SMAPI")]
    [InlineData("2.0.0", "2.0.0.0", "6.0.0", "Stardew game")]
    public void VersionsOutsideTheSupportedBandsFailClosed(
        string gameVersion,
        string gameFileVersion,
        string smapiVersion,
        string incompatibleComponent)
    {
        Assert.False(
            RuntimeVersionCompatibility.TryValidate(
                gameVersion,
                gameFileVersion,
                smapiVersion,
                out string error));
        Assert.Contains(incompatibleComponent, error, StringComparison.Ordinal);
        Assert.Contains(gameVersion, error, StringComparison.Ordinal);
        Assert.Contains(gameFileVersion, error, StringComparison.Ordinal);
        Assert.Contains(smapiVersion, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("current", "1.6.15.24356", "4.5.2")]
    [InlineData("1.6.15", "", "4.5.2")]
    [InlineData("1.6.15", "1.6.15.24356", "preview")]
    public void MalformedVersionReportsFailClosed(
        string gameVersion,
        string gameFileVersion,
        string smapiVersion)
    {
        Assert.False(
            RuntimeVersionCompatibility.TryValidate(
                gameVersion,
                gameFileVersion,
                smapiVersion,
                out string error));
        Assert.Contains("not parseable", error, StringComparison.Ordinal);
    }
}
