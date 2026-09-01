using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static class ProjectReviewArtifactRole
{
    public const string Target = "target";
    public const string Companion = "companion";
    public const string ContentPack = "contentPack";
}

internal sealed record ProjectReviewProblem(
    string Code,
    string? Path,
    string Message);

internal sealed record ProjectReviewDependency(
    string UniqueId,
    string? MinimumVersion);

internal sealed record ProjectReviewManifest(
    string Kind,
    string Name,
    string UniqueId,
    string Version,
    string? EntryDll,
    string? ContentPackFor,
    string? ContentPackForMinimumVersion,
    IReadOnlyList<ProjectReviewDependency> RequiredDependencies);

internal sealed record ProjectReviewPreparedArtifact(
    string Role,
    string SourceRoot,
    string PreparedPath,
    string TopLevelDirectory,
    ProjectReviewManifest Manifest,
    string BuildIdentity,
    string? BuildLog,
    string? PackageLog);

internal sealed record ProjectReviewPreparationResult(
    IReadOnlyList<ProjectReviewPreparedArtifact> Artifacts,
    string? PreparationRoot,
    ProjectReviewProblem? Problem);

internal sealed record ProjectReviewRoleStagingPath(
    string Role,
    string StagingPath);

internal sealed record ProjectReviewOwnedArtifact(
    string Role,
    string SourceRoot,
    string TopLevelDirectory,
    IReadOnlyList<ProjectReviewRoleStagingPath> RoleStagingPaths,
    ProjectReviewManifest Manifest,
    string BuildIdentity,
    string? BuildLog,
    string? PackageLog)
{
    [JsonIgnore]
    public string StagingPath => RoleStagingPaths.Count == 1
        ? RoleStagingPaths[0].StagingPath
        : throw new InvalidOperationException(
            "A network-2 review artifact has role-specific staging paths; select one by role.");

    public string StagingPathFor(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return RoleStagingPaths.Single(path => string.Equals(
            path.Role,
            role,
            StringComparison.Ordinal)).StagingPath;
    }
}

internal sealed record ProjectReviewStaging(
    int SchemaVersion,
    string Topology,
    string OwnershipPath,
    IReadOnlyList<ProjectReviewOwnedArtifact> Artifacts)
{
    [JsonIgnore]
    public ProjectReviewOwnedArtifact Target => Artifacts.Single(artifact =>
        string.Equals(
            artifact.Role,
            ProjectReviewArtifactRole.Target,
            StringComparison.Ordinal));

    [JsonIgnore]
    public ProjectModLaunchState TargetLaunchState => new(
        Target.Manifest.UniqueId,
        ProjectModLaunchState.NormalizeVersion(Target.Manifest.Version),
        Target.BuildIdentity);
}

internal sealed record ProjectReviewStagingResult(
    ProjectReviewStaging? Staging,
    ProjectReviewProblem? Problem);

internal sealed record ProjectReviewCleanupResult(
    bool Removed,
    ProjectReviewProblem? Problem);

internal sealed record ProjectReviewArtifactReport(
    string Role,
    string SourceRoot,
    string Kind,
    string UniqueId,
    string Version,
    string? ContentPackFor,
    string BuildIdentity,
    string StagingPath,
    string? BuildLog,
    string? PackageLog);

internal sealed record ProjectReviewReport(
    int SchemaVersion,
    string? Root,
    string LabRoot,
    string State,
    LiveLabReport? Lab,
    IReadOnlyList<ProjectReviewArtifactReport> Artifacts,
    bool InteractiveConsole,
    string PersistentSavesPath,
    bool StagingRemoved,
    IReadOnlyList<ProjectReviewProblem> Problems,
    IReadOnlyList<string> Warnings);

internal sealed record ProjectReviewCommandReport(
    int SchemaVersion,
    string? Root,
    string LabRoot,
    string State,
    LiveLabReport? Lab,
    bool? CommandWritten,
    IReadOnlyList<ProjectReviewProblem> Problems,
    IReadOnlyList<string> Warnings);
