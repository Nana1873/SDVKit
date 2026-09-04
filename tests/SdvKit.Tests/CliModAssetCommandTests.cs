using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class CliModAssetCommandTests
{
    [Fact]
    public void AssetsDispatchesBoundedSingleQueryAndWritesStableJson()
    {
        ReviewModAssetQuery? received = null;
        string? receivedLabRoot = null;
        ProjectReviewModAssetCommandRunner runner = (query, labRoot) =>
        {
            received = query;
            receivedLabRoot = labRoot;
            return new LiveLabCommandResult(
                0,
                Report(query.Operation) with
                {
                    Assets = [],
                    Page = new ReviewModAssetPage(5, 2, 0, 0, null),
                    Coverage = new ReviewModAssetCoverageReport(
                        ReviewModAssetContract.CoverageScope,
                        new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0),
                });
        };

        (int exitCode, string output, string error) = Run(
            runner,
            "project",
            "review",
            "mod-assets",
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
            new ReviewModAssetQuery(
                ReviewModAssetContract.AssetsOperation,
                null,
                null,
                5,
                2),
            received);
        Assert.Equal(Environment.CurrentDirectory, receivedLabRoot);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(
            ReviewModAssetContract.CoverageScope,
            document.RootElement.GetProperty("coverageScope").GetString());
    }

    [Fact]
    public void KeysAndGetDispatchCanonicalNamespaceOperands()
    {
        var received = new List<ReviewModAssetQuery>();
        ProjectReviewModAssetCommandRunner runner = (query, _) =>
        {
            received.Add(query);
            return new LiveLabCommandResult(3, Blocked(query.Operation));
        };

        Assert.Equal(3, Run(
            runner,
            "project", "review", "mod-assets", "keys",
            "Mods/Example.Mod/Words", "--offset", "3", "--limit", "4", "--json").ExitCode);
        Assert.Equal(3, Run(
            runner,
            "project", "review", "mod-assets", "get",
            "--topology", "single", "--json", "--",
            "Mods/Example.Mod/Words", "--limit").ExitCode);

        Assert.Equal(
            new ReviewModAssetQuery(
                ReviewModAssetContract.KeysOperation,
                "Mods/Example.Mod/Words",
                null,
                3,
                4),
            received[0]);
        Assert.Equal(
            new ReviewModAssetQuery(
                ReviewModAssetContract.GetOperation,
                "Mods/Example.Mod/Words",
                "--limit",
                0,
                1),
            received[1]);
    }

    [Theory]
    [InlineData("project", "review", "mod-assets")]
    [InlineData("project", "review", "mod-assets", "unknown", "--json")]
    [InlineData("project", "review", "mod-assets", "assets")]
    [InlineData("project", "review", "mod-assets", "assets", "extra", "--json")]
    [InlineData("project", "review", "mod-assets", "assets", "--limit", "0", "--json")]
    [InlineData("project", "review", "mod-assets", "assets", "--limit", "101", "--json")]
    [InlineData("project", "review", "mod-assets", "assets", "--topology", "network-2", "--json")]
    [InlineData("project", "review", "mod-assets", "keys", "Mods/Example.Mod/Words", "--json", "--json")]
    [InlineData("project", "review", "mod-assets", "keys", "mods/Example.Mod/Words", "--json")]
    [InlineData("project", "review", "mod-assets", "keys", "Mods\\Example.Mod\\Words", "--json")]
    [InlineData("project", "review", "mod-assets", "keys", "Mods/Example.Mod/../Words", "--json")]
    [InlineData("project", "review", "mod-assets", "keys", "Mods-Example.Mod-Words", "--json")]
    [InlineData("project", "review", "mod-assets", "get", "Mods/Example.Mod/Words", "Key", "--limit", "1", "--json")]
    [InlineData("project", "review", "mod-assets", "get", "Mods/Example.Mod/Words", "--unknown", "--json")]
    public void SyntaxErrorsUseTheExactModAssetUsage(params string[] arguments)
    {
        ProjectReviewModAssetCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-mod-assets should not run.");

        (int exitCode, string output, string error) = Run(runner, arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("mod-assets assets", error, StringComparison.Ordinal);
        Assert.Contains("mod-assets keys <Mods/owner/asset>", error, StringComparison.Ordinal);
        Assert.Contains("mod-assets get <Mods/owner/asset> <key>", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedUtf16OperandsFailBeforeDispatch()
    {
        ProjectReviewModAssetCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-mod-assets should not run.");

        Assert.Equal(2, Run(
            runner,
            "project", "review", "mod-assets", "keys",
            "Mods/Example.Mod/\uD800", "--json").ExitCode);
        Assert.Equal(2, Run(
            runner,
            "project", "review", "mod-assets", "get",
            "Mods/Example.Mod/Words", "\uD800", "--json").ExitCode);
    }

    [Theory]
    [InlineData("mod-assets", "--help")]
    [InlineData("mod-assets", "assets", "--help")]
    public void HelpListsOnlyTheBoundedSingleSurface(params string[] suffix)
    {
        ProjectReviewModAssetCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-mod-assets should not run.");

        (int exitCode, string output, string error) = Run(
            runner,
            ["project", "review", .. suffix]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains("mod-assets assets", output, StringComparison.Ordinal);
        Assert.Contains("mod-assets keys", output, StringComparison.Ordinal);
        Assert.Contains("mod-assets get", output, StringComparison.Ordinal);
        Assert.Contains("active owned single review", output, StringComparison.Ordinal);
        Assert.Contains("before '--'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("network-2", output, StringComparison.Ordinal);
    }

    private static ReviewModAssetReport Report(string operation) =>
        new(
            ReviewModAssetContract.SchemaVersion,
            "ready",
            operation,
            "1.6.15",
            "1.6.15.24356",
            ReviewModAssetContract.CoverageScope,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static ReviewModAssetReport Blocked(string operation) =>
        Report(operation) with
        {
            State = "blocked",
            Problems = [new ReviewModAssetProblem("expected", "Expected.")],
        };

    private static (int ExitCode, string Output, string Error) Run(
        ProjectReviewModAssetCommandRunner runner,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runProjectReviewModAsset: runner);
        return (exitCode, output.ToString(), error.ToString());
    }
}
