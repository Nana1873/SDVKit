namespace SdvKit.Cli.LiveLab;

internal static class RuntimeSnapshotContract
{
    public const int SchemaVersion = 1;
}

internal sealed record RuntimeSnapshotMarker(
    int SchemaVersion,
    bool WorldReady,
    string? Season,
    int? DayOfMonth,
    int? Year,
    int? TimeOfDay,
    string? LocationId,
    int? TileX,
    int? TileY,
    bool MenuOpen,
    DateTimeOffset ObservedAtUtc,
    LocalPlayerSnapshot? LocalPlayer = null);

internal sealed record RuntimeSnapshotReport(
    string State,
    int? SchemaVersion,
    bool? WorldReady,
    string? Season,
    int? DayOfMonth,
    int? Year,
    int? TimeOfDay,
    string? LocationId,
    int? TileX,
    int? TileY,
    bool? MenuOpen,
    DateTimeOffset? ObservedAtUtc,
    LocalPlayerSnapshot? LocalPlayer = null);
