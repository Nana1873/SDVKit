using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed partial class LiveLabService
{
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

    private static void AddProjectModEnvironment(
        IDictionary<string, string> environment,
        ProjectModLaunchState launch)
    {
        launch.Validate();
        environment["SDVKIT_PROJECT_MOD_UNIQUE_ID"] = launch.UniqueId;
        environment["SDVKIT_PROJECT_MOD_VERSION"] = launch.Version;
        environment["SDVKIT_PROJECT_MOD_BUILD_IDENTITY"] = launch.BuildIdentity;
    }
}
