using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class AlwaysOnStatusReaderTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 30, 20, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ObservedAt = StartedAt.AddSeconds(10);

    [Fact]
    public void MissingMarkerIsPending()
    {
        using TemporaryDirectory temporary = new();

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            System.IO.Path.Combine(temporary.Path, "missing.json"),
            "launch-1",
            Process(),
            ObservedAt);

        Assert.Equal("pending", report.State);
        Assert.Null(report.Tick);
    }

    [Fact]
    public void MatchingFreshMarkerReportsActiveTicks()
    {
        using TemporaryDirectory temporary = new();
        string statusPath = WriteMarker(temporary, Process());

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt.AddSeconds(2));

        Assert.Equal("active", report.State);
        Assert.Equal(600, report.Tick);
        Assert.False(report.IsActive);
        Assert.False(report.PauseWhenOutOfFocus);
        Assert.Equal(ObservedAt, report.ObservedAtUtc);
    }

    [Fact]
    public void RealStatusWriterRoundTripsThroughReaderAndExactCurrentProcess()
    {
        using TemporaryDirectory temporary = new();
        const string launchId = "11111111111111111111111111111111";
        string statusPath = System.IO.Path.Combine(temporary.Path, "always-on.json");
        var writer = new StatusWriter(launchId, statusPath);

        writer.Write(
            "active",
            tick: 1234,
            isActive: false,
            pauseWhenOutOfFocus: false);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statusPath));
        JsonElement marker = document.RootElement;
        Assert.Equal(
            [
                "schemaVersion",
                "launchId",
                "processId",
                "processStartTimeUtc",
                "phase",
                "tick",
                "isActive",
                "pauseWhenOutOfFocus",
                "observedAtUtc",
            ],
            marker.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(1, marker.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(launchId, marker.GetProperty("launchId").GetString());
        Assert.Equal(Environment.ProcessId, marker.GetProperty("processId").GetInt32());
        Assert.Equal("active", marker.GetProperty("phase").GetString());
        Assert.Equal(1234, marker.GetProperty("tick").GetInt32());
        Assert.False(marker.GetProperty("isActive").GetBoolean());
        Assert.False(marker.GetProperty("pauseWhenOutOfFocus").GetBoolean());

        using System.Diagnostics.Process current =
            System.Diagnostics.Process.GetCurrentProcess();
        DateTimeOffset processStartTimeUtc = current.StartTime.ToUniversalTime();
        var process = new OwnedProcessIdentity(
            Environment.ProcessId,
            processStartTimeUtc,
            Environment.ProcessPath
                ?? throw new InvalidOperationException("The test process path is unavailable."));
        Assert.Equal(
            process.StartTimeUtc.UtcTicks,
            marker.GetProperty("processStartTimeUtc").GetDateTimeOffset().UtcTicks);
        DateTimeOffset observedAtUtc = marker
            .GetProperty("observedAtUtc")
            .GetDateTimeOffset();

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            launchId,
            process,
            observedAtUtc);

        Assert.Equal("active", report.State);
        Assert.Equal(1234, report.Tick);
        Assert.False(report.IsActive);
        Assert.False(report.PauseWhenOutOfFocus);
        Assert.Equal(observedAtUtc, report.ObservedAtUtc);
        if (OperatingSystem.IsWindows())
        {
            LabProcessInspectResult inspection = new WindowsLabProcessHost().Inspect(process);
            Assert.Equal(LabProcessInspectStatus.Running, inspection.Status);
        }
    }

    [Fact]
    public void MarkerForAnotherExactProcessIsRejected()
    {
        using TemporaryDirectory temporary = new();
        string statusPath = WriteMarker(
            temporary,
            Process() with { StartTimeUtc = StartedAt.AddTicks(1) });

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt);

        Assert.Equal("mismatch", report.State);
        Assert.Null(report.Tick);
    }

    [Fact]
    public void OldMarkerIsReportedAsStaleInsteadOfActive()
    {
        using TemporaryDirectory temporary = new();
        string statusPath = WriteMarker(temporary, Process());

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt.AddSeconds(6));

        Assert.Equal("stale", report.State);
        Assert.Equal(600, report.Tick);
    }

    [Fact]
    public void RestoreFailureIsReportedWithoutClaimingAConfirmedOptionValue()
    {
        using TemporaryDirectory temporary = new();
        string path = System.IO.Path.Combine(temporary.Path, "always-on.json");
        var marker = new AlwaysOnStatusMarker(
            1,
            "launch-1",
            Process().ProcessId,
            Process().StartTimeUtc,
            "restoreFailed",
            700,
            IsActive: false,
            PauseWhenOutOfFocus: null,
            ObservedAt);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt);

        Assert.Equal("restoreFailed", report.State);
        Assert.Null(report.PauseWhenOutOfFocus);
    }

    private static OwnedProcessIdentity Process() =>
        new(4242, StartedAt, @"E:\Games\StardewModdingAPI.exe");

    private static string WriteMarker(
        TemporaryDirectory temporary,
        OwnedProcessIdentity process)
    {
        string path = System.IO.Path.Combine(temporary.Path, "always-on.json");
        var marker = new AlwaysOnStatusMarker(
            1,
            "launch-1",
            process.ProcessId,
            process.StartTimeUtc,
            "active",
            600,
            IsActive: false,
            PauseWhenOutOfFocus: false,
            ObservedAt);
        File.WriteAllText(path, JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
        return path;
    }
}
