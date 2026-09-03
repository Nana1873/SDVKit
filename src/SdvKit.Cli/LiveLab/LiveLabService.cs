using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed record LiveLabProblem(string Code, string Message);

internal sealed record LiveLabReport(
    int SchemaVersion,
    string Topology,
    string State,
    string? LaunchId,
    int? ProcessId,
    DateTimeOffset? ProcessStartTimeUtc,
    string? ExecutablePath,
    string? ModsPath,
    string? BuildLogPath,
    AlwaysOnStatusReport? AlwaysOn,
    IReadOnlyList<LiveLabProblem> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? TestSaveLogPaths = null);

internal sealed record TestSaveWorkflowReport(
    int SchemaVersion,
    string Topology,
    string State,
    string? FixtureId,
    string? SaveId,
    string Scenario,
    int RequiredTicks,
    int? ObservedTicks,
    string BaselinePath,
    IReadOnlyList<string> LogPaths,
    IReadOnlyList<LiveLabProblem> Problems,
    IReadOnlyList<string> Warnings);

internal sealed class LiveLabService
{
    private sealed record TestSaveWaitResult(
        bool Succeeded,
        TestSaveStatusReport? Status,
        LiveLabProblem? Problem);

    private const int Success = 0;
    private const int OperationFailed = 3;

    private static readonly TimeSpan CleanStopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupRollbackSignalGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AlwaysOnStartupGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TestSaveTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TestSavePollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly string[] IsolationWarnings =
    [
        "The controlled process resolves Stardew's own preferences, saves, startup preferences, and standard SMAPI logs to a project-owned data root below .sdvkit; SDVKit does not select or modify their normal counterparts or the normal Mods directory.",
        "This is process-level data isolation, not a Windows sandbox; tested mods and external services can still access shared machine resources.",
    ];

    private static readonly string[] TestSaveWarnings =
    [
        "The controlled process resolves Stardew's own preferences, saves, startup preferences, and standard SMAPI logs to a project-owned data root below .sdvkit; SDVKit does not select personal data or the normal Mods directory.",
        "Only one exact SDVKit-owned direct-child save junction is exposed inside that project-owned data root.",
    ];

    private static readonly string[] RestoreUnconfirmedWarnings =
    [
        "AlwaysOn could not confirm restoration of the isolated profile options before normal exit. The exact lab process still stopped safely; the next start reapplies the required lab values.",
    ];

    private static readonly string[] TestSaveEnvironmentNames =
    [
        "SDVKIT_TEST_SAVE_MODE",
        "SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID",
        "SDVKIT_TEST_SAVE_FIXTURE_ID",
        "SDVKIT_TEST_SAVE_UNIQUE_GAME_ID",
        "SDVKIT_TEST_SAVE_ID",
        "SDVKIT_TEST_SAVE_PLAYER_NAME",
        "SDVKIT_TEST_SAVE_FARM_NAME",
        "SDVKIT_TEST_SAVE_FAVORITE_THING",
        "SDVKIT_TEST_SAVE_LOG_PATH",
    ];

    private static readonly string[] NetworkTwoEnvironmentNames =
    [
        "SDVKIT_NETWORK_TWO_ROLE",
        "SDVKIT_NETWORK_TWO_BUILD_ID",
        "SDVKIT_NETWORK_TWO_FIXTURE_ID",
        "SDVKIT_NETWORK_TWO_SAVE_ID",
        "SDVKIT_NETWORK_TWO_LOG_PATH",
        "SDVKIT_NETWORK_TWO_EXPECTED_FARMHAND_ID",
    ];

    private static readonly string[] ProjectModEnvironmentNames =
    [
        "SDVKIT_PROJECT_MOD_UNIQUE_ID",
        "SDVKIT_PROJECT_MOD_VERSION",
        "SDVKIT_PROJECT_MOD_BUILD_IDENTITY",
        "SDVKIT_PROJECT_REVIEW",
    ];

    private readonly LiveLabPaths _paths;
    private readonly ILiveLabStateStore _stateStore;
    private readonly IAlwaysOnBuilder _alwaysOnBuilder;
    private readonly ILabProcessHost _processHost;
    private readonly Func<DoctorReport> _discoverInstallations;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _createLaunchId;
    private readonly ITestSaveFixtureStore _testSaveStore;
    private readonly Action<TimeSpan> _delay;
    private readonly string _reportTopology;
    private IReadOnlyList<string> _lastTestSaveLogPaths = [];

    internal AlwaysOnStatusReport? LastAlwaysOn { get; private set; }

    internal LiveLabService(
        LiveLabPaths paths,
        ILiveLabStateStore stateStore,
        IAlwaysOnBuilder alwaysOnBuilder,
        ILabProcessHost processHost,
        Func<DoctorReport> discoverInstallations,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? createLaunchId = null,
        ITestSaveFixtureStore? testSaveStore = null,
        Action<TimeSpan>? delay = null,
        string reportTopology = LiveLabState.SingleTopology)
    {
        _paths = paths;
        _stateStore = stateStore;
        _alwaysOnBuilder = alwaysOnBuilder;
        _processHost = processHost;
        _discoverInstallations = discoverInstallations;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _createLaunchId = createLaunchId ?? (() => Guid.NewGuid().ToString("N"));
        _testSaveStore = testSaveStore ?? new TestSaveFixtureStore(paths);
        _delay = delay ?? Thread.Sleep;
        _reportTopology = reportTopology;
    }

    public static LiveLabCommandResult Execute(
        string action,
        string projectRoot,
        Func<DoctorReport> discoverInstallations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(projectRoot);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Result(
                OperationFailed,
                new LiveLabReport(
                    1,
                    LiveLabState.SingleTopology,
                    "blocked",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [Problem("labPathInvalid", exception.Message)],
                    IsolationWarnings));
        }

        var service = new LiveLabService(
            paths,
            new JsonLiveLabStateStore(paths.StatePath),
            new AlwaysOnBuilder(),
            new WindowsLabProcessHost(),
            discoverInstallations);
        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(paths.ProjectRoot);
            if (operationLock is null)
            {
                return Result(
                    OperationFailed,
                    service.Report(
                        "blocked",
                        null,
                        [Problem(
                            "labBusy",
                            "Another live-lab operation is still running for this project.")]));
            }

            return service.Execute(action);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Result(
                OperationFailed,
                service.Report(
                    "blocked",
                    null,
                    [Problem("labOperationFailed", exception.Message)]));
        }
    }

    internal LiveLabCommandResult Execute(string action)
    {
        return action switch
        {
            "start" => Start(),
            "status" => Status(),
            "stop" => Stop(),
            "test-save" => TestSave(projectMod: null),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    internal LiveLabCommandResult StartNetwork(
        TestSaveLaunchState? testSave,
        NetworkTwoLaunchState networkTwo,
        AlwaysOnBuildResult preparedBuild,
        ProjectModLaunchState? projectMod = null,
        bool interactiveConsole = false)
    {
        ArgumentNullException.ThrowIfNull(networkTwo);
        ArgumentNullException.ThrowIfNull(preparedBuild);
        return Start(
            testSave,
            networkTwo,
            preparedBuild,
            projectMod,
            interactiveConsole);
    }

    internal LiveLabCommandResult RunProjectTestSave(ProjectModLaunchState projectMod)
    {
        ArgumentNullException.ThrowIfNull(projectMod);
        projectMod.Validate();
        return TestSave(projectMod);
    }

    internal LiveLabCommandResult StartProjectReview(
        ProjectModLaunchState projectMod,
        bool useTestSave = false)
    {
        ArgumentNullException.ThrowIfNull(projectMod);
        projectMod.Validate();
        if (!useTestSave)
        {
            return Start(projectMod: projectMod, interactiveConsole: true);
        }

        TestSaveLaunchState testSave;
        try
        {
            testSave = _testSaveStore.PrepareReviewForStart(
                resetFromBaseline: false).LaunchState;
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                "blocked",
                null,
                "testSavePreparationFailed",
                exception.Message);
        }

        LiveLabCommandResult started;
        try
        {
            started = Start(
                testSave: testSave,
                projectMod: projectMod,
                interactiveConsole: true);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            started = Failure(
                "blocked",
                null,
                "testSaveStartFailed",
                exception.Message);
        }

        return started.ExitCode == Success
            ? started
            : CleanupFailedProjectReviewStart(testSave, started);
    }

    internal LiveLabCommandResult StatusProjectReview() => Status();

    internal LiveLabCommandResult StopProjectReview() => Stop();

    internal LiveLabCommandResult FinalizeExitedProjectReview()
    {
        LiveLabState? state = ReadState();
        if (state is null)
        {
            return Result(Success, Report("stopped", null, []));
        }

        if (!string.Equals(
                state.Topology,
                LiveLabState.SingleTopology,
                StringComparison.Ordinal)
            || state.ProjectMod is null
            || state.NetworkTwo is not null
            || (state.TestSave is not null
                && !string.Equals(
                    state.TestSave.Mode,
                    TestSaveContract.ReviewMode,
                    StringComparison.Ordinal)))
        {
            return Failure(
                "blocked",
                state,
                "projectReviewStateMismatch",
                "Only a retained single-player project-review process, optionally bound to its exact review fixture, can be finalized without an AlwaysOn exit marker.");
        }

        LabProcessInspectResult observation =
            _processHost.Inspect(state.OwnedProcessIdentity);
        if (observation.Status == LabProcessInspectStatus.Running)
        {
            return RunningStatus(state);
        }

        if (observation.Status == LabProcessInspectStatus.IdentityMismatch)
        {
            return Failure(
                "ownershipMismatch",
                state,
                "processIdentityMismatch",
                observation.Error ?? "The PID no longer identifies the owned review process.");
        }

        if (observation.Status == LabProcessInspectStatus.Unreadable)
        {
            return Failure(
                "unreadable",
                state,
                "processUnreadable",
                observation.Error ?? "The owned review process identity could not be read.");
        }

        if (observation.Status != LabProcessInspectStatus.Exited)
        {
            return Failure(
                "unreadable",
                state,
                "processUnreadable",
                "The owned review process returned an unknown observation state.");
        }

        AlwaysOnStatusReport alwaysOn = ReadAlwaysOn(state);
        bool projectModSucceeded = ProjectModLoadSucceeded(alwaysOn, state.ProjectMod);
        bool testSaveSucceeded = state.TestSave is null
            || (alwaysOn.TestSave is not null
                && string.Equals(
                    alwaysOn.TestSave.State,
                    "ready",
                    StringComparison.Ordinal)
                && string.Equals(
                    alwaysOn.TestSave.Mode,
                    TestSaveContract.ReviewMode,
                    StringComparison.Ordinal)
                && string.Equals(
                    alwaysOn.TestSave.Phase,
                    "passed",
                    StringComparison.Ordinal)
                && alwaysOn.TestSave.IdentityVerified == true);
        try
        {
            if (state.TestSave is not null)
            {
                TestSaveCleanupResult cleanup = _testSaveStore.AbortStopped(
                    state.TestSave,
                    state.LaunchId);
                _lastTestSaveLogPaths = cleanup.ArchivedLogPaths;
            }

            File.Delete(state.StopRequestPath);
            _stateStore.Delete();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                "exited",
                state,
                "runtimeCleanupFailed",
                $"The exact project-review process exited, but its fixture binding or retained ownership record could not be safely released: {exception.Message}",
                alwaysOn: alwaysOn);
        }

        if (!projectModSucceeded)
        {
            return Failure(
                "stopped",
                state,
                "projectModLoadUnconfirmed",
                alwaysOn.ProjectMod?.Message
                    ?? "SMAPI did not confirm the expected project mod identity and version as loaded before the review process exited.",
                alwaysOn: alwaysOn);
        }

        if (!testSaveSucceeded)
        {
            return Failure(
                "stopped",
                state,
                "testSaveIncomplete",
                alwaysOn.TestSave?.Message
                    ?? "The exact review fixture did not reach its verified loaded phase before the process exited.",
                alwaysOn: alwaysOn);
        }

        return Result(
            Success,
            Report("stopped", state, [], alwaysOn: alwaysOn));
    }

    internal LiveLabCommandResult StatusNetwork() => Status();

    internal LiveLabCommandResult StopNetwork()
    {
        LiveLabCommandResult stopped = Stop();
        stopped = ReleaseExitedNetworkStateWithoutRestore(stopped);
        if (stopped.ExitCode == Success
            || stopped.Report is not LiveLabReport report
            || !report.Problems.Any(problem => string.Equals(
                problem.Code,
                "cleanStopTimedOut",
                StringComparison.Ordinal)))
        {
            return stopped;
        }

        LiveLabState? state = ReadState();
        if (state is null || state.NetworkTwo is null)
        {
            return stopped;
        }

        LabProcessCloseResult closed = _processHost.RequestCloseAndWait(
            state.OwnedProcessIdentity,
            CleanStopTimeout);
        if (closed.Status is not (LabProcessCloseStatus.Closed
            or LabProcessCloseStatus.AlreadyExited))
        {
            return Failure(
                closed.Status == LabProcessCloseStatus.IdentityMismatch
                    ? "ownershipMismatch"
                    : "running",
                state,
                "networkCleanCloseFailed",
                $"The normal network-2 stop timed out and the exact-process window-close fallback did not complete ({DescribeCloseResult(closed)}).",
                alwaysOn: ReadAlwaysOn(state));
        }

        return CompleteControlledStop(state, ReadAlwaysOn(state));
    }

    private LiveLabCommandResult ReleaseExitedNetworkStateWithoutRestore(
        LiveLabCommandResult stopped)
    {
        if (stopped.Report is not LiveLabReport report
            || !report.Problems.Any(problem => string.Equals(
                problem.Code,
                "cleanStopNotConfirmed",
                StringComparison.Ordinal)))
        {
            return stopped;
        }

        LiveLabState? state = ReadState();
        if (state?.NetworkTwo is null
            || _processHost.Inspect(state.OwnedProcessIdentity).Status
                != LabProcessInspectStatus.Exited)
        {
            return stopped;
        }

        try
        {
            File.Delete(state.StopRequestPath);
            _stateStore.Delete();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                "exited",
                state,
                "runtimeCleanupFailed",
                $"The exact network-2 process exited without restore proof, and its retained ownership record could not be released: {exception.Message}",
                alwaysOn: report.AlwaysOn);
        }

        return Failure(
            "stopped",
            state,
            "networkRestoreUnconfirmed",
            "The exact network-2 process exited without an AlwaysOn restore marker. Its ownership record was released for bounded recovery, but this run is not accepted as a clean stop.",
            alwaysOn: report.AlwaysOn);
    }

    private LiveLabCommandResult TestSave(ProjectModLaunchState? projectMod)
    {
        var warnings = TestSaveWarnings.ToList();
        LiveLabState? existing = ReadState();
        if (existing is not null)
        {
            return TestSaveWorkflowFailure(
                existing.TestSave?.Identity,
                [],
                "labNotStopped",
                "The disposable test-save workflow requires the existing single lab to be stopped first.",
                warnings);
        }

        var logs = new List<string>();
        TestSaveIdentity? identity = null;
        for (var run = 0; run < 2; run++)
        {
            _lastTestSaveLogPaths = [];
            TestSavePreparation preparation;
            try
            {
                preparation = _testSaveStore.PrepareForStart();
                identity = preparation.LaunchState.Identity;
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                return TestSaveWorkflowFailure(
                    identity,
                    logs,
                    "testSavePreparationFailed",
                    exception.Message,
                    warnings);
            }

            TestSaveLaunchState launch = preparation.LaunchState;
            try
            {
                LiveLabCommandResult started = Start(launch, projectMod: projectMod);
                if (started.ExitCode != Success)
                {
                    List<LiveLabProblem> problems =
                        AssertLiveLabProblems(started, "testSaveStartFailed");
                    TryCleanupPreparedRun(
                        launch,
                        (LiveLabReport)started.Report,
                        logs,
                        problems,
                        warnings);
                    return TestSaveWorkflowFailure(identity, logs, problems, warnings);
                }

                TestSaveWaitResult waited = WaitForTestSave(launch);
                if (!waited.Succeeded)
                {
                    var problems = new List<LiveLabProblem>();
                    if (waited.Problem is not null)
                    {
                        problems.Add(waited.Problem);
                    }

                    LiveLabCommandResult stoppedAfterFailure = Stop();
                    logs.AddRange(_lastTestSaveLogPaths);
                    AddReportWarnings(stoppedAfterFailure, warnings);
                    if (stoppedAfterFailure.ExitCode != Success)
                    {
                        problems.AddRange(AssertLiveLabProblems(
                            stoppedAfterFailure,
                            "testSaveCleanupFailed"));
                    }

                    return TestSaveWorkflowFailure(identity, logs, problems, warnings);
                }

                LiveLabCommandResult stopped = Stop();
                logs.AddRange(_lastTestSaveLogPaths);
                AddReportWarnings(stopped, warnings);
                if (stopped.ExitCode != Success)
                {
                    return TestSaveWorkflowFailure(
                        identity,
                        logs,
                        AssertLiveLabProblems(stopped, "testSaveCleanupFailed"),
                        warnings);
                }

                if (string.Equals(
                        launch.Mode,
                        TestSaveContract.ScenarioMode,
                        StringComparison.Ordinal))
                {
                    return Result(
                        Success,
                        new TestSaveWorkflowReport(
                            1,
                            LiveLabState.SingleTopology,
                            "passed",
                            identity.FixtureId,
                            identity.SaveId,
                            "hud-tick-smoke",
                            TestSaveContract.RequiredScenarioTicks,
                            waited.Status?.WaitedTicks,
                            _paths.TestSaveBaselinePath,
                            logs,
                            [],
                            warnings));
                }
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                var problems = new List<LiveLabProblem>
                {
                    Problem("testSaveRunFailed", exception.Message),
                };
                TryCleanupPreparedRun(launch, null, logs, problems, warnings);
                return TestSaveWorkflowFailure(identity, logs, problems, warnings);
            }
        }

        return TestSaveWorkflowFailure(
            identity,
            logs,
            "testSaveScenarioMissing",
            "The fixture was created, but its exact baseline scenario did not run.",
            warnings);
    }

    private TestSaveWaitResult WaitForTestSave(TestSaveLaunchState expected)
    {
        DateTimeOffset startedAt = _utcNow().ToUniversalTime();
        DateTimeOffset deadline = startedAt + TestSaveTimeout;
        DateTimeOffset alwaysOnSettleDeadline = startedAt + AlwaysOnStartupGrace;
        string expectedPhase = string.Equals(
            expected.Mode,
            TestSaveContract.CreateMode,
            StringComparison.Ordinal)
            ? "created"
            : "passed";
        bool retriedInvalidStatus = false;
        while (_utcNow().ToUniversalTime() <= deadline)
        {
            LiveLabCommandResult result = Status();
            LiveLabReport report = (LiveLabReport)result.Report;
            TestSaveStatusReport? testSave = report.AlwaysOn?.TestSave;
            if (result.ExitCode != Success)
            {
                if (report.Problems.Count == 1
                    && string.Equals(
                        report.Problems[0].Code,
                        "alwaysOnNotApplied",
                        StringComparison.Ordinal)
                    && _utcNow().ToUniversalTime() <= alwaysOnSettleDeadline)
                {
                    _delay(TestSavePollInterval);
                    continue;
                }

                if (report.Problems.Count == 1
                    && string.Equals(
                        report.Problems[0].Code,
                        "alwaysOnInvalid",
                        StringComparison.Ordinal)
                    && !retriedInvalidStatus)
                {
                    // The writer atomically replaces this frequently updated marker.
                    // One poll can race that replacement; a repeated invalid read is
                    // still terminal, and normal lab status remains strict.
                    retriedInvalidStatus = true;
                    _delay(TestSavePollInterval);
                    continue;
                }

                LiveLabProblem problem = report.Problems.Count > 0
                    ? report.Problems[0]
                    : Problem("testSaveStatusFailed", "The exact test-save lab status failed.");
                return new TestSaveWaitResult(false, testSave, problem);
            }

            retriedInvalidStatus = false;

            if (testSave?.State is "invalid" or "mismatch" or "unexpected")
            {
                return new TestSaveWaitResult(
                    false,
                    testSave,
                    Problem(
                        "testSaveStatusMismatch",
                        $"The AlwaysOn test-save marker is {testSave.State}."));
            }

            if (testSave is not null
                && string.Equals(testSave.Phase, "failed", StringComparison.Ordinal))
            {
                return new TestSaveWaitResult(
                    false,
                    testSave,
                    Problem(
                        "testSaveFailed",
                        testSave.Message ?? "The game-side test-save workflow failed."));
            }

            if (testSave is not null
                && string.Equals(testSave.State, "ready", StringComparison.Ordinal)
                && string.Equals(testSave.Phase, expectedPhase, StringComparison.Ordinal)
                && testSave.IdentityVerified == true)
            {
                if (string.Equals(
                        expected.Mode,
                        TestSaveContract.ScenarioMode,
                        StringComparison.Ordinal)
                    && testSave.WaitedTicks < TestSaveContract.RequiredScenarioTicks)
                {
                    return new TestSaveWaitResult(
                        false,
                        testSave,
                        Problem(
                            "testSaveWaitIncomplete",
                            "The game-side scenario reported completion before 120 observed update ticks."));
                }

                return new TestSaveWaitResult(true, testSave, null);
            }

            _delay(TestSavePollInterval);
        }

        return new TestSaveWaitResult(
            false,
            null,
            Problem(
                "testSaveTimedOut",
                "The bounded game-side test-save workflow did not complete within two minutes."));
    }

    private void TryCleanupPreparedRun(
        TestSaveLaunchState launch,
        LiveLabReport? started,
        List<string> logs,
        List<LiveLabProblem> startProblems,
        List<string> warnings)
    {
        try
        {
            if (ReadState() is not null)
            {
                LiveLabCommandResult stopped = Stop();
                logs.AddRange(_lastTestSaveLogPaths);
                AddReportWarnings(stopped, warnings);
                if (stopped.ExitCode != Success)
                {
                    startProblems.AddRange(AssertLiveLabProblems(
                        stopped,
                        "testSaveCleanupFailed"));
                }

                return;
            }

            bool unverifiedChildMayBeRunning = started?.Problems.Any(problem =>
                string.Equals(
                    problem.Code,
                    "unverifiedChildAbortUnconfirmed",
                    StringComparison.Ordinal)) == true;
            if (unverifiedChildMayBeRunning
                || (started?.ProcessId is not null
                    && started.State is "blocked" or "running" or "ownershipMismatch"))
            {
                startProblems.Add(Problem(
                    "testSaveCleanupDeferred",
                    "The launched process was not confirmed stopped, so SDVKit left its exact fixture binding in place instead of mutating a possibly active save."));
                return;
            }

            string cleanupId = started?.LaunchId is not null
                && Guid.TryParseExact(started.LaunchId, "N", out _)
                ? started.LaunchId
                : Guid.NewGuid().ToString("N");
            TestSaveCleanupResult cleanup = _testSaveStore.AbortStopped(launch, cleanupId);
            logs.AddRange(cleanup.ArchivedLogPaths);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            startProblems.Add(Problem("testSaveCleanupFailed", exception.Message));
        }
    }

    private LiveLabCommandResult CleanupFailedProjectReviewStart(
        TestSaveLaunchState launch,
        LiveLabCommandResult started)
    {
        if (started.Report is not LiveLabReport report || ReadState() is not null)
        {
            return started;
        }

        var problems = report.Problems.ToList();
        bool unverifiedChildMayBeRunning = problems.Any(problem =>
            string.Equals(
                problem.Code,
                "unverifiedChildAbortUnconfirmed",
                StringComparison.Ordinal));
        if (unverifiedChildMayBeRunning
            || (report.ProcessId is not null
                && report.State is "blocked" or "running" or "ownershipMismatch"))
        {
            problems.Add(Problem(
                "testSaveCleanupDeferred",
                "The launched process was not confirmed stopped, so SDVKit retained the exact fixture binding instead of mutating a possibly active save."));
            return Result(
                OperationFailed,
                report with
                {
                    Problems = problems,
                    Warnings = TestSaveWarnings,
                });
        }

        try
        {
            string cleanupId = report.LaunchId is not null
                && Guid.TryParseExact(report.LaunchId, "N", out _)
                ? report.LaunchId
                : Guid.NewGuid().ToString("N");
            TestSaveCleanupResult cleanup = _testSaveStore.AbortStopped(
                launch,
                cleanupId);
            _lastTestSaveLogPaths = cleanup.ArchivedLogPaths;
            return Result(
                OperationFailed,
                report with
                {
                    TestSaveLogPaths = cleanup.ArchivedLogPaths,
                    Warnings = TestSaveWarnings,
                });
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            problems.Add(Problem("testSaveCleanupFailed", exception.Message));
            return Result(
                OperationFailed,
                report with
                {
                    Problems = problems,
                    Warnings = TestSaveWarnings,
                });
        }
    }

    private LiveLabCommandResult TestSaveWorkflowFailure(
        TestSaveIdentity? identity,
        IReadOnlyList<string> logs,
        string code,
        string message,
        IReadOnlyList<string> warnings) =>
        TestSaveWorkflowFailure(identity, logs, [Problem(code, message)], warnings);

    private LiveLabCommandResult TestSaveWorkflowFailure(
        TestSaveIdentity? identity,
        IReadOnlyList<string> logs,
        IReadOnlyList<LiveLabProblem> problems,
        IReadOnlyList<string> warnings)
    {
        return Result(
            OperationFailed,
            new TestSaveWorkflowReport(
                1,
                LiveLabState.SingleTopology,
                "failed",
                identity?.FixtureId,
                identity?.SaveId,
                "hud-tick-smoke",
                TestSaveContract.RequiredScenarioTicks,
                null,
                _paths.TestSaveBaselinePath,
                logs,
                problems,
                warnings));
    }

    private static List<LiveLabProblem> AssertLiveLabProblems(
        LiveLabCommandResult result,
        string fallbackCode)
    {
        if (result.Report is LiveLabReport report && report.Problems.Count > 0)
        {
            return report.Problems.ToList();
        }

        return [Problem(fallbackCode, "The live-lab operation failed without a detailed problem.")];
    }

    private static void AddReportWarnings(
        LiveLabCommandResult result,
        List<string> warnings)
    {
        if (result.Report is not LiveLabReport report)
        {
            return;
        }

        foreach (string warning in report.Warnings)
        {
            if (!warnings.Contains(warning, StringComparer.Ordinal))
            {
                warnings.Add(warning);
            }
        }
    }

    private LiveLabCommandResult Start(
        TestSaveLaunchState? testSave = null,
        NetworkTwoLaunchState? networkTwo = null,
        AlwaysOnBuildResult? preparedBuild = null,
        ProjectModLaunchState? projectMod = null,
        bool interactiveConsole = false)
    {
        projectMod?.Validate();
        if (networkTwo is not null)
        {
            networkTwo.Validate();
            bool isHost = string.Equals(
                networkTwo.Role,
                NetworkTwoContract.HostRole,
                StringComparison.Ordinal);
            if (isHost != (testSave is not null)
                || (testSave is not null
                    && (!string.Equals(
                            networkTwo.FixtureId,
                            testSave.Identity.FixtureId,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            networkTwo.SaveId,
                            testSave.Identity.SaveId,
                            StringComparison.Ordinal))))
            {
                throw new InvalidDataException(
                    "The network-2 role does not match its exact disposable fixture binding.");
            }
        }

        LiveLabState? existing = ReadState();
        if (existing is not null)
        {
            LiveLabCommandResult? retained = InspectExistingForStart(existing);
            if (retained is not null)
            {
                return retained;
            }
        }

        DoctorReport doctor = _discoverInstallations();
        if (!string.Equals(doctor.Status, DoctorReport.Ready, StringComparison.Ordinal)
            || doctor.Installations.Count != 1)
        {
            return Failure(
                "notReady",
                null,
                "installationNotReady",
                "Start requires exactly one ready Stardew Valley + SMAPI installation from doctor.");
        }

        _paths.EnsureDirectories();
        try
        {
            LabWindowPreferences.Prepare(_paths.StardewDataPath);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                "blocked",
                null,
                "labWindowPreparationFailed",
                exception.Message);
        }

        if (testSave is null)
        {
            _paths.RejectUserProfileReparsePoints();
        }
        _stateStore.VerifyWritable();
        string gamePath = doctor.Installations[0].GamePath;
        AlwaysOnBuildResult build = preparedBuild
            ?? _alwaysOnBuilder.BuildAndInstall(gamePath, _paths);
        if (!build.Succeeded)
        {
            return Failure(
                "buildFailed",
                null,
                "alwaysOnBuildFailed",
                build.Error ?? "AlwaysOn build failed.",
                build.LogPath);
        }

        File.Delete(_paths.StatusPath);
        File.Delete(_paths.StopRequestPath);
        string launchId = _createLaunchId();
        if (!Guid.TryParseExact(launchId, "N", out _))
        {
            return Failure(
                "blocked",
                null,
                "launchIdInvalid",
                "The generated launch ID is invalid.",
                build.LogPath);
        }

        string executablePath = Path.Combine(gamePath, "StardewModdingAPI.exe");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["USERPROFILE"] = _paths.UserProfilePath,
            ["APPDATA"] = _paths.RoamingAppDataPath,
            ["LOCALAPPDATA"] = _paths.LocalAppDataPath,
            ["SDVKIT_LAB_DATA_PATH"] = _paths.StardewDataPath,
            ["SDVKIT_LAB_LAUNCH_ID"] = launchId,
            ["SDVKIT_LAB_STATUS_PATH"] = _paths.StatusPath,
            ["SDVKIT_LAB_STOP_PATH"] = _paths.StopRequestPath,
            ["SDVKIT_LAB_WINDOWED"] = "1",
        };
        foreach (string name in TestSaveEnvironmentNames)
        {
            environment[name] = string.Empty;
        }

        foreach (string name in NetworkTwoEnvironmentNames)
        {
            environment[name] = string.Empty;
        }

        foreach (string name in ProjectModEnvironmentNames)
        {
            environment[name] = string.Empty;
        }

        if (testSave is not null)
        {
            AddTestSaveEnvironment(environment, testSave);
        }

        if (networkTwo is not null)
        {
            AddNetworkTwoEnvironment(environment, networkTwo);
        }

        if (projectMod is not null)
        {
            AddProjectModEnvironment(environment, projectMod);
            if (interactiveConsole)
            {
                environment["SDVKIT_PROJECT_REVIEW"] = "1";
            }
        }

        var specification = new LabProcessStartSpec(
            executablePath,
            gamePath,
            ["--mods-path", _paths.ModsPath],
            environment,
            _paths.StandardOutputPath,
            _paths.StandardErrorPath,
            StartMinimizedWithoutActivation: networkTwo is not null
                && !interactiveConsole,
            StartVisibleWithoutActivation: interactiveConsole,
            InteractiveConsole: interactiveConsole);
        LabProcessStartResult started = _processHost.Start(specification);
        if (started.Identity is null)
        {
            return Failure(
                "launchFailed",
                null,
                StartProblemCode(started.Status),
                started.Error ?? "The exact SMAPI process did not start.",
                build.LogPath);
        }

        var state = new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            networkTwo is null
                ? LiveLabState.SingleTopology
                : NetworkTwoContract.Topology,
            launchId,
            started.Identity,
            _paths.ModsPath,
            _paths.StatusPath,
            _paths.StopRequestPath,
            testSave,
            networkTwo,
            projectMod);
        if (started.Status != LabProcessStartStatus.Started)
        {
            return HandleLaunchVerificationFailure(
                state,
                build.LogPath,
                started);
        }

        try
        {
            _stateStore.Write(state);
        }
        catch (Exception writeException)
        {
            return HandleStateWriteFailure(
                state,
                build.LogPath,
                writeException);
        }

        AlwaysOnStatusReport alwaysOn = ReadAlwaysOn(state);
        return Result(
            Success,
            Report("running", state, [], build.LogPath, alwaysOn));
    }

    private LiveLabCommandResult HandleLaunchVerificationFailure(
        LiveLabState state,
        string buildLogPath,
        LabProcessStartResult started)
    {
        if (started.Status == LabProcessStartStatus.ExitedBeforeIdentityVerification)
        {
            return Failure(
                "launchFailed",
                state,
                StartProblemCode(started.Status),
                started.Error ?? "The exact created process exited during verification.",
                buildLogPath);
        }

        LabProcessCloseResult rollback = RequestStartupRollback(state);
        string launchError = started.Error
            ?? "The exact created process failed launch verification.";
        if (rollback.Status is LabProcessCloseStatus.Closed
            or LabProcessCloseStatus.AlreadyExited)
        {
            return Failure(
                "stopped",
                state,
                StartProblemCode(started.Status),
                $"{launchError} The exact created process was cleanly rolled back.",
                buildLogPath);
        }

        try
        {
            _stateStore.Write(state);
        }
        catch (Exception writeException)
        {
            OwnedProcessIdentity process = state.OwnedProcessIdentity;
            return Failure(
                "blocked",
                state,
                "ownershipRecordLost",
                $"{launchError} Cleanup did not complete ({DescribeCloseResult(rollback)}), and the exact ownership record could not be persisted. The process may still be running; identify only PID {process.ProcessId}, start time {process.StartTimeUtc:O}, executable '{process.ExecutablePath}': {writeException.Message}",
                buildLogPath);
        }

        return Failure(
            rollback.Status == LabProcessCloseStatus.IdentityMismatch
                ? "ownershipMismatch"
                : "running",
            state,
            "launchVerificationFailedOwnershipRetained",
            $"{launchError} Cleanup did not complete ({DescribeCloseResult(rollback)}); the exact ownership record was retained for lab stop.",
            buildLogPath);
    }

    private LiveLabCommandResult HandleStateWriteFailure(
        LiveLabState state,
        string buildLogPath,
        Exception writeException)
    {
        LabProcessCloseResult rollback = RequestStartupRollback(state);

        if (rollback.Status is LabProcessCloseStatus.Closed
            or LabProcessCloseStatus.AlreadyExited)
        {
            return Failure(
                "stopped",
                state,
                "stateWriteFailedLaunchRolledBack",
                $"The ownership record could not be persisted, so the exact started process was closed before start returned: {writeException.Message}",
                buildLogPath);
        }

        try
        {
            _stateStore.Write(state);
        }
        catch (Exception retryException)
        {
            OwnedProcessIdentity process = state.OwnedProcessIdentity;
            return Failure(
                "blocked",
                state,
                "ownershipRecordLost",
                $"The exact started process could not be cleanly rolled back ({DescribeCloseResult(rollback)}) and its ownership record could not be persisted after two attempts. The process may still be running; identify only PID {process.ProcessId}, start time {process.StartTimeUtc:O}, executable '{process.ExecutablePath}'. Initial write: {writeException.Message} Retry: {retryException.Message}",
                buildLogPath);
        }

        return Failure(
            rollback.Status == LabProcessCloseStatus.IdentityMismatch
                ? "ownershipMismatch"
                : "running",
            state,
            "stateWriteRecovered",
            $"The initial ownership write failed and clean launch rollback did not complete ({DescribeCloseResult(rollback)}). The exact ownership record was persisted on retry; run lab stop: {writeException.Message}",
            buildLogPath);
    }

    private LabProcessCloseResult RequestStartupRollback(LiveLabState state)
    {
        string? stopRequestError = null;
        try
        {
            StopRequestFile.Write(state.StopRequestPath, state.LaunchId);
            LabProcessWaitResult wait = _processHost.WaitForExit(
                state.OwnedProcessIdentity,
                StartupRollbackSignalGrace);
            switch (wait.Status)
            {
                case LabProcessWaitStatus.Exited:
                    return new LabProcessCloseResult(LabProcessCloseStatus.Closed);
                case LabProcessWaitStatus.IdentityMismatch:
                    return new LabProcessCloseResult(
                        LabProcessCloseStatus.IdentityMismatch,
                        wait.Error);
                case LabProcessWaitStatus.Unreadable:
                    return new LabProcessCloseResult(
                        LabProcessCloseStatus.Unreadable,
                        wait.Error);
                case LabProcessWaitStatus.TimedOut:
                    break;
                default:
                    throw new InvalidOperationException("Unknown process wait result.");
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            stopRequestError = exception.Message;
        }

        LabProcessCloseResult close;
        try
        {
            close = _processHost.RequestCloseAndWait(
                state.OwnedProcessIdentity,
                CleanStopTimeout - StartupRollbackSignalGrace);
        }
        catch (Exception exception)
        {
            close = new LabProcessCloseResult(
                LabProcessCloseStatus.Unreadable,
                exception.Message);
        }

        if (string.IsNullOrWhiteSpace(stopRequestError))
        {
            return close;
        }

        string closeDescription = string.IsNullOrWhiteSpace(close.Error)
            ? close.Status.ToString()
            : $"{close.Status}: {close.Error}";
        return close with
        {
            Error = $"Stop request failed: {stopRequestError} Window-close fallback: {closeDescription}",
        };
    }

    private LiveLabCommandResult Status()
    {
        LiveLabState? state = ReadState();
        if (state is null)
        {
            return Result(Success, Report("stopped", null, []));
        }

        LabProcessInspectResult observation = _processHost.Inspect(state.OwnedProcessIdentity);
        return observation.Status switch
        {
            LabProcessInspectStatus.Running => RunningStatus(state),
            LabProcessInspectStatus.Exited => Failure(
                "exited",
                state,
                "ownedProcessExited",
                "The exact owned process exited without a completed stop."),
            LabProcessInspectStatus.IdentityMismatch => Failure(
                "ownershipMismatch",
                state,
                "processIdentityMismatch",
                observation.Error ?? "The PID no longer identifies the owned process."),
            LabProcessInspectStatus.Unreadable => Failure(
                "unreadable",
                state,
                "processUnreadable",
                observation.Error ?? "The owned process identity could not be read."),
            _ => throw new InvalidOperationException("Unknown process observation."),
        };
    }

    private LiveLabCommandResult Stop()
    {
        LiveLabState? state = ReadState();
        if (state is null)
        {
            return Result(Success, Report("stopped", null, []));
        }

        LabProcessInspectResult observation = _processHost.Inspect(state.OwnedProcessIdentity);
        if (observation.Status == LabProcessInspectStatus.Exited)
        {
            AlwaysOnStatusReport exitedStatus = ReadAlwaysOn(state);
            return CompleteControlledStop(state, exitedStatus);
        }

        if (observation.Status == LabProcessInspectStatus.IdentityMismatch)
        {
            return Failure(
                "ownershipMismatch",
                state,
                "processIdentityMismatch",
                observation.Error ?? "The PID no longer identifies the owned process.");
        }

        if (observation.Status == LabProcessInspectStatus.Unreadable)
        {
            return Failure(
                "unreadable",
                state,
                "processUnreadable",
                observation.Error ?? "The owned process identity could not be read.");
        }

        try
        {
            StopRequestFile.Write(state.StopRequestPath, state.LaunchId);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                "running",
                state,
                "stopRequestFailed",
                $"The exact process was left alone because its project-local stop request could not be written: {exception.Message}");
        }

        LabProcessWaitResult wait = _processHost.WaitForExit(
            state.OwnedProcessIdentity,
            CleanStopTimeout);
        if (wait.Status == LabProcessWaitStatus.Exited)
        {
            AlwaysOnStatusReport exitingStatus = ReadAlwaysOn(state);
            return CompleteControlledStop(state, exitingStatus);
        }

        AlwaysOnStatusReport stopStatus = ReadAlwaysOn(state);
        return wait.Status switch
        {
            LabProcessWaitStatus.IdentityMismatch => Failure(
                "ownershipMismatch",
                state,
                "processIdentityMismatch",
                wait.Error ?? "The PID no longer identifies the owned process.",
                alwaysOn: stopStatus),
            LabProcessWaitStatus.Unreadable => Failure(
                "unreadable",
                state,
                "processUnreadable",
                wait.Error ?? "The exact process could not be observed while waiting for clean exit.",
                alwaysOn: stopStatus),
            LabProcessWaitStatus.TimedOut => Failure(
                "running",
                state,
                "cleanStopTimedOut",
                wait.Error ?? "The exact process did not complete the game-side clean stop within 30 seconds.",
                alwaysOn: stopStatus),
            _ => throw new InvalidOperationException("Unknown process wait result."),
        };
    }

    private LiveLabCommandResult? InspectExistingForStart(LiveLabState state)
    {
        LabProcessInspectResult observation = _processHost.Inspect(state.OwnedProcessIdentity);
        if (observation.Status == LabProcessInspectStatus.Exited)
        {
            AlwaysOnStatusReport alwaysOn = ReadAlwaysOn(state);
            if (alwaysOn.State is "exiting" or "restoreFailed")
            {
                if (state.TestSave is not null)
                {
                    LiveLabCommandResult finalized = CompleteControlledStop(state, alwaysOn);
                    if (finalized.ExitCode != Success)
                    {
                        return finalized;
                    }

                    try
                    {
                        File.Delete(_paths.StatusPath);
                    }
                    catch (Exception exception) when (IsControlledFailure(exception))
                    {
                        return Failure(
                            "stopped",
                            state,
                            "runtimeCleanupFailed",
                            $"The confirmed stopped test-save runtime could not clear its status marker: {exception.Message}",
                            alwaysOn: alwaysOn);
                    }

                    return null;
                }

                try
                {
                    File.Delete(_paths.StopRequestPath);
                    _stateStore.Delete();
                    File.Delete(_paths.StatusPath);
                }
                catch (Exception exception) when (IsControlledFailure(exception))
                {
                    return Failure(
                        "exited",
                        state,
                        "runtimeCleanupFailed",
                        $"The confirmed stopped runtime could not be cleaned for a new start: {exception.Message}",
                        alwaysOn: alwaysOn);
                }

                return null;
            }

            return Failure(
                "exited",
                state,
                "cleanStopNotConfirmed",
                "The retained process exited without an AlwaysOn exiting marker; automatic recovery is outside this workflow.",
                alwaysOn: alwaysOn);
        }

        if (observation.Status == LabProcessInspectStatus.Running)
        {
            return RunningStatus(state);
        }

        return observation.Status == LabProcessInspectStatus.IdentityMismatch
            ? Failure(
                "ownershipMismatch",
                state,
                "processIdentityMismatch",
                observation.Error ?? "The retained PID no longer identifies the owned process.")
            : Failure(
                "unreadable",
                state,
                "processUnreadable",
                observation.Error ?? "The retained process identity could not be read.");
    }

    private LiveLabCommandResult RunningStatus(LiveLabState state)
    {
        AlwaysOnStatusReport alwaysOn = ReadAlwaysOn(state);
        if (string.Equals(alwaysOn.State, "pending", StringComparison.Ordinal)
            && _utcNow().ToUniversalTime() - state.OwnedProcessIdentity.StartTimeUtc
                > AlwaysOnStartupGrace)
        {
            return Failure(
                "running",
                state,
                "alwaysOnPending",
                "The owned process is running, but AlwaysOn did not publish a status marker within 30 seconds.",
                alwaysOn: alwaysOn);
        }

        if (string.Equals(alwaysOn.State, "restoreFailed", StringComparison.Ordinal))
        {
            return Failure(
                "running",
                state,
                "alwaysOnRestoreFailed",
                "AlwaysOn could not confirm restoration for the requested clean stop and requested normal exit, but the exact process is still running.",
                alwaysOn: alwaysOn);
        }

        if (string.Equals(alwaysOn.State, "exiting", StringComparison.Ordinal))
        {
            return Failure(
                "running",
                state,
                "cleanStopIncomplete",
                "AlwaysOn requested normal exit, but the exact process is still running.",
                alwaysOn: alwaysOn);
        }

        if (alwaysOn.State is "invalid" or "mismatch" or "stale")
        {
            return Failure(
                "running",
                state,
                $"alwaysOn{char.ToUpperInvariant(alwaysOn.State[0])}{alwaysOn.State[1..]}",
                $"The AlwaysOn status marker is {alwaysOn.State}.",
                alwaysOn: alwaysOn);
        }

        if (string.Equals(alwaysOn.State, "active", StringComparison.Ordinal)
            && alwaysOn.PauseWhenOutOfFocus != false)
        {
            return Failure(
                "running",
                state,
                "alwaysOnNotApplied",
                "AlwaysOn is active, but pauseWhenOutOfFocus is not false.",
                alwaysOn: alwaysOn);
        }

        if (state.TestSave is not null)
        {
            TestSaveStatusReport? testSave = alwaysOn.TestSave;
            if (testSave?.State is "invalid" or "mismatch" or "unexpected")
            {
                return Failure(
                    "running",
                    state,
                    "testSaveStatusMismatch",
                    $"The AlwaysOn test-save marker is {testSave.State}.",
                    alwaysOn: alwaysOn);
            }

            if (testSave is not null
                && string.Equals(testSave.Phase, "failed", StringComparison.Ordinal))
            {
                return Failure(
                    "running",
                    state,
                    "testSaveFailed",
                    testSave.Message ?? "The game-side test-save workflow failed.",
                    alwaysOn: alwaysOn);
            }

            if ((testSave is null || string.Equals(testSave.State, "pending", StringComparison.Ordinal))
                && _utcNow().ToUniversalTime() - state.OwnedProcessIdentity.StartTimeUtc
                    > AlwaysOnStartupGrace)
            {
                return Failure(
                    "running",
                    state,
                    "testSavePending",
                    "AlwaysOn did not publish the launch-bound test-save marker within 30 seconds.",
                    alwaysOn: alwaysOn);
            }
        }


        if (state.NetworkTwo is not null)
        {
            NetworkTwoStatusReport? networkTwo = alwaysOn.NetworkTwo;
            if (networkTwo?.State is "invalid" or "mismatch" or "unexpected")
            {
                return Failure(
                    "running",
                    state,
                    "networkTwoStatusMismatch",
                    $"The AlwaysOn network-2 marker is {networkTwo.State}.",
                    alwaysOn: alwaysOn);
            }

            if (networkTwo is not null
                && string.Equals(networkTwo.Phase, "failed", StringComparison.Ordinal))
            {
                return Failure(
                    "running",
                    state,
                    "networkTwoFailed",
                    networkTwo.Message ?? "The game-side network-2 workflow failed.",
                    alwaysOn: alwaysOn);
            }

            if ((networkTwo is null
                    || string.Equals(networkTwo.State, "pending", StringComparison.Ordinal))
                && _utcNow().ToUniversalTime() - state.OwnedProcessIdentity.StartTimeUtc
                    > AlwaysOnStartupGrace)
            {
                return Failure(
                    "running",
                    state,
                    "networkTwoPending",
                    "AlwaysOn did not publish the launch-bound network-2 marker within 30 seconds.",
                    alwaysOn: alwaysOn);
            }

            bool isHost = string.Equals(
                state.NetworkTwo.Role,
                NetworkTwoContract.HostRole,
                StringComparison.Ordinal);
            if (isHost
                && string.Equals(alwaysOn.State, "active", StringComparison.Ordinal)
                && (alwaysOn.EnableServer != true
                    || alwaysOn.IpConnectionsEnabled != true))
            {
                return Failure(
                    "running",
                    state,
                    "networkHostOptionsNotApplied",
                    "AlwaysOn is active for the host, but the required LAN server options are not both true.",
                    alwaysOn: alwaysOn);
            }
        }

        if (state.ProjectMod is not null)
        {
            ProjectModStatusReport? projectMod = alwaysOn.ProjectMod;
            if (projectMod?.State is "invalid" or "mismatch" or "unexpected")
            {
                return Failure(
                    "running",
                    state,
                    "projectModStatusMismatch",
                    $"The AlwaysOn project-mod marker is {projectMod.State}.",
                    alwaysOn: alwaysOn);
            }

            if (projectMod is not null
                && string.Equals(projectMod.State, "failed", StringComparison.Ordinal))
            {
                return Failure(
                    "running",
                    state,
                    "projectModLoadFailed",
                    projectMod.Message
                        ?? "SMAPI did not confirm the expected project mod as loaded.",
                    alwaysOn: alwaysOn);
            }

            if ((projectMod is null
                    || string.Equals(projectMod.State, "pending", StringComparison.Ordinal))
                && _utcNow().ToUniversalTime() - state.OwnedProcessIdentity.StartTimeUtc
                    > AlwaysOnStartupGrace)
            {
                return Failure(
                    "running",
                    state,
                    "projectModPending",
                    "AlwaysOn did not publish the launch-bound project-mod load marker within 30 seconds.",
                    alwaysOn: alwaysOn);
            }
        }

        return Result(Success, Report("running", state, [], alwaysOn: alwaysOn));
    }

    private LiveLabCommandResult CompleteControlledStop(
        LiveLabState state,
        AlwaysOnStatusReport alwaysOn)
    {
        LastAlwaysOn = alwaysOn;
        bool restoreUnconfirmed = string.Equals(
            alwaysOn.State,
            "restoreFailed",
            StringComparison.Ordinal);
        if (!restoreUnconfirmed
            && !string.Equals(alwaysOn.State, "exiting", StringComparison.Ordinal))
        {
            if (state.TestSave is not null)
            {
                try
                {
                    TestSaveCleanupResult cleanup = _testSaveStore.AbortStopped(
                        state.TestSave,
                        state.LaunchId);
                    _lastTestSaveLogPaths = cleanup.ArchivedLogPaths;
                }
                catch (Exception exception) when (IsControlledFailure(exception))
                {
                    return Failure(
                        "exited",
                        state,
                        "testSaveCleanupFailed",
                        $"The exact process exited and its test-save junction could not be safely removed: {exception.Message}",
                        alwaysOn: alwaysOn);
                }
            }

            return Failure(
                "exited",
                state,
                "cleanStopNotConfirmed",
                "The exact process exited, but AlwaysOn did not confirm restoration during normal game exit.",
                alwaysOn: alwaysOn);
        }

        bool testSaveSucceeded = true;
        bool networkTwoSucceeded = true;
        bool projectModSucceeded = true;
        bool scenarioLogArchived = true;
        if (state.TestSave is not null)
        {
            TestSaveStatusReport? testSave = alwaysOn.TestSave;
            string expectedPhase = string.Equals(
                state.TestSave.Mode,
                TestSaveContract.CreateMode,
                StringComparison.Ordinal)
                ? "created"
                : "passed";
            testSaveSucceeded = testSave is not null
                && string.Equals(testSave.State, "ready", StringComparison.Ordinal)
                && string.Equals(testSave.Phase, expectedPhase, StringComparison.Ordinal)
                && testSave.IdentityVerified == true;
            try
            {
                TestSaveCleanupResult cleanup = testSaveSucceeded
                    ? _testSaveStore.CompleteStopped(state.TestSave, state.LaunchId)
                    : _testSaveStore.AbortStopped(state.TestSave, state.LaunchId);
                _lastTestSaveLogPaths = cleanup.ArchivedLogPaths;
                scenarioLogArchived = cleanup.ScenarioLogArchived;
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                return Failure(
                    "exited",
                    state,
                    "testSaveCleanupFailed",
                    $"The clean process stop was confirmed, but exact test-save cleanup failed: {exception.Message}",
                    alwaysOn: alwaysOn);
            }
        }


        if (state.NetworkTwo is not null)
        {
            NetworkTwoStatusReport? networkTwo = alwaysOn.NetworkTwo;
            bool pairIdentitiesMatch = networkTwo?.LocalPlayerId is not (null or 0)
                && networkTwo.RemotePlayerId is not (null or 0)
                && networkTwo.LocalPlayerId != networkTwo.RemotePlayerId;
            networkTwoSucceeded = networkTwo is not null
                && string.Equals(networkTwo.State, "ready", StringComparison.Ordinal)
                && string.Equals(networkTwo.Phase, "passed", StringComparison.Ordinal)
                && networkTwo.IdentityVerified == true
                && networkTwo.JoinedTicks >= NetworkTwoContract.RequiredJoinedTicks
                && pairIdentitiesMatch;

        }

        if (state.ProjectMod is not null)
        {
            projectModSucceeded = ProjectModLoadSucceeded(alwaysOn, state.ProjectMod);
        }

        try
        {
            File.Delete(state.StopRequestPath);
            _stateStore.Delete();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                "exited",
                state,
                "runtimeCleanupFailed",
                $"The clean stop was confirmed, but its project-local ownership record could not be removed: {exception.Message}",
                alwaysOn: alwaysOn);
        }

        if (!projectModSucceeded)
        {
            return Failure(
                "stopped",
                state,
                "projectModLoadUnconfirmed",
                alwaysOn.ProjectMod?.Message
                    ?? "SMAPI did not confirm the expected project mod identity and version as loaded.",
                alwaysOn: alwaysOn);
        }

        if (!testSaveSucceeded)
        {
            return Failure(
                "stopped",
                state,
                "testSaveIncomplete",
                alwaysOn.TestSave?.Message
                    ?? "The game-side test-save workflow did not reach its verified terminal phase.",
                alwaysOn: alwaysOn);
        }


        if (!networkTwoSucceeded)
        {
            return Failure(
                "stopped",
                state,
                "networkTwoIncomplete",
                alwaysOn.NetworkTwo?.Message
                    ?? "The game-side network-2 workflow did not reach its verified terminal phase.",
                alwaysOn: alwaysOn);
        }

        if (!scenarioLogArchived)
        {
            return Failure(
                "stopped",
                state,
                "testSaveScenarioLogMissing",
                "The exact process and fixture were cleaned up, but the required project-local test-save scenario log was not produced.",
                alwaysOn: alwaysOn);
        }

        return Result(
            Success,
            Report(
                "stopped",
                state,
                [],
                alwaysOn: alwaysOn,
                additionalWarnings: restoreUnconfirmed
                    ? RestoreUnconfirmedWarnings
                    : null));
    }

    private static bool ProjectModLoadSucceeded(
        AlwaysOnStatusReport alwaysOn,
        ProjectModLaunchState expected)
    {
        ProjectModStatusReport? projectMod = alwaysOn.ProjectMod;
        return projectMod is not null
            && string.Equals(projectMod.State, "ready", StringComparison.Ordinal)
            && string.Equals(
                projectMod.Phase,
                ProjectModContract.LoadedPhase,
                StringComparison.Ordinal)
            && projectMod.LoadConfirmed == true
            && string.Equals(
                projectMod.LoadedUniqueId,
                expected.UniqueId,
                StringComparison.Ordinal)
            && string.Equals(
                projectMod.LoadedVersion,
                expected.Version,
                StringComparison.Ordinal)
            && string.Equals(
                projectMod.BuildIdentity,
                expected.BuildIdentity,
                StringComparison.Ordinal);
    }

    private LiveLabState? ReadState()
    {
        LiveLabState? state = _stateStore.Read();
        if (state is null)
        {
            return null;
        }

        if (!PathEquals(state.ModsPath, _paths.ModsPath)
            || !PathEquals(state.StatusPath, _paths.StatusPath)
            || !PathEquals(state.StopRequestPath, _paths.StopRequestPath)
            || (state.TestSave is not null
                && (!PathEquals(state.TestSave.WorkPath, _paths.TestSaveWorkPath)
                    || !PathEquals(
                        state.TestSave.ScenarioLogPath,
                        _paths.TestSaveScenarioLogPath)))
            || (state.NetworkTwo is not null
                && !PathEquals(
                    state.NetworkTwo.NetworkLogPath,
                    Path.Combine(_paths.RuntimePath, "network-2.log"))))
        {
            throw new InvalidDataException(
                "The retained live-lab paths do not match this project-local live-lab instance.");
        }

        return state;
    }

    private AlwaysOnStatusReport ReadAlwaysOn(LiveLabState state)
    {
        return AlwaysOnStatusReader.Read(
            state.StatusPath,
            state.LaunchId,
            state.OwnedProcessIdentity,
            _utcNow().ToUniversalTime(),
            state.TestSave,
            state.NetworkTwo,
            state.ProjectMod);
    }

    private LiveLabCommandResult Failure(
        string stateName,
        LiveLabState? state,
        string code,
        string message,
        string? buildLogPath = null,
        AlwaysOnStatusReport? alwaysOn = null)
    {
        return Result(
            OperationFailed,
            Report(
                stateName,
                state,
                [Problem(code, message)],
                buildLogPath,
                alwaysOn,
                additionalWarnings: string.Equals(
                    alwaysOn?.State,
                    "restoreFailed",
                    StringComparison.Ordinal)
                        ? RestoreUnconfirmedWarnings
                        : null));
    }

    private LiveLabReport Report(
        string stateName,
        LiveLabState? state,
        IReadOnlyList<LiveLabProblem> problems,
        string? buildLogPath = null,
        AlwaysOnStatusReport? alwaysOn = null,
        IReadOnlyList<string>? additionalWarnings = null)
    {
        OwnedProcessIdentity? process = state?.OwnedProcessIdentity;
        IReadOnlyList<string> baseWarnings = state?.TestSave is null
            ? IsolationWarnings
            : TestSaveWarnings;
        IReadOnlyList<string> warnings = additionalWarnings is null
            or { Count: 0 }
            ? baseWarnings
            : [.. baseWarnings, .. additionalWarnings];
        return new LiveLabReport(
            1,
            state?.Topology ?? _reportTopology,
            stateName,
            state?.LaunchId,
            process?.ProcessId,
            process?.StartTimeUtc,
            process?.ExecutablePath,
            _paths.ModsPath,
            buildLogPath,
            alwaysOn,
            problems,
            warnings,
            state?.TestSave is null ? [] : _lastTestSaveLogPaths);
    }

    private static LiveLabProblem Problem(string code, string message) =>
        new(code, message);

    private static LiveLabCommandResult Result(int exitCode, object report) =>
        new(exitCode, report);

    private static string StartProblemCode(LabProcessStartStatus status) => status switch
    {
        LabProcessStartStatus.ExitedBeforeIdentityVerification => "processExitedDuringStart",
        LabProcessStartStatus.IdentityMismatch => "processIdentityMismatch",
        LabProcessStartStatus.Unreadable => "processUnreadable",
        LabProcessStartStatus.AbortUnconfirmed => "unverifiedChildAbortUnconfirmed",
        _ => "processStartFailed",
    };

    private static void AddTestSaveEnvironment(
        IDictionary<string, string> environment,
        TestSaveLaunchState launch)
    {
        launch.Validate();
        TestSaveIdentity identity = launch.Identity;
        environment["SDVKIT_TEST_SAVE_MODE"] = launch.Mode;
        environment["SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID"] = identity.WorkspaceOwnerId;
        environment["SDVKIT_TEST_SAVE_FIXTURE_ID"] = identity.FixtureId;
        environment["SDVKIT_TEST_SAVE_UNIQUE_GAME_ID"] =
            identity.UniqueGameId.ToString(CultureInfo.InvariantCulture);
        environment["SDVKIT_TEST_SAVE_ID"] = identity.SaveId;
        environment["SDVKIT_TEST_SAVE_PLAYER_NAME"] = identity.PlayerName;
        environment["SDVKIT_TEST_SAVE_FARM_NAME"] = identity.FarmName;
        environment["SDVKIT_TEST_SAVE_FAVORITE_THING"] = identity.FavoriteThing;
        environment["SDVKIT_TEST_SAVE_LOG_PATH"] = launch.ScenarioLogPath;
    }

    private static void AddNetworkTwoEnvironment(
        IDictionary<string, string> environment,
        NetworkTwoLaunchState launch)
    {
        launch.Validate();
        environment["SDVKIT_NETWORK_TWO_ROLE"] = launch.Role;
        environment["SDVKIT_NETWORK_TWO_BUILD_ID"] = launch.BuildIdentity;
        environment["SDVKIT_NETWORK_TWO_FIXTURE_ID"] = launch.FixtureId;
        environment["SDVKIT_NETWORK_TWO_SAVE_ID"] = launch.SaveId;
        environment["SDVKIT_NETWORK_TWO_LOG_PATH"] = launch.NetworkLogPath;
        environment["SDVKIT_NETWORK_TWO_EXPECTED_FARMHAND_ID"] =
            launch.ExpectedFarmhandId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void AddProjectModEnvironment(
        IDictionary<string, string> environment,
        ProjectModLaunchState launch)
    {
        launch.Validate();
        environment["SDVKIT_PROJECT_MOD_UNIQUE_ID"] = launch.UniqueId;
        environment["SDVKIT_PROJECT_MOD_VERSION"] = launch.Version;
        environment["SDVKIT_PROJECT_MOD_BUILD_IDENTITY"] = launch.BuildIdentity;
    }

    private static string DescribeCloseResult(LabProcessCloseResult close)
    {
        string status = close.Status.ToString();
        return string.IsNullOrWhiteSpace(close.Error)
            ? status
            : $"{status}: {close.Error}";
    }

    private static bool PathEquals(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), comparison);
    }

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or PlatformNotSupportedException
            or JsonException
            or SecurityException
            or UnauthorizedAccessException;
}
