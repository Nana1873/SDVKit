using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed partial class LiveLabService
{
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
            GameSideStatusReport exitedStatus = ReadGameSide(state);
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
            GameSideStatusReport exitingStatus = ReadGameSide(state);
            return CompleteControlledStop(state, exitingStatus);
        }

        GameSideStatusReport stopStatus = ReadGameSide(state);
        return wait.Status switch
        {
            LabProcessWaitStatus.IdentityMismatch => Failure(
                "ownershipMismatch",
                state,
                "processIdentityMismatch",
                wait.Error ?? "The PID no longer identifies the owned process.",
                gameSide: stopStatus),
            LabProcessWaitStatus.Unreadable => Failure(
                "unreadable",
                state,
                "processUnreadable",
                wait.Error ?? "The exact process could not be observed while waiting for clean exit.",
                gameSide: stopStatus),
            LabProcessWaitStatus.TimedOut => Failure(
                "running",
                state,
                "cleanStopTimedOut",
                wait.Error ?? "The exact process did not complete the game-side clean stop within 30 seconds.",
                gameSide: stopStatus),
            _ => throw new InvalidOperationException("Unknown process wait result."),
        };
    }

    private LiveLabCommandResult CompleteControlledStop(
        LiveLabState state,
        GameSideStatusReport gameSide)
    {
        LastGameSide = gameSide;
        bool restoreUnconfirmed = string.Equals(
            gameSide.State,
            "restoreFailed",
            StringComparison.Ordinal);
        if (!restoreUnconfirmed
            && !string.Equals(gameSide.State, "exiting", StringComparison.Ordinal))
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
                        gameSide: gameSide);
                }
            }

            return Failure(
                "exited",
                state,
                "cleanStopNotConfirmed",
                "The exact process exited, but GameSide did not confirm restoration during normal game exit.",
                gameSide: gameSide);
        }

        bool testSaveSucceeded = true;
        bool networkTwoSucceeded = true;
        bool projectModSucceeded = true;
        bool scenarioLogArchived = true;
        if (state.TestSave is not null)
        {
            TestSaveStatusReport? testSave = gameSide.TestSave;
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
                    gameSide: gameSide);
            }
        }


        if (state.NetworkTwo is not null)
        {
            NetworkTwoStatusReport? networkTwo = gameSide.NetworkTwo;
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
            projectModSucceeded = ProjectModLoadSucceeded(gameSide, state.ProjectMod);
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
                gameSide: gameSide);
        }

        if (!projectModSucceeded)
        {
            return Failure(
                "stopped",
                state,
                "projectModLoadUnconfirmed",
                gameSide.ProjectMod?.Message
                    ?? "SMAPI did not confirm the expected project mod identity and version as loaded.",
                gameSide: gameSide);
        }

        if (!testSaveSucceeded)
        {
            return Failure(
                "stopped",
                state,
                "testSaveIncomplete",
                gameSide.TestSave?.Message
                    ?? "The game-side test-save workflow did not reach its verified terminal phase.",
                gameSide: gameSide);
        }


        if (!networkTwoSucceeded)
        {
            return Failure(
                "stopped",
                state,
                "networkTwoIncomplete",
                gameSide.NetworkTwo?.Message
                    ?? "The game-side network-2 workflow did not reach its verified terminal phase.",
                gameSide: gameSide);
        }

        if (!scenarioLogArchived)
        {
            return Failure(
                "stopped",
                state,
                "testSaveScenarioLogMissing",
                "The exact process and fixture were cleaned up, but the required project-local test-save scenario log was not produced.",
                gameSide: gameSide);
        }

        return Result(
            Success,
            Report(
                "stopped",
                state,
                [],
                gameSide: gameSide,
                additionalWarnings: restoreUnconfirmed
                    ? RestoreUnconfirmedWarnings
                    : null));
    }

    private static string DescribeCloseResult(LabProcessCloseResult close)
    {
        string status = close.Status.ToString();
        return string.IsNullOrWhiteSpace(close.Error)
            ? status
            : $"{status}: {close.Error}";
    }
}
