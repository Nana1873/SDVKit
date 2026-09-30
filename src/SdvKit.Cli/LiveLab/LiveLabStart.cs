using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed partial class LiveLabService
{
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
            ["SDVKIT_REVIEW_CONSOLE_BACKGROUND"] = interactiveConsole ? "1" : string.Empty,
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

    private static string StartProblemCode(LabProcessStartStatus status) => status switch
    {
        LabProcessStartStatus.ExitedBeforeIdentityVerification => "processExitedDuringStart",
        LabProcessStartStatus.IdentityMismatch => "processIdentityMismatch",
        LabProcessStartStatus.Unreadable => "processUnreadable",
        LabProcessStartStatus.AbortUnconfirmed => "unverifiedChildAbortUnconfirmed",
        _ => "processStartFailed",
    };
}
