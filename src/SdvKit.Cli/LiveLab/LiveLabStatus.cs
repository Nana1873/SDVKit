using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed partial class LiveLabService
{
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

    private LiveLabCommandResult RunningStatus(LiveLabState state)
    {
        GameSideStatusReport gameSide = ReadGameSide(state);
        if (string.Equals(gameSide.State, "pending", StringComparison.Ordinal)
            && _utcNow().ToUniversalTime() - state.OwnedProcessIdentity.StartTimeUtc
                > GameSideStartupGrace)
        {
            return Failure(
                "running",
                state,
                "gameSidePending",
                "The owned process is running, but GameSide did not publish a status marker within 30 seconds.",
                gameSide: gameSide);
        }

        if (string.Equals(gameSide.State, "restoreFailed", StringComparison.Ordinal))
        {
            return Failure(
                "running",
                state,
                "gameSideRestoreFailed",
                "GameSide could not confirm restoration for the requested clean stop and requested normal exit, but the exact process is still running.",
                gameSide: gameSide);
        }

        if (string.Equals(gameSide.State, "exiting", StringComparison.Ordinal))
        {
            return Failure(
                "running",
                state,
                "cleanStopIncomplete",
                "GameSide requested normal exit, but the exact process is still running.",
                gameSide: gameSide);
        }

        if (gameSide.State is "invalid" or "mismatch" or "stale")
        {
            return Failure(
                "running",
                state,
                $"gameSide{char.ToUpperInvariant(gameSide.State[0])}{gameSide.State[1..]}",
                $"The GameSide status marker is {gameSide.State}.",
                gameSide: gameSide);
        }

        if (string.Equals(gameSide.State, "active", StringComparison.Ordinal)
            && gameSide.PauseWhenOutOfFocus != false)
        {
            return Failure(
                "running",
                state,
                "gameSideNotApplied",
                "GameSide is active, but pauseWhenOutOfFocus is not false.",
                gameSide: gameSide);
        }

        if (state.TestSave is not null)
        {
            TestSaveStatusReport? testSave = gameSide.TestSave;
            if (testSave?.State is "invalid" or "mismatch" or "unexpected")
            {
                return Failure(
                    "running",
                    state,
                    "testSaveStatusMismatch",
                    $"The GameSide test-save marker is {testSave.State}.",
                    gameSide: gameSide);
            }

            if (testSave is not null
                && string.Equals(testSave.Phase, "failed", StringComparison.Ordinal))
            {
                return Failure(
                    "running",
                    state,
                    "testSaveFailed",
                    testSave.Message ?? "The game-side test-save workflow failed.",
                    gameSide: gameSide);
            }

            if ((testSave is null || string.Equals(testSave.State, "pending", StringComparison.Ordinal))
                && _utcNow().ToUniversalTime() - state.OwnedProcessIdentity.StartTimeUtc
                    > GameSideStartupGrace)
            {
                return Failure(
                    "running",
                    state,
                    "testSavePending",
                    "GameSide did not publish the launch-bound test-save marker within 30 seconds.",
                    gameSide: gameSide);
            }
        }


        if (state.NetworkTwo is not null)
        {
            NetworkTwoStatusReport? networkTwo = gameSide.NetworkTwo;
            if (networkTwo?.State is "invalid" or "mismatch" or "unexpected")
            {
                return Failure(
                    "running",
                    state,
                    "networkTwoStatusMismatch",
                    $"The GameSide network-2 marker is {networkTwo.State}.",
                    gameSide: gameSide);
            }

            if (networkTwo is not null
                && string.Equals(networkTwo.Phase, "failed", StringComparison.Ordinal))
            {
                return Failure(
                    "running",
                    state,
                    "networkTwoFailed",
                    networkTwo.Message ?? "The game-side network-2 workflow failed.",
                    gameSide: gameSide);
            }

            if ((networkTwo is null
                    || string.Equals(networkTwo.State, "pending", StringComparison.Ordinal))
                && _utcNow().ToUniversalTime() - state.OwnedProcessIdentity.StartTimeUtc
                    > GameSideStartupGrace)
            {
                return Failure(
                    "running",
                    state,
                    "networkTwoPending",
                    "GameSide did not publish the launch-bound network-2 marker within 30 seconds.",
                    gameSide: gameSide);
            }

            bool isHost = string.Equals(
                state.NetworkTwo.Role,
                NetworkTwoContract.HostRole,
                StringComparison.Ordinal);
            if (isHost
                && string.Equals(gameSide.State, "active", StringComparison.Ordinal)
                && (gameSide.EnableServer != true
                    || gameSide.IpConnectionsEnabled != true))
            {
                return Failure(
                    "running",
                    state,
                    "networkHostOptionsNotApplied",
                    "GameSide is active for the host, but the required LAN server options are not both true.",
                    gameSide: gameSide);
            }
        }

        if (state.ProjectMod is not null)
        {
            ProjectModStatusReport? projectMod = gameSide.ProjectMod;
            if (projectMod?.State is "invalid" or "mismatch" or "unexpected")
            {
                return Failure(
                    "running",
                    state,
                    "projectModStatusMismatch",
                    $"The GameSide project-mod marker is {projectMod.State}.",
                    gameSide: gameSide);
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
                    gameSide: gameSide);
            }

            if ((projectMod is null
                    || string.Equals(projectMod.State, "pending", StringComparison.Ordinal))
                && _utcNow().ToUniversalTime() - state.OwnedProcessIdentity.StartTimeUtc
                    > GameSideStartupGrace)
            {
                return Failure(
                    "running",
                    state,
                    "projectModPending",
                    "GameSide did not publish the launch-bound project-mod load marker within 30 seconds.",
                    gameSide: gameSide);
            }
        }

        return Result(Success, Report("running", state, [], gameSide: gameSide));
    }

    private static bool ProjectModLoadSucceeded(
        GameSideStatusReport gameSide,
        ProjectModLaunchState expected)
    {
        ProjectModStatusReport? projectMod = gameSide.ProjectMod;
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
}
