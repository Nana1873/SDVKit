using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class CliReviewQueryOptionsTests
{
    [Theory]
    [InlineData("data", "keys")]
    [InlineData("audio", "cue")]
    [InlineData("map", "get")]
    [InlineData("texture", "get")]
    [InlineData("mod-assets", "keys")]
    public void UnknownOptionsAreUsageErrorsBeforeAnyReviewDispatch(string family, string operation)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int dispatched = 0;
        LiveLabCommandResult Unexpected()
        {
            dispatched++;
            return new LiveLabCommandResult(3, new { state = "blocked" });
        }

        int exitCode = CliApplication.Run(
            ["project", "review", family, operation, "--bogus", "--json"],
            output,
            error,
            () => throw new InvalidOperationException("Discovery must not run."),
            runProjectReviewData: (_, _) => Unexpected(),
            runProjectReviewAudio: (_, _) => Unexpected(),
            runProjectReviewMap: (_, _) => Unexpected(),
            runProjectReviewTexture: (_, _) => Unexpected(),
            runProjectReviewModAsset: (_, _) => Unexpected());

        Assert.Equal(2, exitCode);
        Assert.Equal(0, dispatched);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains($"project review {family}", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--offset")]
    [InlineData("--bogus")]
    public void DataKeysBeginningWithDashRequireAndSupportEndOfOptions(string key)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        ReviewDataQuery? received = null;

        int exitCode = CliApplication.Run(
            ["project", "review", "data", "get", "Data/Objects", "--json", "--", key],
            output,
            error,
            () => throw new InvalidOperationException("Discovery must not run."),
            runProjectReviewData: (query, _) =>
            {
                received = query;
                return new LiveLabCommandResult(0, new { state = "ready" });
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(new ReviewDataQuery("get", "Data/Objects", key, 0, 1), received);
        Assert.Equal(string.Empty, error.ToString());
    }
}
