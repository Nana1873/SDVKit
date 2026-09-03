namespace SdvKit.Cli.LiveLab;

internal sealed record NetworkTwoRoleReport(
    string Role,
    string State,
    string? LaunchId,
    int? ProcessId,
    DateTimeOffset? ProcessStartTimeUtc,
    string? ExecutablePath,
    AlwaysOnStatusReport? AlwaysOn,
    bool ContinuedWhileUnfocused,
    int? FirstUnfocusedTick,
    int? LastUnfocusedTick,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> LogPaths);

internal sealed record NetworkTwoSmokeReport(
    int SchemaVersion,
    string Topology,
    string State,
    string? FixtureId,
    string? SaveId,
    string? BuildIdentity,
    bool FixtureReset,
    NetworkTwoRoleReport Host,
    NetworkTwoRoleReport Farmhand,
    IReadOnlyList<LiveLabProblem> Problems,
    IReadOnlyList<string> Warnings);
