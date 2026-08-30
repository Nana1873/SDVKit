using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class LiveLabServiceTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 30, 20, 0, 0, TimeSpan.Zero);

    private const string LaunchId = "11111111111111111111111111111111";

    [Fact]
    public void StartUsesTheReadyInstallAndOnlyTheProjectLocalModsPath()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        FakeStateStore stateStore = new();
        FakeBuilder builder = new();
        FakeProcessHost process = new()
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            builder,
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("start");

        Assert.Equal(0, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("running", report.State);
        Assert.Equal("pending", report.AlwaysOn?.State);
        Assert.Equal(gamePath, builder.GamePath);
        LabProcessStartSpec specification = Assert.IsType<LabProcessStartSpec>(process.Specification);
        Assert.Equal(System.IO.Path.Combine(gamePath, "StardewModdingAPI.exe"), specification.ExecutablePath);
        Assert.Equal(["--mods-path", paths.ModsPath], specification.Arguments);
        Assert.NotEqual(
            System.IO.Path.Combine(gamePath, "Mods"),
            specification.Arguments[1]);
        Assert.Equal(LaunchId, specification.Environment["SDVKIT_LAB_LAUNCH_ID"]);
        Assert.Equal(paths.StatusPath, specification.Environment["SDVKIT_LAB_STATUS_PATH"]);
        Assert.Equal(paths.StopRequestPath, specification.Environment["SDVKIT_LAB_STOP_PATH"]);
        Assert.Equal(paths.StandardOutputPath, specification.StandardOutputPath);
        Assert.Equal(paths.StandardErrorPath, specification.StandardErrorPath);
        KeyValuePair<string, string>[] disabledTestSaveEnvironment = specification.Environment
            .Where(pair => pair.Key.StartsWith("SDVKIT_TEST_SAVE_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(9, disabledTestSaveEnvironment.Length);
        Assert.All(
            disabledTestSaveEnvironment,
            pair => Assert.Equal(string.Empty, pair.Value));
        Assert.Equal(1, stateStore.VerifyWritableCount);
        Assert.Equal(paths.ModsPath, stateStore.State?.ModsPath);
        Assert.StartsWith(paths.SingleRoot, paths.ModsPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedOwnershipWriteClosesTheExactStartedProcessBeforeReturning()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        FakeStateStore stateStore = new() { WriteFailuresRemaining = 1 };
        OwnedProcessIdentity identity = Identity(gamePath);
        FakeProcessHost process = new()
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                identity),
            CloseResult = new LabProcessCloseResult(LabProcessCloseStatus.Closed),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("start");

        Assert.Equal(3, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("stopped", report.State);
        Assert.Equal(identity.ProcessId, report.ProcessId);
        Assert.Equal(
            "stateWriteFailedLaunchRolledBack",
            Assert.Single(report.Problems).Code);
        Assert.Equal(identity, process.ClosedIdentity);
        Assert.Null(stateStore.State);
    }

    [Fact]
    public void FailedLaunchRollbackRetriesAndRetainsExactOwnershipRecord()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        FakeStateStore stateStore = new() { WriteFailuresRemaining = 1 };
        FakeProcessHost process = new()
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
            CloseResult = new LabProcessCloseResult(LabProcessCloseStatus.TimedOut),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("start");

        Assert.Equal(3, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("running", report.State);
        Assert.Equal("stateWriteRecovered", Assert.Single(report.Problems).Code);
        Assert.Equal(2, stateStore.WriteCount);
        Assert.NotNull(stateStore.State);
    }

    [Fact]
    public void NotReadyStartDoesNotCreateLabDirectoriesOrLaunch()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        FakeBuilder builder = new();
        FakeProcessHost process = new();
        LiveLabService service = Service(
            paths,
            new FakeStateStore(),
            builder,
            process,
            GameInstallationDiscovery.Inspect([]));

        LiveLabCommandResult result = service.Execute("start");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("notReady", Assert.IsType<LiveLabReport>(result.Report).State);
        Assert.Equal(0, builder.CallCount);
        Assert.Equal(0, process.StartCount);
        Assert.False(Directory.Exists(System.IO.Path.Combine(temporary.Path, ".sdvkit")));
    }

    [Fact]
    public void FailedLaunchVerificationRollsBackTheExactCreatedProcess()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        OwnedProcessIdentity identity = Identity(gamePath);
        FakeProcessHost process = new()
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.IdentityMismatch,
                identity,
                "simulated verification mismatch"),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.TimedOut),
            CloseResult = new LabProcessCloseResult(LabProcessCloseStatus.Closed),
        };
        LiveLabService service = Service(
            paths,
            new FakeStateStore(),
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("start");

        Assert.Equal(3, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("stopped", report.State);
        Assert.Equal(identity.ProcessId, report.ProcessId);
        Assert.Equal("processIdentityMismatch", Assert.Single(report.Problems).Code);
        Assert.Equal(identity, process.ClosedIdentity);
        Assert.Equal(1, process.CloseCount);
    }

    [Fact]
    public void StopNeverClosesAProcessWhoseExactIdentityMismatches()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        FakeStateStore stateStore = new()
        {
            State = State(paths, gamePath),
        };
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(
                LabProcessInspectStatus.IdentityMismatch,
                "start time changed"),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("stop");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("ownershipMismatch", Assert.IsType<LiveLabReport>(result.Report).State);
        Assert.Equal(0, process.CloseCount);
        Assert.NotNull(stateStore.State);
    }

    [Fact]
    public void CleanStopDeletesOnlyTheOwnedRuntimeRecord()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabState state = State(paths, gamePath);
        WriteExitingMarker(paths, state);
        FakeStateStore stateStore = new() { State = state };
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.Exited),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("stop");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", Assert.IsType<LiveLabReport>(result.Report).State);
        Assert.Equal(1, process.WaitCount);
        Assert.Equal(0, process.CloseCount);
        Assert.Equal(state.OwnedProcessIdentity, process.WaitedIdentity);
        Assert.Null(stateStore.State);
        Assert.False(File.Exists(paths.StopRequestPath));
    }

    [Fact]
    public void StopWithoutExitingMarkerRetainsOwnershipAndReportsUnconfirmedCleanup()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        LiveLabState state = State(paths, gamePath);
        FakeStateStore stateStore = new() { State = state };
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.Exited),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("stop");

        Assert.Equal(3, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("exited", report.State);
        Assert.Equal("cleanStopNotConfirmed", Assert.Single(report.Problems).Code);
        Assert.NotNull(stateStore.State);
    }

    [Fact]
    public void CleanStopTimeoutRetainsOwnershipAndNeverKills()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        FakeStateStore stateStore = new() { State = State(paths, gamePath) };
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.TimedOut),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("stop");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("running", Assert.IsType<LiveLabReport>(result.Report).State);
        Assert.Equal(1, process.WaitCount);
        Assert.Equal(0, process.CloseCount);
        Assert.NotNull(stateStore.State);
    }

    [Fact]
    public void RestoreFailureLeavesTheExactProcessAndOwnershipAlone()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabState state = State(paths, gamePath);
        WriteStatusMarker(paths, state, "restoreFailed", null);
        FakeStateStore stateStore = new() { State = state };
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.TimedOut),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("stop");

        Assert.Equal(3, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("alwaysOnRestoreFailed", Assert.Single(report.Problems).Code);
        Assert.Equal("restoreFailed", report.AlwaysOn?.State);
        Assert.NotNull(stateStore.State);
        Assert.Equal(0, process.CloseCount);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("start")]
    public void ExitingMarkerWithRunningExactProcessBlocksStatusAndStart(string action)
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabState state = State(paths, gamePath);
        WriteExitingMarker(paths, state);
        FakeStateStore stateStore = new() { State = state };
        FakeBuilder builder = new();
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            builder,
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute(action);

        Assert.Equal(3, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("running", report.State);
        Assert.Equal("cleanStopIncomplete", Assert.Single(report.Problems).Code);
        Assert.Equal("exiting", report.AlwaysOn?.State);
        Assert.NotNull(stateStore.State);
        Assert.Equal(0, builder.CallCount);
        Assert.Equal(0, process.StartCount);
        Assert.Equal(0, process.WaitCount);
        Assert.Equal(0, process.CloseCount);
    }

    [Fact]
    public void RepeatedStartReusesTheRunningOwnedProcess()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        FakeStateStore stateStore = new() { State = State(paths, gamePath) };
        FakeBuilder builder = new();
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            builder,
            process,
            Ready(gamePath));

        LiveLabCommandResult result = service.Execute("start");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("running", Assert.IsType<LiveLabReport>(result.Report).State);
        Assert.Equal(0, builder.CallCount);
        Assert.Equal(0, process.StartCount);
    }

    [Fact]
    public void FailedExitedStateCleanupPreservesRestoreProofForRetry()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabState exitedState = State(paths, gamePath);
        WriteExitingMarker(paths, exitedState);
        FakeStateStore stateStore = new()
        {
            State = exitedState,
            DeleteFailuresRemaining = 1,
        };
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Exited),
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath));

        LiveLabCommandResult failedCleanup = service.Execute("start");

        Assert.Equal(3, failedCleanup.ExitCode);
        Assert.Equal(
            "runtimeCleanupFailed",
            Assert.Single(Assert.IsType<LiveLabReport>(failedCleanup.Report).Problems).Code);
        Assert.NotNull(stateStore.State);
        Assert.True(File.Exists(paths.StatusPath));
        Assert.Equal(0, process.StartCount);

        LiveLabCommandResult retried = service.Execute("start");

        Assert.Equal(0, retried.ExitCode);
        Assert.Equal("running", Assert.IsType<LiveLabReport>(retried.Report).State);
        Assert.NotNull(stateStore.State);
        Assert.False(File.Exists(paths.StatusPath));
        Assert.Equal(1, process.StartCount);
    }

    [Fact]
    public void PendingAlwaysOnBecomesAControlledFailureAfterStartupGrace()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = System.IO.Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        FakeStateStore stateStore = new() { State = State(paths, gamePath) };
        FakeProcessHost process = new()
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath),
            StartedAt.AddSeconds(31));

        LiveLabCommandResult result = service.Execute("status");

        Assert.Equal(3, result.ExitCode);
        LiveLabProblem problem = Assert.Single(
            Assert.IsType<LiveLabReport>(result.Report).Problems);
        Assert.Equal("alwaysOnPending", problem.Code);
    }

    [Fact]
    public void MalformedStateIsReportedWithoutInspectingAnyProcess()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        File.WriteAllText(paths.StatePath, "{ not valid json }");

        LiveLabCommandResult result = LiveLabService.Execute(
            "status",
            temporary.Path,
            () => GameInstallationDiscovery.Inspect([]));

        Assert.Equal(3, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal("labOperationFailed", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void TestSaveActionRetriesOneInvalidStatusSnapshotThenRunsTheSameLifecycle()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        var stateStore = new FakeStateStore();
        var fixtureStore = new FakeTestSaveStore(paths);
        var process = new FakeProcessHost
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.Exited),
        };
        var invalidMarkersWritten = 0;
        process.BeforeStartReturn = () =>
        {
            invalidMarkersWritten++;
            File.WriteAllText(paths.StatusPath, "{ transient replacement read }");
        };

        void PublishTerminal(string lifecyclePhase)
        {
            LiveLabState state = Assert.IsType<LiveLabState>(stateStore.State);
            TestSaveLaunchState launch = Assert.IsType<TestSaveLaunchState>(state.TestSave);
            string terminal = launch.Mode == TestSaveContract.CreateMode ? "created" : "passed";
            WriteStatusMarker(
                paths,
                state,
                lifecyclePhase,
                pauseWhenOutOfFocus: lifecyclePhase == "active" ? false : true,
                new TestSaveStatusMarker(
                    TestSaveContract.SchemaVersion,
                    launch.Mode,
                    terminal,
                    launch.Identity.FixtureId,
                    launch.Identity.SaveId,
                    IdentityVerified: true,
                    WaitedTicks: launch.Mode == TestSaveContract.ScenarioMode ? 120 : 0,
                    Message: "simulated terminal fixture",
                    launch.ScenarioLogPath));
        }

        process.BeforeWaitReturn = () => PublishTerminal("exiting");
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath),
            testSaveStore: fixtureStore,
            delay: _ => PublishTerminal("active"));

        LiveLabCommandResult result = service.Execute("test-save");

        Assert.Equal(0, result.ExitCode);
        TestSaveWorkflowReport report = Assert.IsType<TestSaveWorkflowReport>(result.Report);
        Assert.Equal("passed", report.State);
        Assert.Equal(120, report.ObservedTicks);
        Assert.Equal(2, process.StartCount);
        Assert.Equal(2, invalidMarkersWritten);
        Assert.Equal(2, process.WaitCount);
        Assert.Equal(2, fixtureStore.PrepareCount);
        Assert.Equal(2, fixtureStore.CompleteCount);
        Assert.Equal(0, fixtureStore.AbortCount);
        Assert.Null(stateStore.State);
        Assert.Equal(8, report.LogPaths.Count);
        Assert.Equal(TestSaveContract.ScenarioMode, fixtureStore.CompletedModes[^1]);
        Assert.Equal(
            TestSaveContract.ScenarioMode,
            process.Specification?.Environment["SDVKIT_TEST_SAVE_MODE"]);
        Assert.Equal(
            fixtureStore.Identity.SaveId,
            process.Specification?.Environment["SDVKIT_TEST_SAVE_ID"]);
    }

    [Fact]
    public void TestSaveActionRefusesAnExistingLabBeforePreparingAnyFixture()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        var fixtureStore = new FakeTestSaveStore(paths);
        LiveLabService service = Service(
            paths,
            new FakeStateStore { State = State(paths, gamePath) },
            new FakeBuilder(),
            new FakeProcessHost(),
            Ready(gamePath),
            testSaveStore: fixtureStore);

        LiveLabCommandResult result = service.Execute("test-save");

        Assert.Equal(3, result.ExitCode);
        TestSaveWorkflowReport report = Assert.IsType<TestSaveWorkflowReport>(result.Report);
        Assert.Equal("labNotStopped", Assert.Single(report.Problems).Code);
        Assert.Equal(0, fixtureStore.PrepareCount);
    }

    [Fact]
    public void TestSaveStartExceptionAbortsThePreparedFixtureWithoutLaunching()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        var fixtureStore = new FakeTestSaveStore(paths);
        var builder = new FakeBuilder
        {
            ExceptionToThrow = new IOException("simulated build exception"),
        };
        var process = new FakeProcessHost();
        var stateStore = new FakeStateStore();
        LiveLabService service = Service(
            paths,
            stateStore,
            builder,
            process,
            Ready(gamePath),
            testSaveStore: fixtureStore);

        LiveLabCommandResult result = service.Execute("test-save");

        Assert.Equal(3, result.ExitCode);
        TestSaveWorkflowReport report = Assert.IsType<TestSaveWorkflowReport>(result.Report);
        Assert.Contains(report.Problems, problem => problem.Code == "testSaveRunFailed");
        Assert.Equal(1, fixtureStore.PrepareCount);
        Assert.Equal(1, fixtureStore.AbortCount);
        Assert.Equal(0, process.StartCount);
        Assert.Null(stateStore.State);
    }

    [Fact]
    public void UnverifiedChildWithUnconfirmedAbortKeepsTheFixtureMounted()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        var fixtureStore = new FakeTestSaveStore(paths);
        var process = new FakeProcessHost
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.AbortUnconfirmed,
                Error: "simulated unverified child may still be running"),
        };
        LiveLabService service = Service(
            paths,
            new FakeStateStore(),
            new FakeBuilder(),
            process,
            Ready(gamePath),
            testSaveStore: fixtureStore);

        LiveLabCommandResult result = service.Execute("test-save");

        Assert.Equal(3, result.ExitCode);
        TestSaveWorkflowReport report = Assert.IsType<TestSaveWorkflowReport>(result.Report);
        Assert.Contains(
            report.Problems,
            problem => problem.Code == "unverifiedChildAbortUnconfirmed");
        Assert.Contains(
            report.Problems,
            problem => problem.Code == "testSaveCleanupDeferred");
        Assert.Equal(1, fixtureStore.PrepareCount);
        Assert.Equal(0, fixtureStore.AbortCount);
    }

    [Fact]
    public void GameSideTestSaveFailureUsesStopAndAbortThenClearsOwnership()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        var stateStore = new FakeStateStore();
        var fixtureStore = new FakeTestSaveStore(paths);
        var process = new FakeProcessHost
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.Exited),
        };

        void PublishFailure(string lifecyclePhase)
        {
            LiveLabState state = Assert.IsType<LiveLabState>(stateStore.State);
            TestSaveLaunchState launch = Assert.IsType<TestSaveLaunchState>(state.TestSave);
            WriteStatusMarker(
                paths,
                state,
                lifecyclePhase,
                pauseWhenOutOfFocus: lifecyclePhase == "active" ? false : true,
                new TestSaveStatusMarker(
                    TestSaveContract.SchemaVersion,
                    launch.Mode,
                    "failed",
                    launch.Identity.FixtureId,
                    launch.Identity.SaveId,
                    IdentityVerified: false,
                    WaitedTicks: 0,
                    Message: "simulated game-side failure",
                    launch.ScenarioLogPath));
        }

        process.BeforeWaitReturn = () => PublishFailure("exiting");
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath),
            testSaveStore: fixtureStore,
            delay: _ => PublishFailure("active"));

        LiveLabCommandResult result = service.Execute("test-save");

        Assert.Equal(3, result.ExitCode);
        TestSaveWorkflowReport report = Assert.IsType<TestSaveWorkflowReport>(result.Report);
        Assert.Contains(report.Problems, problem => problem.Code == "testSaveFailed");
        Assert.Equal(1, fixtureStore.AbortCount);
        Assert.Equal(0, fixtureStore.CompleteCount);
        Assert.Equal(1, process.WaitCount);
        Assert.Null(stateStore.State);
    }

    [Fact]
    public void MissingProjectLocalScenarioLogCannotProduceAGreenWorkflow()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = temporary.WriteFile("game/.keep");
        gamePath = Path.GetDirectoryName(gamePath)!;
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        var stateStore = new FakeStateStore();
        var fixtureStore = new FakeTestSaveStore(paths)
        {
            ScenarioLogArchived = false,
        };
        var process = new FakeProcessHost
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.Exited),
        };

        void PublishCreated(string lifecyclePhase)
        {
            LiveLabState state = Assert.IsType<LiveLabState>(stateStore.State);
            TestSaveLaunchState launch = Assert.IsType<TestSaveLaunchState>(state.TestSave);
            WriteStatusMarker(
                paths,
                state,
                lifecyclePhase,
                pauseWhenOutOfFocus: lifecyclePhase == "active" ? false : true,
                new TestSaveStatusMarker(
                    TestSaveContract.SchemaVersion,
                    launch.Mode,
                    "created",
                    launch.Identity.FixtureId,
                    launch.Identity.SaveId,
                    IdentityVerified: true,
                    WaitedTicks: 0,
                    Message: "simulated created fixture",
                    launch.ScenarioLogPath));
        }

        process.BeforeWaitReturn = () => PublishCreated("exiting");
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            Ready(gamePath),
            testSaveStore: fixtureStore,
            delay: _ => PublishCreated("active"));

        LiveLabCommandResult result = service.Execute("test-save");

        Assert.Equal(3, result.ExitCode);
        TestSaveWorkflowReport report = Assert.IsType<TestSaveWorkflowReport>(result.Report);
        Assert.Contains(
            report.Problems,
            problem => problem.Code == "testSaveScenarioLogMissing");
        Assert.Equal(1, fixtureStore.CompleteCount);
        Assert.Null(stateStore.State);
    }

    private static LiveLabService Service(
        LiveLabPaths paths,
        FakeStateStore stateStore,
        FakeBuilder builder,
        FakeProcessHost process,
        DoctorReport doctor,
        DateTimeOffset? nowUtc = null,
        ITestSaveFixtureStore? testSaveStore = null,
        Action<TimeSpan>? delay = null)
    {
        return new LiveLabService(
            paths,
            stateStore,
            builder,
            process,
            () => doctor,
            () => nowUtc ?? StartedAt.AddSeconds(10),
            () => LaunchId,
            testSaveStore,
            delay);
    }

    private static DoctorReport Ready(string gamePath) =>
        new(1, DoctorReport.Ready, [new DetectedInstallation(gamePath)]);

    private static OwnedProcessIdentity Identity(string gamePath) =>
        new(
            4242,
            StartedAt,
            System.IO.Path.Combine(gamePath, "StardewModdingAPI.exe"));

    private static LiveLabState State(LiveLabPaths paths, string gamePath) =>
        new(
            LiveLabState.CurrentSchemaVersion,
            LiveLabState.SingleTopology,
            LaunchId,
            Identity(gamePath),
            paths.ModsPath,
            paths.StatusPath,
            paths.StopRequestPath);

    private static void WriteExitingMarker(LiveLabPaths paths, LiveLabState state)
    {
        WriteStatusMarker(paths, state, "exiting", true);
    }

    private static void WriteStatusMarker(
        LiveLabPaths paths,
        LiveLabState state,
        string phase,
        bool? pauseWhenOutOfFocus,
        TestSaveStatusMarker? testSave = null)
    {
        paths.EnsureDirectories();
        var marker = new AlwaysOnStatusMarker(
            1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            phase,
            600,
            IsActive: false,
            PauseWhenOutOfFocus: pauseWhenOutOfFocus,
            StartedAt.AddSeconds(10),
            testSave);
        File.WriteAllText(
            paths.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    private sealed class FakeStateStore : ILiveLabStateStore
    {
        public LiveLabState? State { get; set; }

        public int VerifyWritableCount { get; private set; }

        public int WriteCount { get; private set; }

        public int WriteFailuresRemaining { get; set; }

        public int DeleteFailuresRemaining { get; set; }

        public LiveLabState? Read() => State;

        public void VerifyWritable()
        {
            VerifyWritableCount++;
        }

        public void Write(LiveLabState state)
        {
            WriteCount++;
            if (WriteFailuresRemaining > 0)
            {
                WriteFailuresRemaining--;
                throw new IOException("simulated ownership write failure");
            }

            State = state;
        }

        public void Delete()
        {
            if (DeleteFailuresRemaining > 0)
            {
                DeleteFailuresRemaining--;
                throw new IOException("simulated ownership delete failure");
            }

            State = null;
        }
    }

    private sealed class FakeBuilder : IAlwaysOnBuilder
    {
        public int CallCount { get; private set; }

        public string? GamePath { get; private set; }

        public Exception? ExceptionToThrow { get; init; }

        public AlwaysOnBuildResult BuildAndInstall(string gamePath, LiveLabPaths paths)
        {
            CallCount++;
            GamePath = gamePath;
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return new AlwaysOnBuildResult(
                true,
                System.IO.Path.Combine(paths.BuildPath, "always-on-build.log"),
                null);
        }
    }

    private sealed class FakeProcessHost : ILabProcessHost
    {
        public LabProcessStartResult StartResult { get; set; } =
            new(LabProcessStartStatus.Failed, Error: "not configured");

        public LabProcessInspectResult InspectResult { get; set; } =
            new(LabProcessInspectStatus.Exited);

        public LabProcessCloseResult CloseResult { get; set; } =
            new(LabProcessCloseStatus.CloseRequestFailed);

        public LabProcessWaitResult WaitResult { get; set; } =
            new(LabProcessWaitStatus.TimedOut);

        public int StartCount { get; private set; }

        public int CloseCount { get; private set; }

        public int WaitCount { get; private set; }

        public LabProcessStartSpec? Specification { get; private set; }

        public OwnedProcessIdentity? ClosedIdentity { get; private set; }

        public OwnedProcessIdentity? WaitedIdentity { get; private set; }

        public Action? BeforeWaitReturn { get; set; }

        public Action? BeforeStartReturn { get; set; }

        public LabProcessStartResult Start(LabProcessStartSpec specification)
        {
            StartCount++;
            Specification = specification;
            BeforeStartReturn?.Invoke();
            return StartResult;
        }

        public LabProcessInspectResult Inspect(OwnedProcessIdentity expected)
        {
            return InspectResult;
        }

        public LabProcessWaitResult WaitForExit(
            OwnedProcessIdentity expected,
            TimeSpan timeout)
        {
            WaitCount++;
            WaitedIdentity = expected;
            BeforeWaitReturn?.Invoke();
            return WaitResult;
        }

        public LabProcessCloseResult RequestCloseAndWait(
            OwnedProcessIdentity expected,
            TimeSpan timeout)
        {
            CloseCount++;
            ClosedIdentity = expected;
            return CloseResult;
        }
    }

    private sealed class FakeTestSaveStore : ITestSaveFixtureStore
    {
        private readonly LiveLabPaths _paths;

        public FakeTestSaveStore(LiveLabPaths paths)
        {
            _paths = paths;
            Identity = new TestSaveIdentity(
                TestSaveContract.SchemaVersion,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                123456789L,
                "SDVKit_123456789",
                TestSaveContract.PlayerName,
                TestSaveContract.FarmName,
                TestSaveContract.FavoriteThing);
        }

        public TestSaveIdentity Identity { get; }

        public int PrepareCount { get; private set; }

        public int CompleteCount { get; private set; }

        public int AbortCount { get; private set; }

        public List<string> CompletedModes { get; } = [];

        public bool ScenarioLogArchived { get; init; } = true;

        public TestSavePreparation PrepareForStart()
        {
            string mode = PrepareCount++ == 0
                ? TestSaveContract.CreateMode
                : TestSaveContract.ScenarioMode;
            return new TestSavePreparation(new TestSaveLaunchState(
                mode,
                Identity,
                Path.Combine(_paths.ProjectRoot, "fake-saves", Identity.SaveId),
                _paths.TestSaveWorkPath,
                _paths.TestSaveScenarioLogPath));
        }

        public TestSaveCleanupResult CompleteStopped(
            TestSaveLaunchState launch,
            string launchId)
        {
            CompleteCount++;
            CompletedModes.Add(launch.Mode);
            return new TestSaveCleanupResult(
            [
                Path.Combine(_paths.TestSaveRoot, $"{launchId}.{launch.Mode}.stdout.log"),
                Path.Combine(_paths.TestSaveRoot, $"{launchId}.{launch.Mode}.stderr.log"),
                Path.Combine(_paths.TestSaveRoot, $"{launchId}.{launch.Mode}.status.json"),
                Path.Combine(_paths.TestSaveRoot, $"{launchId}.{launch.Mode}.scenario.log"),
            ],
            ScenarioLogArchived);
        }

        public TestSaveCleanupResult AbortStopped(
            TestSaveLaunchState launch,
            string launchId)
        {
            AbortCount++;
            return new TestSaveCleanupResult([], ScenarioLogArchived: false);
        }
    }
}
