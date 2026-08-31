using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class NetworkTwoSmokeServiceTests
{
    private const string BuildIdentity =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public void RecoveredRoleIsNotReportedOrArchivedAsEvidenceForTheNewSmoke()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory project = new();
        (_, LiveLabPaths farmhandPaths) =
            PrepareRetainedFarmhand(project, int.MaxValue);

        LiveLabCommandResult result = NetworkTwoSmokeService.Execute(
            project.Path,
            () => throw new InvalidOperationException("Discovery should not run."));

        Assert.Equal(3, result.ExitCode);
        NetworkTwoSmokeReport report = Assert.IsType<NetworkTwoSmokeReport>(result.Report);
        Assert.Equal("testSaveBaselineMissing", Assert.Single(report.Problems).Code);
        Assert.Equal("notStarted", report.Farmhand.State);
        Assert.Null(report.Farmhand.ProcessId);
        Assert.Empty(report.Farmhand.LogPaths);
        Assert.False(Directory.Exists(NetworkLogsPath(project)));
        Assert.True(File.Exists(farmhandPaths.StandardOutputPath));
    }

    [Fact]
    public void FailedRecoveryReportCannotAuthorizeCurrentSmokeEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory project = new();
        (_, LiveLabPaths farmhandPaths) = PrepareRetainedFarmhand(
            project,
            Environment.ProcessId);

        LiveLabCommandResult result = NetworkTwoSmokeService.Execute(
            project.Path,
            () => throw new InvalidOperationException("Discovery should not run."));

        Assert.Equal(3, result.ExitCode);
        NetworkTwoSmokeReport report = Assert.IsType<NetworkTwoSmokeReport>(result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal(Environment.ProcessId, report.Farmhand.ProcessId);
        Assert.Empty(report.Farmhand.LogPaths);
        Assert.False(Directory.Exists(NetworkLogsPath(project)));
        Assert.True(File.Exists(farmhandPaths.StandardOutputPath));
        Assert.True(File.Exists(farmhandPaths.StatePath));
    }

    private static (LiveLabPaths Single, LiveLabPaths Farmhand) PrepareRetainedFarmhand(
        TemporaryDirectory project,
        int processId)
    {
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        farmhandPaths.EnsureDirectories();

        var retainedLaunch = new NetworkTwoLaunchState(
            NetworkTwoContract.FarmhandRole,
            BuildIdentity,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "SDVKit_123456789",
            Path.Combine(farmhandPaths.RuntimePath, "network-2.log"),
            ExpectedFarmhandId: 202L);
        var retainedState = new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            new OwnedProcessIdentity(
                processId,
                new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero),
                Path.Combine(project.Path, "old-run", "StardewModdingAPI.exe")),
            farmhandPaths.ModsPath,
            farmhandPaths.StatusPath,
            farmhandPaths.StopRequestPath,
            TestSave: null,
            retainedLaunch);
        new JsonLiveLabStateStore(farmhandPaths.StatePath).Write(retainedState);
        File.WriteAllText(farmhandPaths.StandardOutputPath, "prior smoke output");
        return (singlePaths, farmhandPaths);
    }

    private static string NetworkLogsPath(TemporaryDirectory project) =>
        Path.Combine(
            project.Path,
            ".sdvkit",
            "lab",
            NetworkTwoContract.Topology,
            "logs");
}
