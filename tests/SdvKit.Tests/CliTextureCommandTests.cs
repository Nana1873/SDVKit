using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class CliTextureCommandTests
{
    [Fact]
    public void AssetsDispatchesBoundedSingleQueryAndWritesStableJson()
    {
        ReviewTextureQuery? received = null;
        string? receivedLabRoot = null;
        ProjectReviewTextureCommandRunner runner = (query, labRoot) =>
        {
            received = query;
            receivedLabRoot = labRoot;
            return new LiveLabCommandResult(
                0,
                new ReviewTextureReport(
                    ReviewTextureContract.SchemaVersion,
                    "ready",
                    query.Operation,
                    "1.6.15",
                    "1.6.15.24356",
                    null,
                    ReviewTextureContract.CanonicalGameContentSource,
                    null,
                    null,
                    new ReviewTextureProvenanceReport(
                        ReviewTextureContract.FinalPipelineStage,
                        false,
                        "Unavailable."),
                    null,
                    [new ReviewTextureAssetReport(
                        "LooseSprites/Cursors",
                        ReviewTextureContract.CanonicalGameContentSource,
                        true)],
                    new ReviewTexturePage(5, 2, 1, 6, null),
                    new ReviewTextureCoverageReport(3550, 3550, 400, 3150, 0),
                    []));
        };

        (int exitCode, string output, string error) = Run(
            runner,
            "project",
            "review",
            "texture",
            "assets",
            "--offset",
            "5",
            "--limit",
            "2",
            "--topology",
            "single",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            new ReviewTextureQuery(
                ReviewTextureContract.AssetsOperation,
                null,
                5,
                2),
            received);
        Assert.Equal(Environment.CurrentDirectory, receivedLabRoot);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(400, root
            .GetProperty("coverage")
            .GetProperty("textures")
            .GetInt32());
        Assert.Equal(
            "LooseSprites/Cursors",
            root.GetProperty("assets")[0].GetProperty("assetName").GetString());
    }

    [Theory]
    [InlineData("get")]
    [InlineData("preview")]
    public void ExactOperationsDispatchOneCanonicalAsset(string operation)
    {
        ReviewTextureQuery? received = null;
        ProjectReviewTextureCommandRunner runner = (query, _) =>
        {
            received = query;
            return new LiveLabCommandResult(
                3,
                new ReviewTextureReport(
                    1,
                    "blocked",
                    query.Operation,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [new ReviewTextureProblem("expected", "Expected.")]));
        };

        (int exitCode, _, string error) = Run(
            runner,
            "project",
            "review",
            "texture",
            operation,
            "LooseSprites/Cursors",
            "--json");

        Assert.Equal(3, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            new ReviewTextureQuery(operation, "LooseSprites/Cursors", 0, 1),
            received);
    }

    [Theory]
    [InlineData("project", "review", "texture")]
    [InlineData("project", "review", "texture", "unknown", "--json")]
    [InlineData("project", "review", "texture", "assets")]
    [InlineData("project", "review", "texture", "assets", "extra", "--json")]
    [InlineData("project", "review", "texture", "assets", "--limit", "0", "--json")]
    [InlineData("project", "review", "texture", "assets", "--limit", "101", "--json")]
    [InlineData("project", "review", "texture", "assets", "--offset", "-1", "--json")]
    [InlineData("project", "review", "texture", "assets", "--topology", "network-2", "--json")]
    [InlineData("project", "review", "texture", "get", "--json")]
    [InlineData("project", "review", "texture", "preview", "LooseSprites/Cursors", "--limit", "1", "--json")]
    [InlineData("project", "review", "texture", "get", "LooseSprites/Cursors", "--json", "--json")]
    public void SyntaxErrorsUseTheExactTextureUsage(params string[] arguments)
    {
        ProjectReviewTextureCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-texture should not run.");

        (int exitCode, string output, string error) = Run(runner, arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains(
            "sdvkit project review texture assets",
            error,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review texture get <asset>",
            error,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review texture preview <asset>",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("texture", "--help")]
    [InlineData("texture", "assets", "--help")]
    public void HelpListsOnlyTheBoundedSingleSurface(params string[] suffix)
    {
        ProjectReviewTextureCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-texture should not run.");

        (int exitCode, string output, string error) = Run(
            runner,
            ["project", "review", .. suffix]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains("texture assets", output, StringComparison.Ordinal);
        Assert.Contains("texture get <asset>", output, StringComparison.Ordinal);
        Assert.Contains("texture preview <asset>", output, StringComparison.Ordinal);
        Assert.Contains("active owned single review", output, StringComparison.Ordinal);
        Assert.DoesNotContain("network-2", output, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output, string Error) Run(
        ProjectReviewTextureCommandRunner runner,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runProjectReviewTexture: runner);
        return (exitCode, output.ToString(), error.ToString());
    }
}
