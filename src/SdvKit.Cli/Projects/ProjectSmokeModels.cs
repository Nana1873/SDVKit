using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal sealed record ProjectSmokeProblem(
    string Code,
    string? Path,
    string Message);

internal sealed record ProjectSmokeArtifactReport(
    string UniqueId,
    string Version,
    string DeclaredVersion,
    string Archive,
    IReadOnlyList<string> Entries,
    string PackageHash,
    string BuildIdentity,
    string? BuildLog,
    string? PackageLog);

internal sealed record ProjectSmokeRoleReport(
    string Role,
    string State,
    string StagingPath,
    string StagedBuildIdentity,
    bool LoadConfirmed,
    string? LoadedUniqueId,
    string? LoadedVersion,
    int RequiredTicks,
    int? ObservedTicks,
    IReadOnlyList<string> LogPaths);

internal sealed record ProjectSmokeReport(
    int SchemaVersion,
    string Root,
    string LabRoot,
    string Topology,
    string State,
    ProjectSmokeArtifactReport? Artifact,
    IReadOnlyList<ProjectSmokeRoleReport> Roles,
    bool FixtureReset,
    bool StagingRemoved,
    IReadOnlyList<string> LoadErrors,
    IReadOnlyList<ProjectSmokeProblem> Problems,
    IReadOnlyList<string> Warnings);

internal sealed record ProjectModManifestInfo(
    string Name,
    string UniqueId,
    string Version,
    string EntryDll,
    IReadOnlyList<ProjectModDependencyInfo> RequiredDependencies);

internal sealed record ProjectModDependencyInfo(
    string UniqueId,
    string? MinimumVersion);

internal sealed record ProjectModManifestReadResult(
    ProjectModManifestInfo? Manifest,
    ProjectSmokeProblem? Problem);

internal sealed record ProjectModArtifact(
    ProjectModManifestInfo Manifest,
    string ArchivePath,
    string ArchiveRelativePath,
    IReadOnlyList<string> Entries,
    string TopLevelDirectory,
    string PackageHash,
    string BuildIdentity);

internal sealed record ProjectModStaging(
    ProjectModArtifact Artifact,
    string Topology,
    string OwnershipPath,
    IReadOnlyList<string> StagingPaths)
{
    public ProjectModLaunchState LaunchState => new(
        Artifact.Manifest.UniqueId,
        ProjectModLaunchState.NormalizeVersion(Artifact.Manifest.Version),
        Artifact.BuildIdentity);
}

internal sealed record ProjectModStagingResult(
    ProjectModStaging? Staging,
    ProjectSmokeProblem? Problem);

internal sealed record ProjectModCleanupResult(
    bool Removed,
    ProjectSmokeProblem? Problem);
