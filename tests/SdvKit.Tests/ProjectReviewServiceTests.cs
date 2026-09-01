using System.Diagnostics;
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
        string role)
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
                Path.Combine(paths.TestSaveWorkPath, saveId),
                paths.TestSaveWorkPath,
                rolePaths.TestSaveScenarioLogPath)
            : null;
        var network = new NetworkTwoLaunchState(
            role,
            target.BuildIdentity,
            fixtureId,
            saveId,
            Path.Combine(rolePaths.RuntimePath, "network-two.log"),
            role == NetworkTwoContract.FarmhandRole ? 987654321 : null);
        return new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            Guid.NewGuid().ToString("N"),
            new OwnedProcessIdentity(
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
        bool isActive = true)
    {
        WriteStatus(
            paths,
            state,
            target,
            ProjectModContract.LoadedPhase,
            loadConfirmed: true,
            isActive: isActive);
    }

    private static void WriteStatus(
        LiveLabPaths paths,
        LiveLabState state,
        ProjectModLaunchState target,
        string phase,
        bool loadConfirmed,
        bool isActive = true,
        string topLevelState = "active")
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
        ProjectReviewConsoleInputResult result) : IProjectReviewConsoleInputSender
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
            return result;
        }
    }
}
