using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed partial class LiveLabService
{
    private sealed record TestSaveWaitResult(
        bool Succeeded,
        TestSaveStatusReport? Status,
        LiveLabProblem? Problem);

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
}
