using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed record AlwaysOnStatusReport(
    string State,
    int? Tick,
    bool? IsActive,
    bool? PauseWhenOutOfFocus,
    DateTimeOffset? ObservedAtUtc,
    TestSaveStatusReport? TestSave = null);

internal sealed record AlwaysOnStatusMarker(
    int SchemaVersion,
    string LaunchId,
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    string Phase,
    int Tick,
    bool IsActive,
    bool? PauseWhenOutOfFocus,
    DateTimeOffset ObservedAtUtc,
    TestSaveStatusMarker? TestSave = null);

internal static class AlwaysOnStatusReader
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(5);

    public static AlwaysOnStatusReport Read(
        string statusPath,
        string launchId,
        OwnedProcessIdentity process,
        DateTimeOffset nowUtc,
        TestSaveLaunchState? expectedTestSave = null)
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

        TestSaveStatusReport? testSave = ReadTestSave(marker.TestSave, expectedTestSave);
        return new AlwaysOnStatusReport(
            state,
            marker.Tick,
            marker.IsActive,
            marker.PauseWhenOutOfFocus,
            marker.ObservedAtUtc,
            testSave);
    }

    private static TestSaveStatusReport? ReadTestSave(
        TestSaveStatusMarker? marker,
        TestSaveLaunchState? expected)
    {
        if (expected is null)
        {
            return marker is null
                ? null
                : InvalidTestSave("unexpected");
        }

        if (marker is null)
        {
            return new TestSaveStatusReport(
                "pending",
                expected.Mode,
                null,
                expected.Identity.FixtureId,
                expected.Identity.SaveId,
                null,
                null,
                null,
                expected.ScenarioLogPath);
        }

        bool knownPhase = marker.Phase is "waitingForTitle"
            or "creating"
            or "created"
            or "loading"
            or "waiting"
            or "passed"
            or "failed";
        if (marker.SchemaVersion != TestSaveContract.SchemaVersion
            || !knownPhase
            || marker.WaitedTicks < 0
            || string.IsNullOrWhiteSpace(marker.ScenarioLogPath))
        {
            return InvalidTestSave("invalid");
        }

        if (!string.Equals(marker.Mode, expected.Mode, StringComparison.Ordinal)
            || !string.Equals(
                marker.FixtureId,
                expected.Identity.FixtureId,
                StringComparison.Ordinal)
            || !string.Equals(marker.SaveId, expected.Identity.SaveId, StringComparison.Ordinal)
            || !PathsEqual(marker.ScenarioLogPath, expected.ScenarioLogPath))
        {
            return InvalidTestSave("mismatch");
        }

        bool wrongModePhase = marker.Mode switch
        {
            TestSaveContract.CreateMode => marker.Phase is "loading" or "waiting" or "passed",
            TestSaveContract.ScenarioMode => marker.Phase is "creating" or "created",
            _ => true,
        };
        bool missingVerification = marker.Phase is "created" or "waiting" or "passed"
            && !marker.IdentityVerified;
        bool insufficientScenarioWait = string.Equals(
                marker.Mode,
                TestSaveContract.ScenarioMode,
                StringComparison.Ordinal)
            && string.Equals(marker.Phase, "passed", StringComparison.Ordinal)
            && marker.WaitedTicks < TestSaveContract.RequiredScenarioTicks;
        if (wrongModePhase || missingVerification || insufficientScenarioWait)
        {
            return InvalidTestSave("invalid");
        }

        return new TestSaveStatusReport(
            "ready",
            marker.Mode,
            marker.Phase,
            marker.FixtureId,
            marker.SaveId,
            marker.IdentityVerified,
            marker.WaitedTicks,
            marker.Message,
            marker.ScenarioLogPath);
    }

    private static TestSaveStatusReport InvalidTestSave(string state) =>
        new(state, null, null, null, null, null, null, null, null);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
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
