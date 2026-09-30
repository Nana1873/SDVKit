using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectReviewService
{
    private static IEnumerable<ProjectReviewProblem> LabProblems(LiveLabReport? report) =>
        report?.Problems.Select(problem => Problem(problem.Code, null, problem.Message))
        ?? [];

    private static LiveLabCommandResult NetworkResult(
        LiveLabPaths paths,
        ProjectReviewStaging? staging,
        string state,
        NetworkTwoSmokeReport? network,
        bool fixtureReset,
        bool stagingRemoved,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        ProjectNetworkReviewRoleReport[] roles =
        [
            NetworkRoleReport(
                paths,
                staging,
                NetworkTwoContract.HostRole),
            NetworkRoleReport(
                paths,
                staging,
                NetworkTwoContract.FarmhandRole),
        ];
        var report = new ProjectNetworkReviewReport(
            1,
            NetworkTwoContract.Topology,
            staging?.Target.SourceRoot,
            paths.ProjectRoot,
            state,
            network,
            roles,
            true,
            fixtureReset,
            stagingRemoved,
            problems,
            NetworkWarnings);
        return new LiveLabCommandResult(
            problems.Count == 0 && state is "running" or "stopped"
                ? Success
                : OperationFailed,
            report);
    }

    private static LiveLabCommandResult NetworkFailure(
        string? root,
        string labRoot,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        ProjectNetworkReviewRoleReport[] roles =
        [
            new(
                NetworkTwoContract.HostRole,
                ".sdvkit/lab/profiles/network-2/host/AppData/Roaming/StardewValley",
                ".sdvkit/lab/profiles/network-2/host/AppData/Roaming/StardewValley/Saves",
                null,
                []),
            new(
                NetworkTwoContract.FarmhandRole,
                ".sdvkit/lab/profiles/network-2/farmhand/AppData/Roaming/StardewValley",
                ".sdvkit/lab/profiles/network-2/farmhand/AppData/Roaming/StardewValley/Saves",
                null,
                []),
        ];
        var report = new ProjectNetworkReviewReport(
            1,
            NetworkTwoContract.Topology,
            root,
            labRoot,
            "blocked",
            null,
            roles,
            true,
            false,
            false,
            problems,
            NetworkWarnings);
        return new LiveLabCommandResult(OperationFailed, report);
    }

    private static ProjectNetworkReviewRoleReport NetworkRoleReport(
        LiveLabPaths paths,
        ProjectReviewStaging? staging,
        string role)
    {
        LiveLabPaths rolePaths = LiveLabPaths.ResolveNetworkRole(paths, role);
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
                RelativePath(paths.ProjectRoot, artifact.StagingPathFor(role)),
                artifact.BuildLog,
                artifact.PackageLog)
            { CpRefresh = artifact.CpRefresh, ProjectFile = artifact.ProjectFile, ConfigReconciliation = artifact.ConfigReconciliation }).ToArray();
        return new ProjectNetworkReviewRoleReport(
            role,
            RelativePath(paths.ProjectRoot, rolePaths.StardewDataPath),
            RelativePath(paths.ProjectRoot, rolePaths.SavesPath),
            null,
            artifacts);
    }

    private static LiveLabCommandResult ReviewResult(
        LiveLabPaths paths,
        ProjectReviewStaging? staging,
        string state,
        LiveLabReport? lab,
        bool stagingRemoved,
        IReadOnlyList<ProjectReviewProblem> problems,
        bool fixtureReset = false)
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
                artifact.PackageLog)
            { CpRefresh = artifact.CpRefresh, ProjectFile = artifact.ProjectFile, ConfigReconciliation = artifact.ConfigReconciliation }).ToArray();
        var report = new ProjectReviewReport(
            1,
            staging?.Target.SourceRoot,
            paths.ProjectRoot,
            state,
            lab,
            artifacts,
            true,
            RelativePath(paths.ProjectRoot, paths.SavesPath),
            lab?.AlwaysOn?.TestSave,
            fixtureReset,
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
            null,
            false,
            stagingRemoved,
            problems,
            Warnings);
        return new LiveLabCommandResult(OperationFailed, report);
    }
}
