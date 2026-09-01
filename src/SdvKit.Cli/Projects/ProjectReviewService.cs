using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static class ProjectReviewService
{
    private const int Success = 0;
    private const int OperationFailed = 3;

    private static readonly string[] Warnings =
    [
        "Project review stages only the explicitly selected local target, companions, and content packs; SDVKit does not search for or download dependencies.",
        "The SMAPI process uses a separate interactive console, so stdout/stderr are not captured by SDVKit; SMAPI's own log and screenshots remain in the isolated single-role profile below .sdvkit.",
        "Review saves persist in the isolated single-role profile across process restarts. Normal saves and the normal or mod-manager-owned Mods directory are not selected or modified.",
        "This is process-level data isolation, not a Windows sandbox; reviewed mods can still access shared machine resources.",
    ];

    private static readonly string[] CommandWarnings =
    [
        "commandWritten=true confirms that one complete text line plus Enter was enqueued into the exact owned SMAPI console; it does not confirm that SMAPI accepted or completed the command.",
        "Submit console input only at an idle SMAPI prompt and do not type concurrently; classic Windows console input cannot prove that no partially typed cooked line already exists.",
    ];

    public static LiveLabCommandResult Execute(
        string action,
        string sourcePath,
        IReadOnlyList<string> companionPaths,
        IReadOnlyList<string> contentPackPaths,
        string labRoot,
        Func<DoctorReport> discoverInstallations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(companionPaths);
        ArgumentNullException.ThrowIfNull(contentPackPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(labRoot);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                SafeFullPath(sourcePath),
                SafeFullPath(labRoot),
                "blocked",
                [Problem("labPathInvalid", null, exception.Message)]);
        }

        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(paths.ProjectRoot);
            if (operationLock is null)
            {
                return Failure(
                    SafeFullPath(sourcePath),
                    paths.ProjectRoot,
                    "blocked",
                    [Problem(
                        "labBusy",
                        null,
                        "Another live-lab operation is still running for this lab root.")],
                    paths);
            }

            var stateStore = new JsonLiveLabStateStore(paths.StatePath);
            var service = new LiveLabService(
                paths,
                stateStore,
                new AlwaysOnBuilder(),
                new WindowsLabProcessHost(),
                discoverInstallations);
            return action switch
            {
                "start" => Start(
                    sourcePath,
                    companionPaths,
                    contentPackPaths,
                    paths,
                    stateStore,
                    service,
                    discoverInstallations),
                "status" => Status(paths, stateStore, service),
                "stop" => Stop(paths, stateStore, service),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                SafeFullPath(sourcePath),
                paths.ProjectRoot,
                "blocked",
                [Problem("projectReviewFailed", null, exception.Message)],
                paths);
        }
    }

    internal static LiveLabCommandResult ExecuteCommand(
        string command,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);

        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            return CommandFailure(
                SafeFullPath(labRoot),
                [Problem("reviewConsoleCommandInvalid", null, validationError)]);
        }

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(labRoot);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return CommandFailure(
                SafeFullPath(labRoot),
                [Problem("labPathInvalid", null, exception.Message)]);
        }

        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(paths.ProjectRoot);
            if (operationLock is null)
            {
                return CommandResult(
                    paths,
                    null,
                    "blocked",
                    null,
                    commandWritten: false,
                    [Problem(
                        "labBusy",
                        null,
                        "Another live-lab operation is still running for this lab root.")]);
            }

            ProjectReviewStagingResult staged = ProjectModStager.ReadReview(paths);
            if (staged.Problem is not null)
            {
                return CommandResult(
                    paths,
                    null,
                    "blocked",
                    null,
                    commandWritten: false,
                    [staged.Problem]);
            }

            var stateStore = new JsonLiveLabStateStore(paths.StatePath);
            LiveLabState? state = stateStore.Read();
            ProjectReviewProblem? bindingProblem = ReviewBindingProblem(
                state,
                staged.Staging,
                paths);
            if (bindingProblem is not null)
            {
                return CommandResult(
                    paths,
                    staged.Staging,
                    "blocked",
                    null,
                    commandWritten: false,
                    [bindingProblem]);
            }

            var service = new LiveLabService(
                paths,
                stateStore,
                new AlwaysOnBuilder(),
                new WindowsLabProcessHost(),
                () => throw new InvalidOperationException(
                    "Project-review console input must not run installation discovery."));
            LiveLabCommandResult status = service.StatusProjectReview();
            LiveLabReport lab = (LiveLabReport)status.Report;
            if (status.ExitCode != Success
                || !string.Equals(lab.State, "running", StringComparison.Ordinal))
            {
                IReadOnlyList<ProjectReviewProblem> problems = LabProblems(lab).ToArray();
                return CommandResult(
                    paths,
                    staged.Staging,
                    "blocked",
                    lab,
                    commandWritten: false,
                    problems.Count > 0
                        ? problems
                        : [Problem(
                            "reviewConsoleNotRunning",
                            null,
                            "The exact owned project-review process is not running; no console input was written.")]);
            }

            LiveLabState exactState = state!;
            if (!ProjectModReadyForConsole(lab.AlwaysOn, exactState.ProjectMod!))
            {
                return CommandResult(
                    paths,
                    staged.Staging,
                    "blocked",
                    lab,
                    commandWritten: false,
                    [Problem(
                        "reviewConsoleTargetNotReady",
                        null,
                        "The exact target mod has not reached its fully confirmed loaded state; no console input was written.")]);
            }

            ProjectReviewConsoleInputResult sent =
                (inputSender ?? new WindowsProjectReviewConsoleInputSender()).SendLine(
                    exactState.OwnedProcessIdentity,
                    command);
            ProjectReviewProblem? inputProblem = ConsoleInputProblem(sent);
            return CommandResult(
                paths,
                staged.Staging,
                inputProblem is null ? "running" : "blocked",
                lab,
                sent.CommandWritten,
                inputProblem is null ? [] : [inputProblem]);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return CommandResult(
                paths,
                null,
                "blocked",
                null,
                commandWritten: false,
                [Problem("projectReviewConsoleFailed", null, exception.Message)]);
        }
    }

    private static LiveLabCommandResult Start(
        string sourcePath,
        IReadOnlyList<string> companionPaths,
        IReadOnlyList<string> contentPackPaths,
        LiveLabPaths paths,
        JsonLiveLabStateStore stateStore,
        LiveLabService service,
        Func<DoctorReport> discoverInstallations)
    {
        ProjectReviewStagingResult retained = ProjectModStager.ReadReview(paths);
        if (retained.Problem is not null)
        {
            return Failure(
                SafeFullPath(sourcePath),
                paths.ProjectRoot,
                "blocked",
                [retained.Problem],
                paths,
                stagingRemoved: false);
        }

        LiveLabState? existing = stateStore.Read();
        if (existing is not null || retained.Staging is not null)
        {
            LiveLabCommandResult reconciled = ReconcileExisting(
                paths,
                stateStore,
                service,
                retained.Staging,
                forStart: true);
            if (reconciled.ExitCode != Success
                || stateStore.Read() is not null
                || ProjectModStager.ReadReview(paths).Staging is not null)
            {
                return reconciled;
            }
        }

        ProjectReviewPreparationResult preparation = ProjectModStager.PrepareReview(
            sourcePath,
            companionPaths,
            contentPackPaths,
            paths,
            discoverInstallations);
        if (preparation.Problem is not null)
        {
            return Failure(
                SafeFullPath(sourcePath),
                paths.ProjectRoot,
                preparation.Problem.Code.Contains(
                    "Collision",
                    StringComparison.OrdinalIgnoreCase)
                    ? "blocked"
                    : "failed",
                [preparation.Problem],
                paths,
                stagingRemoved: preparation.PreparationRoot is null);
        }

        ProjectReviewStagingResult staged = ProjectModStager.StageReview(
            preparation.Artifacts,
            paths);
        if (staged.Staging is null)
        {
            bool preparationRemoved = ProjectModStager.RemoveReviewPreparation(
                preparation.PreparationRoot,
                paths);
            var problems = new List<ProjectReviewProblem>
            {
                staged.Problem ?? Problem(
                    "reviewStagingFailed",
                    null,
                    "The exact project-review set could not be staged."),
            };
            if (!preparationRemoved)
            {
                problems.Add(Problem(
                    "reviewPreparationCleanupIncomplete",
                    null,
                    "The temporary project-review preparation directory was retained."));
            }

            return Failure(
                SafeFullPath(sourcePath),
                paths.ProjectRoot,
                "blocked",
                problems,
                paths,
                stagingRemoved: preparationRemoved
                    && !string.Equals(
                        staged.Problem?.Code,
                        "reviewStagingRollbackIncomplete",
                        StringComparison.Ordinal));
        }

        if (!ProjectModStager.RemoveReviewPreparation(
                preparation.PreparationRoot,
                paths))
        {
            ProjectReviewCleanupResult rollback = ProjectModStager.RemoveReview(paths);
            var problems = new List<ProjectReviewProblem>
            {
                Problem(
                    "reviewPreparationCleanupIncomplete",
                    null,
                    "The exact temporary preparation directory could not be removed, so no process was started."),
            };
            if (rollback.Problem is not null)
            {
                problems.Add(rollback.Problem);
            }

            return ReviewResult(
                paths,
                staged.Staging,
                "blocked",
                null,
                rollback.Removed,
                problems);
        }

        LiveLabCommandResult started = service.StartProjectReview(
            staged.Staging.TargetLaunchState);
        LiveLabReport? lab = started.Report as LiveLabReport;
        if (started.ExitCode == Success)
        {
            return ReviewResult(
                paths,
                staged.Staging,
                "running",
                lab,
                stagingRemoved: false,
                []);
        }

        bool stateRetained = stateStore.Read() is not null;
        ProjectReviewCleanupResult cleanup = stateRetained
            ? new ProjectReviewCleanupResult(
                false,
                Problem(
                    "reviewStagingCleanupDeferred",
                    null,
                    "The exact process outcome is uncertain, so the owned review staging was retained."))
            : ProjectModStager.RemoveReview(paths);
        var startProblems = LabProblems(lab).ToList();
        if (cleanup.Problem is not null)
        {
            startProblems.Add(cleanup.Problem);
        }

        return ReviewResult(
            paths,
            staged.Staging,
            stateRetained || !cleanup.Removed ? "blocked" : "failed",
            lab,
            cleanup.Removed,
            startProblems);
    }

    private static LiveLabCommandResult Status(
        LiveLabPaths paths,
        JsonLiveLabStateStore stateStore,
        LiveLabService service)
    {
        ProjectReviewStagingResult staged = ProjectModStager.ReadReview(paths);
        if (staged.Problem is not null)
        {
            return Failure(
                null,
                paths.ProjectRoot,
                "blocked",
                [staged.Problem],
                paths,
                stagingRemoved: false);
        }

        LiveLabState? state = stateStore.Read();
        if (state is null && staged.Staging is null)
        {
            return ReviewResult(
                paths,
                null,
                "stopped",
                null,
                stagingRemoved: true,
                []);
        }

        return ReconcileExisting(
            paths,
            stateStore,
            service,
            staged.Staging,
            forStart: false);
    }

    private static LiveLabCommandResult Stop(
        LiveLabPaths paths,
        JsonLiveLabStateStore stateStore,
        LiveLabService service)
    {
        ProjectReviewStagingResult staged = ProjectModStager.ReadReview(paths);
        if (staged.Problem is not null)
        {
            return Failure(
                null,
                paths.ProjectRoot,
                "blocked",
                [staged.Problem],
                paths,
                stagingRemoved: false);
        }

        LiveLabState? state = stateStore.Read();
        if (state is null && staged.Staging is null)
        {
            return ReviewResult(
                paths,
                null,
                "stopped",
                null,
                stagingRemoved: true,
                []);
        }

        ProjectReviewProblem? bindingProblem = ReviewBindingProblem(state, staged.Staging, paths);
        if (bindingProblem is not null)
        {
            return ReviewResult(
                paths,
                staged.Staging,
                "blocked",
                null,
                stagingRemoved: false,
                [bindingProblem]);
        }

        LiveLabCommandResult stopped = service.StopProjectReview();
        return CompleteAfterLabResult(
            paths,
            stateStore,
            staged.Staging!,
            stopped);
    }

    private static LiveLabCommandResult ReconcileExisting(
        LiveLabPaths paths,
        JsonLiveLabStateStore stateStore,
        LiveLabService service,
        ProjectReviewStaging? staging,
        bool forStart)
    {
        LiveLabState? state = stateStore.Read();
        ProjectReviewProblem? bindingProblem = ReviewBindingProblem(state, staging, paths);
        if (bindingProblem is not null)
        {
            return ReviewResult(
                paths,
                staging,
                "blocked",
                null,
                stagingRemoved: false,
                [bindingProblem]);
        }

        LiveLabCommandResult status = service.StatusProjectReview();
        LiveLabReport lab = (LiveLabReport)status.Report;
        if (string.Equals(lab.State, "running", StringComparison.Ordinal))
        {
            var problems = LabProblems(lab).ToList();
            if (forStart)
            {
                problems.Add(Problem(
                    "reviewAlreadyRunning",
                    null,
                    "The exact project-review process is already running."));
            }

            return ReviewResult(
                paths,
                staging,
                "running",
                lab,
                stagingRemoved: false,
                problems);
        }

        LiveLabCommandResult final = string.Equals(
            lab.State,
            "exited",
            StringComparison.Ordinal)
                ? service.FinalizeExitedProjectReview()
                : status;
        return CompleteAfterLabResult(paths, stateStore, staging!, final);
    }

    private static LiveLabCommandResult CompleteAfterLabResult(
        LiveLabPaths paths,
        JsonLiveLabStateStore stateStore,
        ProjectReviewStaging staging,
        LiveLabCommandResult labResult)
    {
        LiveLabReport lab = (LiveLabReport)labResult.Report;
        bool stateRetained = stateStore.Read() is not null;
        var problems = LabProblems(lab).ToList();
        if (stateRetained)
        {
            return ReviewResult(
                paths,
                staging,
                "blocked",
                lab,
                stagingRemoved: false,
                problems.Count > 0
                    ? problems
                    : [Problem(
                        "reviewStopIncomplete",
                        null,
                        "The exact review process has not reached a cleanup-safe terminal state.")]);
        }

        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(paths);
        if (cleanup.Problem is not null)
        {
            problems.Add(cleanup.Problem);
        }

        return ReviewResult(
            paths,
            staging,
            cleanup.Removed ? "stopped" : "blocked",
            lab,
            cleanup.Removed,
            problems);
    }

    private static ProjectReviewProblem? ReviewBindingProblem(
        LiveLabState? state,
        ProjectReviewStaging? staging,
        LiveLabPaths paths)
    {
        if (state is null || staging is null)
        {
            return Problem(
                "reviewOwnershipIncomplete",
                null,
                "The retained live-lab state and project-review staging ownership must both be present; nothing was changed.");
        }

        ProjectModLaunchState target = staging.TargetLaunchState;
        if (!string.Equals(state.Topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || state.TestSave is not null
            || state.NetworkTwo is not null
            || state.ProjectMod is null
            || !string.Equals(state.ModsPath, paths.ModsPath, PathComparison())
            || !string.Equals(
                state.ProjectMod.UniqueId,
                target.UniqueId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.ProjectMod.Version, target.Version, StringComparison.Ordinal)
            || !string.Equals(
                state.ProjectMod.BuildIdentity,
                target.BuildIdentity,
                StringComparison.Ordinal))
        {
            return Problem(
                "reviewOwnershipMismatch",
                null,
                "The retained live-lab state does not match the exact owned project-review target; nothing was changed.");
        }

        return null;
    }

    private static IEnumerable<ProjectReviewProblem> LabProblems(LiveLabReport? report) =>
        report?.Problems.Select(problem => Problem(problem.Code, null, problem.Message))
        ?? [];

    private static bool ProjectModReadyForConsole(
        AlwaysOnStatusReport? alwaysOn,
        ProjectModLaunchState expected)
    {
        ProjectModStatusReport? projectMod = alwaysOn?.ProjectMod;
        return string.Equals(alwaysOn?.State, "active", StringComparison.Ordinal)
            && alwaysOn?.PauseWhenOutOfFocus == false
            && projectMod is not null
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

    private static ProjectReviewProblem? ConsoleInputProblem(
        ProjectReviewConsoleInputResult result)
    {
        if (result.Status == ProjectReviewConsoleInputStatus.Written)
        {
            return null;
        }

        string message = result.Error ?? result.Status switch
        {
            ProjectReviewConsoleInputStatus.WrittenDetachFailed =>
                "The command was fully enqueued, but the one-shot console worker did not detach cleanly; do not retry it automatically.",
            ProjectReviewConsoleInputStatus.WrittenProcessExited =>
                "The command was fully enqueued, but the exact SMAPI process exited before delivery could be rechecked; do not retry it automatically.",
            ProjectReviewConsoleInputStatus.WrittenProcessUnreadable =>
                "The command was fully enqueued, but the exact SMAPI process became unreadable before delivery could be rechecked; do not retry it automatically.",
            ProjectReviewConsoleInputStatus.WrittenConsoleChanged =>
                "The command was fully enqueued, but the console process set changed before delivery could be rechecked; do not retry it automatically.",
            ProjectReviewConsoleInputStatus.ProcessExited =>
                "The exact owned SMAPI process exited before console input.",
            ProjectReviewConsoleInputStatus.ProcessIdentityMismatch =>
                "The PID no longer identifies the exact owned SMAPI process; no console input was written.",
            ProjectReviewConsoleInputStatus.ProcessUnreadable =>
                "The exact owned SMAPI process could not be verified; no console input was written.",
            ProjectReviewConsoleInputStatus.AttachFailed =>
                "The one-shot worker could not attach to the exact owned SMAPI console; no console input was written.",
            ProjectReviewConsoleInputStatus.SharedConsole =>
                "The SMAPI console has unexpected attached processes; no console input was written.",
            ProjectReviewConsoleInputStatus.InputBusy =>
                "The SMAPI console has pending input; no console input was written.",
            ProjectReviewConsoleInputStatus.InputOpenFailed =>
                "The exact SMAPI console input buffer could not be opened; no console input was written.",
            ProjectReviewConsoleInputStatus.WriteFailed =>
                "Windows did not enqueue any console input records.",
            ProjectReviewConsoleInputStatus.PartialWrite =>
                "Windows may have enqueued only part of the command; delivery is unknown and must not be retried automatically.",
            ProjectReviewConsoleInputStatus.WorkerTimedOut =>
                "The one-shot console worker timed out; delivery is unknown and must not be retried automatically.",
            ProjectReviewConsoleInputStatus.WorkerStartFailed =>
                "The one-shot console worker could not be started; no console input was written.",
            ProjectReviewConsoleInputStatus.WorkerParentMismatch =>
                "The internal console worker was not started by the exact SDVKit parent process; no console input was written.",
            _ => "The one-shot console worker failed; delivery is unknown and must not be retried automatically.",
        };
        string code = result.Status switch
        {
            ProjectReviewConsoleInputStatus.WrittenDetachFailed => "reviewConsoleDetachFailed",
            ProjectReviewConsoleInputStatus.WrittenProcessExited => "reviewConsoleProcessExitedAfterWrite",
            ProjectReviewConsoleInputStatus.WrittenProcessUnreadable => "reviewConsoleProcessUnreadableAfterWrite",
            ProjectReviewConsoleInputStatus.WrittenConsoleChanged => "reviewConsoleOwnershipChangedAfterWrite",
            ProjectReviewConsoleInputStatus.ProcessExited => "reviewConsoleProcessExited",
            ProjectReviewConsoleInputStatus.ProcessIdentityMismatch => "reviewConsoleIdentityMismatch",
            ProjectReviewConsoleInputStatus.ProcessUnreadable => "reviewConsoleProcessUnreadable",
            ProjectReviewConsoleInputStatus.AttachFailed => "reviewConsoleAttachFailed",
            ProjectReviewConsoleInputStatus.SharedConsole => "reviewConsoleShared",
            ProjectReviewConsoleInputStatus.InputBusy => "reviewConsoleInputBusy",
            ProjectReviewConsoleInputStatus.InputOpenFailed => "reviewConsoleInputOpenFailed",
            ProjectReviewConsoleInputStatus.WriteFailed => "reviewConsoleWriteFailed",
            ProjectReviewConsoleInputStatus.PartialWrite => "reviewConsolePartialWrite",
            ProjectReviewConsoleInputStatus.WorkerTimedOut => "reviewConsoleWorkerTimedOut",
            ProjectReviewConsoleInputStatus.WorkerStartFailed => "reviewConsoleWorkerStartFailed",
            ProjectReviewConsoleInputStatus.WorkerParentMismatch => "reviewConsoleWorkerParentMismatch",
            ProjectReviewConsoleInputStatus.InvalidRequest => "reviewConsoleCommandInvalid",
            _ => "reviewConsoleWorkerFailed",
        };
        return Problem(code, null, message);
    }

    private static LiveLabCommandResult CommandResult(
        LiveLabPaths paths,
        ProjectReviewStaging? staging,
        string state,
        LiveLabReport? lab,
        bool? commandWritten,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        var report = new ProjectReviewCommandReport(
            1,
            staging?.Target.SourceRoot,
            paths.ProjectRoot,
            state,
            lab,
            commandWritten,
            problems,
            [.. Warnings, .. CommandWarnings]);
        return new LiveLabCommandResult(
            problems.Count == 0 && commandWritten == true
                ? Success
                : OperationFailed,
            report);
    }

    private static LiveLabCommandResult CommandFailure(
        string labRoot,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        var report = new ProjectReviewCommandReport(
            1,
            null,
            labRoot,
            "blocked",
            null,
            false,
            problems,
            [.. Warnings, .. CommandWarnings]);
        return new LiveLabCommandResult(OperationFailed, report);
    }

    private static LiveLabCommandResult ReviewResult(
        LiveLabPaths paths,
        ProjectReviewStaging? staging,
        string state,
        LiveLabReport? lab,
        bool stagingRemoved,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        IReadOnlyList<ProjectReviewArtifactReport> artifacts = staging is null
            ? []
            : staging.Artifacts.Select(artifact => new ProjectReviewArtifactReport(
                artifact.Role,
                artifact.SourceRoot,
                artifact.Manifest.Kind,
                artifact.Manifest.UniqueId,
                artifact.Manifest.Version,
                artifact.Manifest.ContentPackFor,
                artifact.BuildIdentity,
                RelativePath(paths.ProjectRoot, artifact.StagingPath),
                artifact.BuildLog,
                artifact.PackageLog)).ToArray();
        var report = new ProjectReviewReport(
            1,
            staging?.Target.SourceRoot,
            paths.ProjectRoot,
            state,
            lab,
            artifacts,
            true,
            RelativePath(paths.ProjectRoot, paths.SavesPath),
            stagingRemoved,
            problems,
            Warnings);
        return new LiveLabCommandResult(
            problems.Count == 0 && state is "running" or "stopped"
                ? Success
                : OperationFailed,
            report);
    }

    private static LiveLabCommandResult Failure(
        string? root,
        string labRoot,
        string state,
        IReadOnlyList<ProjectReviewProblem> problems,
        LiveLabPaths? paths = null,
        bool stagingRemoved = true)
    {
        string savesPath = paths is null
            ? ".sdvkit/lab/profiles/single/AppData/Roaming/StardewValley/Saves"
            : RelativePath(paths.ProjectRoot, paths.SavesPath);
        var report = new ProjectReviewReport(
            1,
            root,
            labRoot,
            state,
            null,
            [],
            true,
            savesPath,
            stagingRemoved,
            problems,
            Warnings);
        return new LiveLabCommandResult(OperationFailed, report);
    }

    private static ProjectReviewProblem Problem(
        string code,
        string? path,
        string message) =>
        new(code, path, message);

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return path;
        }
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or PlatformNotSupportedException
            or SecurityException
            or UnauthorizedAccessException
            or JsonException;
}
