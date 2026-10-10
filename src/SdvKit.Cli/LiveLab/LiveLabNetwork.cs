using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed partial class LiveLabService
{
    internal LiveLabCommandResult StartNetwork(
        TestSaveLaunchState? testSave,
        NetworkTwoLaunchState networkTwo,
        GameSideBuildResult preparedBuild,
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
                gameSide: ReadGameSide(state));
        }

        return CompleteControlledStop(state, ReadGameSide(state));
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
                gameSide: report.GameSide);
        }

        return Failure(
            "stopped",
            state,
            "networkRestoreUnconfirmed",
            "The exact network-2 process exited without a GameSide restore marker. Its ownership record was released for bounded recovery, but this run is not accepted as a clean stop.",
            gameSide: report.GameSide);
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
}
