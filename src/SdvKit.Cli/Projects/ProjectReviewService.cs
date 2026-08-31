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
