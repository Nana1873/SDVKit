using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectReviewService
{
    private static LiveLabCommandResult StartNetwork(
        string sourcePath,
        IReadOnlyList<string> companionPaths,
        IReadOnlyList<string> contentPackPaths,
        LiveLabPaths paths,
        Func<DoctorReport> discoverInstallations,
        string? projectFile)
    {
        ProjectReviewStagingResult retained = ProjectModStager.ReadReview(
            paths,
            NetworkTwoContract.Topology);
        if (retained.Problem is not null)
        {
            return NetworkResult(
                paths,
                null,
                "blocked",
                null,
                fixtureReset: false,
                stagingRemoved: false,
                [retained.Problem]);
        }

        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        LiveLabState? hostState = new JsonLiveLabStateStore(hostPaths.StatePath).Read();
        LiveLabState? farmhandState = new JsonLiveLabStateStore(farmhandPaths.StatePath).Read();
        if (hostState is not null || farmhandState is not null)
        {
            if (retained.Staging is null)
            {
                return NetworkResult(
                    paths,
                    null,
                    "blocked",
                    null,
                    fixtureReset: false,
                    stagingRemoved: false,
                    [Problem(
                        "reviewOwnershipIncomplete",
                        null,
                        "Retained network-2 role state exists without exact project-review staging ownership; nothing was changed.")]);
            }

            ProjectReviewProblem? requestProblem = ReviewSetRequestProblem(
                sourcePath,
                companionPaths,
                contentPackPaths,
                retained.Staging,
                projectFile);
            if (requestProblem is not null)
            {
                return NetworkResult(
                    paths,
                    retained.Staging,
                    "blocked",
                    null,
                    fixtureReset: false,
                    stagingRemoved: false,
                    [requestProblem]);
            }

            if (retained.Staging.GamePath is not null)
            {
                ProjectReviewProblem? gameProblem = RetainedReviewGameProblem(retained.Staging, discoverInstallations());
                if (gameProblem is not null)
                    return NetworkResult(paths, retained.Staging, "blocked", null, fixtureReset: false, stagingRemoved: false, [gameProblem]);
            }

            LiveLabCommandResult existing = NetworkTwoSmokeService.StatusReviewWithinLock(
                paths.ProjectRoot,
                retained.Staging.TargetLaunchState);
            NetworkTwoSmokeReport network = RequireNetworkReport(existing);
            var problems = NetworkProblems(network).ToList();
            problems.Add(Problem(
                "reviewAlreadyRunning",
                null,
                "The exact network-2 review has retained role state; use status or stop instead of starting another pair."));
            return NetworkResult(
                paths,
                retained.Staging,
                network.State,
                network,
                fixtureReset: network.FixtureReset,
                stagingRemoved: false,
                problems);
        }

        ProjectReviewStaging staging;
        bool resetFromBaseline;
        if (retained.Staging is not null)
        {
            ProjectReviewProblem? requestProblem = ReviewSetRequestProblem(
                sourcePath,
                companionPaths,
                contentPackPaths,
                retained.Staging,
                projectFile);
            if (requestProblem is not null)
            {
                return NetworkResult(
                    paths,
                    retained.Staging,
                    "blocked",
                    null,
                    fixtureReset: false,
                    stagingRemoved: false,
                    [requestProblem]);
            }

            ProjectReviewProblem? gameProblem = RetainedReviewGameProblem(retained.Staging, discoverInstallations());
            if (gameProblem is not null)
                return NetworkResult(paths, retained.Staging, "blocked", null, fixtureReset: false, stagingRemoved: false, [gameProblem]);

            staging = retained.Staging;
            resetFromBaseline = false;
        }
        else
        {
            ProjectProblem? gameProblem = ProjectBuilder.GetGamePath(discoverInstallations(), out string? selectedGame);
            if (gameProblem is not null)
                return NetworkResult(paths, null, "blocked", null, fixtureReset: false, stagingRemoved: true,
                    [Problem(gameProblem.Code, null, "Select one complete game/SMAPI installation; run doctor --json for candidates and corrective actions.")]);

            ProjectReviewPreparationResult preparation = ProjectModStager.PrepareReview(
                sourcePath,
                companionPaths,
                contentPackPaths,
                paths,
                discoverInstallations,
                projectFile: projectFile);
            if (preparation.Problem is not null)
            {
                return NetworkResult(
                    paths,
                    null,
                    preparation.Problem.Code.Contains(
                        "Collision",
                        StringComparison.OrdinalIgnoreCase)
                            ? "blocked"
                            : "failed",
                    null,
                    fixtureReset: false,
                    stagingRemoved: preparation.PreparationRoot is null,
                    [preparation.Problem]);
            }

            ProjectReviewStagingResult staged = ProjectModStager.StageReview(
                preparation.Artifacts,
                NetworkTwoContract.Topology,
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
                        "The exact network-2 project-review set could not be staged."),
                };
                if (!preparationRemoved)
                {
                    problems.Add(Problem(
                        "reviewPreparationCleanupIncomplete",
                        null,
                        "The temporary project-review preparation directory was retained."));
                }

                return NetworkResult(
                    paths,
                    null,
                    "blocked",
                    null,
                    fixtureReset: false,
                    stagingRemoved: preparationRemoved,
                    problems);
            }

            staging = staged.Staging;
            if (!ProjectModStager.RemoveReviewPreparation(
                    preparation.PreparationRoot,
                    paths))
            {
                ProjectReviewCleanupResult rollback = ProjectModStager.RemoveReview(
                    paths,
                    NetworkTwoContract.Topology);
                var problems = new List<ProjectReviewProblem>
                {
                    Problem(
                        "reviewPreparationCleanupIncomplete",
                        null,
                        "The exact temporary preparation directory could not be removed, so no role process was started."),
                };
                if (rollback.Problem is not null)
                {
                    problems.Add(rollback.Problem);
                }

                return NetworkResult(
                    paths,
                    staging,
                    "blocked",
                    null,
                    fixtureReset: false,
                    stagingRemoved: rollback.Removed,
                    problems);
            }

            resetFromBaseline = true;
        }

        LiveLabCommandResult started = NetworkTwoSmokeService.StartReviewWithinLock(
            paths.ProjectRoot,
            discoverInstallations,
            staging.TargetLaunchState,
            resetFromBaseline);
        NetworkTwoSmokeReport startedNetwork = RequireNetworkReport(started);
        return NetworkResult(
            paths,
            staging,
            startedNetwork.State,
            startedNetwork,
            fixtureReset: startedNetwork.FixtureReset,
            stagingRemoved: false,
            NetworkProblems(startedNetwork).ToArray());
    }

    private static LiveLabCommandResult StatusNetwork(LiveLabPaths paths)
    {
        ProjectReviewStagingResult staged = ProjectModStager.ReadReview(
            paths,
            NetworkTwoContract.Topology);
        if (staged.Problem is not null)
        {
            return NetworkResult(
                paths,
                null,
                "blocked",
                null,
                fixtureReset: false,
                stagingRemoved: false,
                [staged.Problem]);
        }

        (LiveLabState? Host, LiveLabState? Farmhand) states = ReadNetworkStates(paths);
        if (states.Host is null && states.Farmhand is null)
        {
            return NetworkResult(
                paths,
                staged.Staging,
                "stopped",
                null,
                fixtureReset: false,
                stagingRemoved: staged.Staging is null,
                []);
        }

        if (staged.Staging is null)
        {
            return NetworkResult(
                paths,
                null,
                "blocked",
                null,
                fixtureReset: false,
                stagingRemoved: false,
                [Problem(
                    "reviewOwnershipIncomplete",
                    null,
                    "Retained network-2 role state exists without exact project-review staging ownership; nothing was changed.")]);
        }

        LiveLabCommandResult status = NetworkTwoSmokeService.StatusReviewWithinLock(
            paths.ProjectRoot,
            staged.Staging.TargetLaunchState);
        NetworkTwoSmokeReport network = RequireNetworkReport(status);
        return NetworkResult(
            paths,
            staged.Staging,
            network.State,
            network,
            network.FixtureReset,
            stagingRemoved: false,
            NetworkProblems(network).ToArray());
    }

    private static LiveLabCommandResult StopNetwork(LiveLabPaths paths)
    {
        ProjectReviewStagingResult staged = ProjectModStager.ReadReviewForCleanup(
            paths,
            NetworkTwoContract.Topology);
        if (staged.Problem is not null)
        {
            return NetworkResult(
                paths,
                null,
                "blocked",
                null,
                fixtureReset: false,
                stagingRemoved: false,
                [staged.Problem]);
        }

        (LiveLabState? Host, LiveLabState? Farmhand) states = ReadNetworkStates(paths);
        if (states.Host is null && states.Farmhand is null)
        {
            return NetworkResult(
                paths,
                staged.Staging,
                "stopped",
                null,
                fixtureReset: false,
                stagingRemoved: staged.Staging is null,
                []);
        }

        if (staged.Staging is null)
        {
            return NetworkResult(
                paths,
                null,
                "blocked",
                null,
                fixtureReset: false,
                stagingRemoved: false,
                [Problem(
                    "reviewOwnershipIncomplete",
                    null,
                    "Retained network-2 role state exists without exact project-review staging ownership; nothing was changed.")]);
        }

        LiveLabCommandResult stopped = NetworkTwoSmokeService.StopReviewWithinLock(
            paths.ProjectRoot,
            staged.Staging.TargetLaunchState);
        NetworkTwoSmokeReport network = RequireNetworkReport(stopped);
        return NetworkResult(
            paths,
            staged.Staging,
            network.State,
            network,
            network.FixtureReset,
            stagingRemoved: false,
            NetworkProblems(network).ToArray());
    }

    private static LiveLabCommandResult ResetNetwork(LiveLabPaths paths)
    {
        ProjectReviewStagingResult staged = ProjectModStager.ReadReviewForCleanup(
            paths,
            NetworkTwoContract.Topology);
        if (staged.Problem is not null)
        {
            return NetworkResult(
                paths,
                null,
                "blocked",
                null,
                fixtureReset: false,
                stagingRemoved: false,
                [staged.Problem]);
        }

        (LiveLabState? Host, LiveLabState? Farmhand) states = ReadNetworkStates(paths);
        LiveLabState? singleState = new JsonLiveLabStateStore(paths.StatePath).Read();
        if (states.Host is not null || states.Farmhand is not null || singleState is not null)
        {
            return NetworkResult(
                paths,
                staged.Staging,
                "blocked",
                null,
                fixtureReset: false,
                stagingRemoved: false,
                [Problem(
                    "reviewResetRequiresStoppedLab",
                    null,
                    "Network-2 review reset requires the single lab, host, and farmhand to be stopped; nothing was changed.")]);
        }

        try
        {
            LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
                paths,
                NetworkTwoContract.HostRole);
            new TestSaveFixtureStore(hostPaths).ResetReview();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return NetworkResult(
                paths,
                staged.Staging,
                "blocked",
                null,
                fixtureReset: false,
                stagingRemoved: false,
                [Problem("reviewFixtureResetFailed", null, exception.Message)]);
        }

        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(
            paths,
            NetworkTwoContract.Topology);
        IReadOnlyList<ProjectReviewProblem> problems = cleanup.Problem is null
            ? []
            : [cleanup.Problem];
        return NetworkResult(
            paths,
            staged.Staging,
            cleanup.Removed ? "stopped" : "blocked",
            null,
            fixtureReset: true,
            stagingRemoved: cleanup.Removed,
            problems);
    }

    private static ProjectReviewProblem? NetworkReviewBindingProblem(
        LiveLabState? state,
        ProjectReviewStaging staging,
        LiveLabPaths rolePaths,
        string role)
    {
        ProjectModLaunchState target = staging.TargetLaunchState;
        bool host = string.Equals(role, NetworkTwoContract.HostRole, StringComparison.Ordinal);
        if (state is null
            || !string.Equals(staging.Topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
            || !string.Equals(state.Topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
            || !string.Equals(state.NetworkTwo?.Role, role, StringComparison.Ordinal)
            || host != (state.TestSave is not null)
            || (host && !string.Equals(
                state.TestSave?.Mode,
                TestSaveContract.ReviewMode,
                StringComparison.Ordinal))
            || state.ProjectMod is null
            || !string.Equals(state.ModsPath, rolePaths.ModsPath, PathComparison())
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
                $"The retained {role} state does not match the exact owned network-2 review target and role binding; no console input was written.");
        }

        return null;
    }

    private static (LiveLabState? Host, LiveLabState? Farmhand) ReadNetworkStates(
        LiveLabPaths paths)
    {
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        return (
            new JsonLiveLabStateStore(hostPaths.StatePath).Read(),
            new JsonLiveLabStateStore(farmhandPaths.StatePath).Read());
    }

    private static NetworkTwoSmokeReport RequireNetworkReport(
        LiveLabCommandResult result) =>
        result.Report as NetworkTwoSmokeReport
        ?? throw new InvalidDataException(
            "The network-2 review returned an unexpected report type.");

    private static IEnumerable<ProjectReviewProblem> NetworkProblems(
        NetworkTwoSmokeReport report) =>
        report.Problems.Select(problem => Problem(problem.Code, null, problem.Message));

    private static bool NetworkPairRunningForPreScenarioCommand(
        NetworkTwoSmokeReport report) =>
        RoleRunningWithActiveAlwaysOn(report.Host)
        && RoleRunningWithActiveAlwaysOn(report.Farmhand);

    private static bool RoleRunningWithActiveAlwaysOn(NetworkTwoRoleReport role) =>
        string.Equals(role.State, "running", StringComparison.Ordinal)
        && string.Equals(role.AlwaysOn?.State, "active", StringComparison.Ordinal)
        && role.AlwaysOn?.PauseWhenOutOfFocus == false;
}
