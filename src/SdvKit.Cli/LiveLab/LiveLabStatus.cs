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
}
