using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectReviewService
{
    private static LiveLabCommandResult ResetSingle(LiveLabPaths paths)
    {
        ProjectReviewStagingResult staged = ProjectModStager.ReadReviewForCleanup(
            paths,
            LiveLabState.SingleTopology);
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

        ProjectReviewStagingResult networkStaging =
            ProjectModStager.ReadReviewForCleanup(
                paths,
                NetworkTwoContract.Topology);
        if (networkStaging.Problem is not null)
        {
            return ReviewResult(
                paths,
                staged.Staging,
                "blocked",
                null,
                stagingRemoved: false,
                [networkStaging.Problem]);
        }

        (LiveLabState? Host, LiveLabState? Farmhand) networkStates =
            ReadNetworkStates(paths);
        LiveLabState? singleState = new JsonLiveLabStateStore(paths.StatePath).Read();
        if (singleState is not null
            || networkStates.Host is not null
            || networkStates.Farmhand is not null
            || networkStaging.Staging is not null)
        {
            return ReviewResult(
                paths,
                staged.Staging,
                "blocked",
                null,
                stagingRemoved: false,
                [Problem(
                    "reviewResetRequiresStoppedLab",
                    null,
                    "Single review reset requires the single lab, host, and farmhand to be stopped and no retained network-2 review; nothing was changed.")]);
        }

        try
        {
            new TestSaveFixtureStore(paths).ResetReview();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return ReviewResult(
                paths,
                staged.Staging,
                "blocked",
                null,
                stagingRemoved: false,
                [Problem("reviewFixtureResetFailed", null, exception.Message)]);
        }

        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(paths);
        IReadOnlyList<ProjectReviewProblem> problems = cleanup.Problem is null
            ? []
            : [cleanup.Problem];
        return ReviewResult(
            paths,
            staged.Staging,
            cleanup.Removed ? "stopped" : "blocked",
            null,
            cleanup.Removed,
            problems,
            fixtureReset: true);
    }

    private static LiveLabCommandResult Start(
        string sourcePath,
        IReadOnlyList<string> companionPaths,
        IReadOnlyList<string> contentPackPaths,
        LiveLabPaths paths,
        JsonLiveLabStateStore stateStore,
        LiveLabService service,
        Func<DoctorReport> discoverInstallations,
        bool useTestSave,
        string? projectFile)
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
        if (existing is not null
            && (existing.TestSave is not null) != useTestSave)
        {
            return ReviewResult(
                paths,
                retained.Staging,
                "blocked",
                null,
                stagingRemoved: false,
                [Problem(
                    "reviewTestSaveSelectionMismatch",
                    null,
                    "The retained single review does not match the requested --test-save selection; nothing was changed.")]);
        }

        if (retained.Staging is not null && (projectFile is not null || retained.Staging.Target.ProjectFile is not null
            || retained.Staging.Artifacts.Any(artifact => artifact.Role == ProjectReviewArtifactRole.ContentPack
                && PathEqualsBundleRoot(retained.Staging.Target.SourceRoot, artifact.SourceRoot))))
        {
            ProjectReviewProblem? requestProblem = ReviewSetRequestProblem(sourcePath, companionPaths, contentPackPaths, retained.Staging, projectFile);
            if (requestProblem is not null)
                return ReviewResult(paths, retained.Staging, "blocked", null, stagingRemoved: false, [requestProblem]);
        }

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

        if (useTestSave)
        {
            (LiveLabState? Host, LiveLabState? Farmhand) networkStates =
                ReadNetworkStates(paths);
            ProjectReviewStagingResult networkStaging = ProjectModStager.ReadReview(
                paths,
                NetworkTwoContract.Topology);
            if (networkStaging.Problem is not null)
            {
                return Failure(
                    SafeFullPath(sourcePath),
                    paths.ProjectRoot,
                    "blocked",
                    [networkStaging.Problem],
                    paths,
                    stagingRemoved: false);
            }

            if (networkStates.Host is not null
                || networkStates.Farmhand is not null
                || networkStaging.Staging is not null)
            {
                return Failure(
                    SafeFullPath(sourcePath),
                    paths.ProjectRoot,
                    "blocked",
                    [Problem(
                        "reviewFixtureInUse",
                        null,
                        "The shared SDVKit test-save is retained by network-2 review state or staging; stop and reset that review first.")],
                    paths,
                    stagingRemoved: true);
            }
        }

        ProjectProblem? gameProblem = ProjectBuilder.GetGamePath(discoverInstallations(), out string? selectedGame);
        if (gameProblem is not null)
            return Failure(SafeFullPath(sourcePath), paths.ProjectRoot, "blocked",
                [Problem(gameProblem.Code, null, "Select one complete game/SMAPI installation; run doctor --json for candidates and corrective actions.")], paths);

        ProjectReviewPreparationResult preparation = ProjectModStager.PrepareReview(
            sourcePath,
            companionPaths,
            contentPackPaths,
            paths,
            discoverInstallations,
            projectFile: projectFile);
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
            paths,
            gamePath: selectedGame);
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
            staged.Staging.TargetLaunchState,
            useTestSave);
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
        bool fixtureCleanupDeferred = lab?.Problems.Any(problem =>
            string.Equals(
                problem.Code,
                "testSaveCleanupDeferred",
                StringComparison.Ordinal)
            || string.Equals(
                problem.Code,
                "testSaveCleanupFailed",
                StringComparison.Ordinal)) == true;
        bool ownershipRetained = stateRetained || fixtureCleanupDeferred;
        ProjectReviewCleanupResult cleanup = ownershipRetained
            ? new ProjectReviewCleanupResult(
                false,
                Problem(
                    "reviewStagingCleanupDeferred",
                    null,
                    "The exact process or fixture cleanup outcome is uncertain, so the owned review staging was retained."))
            : ProjectModStager.RemoveReview(paths);
        var startProblems = LabProblems(lab).ToList();
        if (cleanup.Problem is not null)
        {
            startProblems.Add(cleanup.Problem);
        }

        return ReviewResult(
            paths,
            staged.Staging,
            ownershipRetained || !cleanup.Removed ? "blocked" : "failed",
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
        ProjectReviewStagingResult staged = ProjectModStager.ReadReviewForCleanup(
            paths,
            LiveLabState.SingleTopology);
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
        TestSaveLaunchState? testSave = state.TestSave;
        bool testSaveBindingValid = testSave is null
            || (string.Equals(
                    testSave.Mode,
                    TestSaveContract.ReviewMode,
                    StringComparison.Ordinal)
                && string.Equals(
                    testSave.WorkPath,
                    paths.TestSaveWorkPath,
                    PathComparison())
                && string.Equals(
                    testSave.ScenarioLogPath,
                    paths.TestSaveScenarioLogPath,
                    PathComparison())
                && string.Equals(
                    testSave.SlotPath,
                    Path.Combine(paths.SavesPath, testSave.Identity.SaveId),
                    PathComparison()));
        if (!string.Equals(state.Topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || staging.Target.CpRefresh is { } refresh && refresh.LaunchId != state.LaunchId
            || staging.Artifacts.Any(a => a.ConfigReconciliation is { } config && config.LaunchId != state.LaunchId)
            || !testSaveBindingValid
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
}
