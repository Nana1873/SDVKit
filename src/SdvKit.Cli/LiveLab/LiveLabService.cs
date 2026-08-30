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
    IReadOnlyList<string> Warnings);

internal sealed class LiveLabService
{
    private const int Success = 0;
    private const int OperationFailed = 3;

    private static readonly TimeSpan CleanStopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupRollbackSignalGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AlwaysOnStartupGrace = TimeSpan.FromSeconds(30);

    private static readonly string[] IsolationWarnings =
    [
        "Only the SMAPI mod group is isolated. Stardew AppData preferences, saves, startup preferences, and standard SMAPI logs remain shared.",
        "This workflow does not enumerate, open, copy, select, or modify saves or the normal Mods directory.",
    ];

    private readonly LiveLabPaths _paths;
    private readonly ILiveLabStateStore _stateStore;
    private readonly IAlwaysOnBuilder _alwaysOnBuilder;
    private readonly ILabProcessHost _processHost;
    private readonly Func<DoctorReport> _discoverInstallations;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _createLaunchId;

    internal LiveLabService(
        LiveLabPaths paths,
        ILiveLabStateStore stateStore,
        IAlwaysOnBuilder alwaysOnBuilder,
        ILabProcessHost processHost,
        Func<DoctorReport> discoverInstallations,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? createLaunchId = null)
    {
        _paths = paths;
        _stateStore = stateStore;
        _alwaysOnBuilder = alwaysOnBuilder;
        _processHost = processHost;
        _discoverInstallations = discoverInstallations;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _createLaunchId = createLaunchId ?? (() => Guid.NewGuid().ToString("N"));
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
                            "Another live-lab start, status, or stop operation is still running for this project.")]));
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
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private LiveLabCommandResult Start()
    {
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
        _stateStore.VerifyWritable();
        string gamePath = doctor.Installations[0].GamePath;
        AlwaysOnBuildResult build = _alwaysOnBuilder.BuildAndInstall(gamePath, _paths);
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
        var specification = new LabProcessStartSpec(
            executablePath,
            gamePath,
            ["--mods-path", _paths.ModsPath],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SDVKIT_LAB_LAUNCH_ID"] = launchId,
                ["SDVKIT_LAB_STATUS_PATH"] = _paths.StatusPath,
                ["SDVKIT_LAB_STOP_PATH"] = _paths.StopRequestPath,
            },
            _paths.StandardOutputPath,
            _paths.StandardErrorPath);
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
            LiveLabState.SingleTopology,
            launchId,
            started.Identity,
            _paths.ModsPath,
            _paths.StatusPath,
            _paths.StopRequestPath);
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
            return CompleteConfirmedStop(state, exitedStatus);
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
            return CompleteConfirmedStop(state, exitingStatus);
        }

        AlwaysOnStatusReport stopStatus = ReadAlwaysOn(state);
        if (string.Equals(stopStatus.State, "restoreFailed", StringComparison.Ordinal))
        {
            return Failure(
                "running",
                state,
                "alwaysOnRestoreFailed",
                "AlwaysOn received the stop request but could not confirm restoration; the exact process was left alone.",
                alwaysOn: stopStatus);
        }

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
            if (string.Equals(alwaysOn.State, "exiting", StringComparison.Ordinal))
            {
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
                "AlwaysOn could not confirm restoration for the requested clean stop.",
                alwaysOn: alwaysOn);
        }

        if (string.Equals(alwaysOn.State, "exiting", StringComparison.Ordinal))
        {
            return Failure(
                "running",
                state,
                "cleanStopIncomplete",
                "AlwaysOn confirmed restoration and requested normal exit, but the exact process is still running.",
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

        return Result(Success, Report("running", state, [], alwaysOn: alwaysOn));
    }

    private LiveLabCommandResult CompleteConfirmedStop(
        LiveLabState state,
        AlwaysOnStatusReport alwaysOn)
    {
        if (!string.Equals(alwaysOn.State, "exiting", StringComparison.Ordinal))
        {
            return Failure(
                "exited",
                state,
                "cleanStopNotConfirmed",
                "The exact process exited, but AlwaysOn did not confirm restoration during normal game exit.",
                alwaysOn: alwaysOn);
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

        return Result(
            Success,
            Report("stopped", state, [], alwaysOn: alwaysOn));
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
            || !PathEquals(state.StopRequestPath, _paths.StopRequestPath))
        {
            throw new InvalidDataException(
                "The retained live-lab paths do not match this project-local single topology.");
        }

        return state;
    }

    private AlwaysOnStatusReport ReadAlwaysOn(LiveLabState state)
    {
        return AlwaysOnStatusReader.Read(
            state.StatusPath,
            state.LaunchId,
            state.OwnedProcessIdentity,
            _utcNow().ToUniversalTime());
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
                alwaysOn));
    }

    private LiveLabReport Report(
        string stateName,
        LiveLabState? state,
        IReadOnlyList<LiveLabProblem> problems,
        string? buildLogPath = null,
        AlwaysOnStatusReport? alwaysOn = null)
    {
        OwnedProcessIdentity? process = state?.OwnedProcessIdentity;
        return new LiveLabReport(
            1,
            LiveLabState.SingleTopology,
            stateName,
            state?.LaunchId,
            process?.ProcessId,
            process?.StartTimeUtc,
            process?.ExecutablePath,
            _paths.ModsPath,
            buildLogPath,
            alwaysOn,
            problems,
            IsolationWarnings);
    }

    private static LiveLabProblem Problem(string code, string message) =>
        new(code, message);

    private static LiveLabCommandResult Result(int exitCode, LiveLabReport report) =>
        new(exitCode, report);

    private static string StartProblemCode(LabProcessStartStatus status) => status switch
    {
        LabProcessStartStatus.ExitedBeforeIdentityVerification => "processExitedDuringStart",
        LabProcessStartStatus.IdentityMismatch => "processIdentityMismatch",
        LabProcessStartStatus.Unreadable => "processUnreadable",
        _ => "processStartFailed",
    };

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
