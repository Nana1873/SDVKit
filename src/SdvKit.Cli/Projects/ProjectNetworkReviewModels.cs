using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal sealed record ProjectNetworkReviewRoleReport(
    string Role,
    string StardewDataPath,
    string SavesPath,
    LiveLabReport? Lab,
    IReadOnlyList<ProjectReviewArtifactReport> Artifacts);

internal sealed record ProjectNetworkReviewReport(
    int SchemaVersion,
    string Topology,
    string? Root,
    string LabRoot,
    string State,
    NetworkTwoSmokeReport? Network,
    IReadOnlyList<ProjectNetworkReviewRoleReport> Roles,
    bool InteractiveConsole,
    bool FixtureReset,
    bool StagingRemoved,
    IReadOnlyList<ProjectReviewProblem> Problems,
    IReadOnlyList<string> Warnings);

internal sealed record ProjectNetworkReviewCommandReport(
    int SchemaVersion,
    string Topology,
    string? Root,
    string LabRoot,
    string Role,
    string State,
    LiveLabReport? Lab,
    bool? CommandWritten,
    IReadOnlyList<ProjectReviewProblem> Problems,
    IReadOnlyList<string> Warnings);
