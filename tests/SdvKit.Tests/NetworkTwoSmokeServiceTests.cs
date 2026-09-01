using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class NetworkTwoSmokeServiceTests
{
    private const string BuildIdentity =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Theory]
    [InlineData("joining", "alwaysOnStale", true)]
    [InlineData("joining", "alwaysOnNotApplied", true)]
    [InlineData("joined", "alwaysOnNotApplied", false)]
    [InlineData("joining", "networkTwoPending", false)]
    public void SynchronousJoinLoadAllowsOnlyTheTwoExactTransientProblems(
        string phase,
        string problemCode,
        bool expected)
    {
        var network = new NetworkTwoStatusReport(
            "ready",
            NetworkTwoContract.FarmhandRole,
            phase,
            BuildIdentity,
            "fixture",
            "save",
            true,
            0,
            202L,
            "farmhand",
            null,
            null,
            null,
            "C:\\network.log");
        var alwaysOn = new AlwaysOnStatusReport(
            "active",
            1,
            true,
            true,
            DateTimeOffset.UtcNow,
            NetworkTwo: network);
        var report = new LiveLabReport(
            1,
            NetworkTwoContract.Topology,
            "running",
            null,
            null,
            null,
            null,
            null,
            null,
            alwaysOn,
            [new LiveLabProblem(problemCode, "transient")],
            []);

        Assert.Equal(expected, NetworkTwoSmokeService.IsSynchronousJoinLoad(report));
    }

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

    [Fact]
    public void SmokeLeavesRetainedNetworkReviewStagingByteForByteUntouched()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        ProjectReviewPreparedArtifact target = ProjectReviewStagerTests.Artifact(
            project.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target",
            "1.2.3");
        ProjectReviewStaging staged = Assert.IsType<ProjectReviewStaging>(
            ProjectModStager.StageReview(
                [target],
                NetworkTwoContract.Topology,
                singlePaths).Staging);
        byte[] ownershipBefore = File.ReadAllBytes(staged.OwnershipPath);
        string hostIdentityBefore = ModBuildIdentity.ComputeFileSet(
            staged.Target.StagingPathFor(NetworkTwoContract.HostRole));
        string farmhandIdentityBefore = ModBuildIdentity.ComputeFileSet(
            staged.Target.StagingPathFor(NetworkTwoContract.FarmhandRole));
        var discoveryCalled = false;

        LiveLabCommandResult result = NetworkTwoSmokeService.Execute(
            project.Path,
            () =>
            {
                discoveryCalled = true;
                throw new InvalidOperationException("Discovery should not run.");
            });

        Assert.Equal(3, result.ExitCode);
        NetworkTwoSmokeReport report = Assert.IsType<NetworkTwoSmokeReport>(result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal("networkTwoReviewRetained", Assert.Single(report.Problems).Code);
        Assert.False(discoveryCalled);
        Assert.Equal(ownershipBefore, File.ReadAllBytes(staged.OwnershipPath));
        Assert.Equal(
            hostIdentityBefore,
            ModBuildIdentity.ComputeFileSet(
                staged.Target.StagingPathFor(NetworkTwoContract.HostRole)));
        Assert.Equal(
            farmhandIdentityBefore,
            ModBuildIdentity.ComputeFileSet(
                staged.Target.StagingPathFor(NetworkTwoContract.FarmhandRole)));
        Assert.False(File.Exists(LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole).StatePath));
        Assert.False(File.Exists(LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole).StatePath));
    }

    [Fact]
    public void SmokeLeavesRetainedReviewHostStateAndFixtureWorkUntouched()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        hostPaths.EnsureDirectories();
        TestSaveLaunchState review = ReviewLaunch(hostPaths);
        Directory.CreateDirectory(review.WorkPath);
        string fixtureSentinel = Path.Combine(review.WorkPath, "retained-review");
        File.WriteAllText(fixtureSentinel, "preserve this exact work fixture");
        LiveLabState retained = ReviewState(
            hostPaths,
            NetworkTwoContract.HostRole,
            int.MaxValue,
            projectMod: null,
            review);
        new JsonLiveLabStateStore(hostPaths.StatePath).Write(retained);
        byte[] stateBefore = File.ReadAllBytes(hostPaths.StatePath);
        var discoveryCalled = false;

        LiveLabCommandResult result = NetworkTwoSmokeService.Execute(
            project.Path,
            () =>
            {
                discoveryCalled = true;
                throw new InvalidOperationException("Discovery should not run.");
            });

        Assert.Equal(3, result.ExitCode);
        NetworkTwoSmokeReport report = Assert.IsType<NetworkTwoSmokeReport>(result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal("networkTwoReviewRetained", Assert.Single(report.Problems).Code);
        Assert.False(discoveryCalled);
        Assert.Equal(stateBefore, File.ReadAllBytes(hostPaths.StatePath));
        Assert.Equal(
            "preserve this exact work fixture",
            File.ReadAllText(fixtureSentinel));
        Assert.False(File.Exists(hostPaths.StopRequestPath));
    }

    [Fact]
    public void FarmhandOnlyReviewStopBlocksBeforeMutationWithoutHostUnmountProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        farmhandPaths.EnsureDirectories();
        var projectMod = new ProjectModLaunchState(
            "Nana.Target",
            "1.2.3",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
        LiveLabState retained = ReviewState(
            farmhandPaths,
            NetworkTwoContract.FarmhandRole,
            int.MaxValue,
            projectMod,
            review: null);
        new JsonLiveLabStateStore(farmhandPaths.StatePath).Write(retained);
        byte[] stateBefore = File.ReadAllBytes(farmhandPaths.StatePath);

        LiveLabCommandResult result = NetworkTwoSmokeService.StopReviewWithinLock(
            project.Path,
            projectMod);

        Assert.Equal(3, result.ExitCode);
        NetworkTwoSmokeReport report = Assert.IsType<NetworkTwoSmokeReport>(
            result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal("notStarted", report.Farmhand.State);
        Assert.Equal("notStarted", report.Host.State);
        Assert.Equal(
            "networkTwoReviewHostOwnershipMissing",
            Assert.Single(report.Problems).Code);
        Assert.Equal(stateBefore, File.ReadAllBytes(farmhandPaths.StatePath));
        Assert.False(File.Exists(farmhandPaths.StopRequestPath));
        Assert.False(report.FixtureReset);
    }

    [Theory]
    [InlineData("mods")]
    [InlineData("status")]
    [InlineData("stop")]
    [InlineData("networkLog")]
    [InlineData("testSaveSlot")]
    public void ReviewStopBlocksPathDriftBeforeTouchingFarmhandOwnership(string drift)
    {
        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        hostPaths.EnsureDirectories();
        farmhandPaths.EnsureDirectories();
        var projectMod = new ProjectModLaunchState(
            "Nana.Target",
            "1.2.3",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
        LiveLabState host = ReviewState(
            hostPaths,
            NetworkTwoContract.HostRole,
            int.MaxValue,
            projectMod,
            ReviewLaunch(hostPaths));
        host = drift switch
        {
            "mods" => host with
            {
                ModsPath = Path.Combine(hostPaths.SingleRoot, "wrong-mods"),
            },
            "status" => host with
            {
                StatusPath = Path.Combine(hostPaths.RuntimePath, "wrong-status.json"),
            },
            "stop" => host with
            {
                StopRequestPath = Path.Combine(hostPaths.RuntimePath, "wrong-stop.request"),
            },
            "networkLog" => host with
            {
                NetworkTwo = host.NetworkTwo! with
                {
                    NetworkLogPath = Path.Combine(hostPaths.RuntimePath, "wrong-network.log"),
                },
            },
            "testSaveSlot" => host with
            {
                TestSave = host.TestSave! with
                {
                    SlotPath = Path.Combine(hostPaths.SavesPath, "wrong-slot"),
                },
            },
            _ => throw new InvalidOperationException($"Unknown drift case: {drift}"),
        };
        LiveLabState farmhand = ReviewState(
            farmhandPaths,
            NetworkTwoContract.FarmhandRole,
            int.MaxValue - 1,
            projectMod,
            review: null);
        new JsonLiveLabStateStore(hostPaths.StatePath).Write(host);
        new JsonLiveLabStateStore(farmhandPaths.StatePath).Write(farmhand);
        byte[] hostBefore = File.ReadAllBytes(hostPaths.StatePath);
        byte[] farmhandBefore = File.ReadAllBytes(farmhandPaths.StatePath);

        LiveLabCommandResult result = NetworkTwoSmokeService.StopReviewWithinLock(
            project.Path,
            projectMod);

        Assert.Equal(3, result.ExitCode);
        NetworkTwoSmokeReport report = Assert.IsType<NetworkTwoSmokeReport>(result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal(
            "networkTwoReviewOwnershipMismatch",
            Assert.Single(report.Problems).Code);
        Assert.Equal(hostBefore, File.ReadAllBytes(hostPaths.StatePath));
        Assert.Equal(farmhandBefore, File.ReadAllBytes(farmhandPaths.StatePath));
        Assert.False(File.Exists(hostPaths.StopRequestPath));
        Assert.False(File.Exists(farmhandPaths.StopRequestPath));
        Assert.Equal("notStarted", report.Host.State);
        Assert.Equal("notStarted", report.Farmhand.State);
    }

    [Fact]
    public void PartialHostReviewStopReleasesExactOwnershipPreservesWorkAndIsRetryable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        hostPaths.EnsureDirectories();
        TestSaveLaunchState review = ReviewLaunch(hostPaths);
        WriteFixtureIdentity(hostPaths, review.Identity);
        string fixtureSentinel = Path.Combine(review.WorkPath, "saved-selection");
        File.WriteAllText(fixtureSentinel, "retain across the recoverable stop");
        var projectMod = new ProjectModLaunchState(
            "Nana.Target",
            "1.2.3",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
        LiveLabState retained = ReviewState(
            hostPaths,
            NetworkTwoContract.HostRole,
            int.MaxValue,
            projectMod,
            review);
        new JsonLiveLabStateStore(hostPaths.StatePath).Write(retained);

        LiveLabCommandResult first = NetworkTwoSmokeService.StopReviewWithinLock(
            project.Path,
            projectMod);

        Assert.Equal(3, first.ExitCode);
        NetworkTwoSmokeReport firstReport = Assert.IsType<NetworkTwoSmokeReport>(
            first.Report);
        Assert.Equal("blocked", firstReport.State);
        Assert.Equal("stopped", firstReport.Farmhand.State);
        Assert.Equal("stopped", firstReport.Host.State);
        Assert.Contains(
            firstReport.Problems,
            problem => string.Equals(
                problem.Code,
                "networkRestoreUnconfirmed",
                StringComparison.Ordinal));
        Assert.False(File.Exists(hostPaths.StatePath));
        Assert.Equal(
            "retain across the recoverable stop",
            File.ReadAllText(fixtureSentinel));
        Assert.False(firstReport.FixtureReset);

        LiveLabCommandResult retry = NetworkTwoSmokeService.StopReviewWithinLock(
            project.Path,
            projectMod);

        Assert.Equal(0, retry.ExitCode);
        NetworkTwoSmokeReport retryReport = Assert.IsType<NetworkTwoSmokeReport>(
            retry.Report);
        Assert.Equal("stopped", retryReport.State);
        Assert.Empty(retryReport.Problems);
        Assert.False(retryReport.FixtureReset);
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

    private static TestSaveLaunchState ReviewLaunch(LiveLabPaths hostPaths)
    {
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            123456789L,
            "SDVKit_123456789",
            TestSaveContract.PlayerName,
            TestSaveContract.FarmName,
            TestSaveContract.FavoriteThing);
        return new TestSaveLaunchState(
            TestSaveContract.ReviewMode,
            identity,
            Path.Combine(hostPaths.SavesPath, identity.SaveId),
            hostPaths.TestSaveWorkPath,
            hostPaths.TestSaveScenarioLogPath);
    }

    private static LiveLabState ReviewState(
        LiveLabPaths paths,
        string role,
        int processId,
        ProjectModLaunchState? projectMod,
        TestSaveLaunchState? review)
    {
        TestSaveIdentity identity = review?.Identity
            ?? new TestSaveIdentity(
                TestSaveContract.SchemaVersion,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                123456789L,
                "SDVKit_123456789",
                TestSaveContract.PlayerName,
                TestSaveContract.FarmName,
                TestSaveContract.FavoriteThing);
        var network = new NetworkTwoLaunchState(
            role,
            BuildIdentity,
            identity.FixtureId,
            identity.SaveId,
            Path.Combine(paths.RuntimePath, "network-2.log"),
            string.Equals(role, NetworkTwoContract.FarmhandRole, StringComparison.Ordinal)
                ? 202L
                : null);
        return new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            "cccccccccccccccccccccccccccccccc",
            new OwnedProcessIdentity(
                processId,
                new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero),
                Path.Combine(paths.ProjectRoot, "old-run", "StardewModdingAPI.exe")),
            paths.ModsPath,
            paths.StatusPath,
            paths.StopRequestPath,
            review,
            network,
            projectMod);
    }

    private static void WriteFixtureIdentity(
        LiveLabPaths paths,
        TestSaveIdentity identity)
    {
        Directory.CreateDirectory(paths.TestSaveWorkPath);
        string json = JsonSerializer.Serialize(identity, LiveLabJsonOptions.CamelCase);
        File.WriteAllText(paths.TestSaveManifestPath, json);
        File.WriteAllText(
            Path.Combine(
                paths.TestSaveWorkPath,
                TestSaveContract.FixtureMarkerFileName),
            json);
    }

    private static string NetworkLogsPath(TemporaryDirectory project) =>
        Path.Combine(
            project.Path,
            ".sdvkit",
            "lab",
            NetworkTwoContract.Topology,
            "logs");
}
