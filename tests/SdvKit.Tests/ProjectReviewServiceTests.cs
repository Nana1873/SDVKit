using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ProjectReviewServiceTests
{
    [Fact]
    public void ContentPackTargetNetworkTwoFailsBeforeLabMutationOrDiscovery()
    {
        using TemporaryDirectory temporary = new();
        string target = temporary.WriteFile(
            "source/TargetPack/manifest.json",
            """
            {
              "Name": "Target pack",
              "Author": "Nana",
              "UniqueID": "Nana.TargetPack",
              "Version": "1.0.0",
              "Description": "Review target.",
              "ContentPackFor": { "UniqueID": "Nana.Provider" }
            }
            """);
        target = Path.GetDirectoryName(target)!;
        temporary.WriteFile("source/TargetPack/content.json", "{}");
        string provider = Path.GetDirectoryName(temporary.WriteFile(
            "source/Provider/manifest.json",
            """
            {
              "Name": "Provider",
              "Author": "Nana",
              "UniqueID": "Nana.Provider",
              "Version": "1.0.0",
              "Description": "Review provider.",
              "EntryDll": "Provider.dll"
            }
            """))!;
        temporary.WriteFile("source/Provider/Provider.dll", "assembly");
        string labRoot = Path.Combine(temporary.Path, "lab");
        string before = ModBuildIdentity.ComputeFileSet(target);
        var doctorCalled = false;

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "start",
            target,
            [provider],
            [],
            NetworkTwoContract.Topology,
            labRoot,
            () =>
            {
                doctorCalled = true;
                return new DoctorReport(1, DoctorReport.NotFound, []);
            });

        Assert.Equal(3, result.ExitCode);
        ProjectNetworkReviewReport report =
            Assert.IsType<ProjectNetworkReviewReport>(result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal(
            "reviewTargetTopologyUnsupported",
            Assert.Single(report.Problems).Code);
        Assert.False(doctorCalled);
        Assert.False(Directory.Exists(Path.Combine(labRoot, ".sdvkit")));
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(target));
    }

    [Fact]
    public void ContentPackTargetStatusReportsExistingKindAndIdentityFields()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewPreparedArtifact target = ProjectReviewStagerTests.Artifact(
            temporary.Path,
            "TargetPack",
            ProjectReviewArtifactRole.Target,
            "Nana.TargetPack",
            "1.0",
            contentPackFor: "Nana.Provider",
            kind: ProjectInspectionReport.ContentPack);
        ProjectReviewPreparedArtifact provider = ProjectReviewStagerTests.Artifact(
            temporary.Path,
            "Provider",
            ProjectReviewArtifactRole.Companion,
            "Nana.Provider",
            "2.0.0");
        ProjectReviewStagingResult staged = ProjectModStager.StageReview(
            [target, provider],
            paths);
        Assert.NotNull(staged.Staging);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            LiveLabState.SingleTopology,
            temporary.Path,
            () => throw new InvalidOperationException("Status must not discover installations."));

        Assert.Equal(3, result.ExitCode);
        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        ProjectReviewArtifactReport targetReport = report.Artifacts.Single(artifact =>
            artifact.Role == ProjectReviewArtifactRole.Target);
        Assert.Equal(ProjectInspectionReport.ContentPack, targetReport.Kind);
        Assert.Equal("Nana.TargetPack", targetReport.UniqueId);
        Assert.Equal("1.0", targetReport.Version);
        Assert.Equal("Nana.Provider", targetReport.ContentPackFor);
        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(paths);
        Assert.True(cleanup.Removed);
        Assert.Null(cleanup.Problem);
    }

    private const string LaunchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void NetworkTwoStatusWithoutStateOrStagingIsStoppedWithDistinctProjectLocalDataPaths()
    {
        using TemporaryDirectory temporary = new();
        var doctorCalled = false;

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            NetworkTwoContract.Topology,
            temporary.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Status must not run doctor.");
            });

        ProjectNetworkReviewReport report =
            Assert.IsType<ProjectNetworkReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(NetworkTwoContract.Topology, report.Topology);
        Assert.Equal("stopped", report.State);
        Assert.False(report.FixtureReset);
        Assert.True(report.StagingRemoved);
        Assert.Null(report.Network);
        Assert.True(report.InteractiveConsole);
        Assert.Empty(report.Problems);
        Assert.False(doctorCalled);

        ProjectNetworkReviewRoleReport host = Assert.Single(
            report.Roles,
            role => string.Equals(
                role.Role,
                NetworkTwoContract.HostRole,
                StringComparison.Ordinal));
        ProjectNetworkReviewRoleReport farmhand = Assert.Single(
            report.Roles,
            role => string.Equals(
                role.Role,
                NetworkTwoContract.FarmhandRole,
                StringComparison.Ordinal));
        Assert.Equal(
            ".sdvkit/lab/profiles/network-2/host/AppData/Roaming/StardewValley",
            host.StardewDataPath);
        Assert.Equal(
            ".sdvkit/lab/profiles/network-2/farmhand/AppData/Roaming/StardewValley",
            farmhand.StardewDataPath);
        Assert.NotEqual(host.StardewDataPath, farmhand.StardewDataPath);
        Assert.NotEqual(host.SavesPath, farmhand.SavesPath);
        Assert.All(report.Roles, role =>
            Assert.StartsWith(
                ".sdvkit/lab/profiles/network-2/",
                role.StardewDataPath,
                StringComparison.Ordinal));
    }

    [Fact]
    public void PreScenarioCommandsReachAnExactRunningRoleBeforeNetworkPairJoin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageNetworkReviewSet(paths, temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        string hostProcessRoot = Path.Combine(temporary.Path, "host-process");
        string farmhandProcessRoot = Path.Combine(temporary.Path, "farmhand-process");
        Directory.CreateDirectory(hostProcessRoot);
        Directory.CreateDirectory(farmhandProcessRoot);
        (OwnedProcessIdentity hostIdentity, Process hostProcess) =
            StartRunningProcess(hostProcessRoot);
        (OwnedProcessIdentity farmhandIdentity, Process farmhandProcess) =
            StartRunningProcess(farmhandProcessRoot);
        using (hostProcess)
        using (farmhandProcess)
        {
            try
            {
                LiveLabState hostState = NetworkReviewState(
                    paths,
                    staging.TargetLaunchState,
                    NetworkTwoContract.HostRole,
                    hostIdentity);
                LiveLabState farmhandState = NetworkReviewState(
                    paths,
                    staging.TargetLaunchState,
                    NetworkTwoContract.FarmhandRole,
                    farmhandIdentity);
                new JsonLiveLabStateStore(hostPaths.StatePath).Write(hostState);
                new JsonLiveLabStateStore(farmhandPaths.StatePath).Write(farmhandState);
                WriteNetworkStatus(
                    hostPaths,
                    hostState,
                    staging.TargetLaunchState,
                    "startingHost");
                WriteNetworkStatus(
                    farmhandPaths,
                    farmhandState,
                    staging.TargetLaunchState,
                    "waitingForTitle");
                string hostStateBefore = FileSnapshot(hostPaths.StatePath);
                string farmhandStateBefore = FileSnapshot(farmhandPaths.StatePath);
                string stagingBefore = string.Join(
                    "\n-- farmhand --\n",
                    TreeSnapshot(hostPaths.ModsPath),
                    TreeSnapshot(farmhandPaths.ModsPath));
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));

                LiveLabCommandResult pending = ProjectReviewService.ExecuteCommand(
                    "sdvkit input press F8",
                    NetworkTwoContract.Topology,
                    NetworkTwoContract.FarmhandRole,
                    temporary.Path,
                    sender);

                ProjectNetworkReviewCommandReport pendingReport =
                    Assert.IsType<ProjectNetworkReviewCommandReport>(pending.Report);
                Assert.Equal(0, pending.ExitCode);
                Assert.Equal("running", pendingReport.State);
                Assert.True(pendingReport.CommandWritten);
                Assert.Empty(pendingReport.Problems);
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(farmhandIdentity, sender.Identity);

                WriteNetworkStatus(
                    farmhandPaths,
                    farmhandState,
                    staging.TargetLaunchState,
                    "failed");
                LiveLabCommandResult failed = ProjectReviewService.ExecuteCommand(
                    "sdvkit screenshot viewport join-failed",
                    NetworkTwoContract.Topology,
                    NetworkTwoContract.FarmhandRole,
                    temporary.Path,
                    sender);

                ProjectNetworkReviewCommandReport failedReport =
                    Assert.IsType<ProjectNetworkReviewCommandReport>(failed.Report);
                Assert.Equal(0, failed.ExitCode);
                Assert.Equal("running", failedReport.State);
                Assert.True(failedReport.CommandWritten);
                Assert.Empty(failedReport.Problems);
                Assert.Equal(2, sender.CallCount);
                Assert.Equal(farmhandIdentity, sender.Identity);

                foreach (string command in new[]
                {
                    "sdvkit screenshot map-before-join",
                    "sdvkit fixture status",
                    "sdvkit data aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa assets 0 20",
                    "sdvkit input press F8 extra",
                })
                {
                    LiveLabCommandResult blocked = ProjectReviewService.ExecuteCommand(
                        command,
                        NetworkTwoContract.Topology,
                        NetworkTwoContract.FarmhandRole,
                        temporary.Path,
                        sender);

                    ProjectNetworkReviewCommandReport blockedReport =
                        Assert.IsType<ProjectNetworkReviewCommandReport>(blocked.Report);
                    Assert.Equal(3, blocked.ExitCode);
                    Assert.Equal("blocked", blockedReport.State);
                    Assert.False(blockedReport.CommandWritten);
                    Assert.NotEmpty(blockedReport.Problems);
                }

                Assert.Equal(2, sender.CallCount);
                Assert.Equal(hostStateBefore, FileSnapshot(hostPaths.StatePath));
                Assert.Equal(farmhandStateBefore, FileSnapshot(farmhandPaths.StatePath));
                Assert.Equal(
                    stagingBefore,
                    string.Join(
                        "\n-- farmhand --\n",
                        TreeSnapshot(hostPaths.ModsPath),
                        TreeSnapshot(farmhandPaths.ModsPath)));
            }
            finally
            {
                EnsureExited(farmhandProcess);
                EnsureExited(hostProcess);
            }
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    public void NetworkTwoStoppedReviewRetainsExactRoleStagingAndIdentity(string action)
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStaging staging = StageNetworkReviewSet(paths, temporary.Path);
        string networkRoot = Path.Combine(
            temporary.Path,
            ".sdvkit",
            "lab",
            NetworkTwoContract.Topology);
        string before = TreeSnapshot(networkRoot);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            action,
            staging.Target.SourceRoot,
            staging.Artifacts
                .Where(artifact => artifact.Role == ProjectReviewArtifactRole.Companion)
                .Select(artifact => artifact.SourceRoot)
                .ToArray(),
            staging.Artifacts
                .Where(artifact => artifact.Role == ProjectReviewArtifactRole.ContentPack)
                .Select(artifact => artifact.SourceRoot)
                .ToArray(),
            NetworkTwoContract.Topology,
            temporary.Path,
            () => throw new InvalidOperationException(
                $"{action} must not run doctor."));

        ProjectNetworkReviewReport report =
            Assert.IsType<ProjectNetworkReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.False(report.FixtureReset);
        Assert.False(report.StagingRemoved);
        Assert.Null(report.Network);
        Assert.Empty(report.Problems);
        Assert.Equal(before, TreeSnapshot(networkRoot));
        Assert.True(File.Exists(staging.OwnershipPath));

        ProjectNetworkReviewRoleReport host = Assert.Single(
            report.Roles,
            role => role.Role == NetworkTwoContract.HostRole);
        ProjectNetworkReviewRoleReport farmhand = Assert.Single(
            report.Roles,
            role => role.Role == NetworkTwoContract.FarmhandRole);
        Assert.Equal(3, host.Artifacts.Count);
        Assert.Equal(3, farmhand.Artifacts.Count);
        Assert.Equal(
            host.Artifacts.Select(ArtifactIdentity),
            farmhand.Artifacts.Select(ArtifactIdentity));
        Assert.All(host.Artifacts, artifact =>
            Assert.Contains(
                "/network-2/host/mods/",
                artifact.StagingPath,
                StringComparison.Ordinal));
        Assert.All(farmhand.Artifacts, artifact =>
            Assert.Contains(
                "/network-2/farmhand/mods/",
                artifact.StagingPath,
                StringComparison.Ordinal));
        Assert.All(staging.Artifacts, artifact =>
        {
            Assert.True(Directory.Exists(artifact.StagingPathFor(
                NetworkTwoContract.HostRole)));
            Assert.True(Directory.Exists(artifact.StagingPathFor(
                NetworkTwoContract.FarmhandRole)));
        });
    }

    [Fact]
    public void NetworkTwoStopAcceptsRuntimeContentDriftInExactOwnedStaging()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStaging staging = StageNetworkReviewSet(paths, temporary.Path);
        string stagedDll = Path.Combine(
            staging.Target.StagingPathFor(NetworkTwoContract.HostRole),
            "Target.dll");
        File.AppendAllText(stagedDll, "runtime drift");
        ProjectReviewStagingResult strict = ProjectModStager.ReadReview(
            paths,
            NetworkTwoContract.Topology);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "stop",
            temporary.Path,
            [],
            [],
            NetworkTwoContract.Topology,
            temporary.Path,
            () => throw new InvalidOperationException("Stop must not run doctor."));

        Assert.Null(strict.Staging);
        Assert.Equal(
            "reviewStagingOwnershipDrifted",
            Assert.IsType<ProjectReviewProblem>(strict.Problem).Code);
        ProjectNetworkReviewReport report =
            Assert.IsType<ProjectNetworkReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.False(report.StagingRemoved);
        Assert.Empty(report.Problems);
        Assert.EndsWith("runtime drift", File.ReadAllText(stagedDll), StringComparison.Ordinal);
        Assert.True(File.Exists(staging.OwnershipPath));

        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(
            paths,
            NetworkTwoContract.Topology);
        Assert.True(cleanup.Removed, cleanup.Problem?.Message);
    }

    [Fact]
    public void NetworkTwoRetainedStartWithDifferentExplicitSetBlocksWithoutMutation()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStaging staging = StageNetworkReviewSet(paths, temporary.Path);
        string networkRoot = Path.Combine(
            temporary.Path,
            ".sdvkit",
            "lab",
            NetworkTwoContract.Topology);
        string before = TreeSnapshot(networkRoot);
        var doctorCalled = false;

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "start",
            Path.Combine(temporary.Path, "DifferentTarget"),
            staging.Artifacts
                .Where(artifact => artifact.Role == ProjectReviewArtifactRole.Companion)
                .Select(artifact => artifact.SourceRoot)
                .ToArray(),
            staging.Artifacts
                .Where(artifact => artifact.Role == ProjectReviewArtifactRole.ContentPack)
                .Select(artifact => artifact.SourceRoot)
                .ToArray(),
            NetworkTwoContract.Topology,
            temporary.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException(
                    "A retained set mismatch must not run doctor.");
            });

        ProjectNetworkReviewReport report =
            Assert.IsType<ProjectNetworkReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.FixtureReset);
        Assert.False(report.StagingRemoved);
        Assert.Equal("reviewSetMismatch", Assert.Single(report.Problems).Code);
        Assert.False(doctorCalled);
        Assert.Equal(before, TreeSnapshot(networkRoot));
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.False(File.Exists(LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole).StatePath));
        Assert.False(File.Exists(LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole).StatePath));
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole)]
    [InlineData(NetworkTwoContract.FarmhandRole)]
    public void NetworkTwoResetWithAnyRoleStateBlocksWithoutMutation(string role)
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStaging staging = StageNetworkReviewSet(paths, temporary.Path);
        LiveLabPaths rolePaths = LiveLabPaths.ResolveNetworkRole(paths, role);
        new JsonLiveLabStateStore(rolePaths.StatePath).Write(
            NetworkReviewState(paths, staging.TargetLaunchState, role));
        string networkRoot = Path.Combine(
            temporary.Path,
            ".sdvkit",
            "lab",
            NetworkTwoContract.Topology);
        string before = TreeSnapshot(networkRoot);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "reset",
            temporary.Path,
            [],
            [],
            NetworkTwoContract.Topology,
            temporary.Path,
            () => throw new InvalidOperationException("Reset must not run doctor."));

        ProjectNetworkReviewReport report =
            Assert.IsType<ProjectNetworkReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.FixtureReset);
        Assert.False(report.StagingRemoved);
        Assert.Equal(
            "reviewResetRequiresStoppedLab",
            Assert.Single(report.Problems).Code);
        Assert.Equal(before, TreeSnapshot(networkRoot));
        Assert.True(File.Exists(rolePaths.StatePath));
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.All(staging.Artifacts, artifact =>
        {
            Assert.True(Directory.Exists(artifact.StagingPathFor(
                NetworkTwoContract.HostRole)));
            Assert.True(Directory.Exists(artifact.StagingPathFor(
                NetworkTwoContract.FarmhandRole)));
        });
    }

    [Fact]
    public void NetworkTwoResetRetriesOwnedPartialStagingCleanup()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStaging staging = StageNetworkReviewSet(paths, temporary.Path);
        TestSaveIdentity identity = WriteReviewFixture(paths);
        string alreadyRemoved = staging.Target.StagingPathFor(
            NetworkTwoContract.HostRole);
        Directory.Delete(alreadyRemoved, recursive: true);

        ProjectReviewStagingResult strict = ProjectModStager.ReadReview(
            paths,
            NetworkTwoContract.Topology);
        Assert.Null(strict.Staging);
        Assert.Equal(
            "reviewStagingOwnershipInvalid",
            Assert.IsType<ProjectReviewProblem>(strict.Problem).Code);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "reset",
            temporary.Path,
            [],
            [],
            NetworkTwoContract.Topology,
            temporary.Path,
            () => throw new InvalidOperationException("Reset must not run doctor."));

        ProjectNetworkReviewReport report =
            Assert.IsType<ProjectNetworkReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.FixtureReset);
        Assert.True(report.StagingRemoved);
        Assert.Empty(report.Problems);
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.All(staging.Artifacts, artifact =>
        {
            Assert.False(Directory.Exists(artifact.StagingPathFor(
                NetworkTwoContract.HostRole)));
            Assert.False(Directory.Exists(artifact.StagingPathFor(
                NetworkTwoContract.FarmhandRole)));
        });
        Assert.Equal(
            "baseline-save",
            File.ReadAllText(Path.Combine(
                paths.TestSaveWorkPath,
                identity.SaveId)));
        Assert.False(File.Exists(Path.Combine(paths.TestSaveWorkPath, "review-only")));
    }

    [Fact]
    public void SingleResetRestoresTheExactFixtureAndRemovesOnlyOwnedStaging()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStaging staging = StageTargetAndCompanion(paths, temporary.Path);
        TestSaveIdentity identity = WriteReviewFixture(paths);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "reset",
            temporary.Path,
            [],
            [],
            LiveLabState.SingleTopology,
            temporary.Path,
            () => throw new InvalidOperationException("Reset must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.FixtureReset);
        Assert.True(report.StagingRemoved);
        Assert.Empty(report.Problems);
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.All(staging.Artifacts, artifact =>
            Assert.False(Directory.Exists(artifact.StagingPath)));
        Assert.Equal(
            "baseline-save",
            File.ReadAllText(Path.Combine(paths.TestSaveWorkPath, identity.SaveId)));
        Assert.False(File.Exists(Path.Combine(paths.TestSaveWorkPath, "review-only")));
    }

    [Fact]
    public void SingleResetWithRetainedSingleStateBlocksWithoutMutation()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStaging staging = StageTargetAndCompanion(paths, temporary.Path);
        WriteReviewFixture(paths);
        new JsonLiveLabStateStore(paths.StatePath).Write(
            ReviewState(paths, staging.TargetLaunchState));
        string stateBefore = FileSnapshot(paths.StatePath);
        string stagingBefore = TreeSnapshot(paths.ModsPath);
        string fixtureBefore = TreeSnapshot(paths.TestSaveRoot);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "reset",
            temporary.Path,
            [],
            [],
            LiveLabState.SingleTopology,
            temporary.Path,
            () => throw new InvalidOperationException("Reset must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.FixtureReset);
        Assert.False(report.StagingRemoved);
        Assert.Equal(
            "reviewResetRequiresStoppedLab",
            Assert.Single(report.Problems).Code);
        Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
        Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
        Assert.Equal(fixtureBefore, TreeSnapshot(paths.TestSaveRoot));
    }

    [Fact]
    public void SingleResetCannotConsumeARetainedNetworkReviewFixture()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStaging networkStaging = StageNetworkReviewSet(
            paths,
            temporary.Path);
        WriteReviewFixture(paths);
        string before = TreeSnapshot(paths.TestSaveRoot);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "reset",
            temporary.Path,
            [],
            [],
            LiveLabState.SingleTopology,
            temporary.Path,
            () => throw new InvalidOperationException("Reset must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.FixtureReset);
        Assert.Equal(
            "reviewResetRequiresStoppedLab",
            Assert.Single(report.Problems).Code);
        Assert.Equal(before, TreeSnapshot(paths.TestSaveRoot));
        Assert.True(File.Exists(networkStaging.OwnershipPath));
    }

    [Fact]
    public void StatusWithoutStateOrStagingIsStoppedWithoutDiscovery()
    {
        using TemporaryDirectory temporary = new();
        var doctorCalled = false;

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Status must not run doctor.");
            });

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.StagingRemoved);
        Assert.Empty(report.Artifacts);
        Assert.Null(report.Lab);
        Assert.True(report.InteractiveConsole);
        Assert.Equal(
            ".sdvkit/lab/profiles/single/AppData/Roaming/StardewValley/Saves",
            report.PersistentSavesPath);
        Assert.Empty(report.Problems);
        Assert.False(doctorCalled);
    }

    [Fact]
    public void SingleStatusReportsTheExactLoadedFixtureIdentityAndOwnership()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        TestSaveIdentity identity = WriteReviewFixture(paths);
        var testSave = new TestSaveLaunchState(
            TestSaveContract.ReviewMode,
            identity,
            Path.Combine(paths.SavesPath, identity.SaveId),
            paths.TestSaveWorkPath,
            paths.TestSaveScenarioLogPath);
        (OwnedProcessIdentity processIdentity, Process child) = StartRunningProcess(
            temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(
                    paths,
                    staging.TargetLaunchState,
                    processIdentity) with
                {
                    TestSave = testSave,
                };
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(
                    paths,
                    state,
                    staging.TargetLaunchState,
                    testSave: new TestSaveStatusMarker(
                        TestSaveContract.SchemaVersion,
                        TestSaveContract.ReviewMode,
                        "passed",
                        identity.FixtureId,
                        identity.SaveId,
                        true,
                        0,
                        "Exact fixture loaded for interactive review.",
                        paths.TestSaveScenarioLogPath));

                LiveLabCommandResult result = ProjectReviewService.Execute(
                    "status",
                    temporary.Path,
                    [],
                    [],
                    LiveLabState.SingleTopology,
                    temporary.Path,
                    () => throw new InvalidOperationException(
                        "Status must not run doctor."));

                ProjectReviewReport report =
                    Assert.IsType<ProjectReviewReport>(result.Report);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("running", report.State);
                TestSaveStatusReport fixture = Assert.IsType<TestSaveStatusReport>(
                    report.TestSave);
                Assert.Equal("ready", fixture.State);
                Assert.Equal(TestSaveContract.ReviewMode, fixture.Mode);
                Assert.Equal("passed", fixture.Phase);
                Assert.Equal(identity.FixtureId, fixture.FixtureId);
                Assert.Equal(identity.SaveId, fixture.SaveId);
                Assert.True(fixture.IdentityVerified);
                Assert.False(report.FixtureReset);
                Assert.Empty(report.Problems);
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void BindingMismatchRetainsExactStateAndStaging()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(
            paths,
            staging.TargetLaunchState with { UniqueId = "Nana.OtherTarget" });
        new JsonLiveLabStateStore(paths.StatePath).Write(state);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.StagingRemoved);
        Assert.Equal(
            "reviewOwnershipMismatch",
            Assert.Single(report.Problems).Code);
        Assert.True(File.Exists(paths.StatePath));
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.True(Directory.Exists(staging.Target.StagingPath));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RetainedStartCannotChangeTheSingleTestSaveSelection(
        bool retainedUsesTestSave,
        bool requestedUsesTestSave)
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(paths, staging.TargetLaunchState);
        if (retainedUsesTestSave)
        {
            TestSaveIdentity identity = WriteReviewFixture(paths);
            state = state with
            {
                TestSave = new TestSaveLaunchState(
                    TestSaveContract.ReviewMode,
                    identity,
                    Path.Combine(paths.SavesPath, identity.SaveId),
                    paths.TestSaveWorkPath,
                    paths.TestSaveScenarioLogPath),
            };
        }

        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        Directory.CreateDirectory(paths.TestSaveRoot);
        string stateBefore = FileSnapshot(paths.StatePath);
        string stagingBefore = TreeSnapshot(paths.ModsPath);
        string fixtureBefore = TreeSnapshot(paths.TestSaveRoot);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "start",
            staging.Target.SourceRoot,
            [],
            [],
            LiveLabState.SingleTopology,
            temporary.Path,
            () => throw new InvalidOperationException("Start must not run doctor."),
            requestedUsesTestSave);

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.Equal(
            "reviewTestSaveSelectionMismatch",
            Assert.Single(report.Problems).Code);
        Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
        Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
        Assert.Equal(fixtureBefore, TreeSnapshot(paths.TestSaveRoot));
    }

    [Fact]
    public void StagingWithoutRuntimeStateIsRetainedFailClosed()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.StagingRemoved);
        Assert.Equal(
            "reviewOwnershipIncomplete",
            Assert.Single(report.Problems).Code);
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.True(Directory.Exists(staging.Target.StagingPath));
    }

    [Fact]
    public void StatusCleansExactStagingAfterTheOwnedReviewProcessExited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        string alwaysOnSentinel = WriteSentinel(
            paths.AlwaysOnModPath,
            "always-on.txt",
            "always-on");
        string saveSentinel = WriteSentinel(
            Path.Combine(paths.SavesPath, "SICQA_Review"),
            "SaveGameInfo",
            "persistent-save");
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(paths, staging.TargetLaunchState);
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        WriteLoadedStatus(paths, state, staging.TargetLaunchState);
        File.WriteAllText(paths.StopRequestPath, LaunchId);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.StagingRemoved);
        Assert.Equal("stopped", Assert.IsType<LiveLabReport>(report.Lab).State);
        Assert.False(File.Exists(paths.StatePath));
        Assert.False(File.Exists(paths.StopRequestPath));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.False(Directory.Exists(staging.Target.StagingPath));
        Assert.Equal("always-on", File.ReadAllText(alwaysOnSentinel));
        Assert.Equal("persistent-save", File.ReadAllText(saveSentinel));
    }

    [Fact]
    public void StatusCleansExitedReviewButFailsWithoutTargetLoadConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        new JsonLiveLabStateStore(paths.StatePath).Write(
            ReviewState(paths, staging.TargetLaunchState));
        File.WriteAllText(paths.StopRequestPath, LaunchId);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.StagingRemoved);
        Assert.Equal(
            "projectModLoadUnconfirmed",
            Assert.Single(report.Problems).Code);
        Assert.False(File.Exists(paths.StatePath));
        Assert.False(File.Exists(paths.StopRequestPath));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.False(Directory.Exists(staging.Target.StagingPath));
    }

    [Theory]
    [InlineData("status", ProjectReviewArtifactRole.Target)]
    [InlineData("stop", ProjectReviewArtifactRole.Target)]
    [InlineData("status", ProjectReviewArtifactRole.Companion)]
    [InlineData("stop", ProjectReviewArtifactRole.Companion)]
    public void RuntimeRootConfigDoesNotBlockStatusOrStopForOwnedCodeMods(
        string action,
        string artifactRole)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string secret = "issue-40-service-secret-must-not-be-reported";
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTargetAndCompanion(paths, temporary.Path);
        ProjectReviewOwnedArtifact runtimeArtifact = Assert.Single(
            staging.Artifacts,
            artifact => artifact.Role == artifactRole);
        File.WriteAllText(
            Path.Combine(runtimeArtifact.StagingPath, "config.json"),
            $"{{\"SharedSecret\":\"{secret}\"}}");
        LiveLabState state = ReviewState(paths, staging.TargetLaunchState);
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        WriteStatus(
            paths,
            state,
            staging.TargetLaunchState,
            ProjectModContract.LoadedPhase,
            loadConfirmed: true,
            topLevelState: action == "stop" ? "exiting" : "active");
        File.WriteAllText(paths.StopRequestPath, LaunchId);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            action,
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException(
                $"{action} must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        string serializedReport = JsonSerializer.Serialize(
            report,
            LiveLabJsonOptions.CamelCase);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.StagingRemoved);
        Assert.Empty(report.Problems);
        Assert.DoesNotContain(secret, serializedReport, StringComparison.Ordinal);
        Assert.False(File.Exists(paths.StatePath));
        Assert.False(File.Exists(paths.StopRequestPath));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.All(staging.Artifacts, artifact =>
            Assert.False(Directory.Exists(artifact.StagingPath)));
    }

    [Fact]
    public void RuntimeContentDriftBlocksStatusButNotSingleStopAndCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        string stagedDll = Path.Combine(staging.Target.StagingPath, "Target.dll");
        File.AppendAllText(stagedDll, "runtime drift");
        LiveLabState state = ReviewState(paths, staging.TargetLaunchState);
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        WriteStatus(
            paths,
            state,
            staging.TargetLaunchState,
            ProjectModContract.LoadedPhase,
            loadConfirmed: true,
            topLevelState: "exiting");
        File.WriteAllText(paths.StopRequestPath, LaunchId);

        LiveLabCommandResult status = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));
        LiveLabCommandResult stop = ProjectReviewService.Execute(
            "stop",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Stop must not run doctor."));

        ProjectReviewReport statusReport = Assert.IsType<ProjectReviewReport>(status.Report);
        Assert.Equal(3, status.ExitCode);
        Assert.Equal("blocked", statusReport.State);
        Assert.Equal(
            "reviewStagingOwnershipDrifted",
            Assert.Single(statusReport.Problems).Code);
        ProjectReviewReport stopReport = Assert.IsType<ProjectReviewReport>(stop.Report);
        Assert.Equal(0, stop.ExitCode);
        Assert.Equal("stopped", stopReport.State);
        Assert.True(stopReport.StagingRemoved);
        Assert.Empty(stopReport.Problems);
        Assert.False(File.Exists(paths.StatePath));
        Assert.False(File.Exists(paths.StopRequestPath));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.False(Directory.Exists(staging.Target.StagingPath));
    }

    [Fact]
    public void StatusReportsMarkerlessPartialStagingFailClosed()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        string partial = WriteSentinel(
            Path.Combine(paths.ModsPath, "PartialTarget"),
            "partial.dll",
            "partial");

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.StagingRemoved);
        Assert.Equal(
            "reviewStagingOwnershipMissing",
            Assert.Single(report.Problems).Code);
        Assert.Equal("partial", File.ReadAllText(partial));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommandWritesOnlyToTheExactRunningReadyLoadedReviewWithoutMutatingOwnership(
        bool isActive)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        const string secret = "issue-40-command-secret-must-not-be-reported";
        ProjectReviewStaging staging = StageTargetAndCompanion(paths, temporary.Path);
        ProjectReviewOwnedArtifact companion = Assert.Single(
            staging.Artifacts,
            artifact => artifact.Role == ProjectReviewArtifactRole.Companion);
        File.WriteAllText(
            Path.Combine(companion.StagingPath, "config.json"),
            $"{{\"SharedSecret\":\"{secret}\"}}");
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState, isActive);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));

                LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                    "sic-review set greenhouse fixture",
                    temporary.Path,
                    sender);

                ProjectReviewCommandReport report =
                    Assert.IsType<ProjectReviewCommandReport>(result.Report);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("running", report.State);
                Assert.True(report.CommandWritten);
                Assert.Empty(report.Problems);
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.Equal("sic-review set greenhouse fixture", sender.Line);
                Assert.DoesNotContain(
                    secret,
                    JsonSerializer.Serialize(report, LiveLabJsonOptions.CamelCase),
                    StringComparison.Ordinal);
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataQueryReusesTheExactReadyReviewBindingAndRejectsMismatchedResponses(
        bool mismatchRequestId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                string? responsePath = null;
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        string[] tokens = line.Split(' ');
                        string requestId = tokens[2];
                        responsePath = ReviewDataContract.ResponsePath(
                            paths.RuntimePath,
                            requestId);
                        var report = new ReviewDataReport(
                            ReviewDataContract.SchemaVersion,
                            "ready",
                            ReviewDataContract.KeysOperation,
                            "1.6.15",
                            "1.6.15.24356",
                            "Data/Buildings",
                            "System.Collections.Generic.Dictionary",
                            "dictionary",
                            "string",
                            null,
                            null,
                            ["Barn", "Coop"],
                            new ReviewDataPage(0, 2, 2, 20, 2),
                            null,
                            null,
                            []);
                        File.WriteAllText(
                            responsePath,
                            JsonSerializer.Serialize(
                                new ReviewDataResponseEnvelope(
                                    ReviewDataContract.SchemaVersion,
                                    mismatchRequestId
                                        ? Guid.NewGuid().ToString("N")
                                        : requestId,
                                    report),
                                LiveLabJsonOptions.CamelCase));
                    });

                LiveLabCommandResult result = ProjectReviewDataService.Execute(
                    new ReviewDataQuery(
                        ReviewDataContract.KeysOperation,
                        "Data/Buildings",
                        null,
                        0,
                        2),
                    temporary.Path,
                    sender);

                ReviewDataReport report = Assert.IsType<ReviewDataReport>(result.Report);
                Assert.Equal(mismatchRequestId ? 3 : 0, result.ExitCode);
                Assert.Equal(mismatchRequestId ? "blocked" : "ready", report.State);
                if (mismatchRequestId)
                {
                    Assert.Equal(
                        "dataResponseInvalid",
                        Assert.Single(report.Problems).Code);
                }
                else
                {
                    Assert.Equal(["Barn", "Coop"], report.Keys);
                }
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.StartsWith("sdvkit data ", sender.Line, StringComparison.Ordinal);
                Assert.DoesNotContain("Data/Buildings", sender.Line, StringComparison.Ordinal);
                Assert.NotNull(responsePath);
                Assert.False(File.Exists(responsePath));
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void MapQueryTimesOutWithoutRetryingTheExactReadyReview()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));

                LiveLabCommandResult result = ProjectReviewMapService.Execute(
                    new ReviewMapQuery(
                        ReviewMapContract.AssetsOperation,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        0,
                        ReviewMapContract.DefaultPageLimit),
                    temporary.Path,
                    sender,
                    responseTimeout: TimeSpan.Zero);

                ReviewMapReport report = Assert.IsType<ReviewMapReport>(result.Report);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("blocked", report.State);
                Assert.Equal("mapResponseTimedOut", Assert.Single(report.Problems).Code);
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.StartsWith("sdvkit map ", sender.Line, StringComparison.Ordinal);
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapQueryReusesTheExactReadyReviewBindingAndConsumesItsResponse(
        bool mismatchRequestId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string? responsePath = null;
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        string requestId = line.Split(' ')[2];
                        responsePath = ReviewMapContract.ResponsePath(paths.RuntimePath, requestId);
                        var coverage = new ReviewMapCoverageReport(1, 1, 1, 0, 1, 0, 0, 0);
                        var report = new ReviewMapReport(
                            ReviewMapContract.SchemaVersion,
                            "ready",
                            ReviewMapContract.AssetsOperation,
                            "1.6.15",
                            "1.6.15.24356",
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            [
                                new ReviewMapAssetReport(
                                    "Maps/Town",
                                    "xTile.Map",
                                    "map",
                                    new ReviewMapSummary(64, 64, 3, 2, 1, 4),
                                    true,
                                    null),
                            ],
                            null,
                            null,
                            null,
                            new ReviewMapPage(0, 50, 1, 1, null),
                            coverage,
                            []);
                        File.WriteAllText(
                            responsePath,
                            JsonSerializer.Serialize(
                                new ReviewMapResponseEnvelope(
                                    ReviewMapContract.SchemaVersion,
                                    mismatchRequestId
                                        ? Guid.NewGuid().ToString("N")
                                        : requestId,
                                    report),
                                LiveLabJsonOptions.CamelCase));
                    });

                LiveLabCommandResult result = ProjectReviewMapService.Execute(
                    new ReviewMapQuery(
                        ReviewMapContract.AssetsOperation,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        0,
                        ReviewMapContract.DefaultPageLimit),
                    temporary.Path,
                    sender);

                ReviewMapReport report = Assert.IsType<ReviewMapReport>(result.Report);
                Assert.Equal(mismatchRequestId ? 3 : 0, result.ExitCode);
                Assert.Equal(mismatchRequestId ? "blocked" : "ready", report.State);
                if (mismatchRequestId)
                {
                    Assert.Equal("mapResponseInvalid", Assert.Single(report.Problems).Code);
                }
                else
                {
                    Assert.True(report.Coverage!.Complete);
                    Assert.Equal("Maps/Town", Assert.Single(report.Assets!).AssetName);
                }
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.StartsWith("sdvkit map ", sender.Line, StringComparison.Ordinal);
                Assert.NotNull(responsePath);
                Assert.False(File.Exists(responsePath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedResponseTransportRejectsNullDataReportOrProblems(bool nullReport)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string? responsePath = null;
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        string requestId = line.Split(' ')[2];
                        responsePath = ReviewDataContract.ResponsePath(paths.RuntimePath, requestId);
                        object envelope = nullReport
                            ? new
                            {
                                schemaVersion = ReviewDataContract.SchemaVersion,
                                requestId,
                                report = (object?)null,
                            }
                            : new
                            {
                                schemaVersion = ReviewDataContract.SchemaVersion,
                                requestId,
                                report = new
                                {
                                    schemaVersion = ReviewDataContract.SchemaVersion,
                                    operation = ReviewDataContract.AssetsOperation,
                                    problems = (object?)null,
                                },
                            };
                        File.WriteAllText(responsePath, JsonSerializer.Serialize(envelope));
                    });

                LiveLabCommandResult result = ProjectReviewDataService.Execute(
                    new ReviewDataQuery(
                        ReviewDataContract.AssetsOperation,
                        null,
                        null,
                        0,
                        ReviewDataContract.DefaultPageLimit),
                    temporary.Path,
                    sender);

                ReviewDataReport report = Assert.IsType<ReviewDataReport>(result.Report);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("dataResponseInvalid", Assert.Single(report.Problems).Code);
                Assert.Equal(1, sender.CallCount);
                Assert.NotNull(responsePath);
                Assert.False(File.Exists(responsePath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void MapAndTextureQueriesShareTheExactReadyReviewTransport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                var lines = new List<string>();
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        lines.Add(line);
                        string[] tokens = line.Split(' ');
                        string requestId = tokens[2];
                        if (tokens[1] == "map")
                        {
                            var mapReport = new ReviewMapReport(
                                ReviewMapContract.SchemaVersion,
                                "ready",
                                ReviewMapContract.AssetsOperation,
                                "1.6.15",
                                "1.6.15.24356",
                                null,
                                null,
                                null,
                                null,
                                null,
                                null,
                                [
                                    new ReviewMapAssetReport(
                                        "Maps/Town",
                                        "xTile.Map",
                                        "map",
                                        new ReviewMapSummary(64, 64, 3, 2, 1, 4),
                                        true,
                                        null),
                                ],
                                null,
                                null,
                                null,
                                new ReviewMapPage(0, 50, 1, 1, null),
                                new ReviewMapCoverageReport(1, 1, 1, 0, 1, 0, 0, 0),
                                []);
                            File.WriteAllText(
                                ReviewMapContract.ResponsePath(paths.RuntimePath, requestId),
                                JsonSerializer.Serialize(
                                    new ReviewMapResponseEnvelope(
                                        ReviewMapContract.SchemaVersion,
                                        requestId,
                                        mapReport),
                                    LiveLabJsonOptions.CamelCase));
                            return;
                        }

                        Assert.Equal("texture", tokens[1]);
                        var textureReport = new ReviewTextureReport(
                            ReviewTextureContract.SchemaVersion,
                            "ready",
                            ReviewTextureContract.GetOperation,
                            "1.6.15",
                            "1.6.15.24356",
                            "LooseSprites/Cursors",
                            ReviewTextureContract.CanonicalGameContentSource,
                            true,
                            new ReviewTextureMetadataReport(64, 32, "Color", 1, false),
                            new ReviewTextureProvenanceReport(
                                ReviewTextureContract.FinalPipelineStage,
                                false,
                                ReviewTextureContract.ProvenanceUnavailableDetail),
                            null,
                            null,
                            null,
                            null,
                            []);
                        File.WriteAllText(
                            ReviewTextureContract.ResponsePath(paths.RuntimePath, requestId),
                            JsonSerializer.Serialize(
                                new ReviewTextureResponseEnvelope(
                                    ReviewTextureContract.SchemaVersion,
                                    requestId,
                                    textureReport),
                                LiveLabJsonOptions.CamelCase));
                    });

                LiveLabCommandResult mapResult = ProjectReviewMapService.Execute(
                    new ReviewMapQuery(
                        ReviewMapContract.AssetsOperation,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        0,
                        ReviewMapContract.DefaultPageLimit),
                    temporary.Path,
                    sender);
                LiveLabCommandResult textureResult = ProjectReviewTextureService.Execute(
                    new ReviewTextureQuery(
                        ReviewTextureContract.GetOperation,
                        "LooseSprites/Cursors",
                        0,
                        1),
                    temporary.Path,
                    sender);

                Assert.Equal(0, mapResult.ExitCode);
                Assert.Equal(0, textureResult.ExitCode);
                Assert.Equal(2, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.Collection(
                    lines,
                    line => Assert.StartsWith("sdvkit map ", line, StringComparison.Ordinal),
                    line => Assert.StartsWith("sdvkit texture ", line, StringComparison.Ordinal));
                Assert.Empty(Directory.GetFiles(paths.RuntimePath, "review-*.json"));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void TexturePreviewQueryUsesTheExactReviewAndRetainsValidatedEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                string? responsePath = null;
                string? previewPath = null;
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        string[] tokens = line.Split(' ');
                        string requestId = tokens[2];
                        responsePath = ReviewTextureContract.ResponsePath(
                            paths.RuntimePath,
                            requestId);
                        previewPath = ReviewTextureContract.PreviewPath(
                            paths.RuntimePath,
                            requestId);
                        byte[] png = PngTestData.CreateRgba8(64, 32);
                        File.WriteAllBytes(previewPath, png);
                        var report = new ReviewTextureReport(
                            ReviewTextureContract.SchemaVersion,
                            "ready",
                            ReviewTextureContract.PreviewOperation,
                            "1.6.15",
                            "1.6.15.24356",
                            "LooseSprites/Cursors",
                            ReviewTextureContract.CanonicalGameContentSource,
                            true,
                            new ReviewTextureMetadataReport(
                                64,
                                32,
                                "Color",
                                1,
                                false),
                            new ReviewTextureProvenanceReport(
                                ReviewTextureContract.FinalPipelineStage,
                                false,
                                ReviewTextureContract.ProvenanceUnavailableDetail),
                            new ReviewTexturePreviewReport(
                                ReviewTextureContract.PreviewFileName(requestId),
                                64,
                                32,
                                png.Length,
                                Convert.ToHexString(SHA256.HashData(png))
                                    .ToLowerInvariant()),
                            null,
                            null,
                            null,
                            []);
                        File.WriteAllText(
                            responsePath,
                            JsonSerializer.Serialize(
                                new ReviewTextureResponseEnvelope(
                                    ReviewTextureContract.SchemaVersion,
                                    requestId,
                                    report),
                                LiveLabJsonOptions.CamelCase));
                    });

                LiveLabCommandResult result = ProjectReviewTextureService.Execute(
                    new ReviewTextureQuery(
                        ReviewTextureContract.PreviewOperation,
                        "LooseSprites/Cursors",
                        0,
                        1),
                    temporary.Path,
                    sender);

                ReviewTextureReport report =
                    Assert.IsType<ReviewTextureReport>(result.Report);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("ready", report.State);
                Assert.NotNull(report.Preview);
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.StartsWith("sdvkit texture ", sender.Line, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "LooseSprites/Cursors",
                    sender.Line,
                    StringComparison.Ordinal);
                Assert.NotNull(responsePath);
                Assert.False(File.Exists(responsePath));
                Assert.NotNull(previewPath);
                Assert.True(File.Exists(previewPath));
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void InvalidTextureResponseNeverDeletesAPreviewCreatedAfterDispatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string? responsePath = null;
                string? previewPath = null;
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        string requestId = line.Split(' ')[2];
                        responsePath = ReviewTextureContract.ResponsePath(
                            paths.RuntimePath,
                            requestId);
                        previewPath = ReviewTextureContract.PreviewPath(
                            paths.RuntimePath,
                            requestId);
                        File.WriteAllText(previewPath, "preserve foreign collision");
                        File.WriteAllText(responsePath, "{}");
                    });

                LiveLabCommandResult result = ProjectReviewTextureService.Execute(
                    new ReviewTextureQuery(
                        ReviewTextureContract.PreviewOperation,
                        "LooseSprites/Cursors",
                        0,
                        1),
                    temporary.Path,
                    sender);

                ReviewTextureReport report =
                    Assert.IsType<ReviewTextureReport>(result.Report);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("textureResponseInvalid", Assert.Single(report.Problems).Code);
                Assert.NotNull(responsePath);
                Assert.False(File.Exists(responsePath));
                Assert.NotNull(previewPath);
                Assert.Equal("preserve foreign collision", File.ReadAllText(previewPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioQueryReusesTheExactReadyReviewBindingAndRejectsMismatchedResponses(
        bool mismatchRequestId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                string? responsePath = null;
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        string[] tokens = line.Split(' ');
                        string requestId = tokens[2];
                        responsePath = ReviewAudioContract.ResponsePath(
                            paths.RuntimePath,
                            requestId);
                        var cue = new ReviewAudioCueReport(
                            "MainTheme",
                            [],
                            false,
                            true,
                            true,
                            3,
                            null,
                            null,
                            null,
                            null,
                            null,
                            []);
                        var report = new ReviewAudioReport(
                            ReviewAudioContract.SchemaVersion,
                            "ready",
                            ReviewAudioContract.CueOperation,
                            "1.6.15",
                            "1.6.15.24356",
                            "MainTheme",
                            [cue],
                            null,
                            new ReviewAudioCoverageReport(
                                0,
                                0,
                                0,
                                0,
                                1,
                                1,
                                0,
                                0,
                                true,
                                null,
                                ReviewAudioContract.BuiltInInventoryStatus),
                            []);
                        File.WriteAllText(
                            responsePath,
                            JsonSerializer.Serialize(
                                new ReviewAudioResponseEnvelope(
                                    ReviewAudioContract.SchemaVersion,
                                    mismatchRequestId
                                        ? Guid.NewGuid().ToString("N")
                                        : requestId,
                                    report),
                                LiveLabJsonOptions.CamelCase));
                    });

                LiveLabCommandResult result = ProjectReviewAudioService.Execute(
                    new ReviewAudioQuery(
                        ReviewAudioContract.CueOperation,
                        "MainTheme",
                        0,
                        1),
                    temporary.Path,
                    sender);

                ReviewAudioReport report = Assert.IsType<ReviewAudioReport>(result.Report);
                Assert.Equal(mismatchRequestId ? 3 : 0, result.ExitCode);
                Assert.Equal(mismatchRequestId ? "blocked" : "ready", report.State);
                if (mismatchRequestId)
                {
                    Assert.Equal(
                        "audioResponseInvalid",
                        Assert.Single(report.Problems).Code);
                }
                else
                {
                    Assert.Equal("MainTheme", Assert.Single(report.Cues!).CueId);
                }
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.StartsWith("sdvkit audio ", sender.Line, StringComparison.Ordinal);
                Assert.DoesNotContain("MainTheme", sender.Line, StringComparison.Ordinal);
                Assert.NotNull(responsePath);
                Assert.False(File.Exists(responsePath));
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void AudioInventoryPreservesABoundedProbeFailureFromAlwaysOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string? responsePath = null;
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        string[] tokens = line.Split(' ');
                        string requestId = tokens[2];
                        responsePath = ReviewAudioContract.ResponsePath(
                            paths.RuntimePath,
                            requestId);
                        var report = new ReviewAudioReport(
                            ReviewAudioContract.SchemaVersion,
                            "blocked",
                            ReviewAudioContract.CuesOperation,
                            "1.6.15",
                            "1.6.15.24356",
                            null,
                            null,
                            null,
                            new ReviewAudioCoverageReport(
                                0,
                                1,
                                0,
                                1,
                                0,
                                0,
                                0,
                                0,
                                true,
                                null,
                                ReviewAudioContract.BuiltInInventoryStatus),
                            [
                                new ReviewAudioProblem(
                                    "audioCueProbeInvalid",
                                    "The soundbank returned inconsistent metadata."),
                            ]);
                        File.WriteAllText(
                            responsePath,
                            JsonSerializer.Serialize(
                                new ReviewAudioResponseEnvelope(
                                    ReviewAudioContract.SchemaVersion,
                                    requestId,
                                    report),
                                LiveLabJsonOptions.CamelCase));
                    });

                LiveLabCommandResult result = ProjectReviewAudioService.Execute(
                    new ReviewAudioQuery(
                        ReviewAudioContract.CuesOperation,
                        null,
                        0,
                        100),
                    temporary.Path,
                    sender);

                ReviewAudioReport report = Assert.IsType<ReviewAudioReport>(result.Report);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("blocked", report.State);
                Assert.Null(report.CueId);
                Assert.Equal(
                    "audioCueProbeInvalid",
                    Assert.Single(report.Problems).Code);
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.StartsWith("sdvkit audio ", sender.Line, StringComparison.Ordinal);
                Assert.NotNull(responsePath);
                Assert.False(File.Exists(responsePath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Theory]
    [InlineData(
        ProjectModContract.WaitingForGameLaunchPhase,
        false,
        "reviewConsoleTargetNotReady")]
    [InlineData(ProjectModContract.LoadedPhase, false, "projectModStatusMismatch")]
    public void CommandDoesNotSendUntilTheExactRunningReviewIsReadyAndLoadConfirmed(
        string phase,
        bool loadConfirmed,
        string expectedProblem)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteStatus(
                    paths,
                    state,
                    staging.TargetLaunchState,
                    phase,
                    loadConfirmed);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));

                LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                    "sic-review set greenhouse fixture",
                    temporary.Path,
                    sender);

                ProjectReviewCommandReport report =
                    Assert.IsType<ProjectReviewCommandReport>(result.Report);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("blocked", report.State);
                Assert.False(report.CommandWritten);
                Assert.Equal(
                    expectedProblem,
                    Assert.Single(report.Problems).Code);
                Assert.Equal(0, sender.CallCount);
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Theory]
    [InlineData("pending", false, "reviewConsoleTestSaveNotReady")]
    [InlineData("loading", false, "reviewConsoleTestSaveNotReady")]
    [InlineData("failed", false, "testSaveFailed")]
    [InlineData("mismatch", false, "testSaveStatusMismatch")]
    [InlineData("passed", true, null)]
    public void CommandRequiresTheExactReviewFixtureToBeCurrentlyLoaded(
        string fixtureState,
        bool expectedWritten,
        string? expectedProblem)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        TestSaveIdentity fixture = WriteReviewFixture(paths);
        var testSave = new TestSaveLaunchState(
            TestSaveContract.ReviewMode,
            fixture,
            Path.Combine(paths.SavesPath, fixture.SaveId),
            paths.TestSaveWorkPath,
            paths.TestSaveScenarioLogPath);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(
                    paths,
                    staging.TargetLaunchState,
                    identity) with
                {
                    TestSave = testSave,
                };
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                string? markerPhase = fixtureState switch
                {
                    "pending" => null,
                    "mismatch" => "passed",
                    _ => fixtureState,
                };
                TestSaveStatusMarker? marker = markerPhase is null
                    ? null
                    : new TestSaveStatusMarker(
                        TestSaveContract.SchemaVersion,
                        TestSaveContract.ReviewMode,
                        markerPhase,
                        fixtureState == "mismatch"
                            ? "cccccccccccccccccccccccccccccccc"
                            : fixture.FixtureId,
                        fixture.SaveId,
                        IdentityVerified: markerPhase == "passed",
                        WaitedTicks: 0,
                        fixtureState,
                        paths.TestSaveScenarioLogPath);
                WriteLoadedStatus(
                    paths,
                    state,
                    staging.TargetLaunchState,
                    testSave: marker);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));

                LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                    "sic-review set greenhouse fixture",
                    temporary.Path,
                    sender);

                ProjectReviewCommandReport report =
                    Assert.IsType<ProjectReviewCommandReport>(result.Report);
                Assert.Equal(expectedWritten ? 0 : 3, result.ExitCode);
                Assert.Equal(expectedWritten, report.CommandWritten);
                Assert.Equal(expectedWritten ? 1 : 0, sender.CallCount);
                if (expectedWritten)
                {
                    Assert.Empty(report.Problems);
                }
                else
                {
                    Assert.Equal(
                        expectedProblem,
                        Assert.Single(report.Problems).Code);
                }

                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void PreScenarioInputAndViewportCommandsCanInspectAFailedOwnedFixture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        TestSaveIdentity fixture = WriteReviewFixture(paths);
        var testSave = new TestSaveLaunchState(
            TestSaveContract.ReviewMode,
            fixture,
            Path.Combine(paths.SavesPath, fixture.SaveId),
            paths.TestSaveWorkPath,
            paths.TestSaveScenarioLogPath);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(
                    paths,
                    staging.TargetLaunchState,
                    identity) with
                {
                    TestSave = testSave,
                };
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(
                    paths,
                    state,
                    staging.TargetLaunchState,
                    testSave: new TestSaveStatusMarker(
                        TestSaveContract.SchemaVersion,
                        TestSaveContract.ReviewMode,
                        "failed",
                        fixture.FixtureId,
                        fixture.SaveId,
                        IdentityVerified: false,
                        WaitedTicks: 0,
                        "Fixture load failed before world ready.",
                        paths.TestSaveScenarioLogPath));
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));
                string[] commands =
                [
                    "sdvkit input press F8",
                    "sdvkit input cursor 100 200",
                    "sdvkit input cursor clear",
                    "sdvkit screenshot viewport fixture-load-failed",
                ];

                for (var index = 0; index < commands.Length; index++)
                {
                    LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                        commands[index],
                        temporary.Path,
                        sender);

                    ProjectReviewCommandReport report =
                        Assert.IsType<ProjectReviewCommandReport>(result.Report);
                    Assert.Equal(0, result.ExitCode);
                    Assert.Equal("running", report.State);
                    Assert.True(report.CommandWritten);
                    Assert.Empty(report.Problems);
                    Assert.Equal(index + 1, sender.CallCount);
                    Assert.Equal(identity, sender.Identity);
                    Assert.Equal(commands[index], sender.Line);
                }

                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void MapFixtureDataAndMalformedInputCommandsRemainFixtureGated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        TestSaveIdentity fixture = WriteReviewFixture(paths);
        var testSave = new TestSaveLaunchState(
            TestSaveContract.ReviewMode,
            fixture,
            Path.Combine(paths.SavesPath, fixture.SaveId),
            paths.TestSaveWorkPath,
            paths.TestSaveScenarioLogPath);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(
                    paths,
                    staging.TargetLaunchState,
                    identity) with
                {
                    TestSave = testSave,
                };
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(
                    paths,
                    state,
                    staging.TargetLaunchState,
                    testSave: new TestSaveStatusMarker(
                        TestSaveContract.SchemaVersion,
                        TestSaveContract.ReviewMode,
                        "loading",
                        fixture.FixtureId,
                        fixture.SaveId,
                        IdentityVerified: false,
                        WaitedTicks: 0,
                        "Loading exact review fixture.",
                        paths.TestSaveScenarioLogPath));
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));
                string[] commands =
                [
                    "sdvkit screenshot map-before-world",
                    "sdvkit fixture status",
                    "sdvkit data aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa assets 0 20",
                    "sdvkit input",
                    "sdvkit input press F8 extra",
                    "sdvkit input press F-8",
                    "sdvkit-input press F8",
                    "prefix sdvkit input press F8",
                    "sdvkit screenshot viewport title extra",
                ];

                foreach (string command in commands)
                {
                    LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                        command,
                        temporary.Path,
                        sender);

                    ProjectReviewCommandReport report =
                        Assert.IsType<ProjectReviewCommandReport>(result.Report);
                    Assert.Equal(3, result.ExitCode);
                    Assert.Equal("blocked", report.State);
                    Assert.False(report.CommandWritten);
                    Assert.Equal(
                        "reviewConsoleTestSaveNotReady",
                        Assert.Single(report.Problems).Code);
                }

                Assert.Equal(0, sender.CallCount);
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void CommandDoesNotSendWhenReviewOwnershipDoesNotBindExactly()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(
            paths,
            staging.TargetLaunchState with { UniqueId = "Nana.OtherTarget" });
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        string stateBefore = FileSnapshot(paths.StatePath);
        string stagingBefore = TreeSnapshot(paths.ModsPath);
        var sender = new RecordingConsoleInputSender(
            new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written));

        LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
            "sic-review set greenhouse fixture",
            temporary.Path,
            sender);

        ProjectReviewCommandReport report =
            Assert.IsType<ProjectReviewCommandReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.CommandWritten);
        Assert.Equal("reviewOwnershipMismatch", Assert.Single(report.Problems).Code);
        Assert.Equal(0, sender.CallCount);
        Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
        Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
    }

    [Fact]
    public void CommandDoesNotSendWhenTheExactlyBoundProcessIsNotRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(paths, staging.TargetLaunchState);
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        WriteLoadedStatus(paths, state, staging.TargetLaunchState);
        string stateBefore = FileSnapshot(paths.StatePath);
        string stagingBefore = TreeSnapshot(paths.ModsPath);
        var sender = new RecordingConsoleInputSender(
            new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written));

        LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
            "sic-review set greenhouse fixture",
            temporary.Path,
            sender);

        ProjectReviewCommandReport report =
            Assert.IsType<ProjectReviewCommandReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.CommandWritten);
        Assert.Equal("ownedProcessExited", Assert.Single(report.Problems).Code);
        Assert.Equal(0, sender.CallCount);
        Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
        Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
    }

    [Fact]
    public void CommandServiceRejectsControlCharactersAndOverlongLinesBeforeSending()
    {
        using TemporaryDirectory temporary = new();
        var sender = new RecordingConsoleInputSender(
            new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written));

        foreach (string command in new[]
        {
            "sic-review\nset",
            new string('x', ProjectReviewConsoleLine.MaximumLength + 1),
        })
        {
            LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                command,
                temporary.Path,
                sender);

            ProjectReviewCommandReport report =
                Assert.IsType<ProjectReviewCommandReport>(result.Report);
            Assert.Equal(3, result.ExitCode);
            Assert.Equal("blocked", report.State);
            Assert.False(report.CommandWritten);
            Assert.Equal(
                "reviewConsoleCommandInvalid",
                Assert.Single(report.Problems).Code);
        }

        Assert.Equal(0, sender.CallCount);
    }

    [Theory]
    [InlineData("sdvkit input press F8", true)]
    [InlineData("sdvkit input press MouseWheelDown", true)]
    [InlineData("sdvkit input cursor 0 2147483647", true)]
    [InlineData("sdvkit input cursor clear", true)]
    [InlineData("sdvkit screenshot viewport title_screen-1", true)]
    [InlineData("sdvkit screenshot map", false)]
    [InlineData("sdvkit screenshot viewport", false)]
    [InlineData("sdvkit screenshot viewport title extra", false)]
    [InlineData("sdvkit fixture status", false)]
    [InlineData("sdvkit data aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa assets 0 20", false)]
    [InlineData("sdvkit input", false)]
    [InlineData("sdvkit input press", false)]
    [InlineData("sdvkit input press F8 extra", false)]
    [InlineData("sdvkit input press F-8", false)]
    [InlineData("sdvkit input cursor -1 0", false)]
    [InlineData("sdvkit input cursor 2147483648 0", false)]
    [InlineData("sdvkit inputter press F8", false)]
    [InlineData("prefix sdvkit input press F8", false)]
    [InlineData("SDVKIT input press F8", false)]
    public void PreScenarioCommandClassificationMatchesOnlyExactBuiltInGrammar(
        string command,
        bool expected) =>
        Assert.Equal(
            expected,
            ProjectReviewConsoleLine.CanRunBeforeScenarioReady(command));

    [Theory]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WorkerStartFailed,
        false,
        "reviewConsoleWorkerStartFailed")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WorkerParentMismatch,
        false,
        "reviewConsoleWorkerParentMismatch")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WrittenDetachFailed,
        true,
        "reviewConsoleDetachFailed")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WrittenProcessExited,
        true,
        "reviewConsoleProcessExitedAfterWrite")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WrittenProcessUnreadable,
        true,
        "reviewConsoleProcessUnreadableAfterWrite")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WrittenConsoleChanged,
        true,
        "reviewConsoleOwnershipChangedAfterWrite")]
    public void CommandMapsWorkerAndPostWriteFailuresWithoutMutatingOwnership(
        int statusValue,
        bool expectedCommandWritten,
        string expectedProblem)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        (ProjectReviewConsoleInputStatus)statusValue));

                LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                    "sic-review set greenhouse fixture",
                    temporary.Path,
                    sender);

                ProjectReviewCommandReport report =
                    Assert.IsType<ProjectReviewCommandReport>(result.Report);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("blocked", report.State);
                Assert.Equal(expectedCommandWritten, report.CommandWritten);
                Assert.Equal(expectedProblem, Assert.Single(report.Problems).Code);
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    private static ProjectReviewStaging StageTarget(
        LiveLabPaths paths,
        string fixtureRoot)
    {
        ProjectReviewPreparedArtifact target = ProjectReviewStagerTests.Artifact(
            fixtureRoot,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewStagingResult result = ProjectModStager.StageReview([target], paths);
        Assert.Null(result.Problem);
        return Assert.IsType<ProjectReviewStaging>(result.Staging);
    }

    private static ProjectReviewStaging StageTargetAndCompanion(
        LiveLabPaths paths,
        string fixtureRoot)
    {
        ProjectReviewPreparedArtifact target = ProjectReviewStagerTests.Artifact(
            fixtureRoot,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewPreparedArtifact companion = ProjectReviewStagerTests.Artifact(
            fixtureRoot,
            "Companion",
            ProjectReviewArtifactRole.Companion,
            "Nana.Companion");
        ProjectReviewStagingResult result = ProjectModStager.StageReview(
            [target, companion],
            paths);
        Assert.Null(result.Problem);
        return Assert.IsType<ProjectReviewStaging>(result.Staging);
    }

    private static ProjectReviewStaging StageNetworkReviewSet(
        LiveLabPaths paths,
        string fixtureRoot)
    {
        ProjectReviewPreparedArtifact target = ProjectReviewStagerTests.Artifact(
            fixtureRoot,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target",
            "1.2.0");
        ProjectReviewPreparedArtifact companion = ProjectReviewStagerTests.Artifact(
            fixtureRoot,
            "Harness",
            ProjectReviewArtifactRole.Companion,
            "Nana.Harness");
        ProjectReviewPreparedArtifact contentPack = ProjectReviewStagerTests.Artifact(
            fixtureRoot,
            "GreenhousePack",
            ProjectReviewArtifactRole.ContentPack,
            "Nana.Target.GreenhousePack",
            contentPackFor: "Nana.Target",
            contentPackForMinimumVersion: "1.0.0");
        ProjectReviewStagingResult result = ProjectModStager.StageReview(
            [target, companion, contentPack],
            NetworkTwoContract.Topology,
            paths);
        Assert.Null(result.Problem);
        return Assert.IsType<ProjectReviewStaging>(result.Staging);
    }

    private static TestSaveIdentity WriteReviewFixture(LiveLabPaths paths)
    {
        const long uniqueGameId = 123456789;
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            uniqueGameId,
            TestSaveContract.GetSaveId(uniqueGameId),
            TestSaveContract.PlayerName,
            TestSaveContract.FarmName,
            TestSaveContract.FavoriteThing);
        string marker = JsonSerializer.Serialize(identity, LiveLabJsonOptions.CamelCase);
        Directory.CreateDirectory(paths.TestSaveRoot);
        File.WriteAllText(paths.TestSaveManifestPath, marker);
        foreach (string payloadPath in new[]
        {
            paths.TestSaveBaselinePath,
            paths.TestSaveWorkPath,
        })
        {
            Directory.CreateDirectory(payloadPath);
            File.WriteAllText(
                Path.Combine(payloadPath, TestSaveContract.FixtureMarkerFileName),
                marker);
            File.WriteAllText(
                Path.Combine(payloadPath, identity.SaveId),
                payloadPath == paths.TestSaveBaselinePath
                    ? "baseline-save"
                    : "review-save");
            File.WriteAllText(Path.Combine(payloadPath, "SaveGameInfo"), "save-info");
        }

        File.WriteAllText(
            Path.Combine(paths.TestSaveWorkPath, "review-only"),
            "review mutation");
        return identity;
    }

    private static string ArtifactIdentity(ProjectReviewArtifactReport artifact) =>
        string.Join(
            "|",
            artifact.Role,
            artifact.Kind,
            artifact.UniqueId,
            artifact.Version,
            artifact.ContentPackFor,
            artifact.BuildIdentity);

    private static LiveLabState NetworkReviewState(
        LiveLabPaths paths,
        ProjectModLaunchState target,
        string role,
        OwnedProcessIdentity? processIdentity = null)
    {
        LiveLabPaths rolePaths = LiveLabPaths.ResolveNetworkRole(paths, role);
        string fixtureId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const long uniqueGameId = 123456789;
        string saveId = TestSaveContract.GetSaveId(uniqueGameId);
        TestSaveLaunchState? testSave = role == NetworkTwoContract.HostRole
            ? new TestSaveLaunchState(
                TestSaveContract.ReviewMode,
                new TestSaveIdentity(
                    TestSaveContract.SchemaVersion,
                    "cccccccccccccccccccccccccccccccc",
                    fixtureId,
                    uniqueGameId,
                    saveId,
                    TestSaveContract.PlayerName,
                    TestSaveContract.FarmName,
                    TestSaveContract.FavoriteThing),
                Path.Combine(rolePaths.SavesPath, saveId),
                paths.TestSaveWorkPath,
                rolePaths.TestSaveScenarioLogPath)
            : null;
        var network = new NetworkTwoLaunchState(
            role,
            target.BuildIdentity,
            fixtureId,
            saveId,
            Path.Combine(rolePaths.RuntimePath, "network-2.log"),
            role == NetworkTwoContract.FarmhandRole ? 987654321 : null);
        return new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            Guid.NewGuid().ToString("N"),
            processIdentity ?? new OwnedProcessIdentity(
                int.MaxValue,
                new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
                Path.Combine(paths.ProjectRoot, "StardewModdingAPI.exe")),
            rolePaths.ModsPath,
            rolePaths.StatusPath,
            rolePaths.StopRequestPath,
            testSave,
            network,
            target);
    }

    private static LiveLabState ReviewState(
        LiveLabPaths paths,
        ProjectModLaunchState target,
        OwnedProcessIdentity? processIdentity = null) =>
        new(
            LiveLabState.CurrentSchemaVersion,
            LiveLabState.SingleTopology,
            LaunchId,
            processIdentity ?? new OwnedProcessIdentity(
                int.MaxValue,
                new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero),
                Path.Combine(paths.ProjectRoot, "StardewModdingAPI.exe")),
            paths.ModsPath,
            paths.StatusPath,
            paths.StopRequestPath,
            ProjectMod: target);

    private static string WriteSentinel(
        string directory,
        string fileName,
        string contents)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void WriteLoadedStatus(
        LiveLabPaths paths,
        LiveLabState state,
        ProjectModLaunchState target,
        bool isActive = true,
        TestSaveStatusMarker? testSave = null)
    {
        WriteStatus(
            paths,
            state,
            target,
            ProjectModContract.LoadedPhase,
            loadConfirmed: true,
            isActive: isActive,
            testSave: testSave);
    }

    private static void WriteStatus(
        LiveLabPaths paths,
        LiveLabState state,
        ProjectModLaunchState target,
        string phase,
        bool loadConfirmed,
        bool isActive = true,
        string topLevelState = "active",
        TestSaveStatusMarker? testSave = null)
    {
        var marker = new AlwaysOnStatusMarker(
            1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            topLevelState,
            600,
            IsActive: isActive,
            PauseWhenOutOfFocus: false,
            DateTimeOffset.UtcNow,
            testSave,
            ProjectMod: new ProjectModStatusMarker(
                ProjectModContract.SchemaVersion,
                phase,
                target.UniqueId,
                target.Version,
                loadConfirmed ? target.UniqueId : null,
                loadConfirmed ? target.Version : null,
                target.BuildIdentity,
                LoadConfirmed: loadConfirmed,
                loadConfirmed ? "Loaded by SMAPI." : "Waiting for game launch."));
        File.WriteAllText(
            paths.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    private static void WriteNetworkStatus(
        LiveLabPaths paths,
        LiveLabState state,
        ProjectModLaunchState target,
        string networkPhase)
    {
        NetworkTwoLaunchState network = Assert.IsType<NetworkTwoLaunchState>(
            state.NetworkTwo);
        TestSaveStatusMarker? testSave = state.TestSave is null
            ? null
            : new TestSaveStatusMarker(
                TestSaveContract.SchemaVersion,
                TestSaveContract.ReviewMode,
                "loading",
                state.TestSave.Identity.FixtureId,
                state.TestSave.Identity.SaveId,
                IdentityVerified: false,
                WaitedTicks: 0,
                "Loading exact network review fixture.",
                state.TestSave.ScenarioLogPath);
        var marker = new AlwaysOnStatusMarker(
            1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            "active",
            600,
            IsActive: false,
            PauseWhenOutOfFocus: false,
            DateTimeOffset.UtcNow,
            testSave,
            EnableServer: network.Role == NetworkTwoContract.HostRole ? true : null,
            IpConnectionsEnabled: network.Role == NetworkTwoContract.HostRole ? true : null,
            NetworkTwo: new NetworkTwoStatusMarker(
                NetworkTwoContract.SchemaVersion,
                network.Role,
                networkPhase,
                network.BuildIdentity,
                network.FixtureId,
                network.SaveId,
                IdentityVerified: false,
                JoinedTicks: 0,
                LocalPlayerId: null,
                LocalPlayerName: null,
                RemotePlayerId: null,
                RemotePlayerName: null,
                networkPhase == "failed"
                    ? "Network review failed before join."
                    : "Network review has not joined yet.",
                network.NetworkLogPath),
            ProjectMod: new ProjectModStatusMarker(
                ProjectModContract.SchemaVersion,
                ProjectModContract.LoadedPhase,
                target.UniqueId,
                target.Version,
                target.UniqueId,
                target.Version,
                target.BuildIdentity,
                LoadConfirmed: true,
                "Loaded by SMAPI."));
        File.WriteAllText(
            paths.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    private static (OwnedProcessIdentity Identity, Process Process) StartRunningProcess(
        string projectRoot)
    {
        var host = new WindowsLabProcessHost();
        string executable = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        LabProcessStartResult started = host.Start(new LabProcessStartSpec(
            executable,
            Path.GetDirectoryName(executable)!,
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-WindowStyle",
                "Hidden",
                "-Command",
                "Start-Sleep -Seconds 30",
            ],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Path.Combine(projectRoot, ".sdvkit", "test-runtime", "stdout.log"),
            Path.Combine(projectRoot, ".sdvkit", "test-runtime", "stderr.log")));
        Assert.Equal(LabProcessStartStatus.Started, started.Status);
        OwnedProcessIdentity identity = Assert.IsType<OwnedProcessIdentity>(started.Identity);
        return (identity, Process.GetProcessById(identity.ProcessId));
    }

    private static string FileSnapshot(string path) =>
        Convert.ToBase64String(File.ReadAllBytes(path));

    private static string TreeSnapshot(string root)
    {
        return string.Join(
            "\n",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                    $"{Path.GetRelativePath(root, path)}:{FileSnapshot(path)}"));
    }

    private static void EnsureExited(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The short-lived game-free test process already exited.
        }
    }

    private sealed class RecordingConsoleInputSender(
        ProjectReviewConsoleInputResult result,
        Action<string>? onSend = null) : IProjectReviewConsoleInputSender
    {
        public int CallCount { get; private set; }

        public OwnedProcessIdentity? Identity { get; private set; }

        public string? Line { get; private set; }

        public ProjectReviewConsoleInputResult SendLine(
            OwnedProcessIdentity expected,
            string line)
        {
            CallCount++;
            Identity = expected;
            Line = line;
            onSend?.Invoke(line);
            return result;
        }
    }
}
