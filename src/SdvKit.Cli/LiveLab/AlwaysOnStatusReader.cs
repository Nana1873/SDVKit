using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed record AlwaysOnStatusReport(
    string State,
    int? Tick,
    bool? IsActive,
    bool? PauseWhenOutOfFocus,
    DateTimeOffset? ObservedAtUtc);

internal sealed record AlwaysOnStatusMarker(
    int SchemaVersion,
    string LaunchId,
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    string Phase,
    int Tick,
    bool IsActive,
    bool? PauseWhenOutOfFocus,
    DateTimeOffset ObservedAtUtc);

internal static class AlwaysOnStatusReader
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(5);

    public static AlwaysOnStatusReport Read(
        string statusPath,
        string launchId,
        OwnedProcessIdentity process,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchId);
        ArgumentNullException.ThrowIfNull(process);

        if (!File.Exists(statusPath))
        {
            return Pending();
        }

        AlwaysOnStatusMarker? marker;
        try
        {
            using FileStream stream = new(
                statusPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            marker = JsonSerializer.Deserialize<AlwaysOnStatusMarker>(
                stream,
                LiveLabJsonOptions.CamelCase);
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException
            or JsonException)
        {
            return new AlwaysOnStatusReport("invalid", null, null, null, null);
        }

        if (marker is null
            || marker.SchemaVersion != 1
            || marker.ProcessId != process.ProcessId
            || marker.ProcessStartTimeUtc == default
            || marker.ProcessStartTimeUtc.UtcTicks != process.StartTimeUtc.UtcTicks
            || !string.Equals(marker.LaunchId, launchId, StringComparison.Ordinal)
            || marker.ObservedAtUtc == default
            || marker.ObservedAtUtc.Offset != TimeSpan.Zero
            || marker.ProcessStartTimeUtc.Offset != TimeSpan.Zero
            || marker.Tick < 0
            || marker.Phase is not ("active" or "exiting" or "restoreFailed"))
        {
            return new AlwaysOnStatusReport("mismatch", null, null, null, null);
        }

        string state = marker.Phase;
        if (string.Equals(state, "active", StringComparison.Ordinal)
            && (marker.ObservedAtUtc > nowUtc.AddSeconds(1)
                || nowUtc - marker.ObservedAtUtc > FreshnessWindow))
        {
            state = "stale";
        }

        return new AlwaysOnStatusReport(
            state,
            marker.Tick,
            marker.IsActive,
            marker.PauseWhenOutOfFocus,
            marker.ObservedAtUtc);
    }

    private static AlwaysOnStatusReport Pending() =>
        new("pending", null, null, null, null);
}

internal static class LiveLabJsonOptions
{
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
