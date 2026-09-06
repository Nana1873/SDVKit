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
    public void RegularReadRecoversAfterMalformedMarkerIsReplaced()
    {
        using TemporaryDirectory temporary = new();
        string path = WriteMarker(temporary, Process());
        Assert.Equal("active", AlwaysOnStatusReader.Read(path, "launch-1", Process(), ObservedAt).State);

        File.WriteAllText(path, "{");

        AlwaysOnStatusReport unavailable = AlwaysOnStatusReader.Read(path, "launch-1", Process(), ObservedAt);
        Assert.Equal("invalid", unavailable.State);
        Assert.Null(unavailable.Tick);
        Assert.Null(unavailable.ObservedAtUtc);

        WriteMarker(temporary, Process(), observedAt: ObservedAt.AddSeconds(1));
        AlwaysOnStatusReport recovered = AlwaysOnStatusReader.Read(path, "launch-1", Process(), ObservedAt.AddSeconds(1));
        Assert.Equal("active", recovered.State);
        Assert.Equal(600, recovered.Tick);
        Assert.Equal(ObservedAt.AddSeconds(1), recovered.ObservedAtUtc);
    }

    [Theory]
    [InlineData(6, false, "stale")]
    [InlineData(-2, false, "stale")]
    [InlineData(0, true, "mismatch")]
    public void ReadAfterExclusiveLockReleaseStillValidatesFreshnessAndIdentity(
        int ageSeconds,
        bool foreignProcess,
        string expectedState)
    {
        if (!OperatingSystem.IsWindows()) return;

        using TemporaryDirectory temporary = new();
        string path = WriteMarker(temporary, Process());
        Assert.Equal("active", AlwaysOnStatusReader.Read(path, "launch-1", Process(), ObservedAt).State);
        OwnedProcessIdentity markerProcess = foreignProcess
            ? Process() with { StartTimeUtc = StartedAt.AddTicks(1) }
            : Process();
        WriteMarker(temporary, markerProcess);
        byte[] originalBytes = File.ReadAllBytes(path);
        DateTimeOffset now = ObservedAt.AddSeconds(ageSeconds);

        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AlwaysOnStatusReport unavailable = AlwaysOnStatusReader.Read(path, "launch-1", Process(), now);
            Assert.Equal("invalid", unavailable.State);
            Assert.Null(unavailable.Tick);
            Assert.Null(unavailable.ObservedAtUtc);
        }

        AlwaysOnStatusReport recovered = AlwaysOnStatusReader.Read(path, "launch-1", Process(), now);
        Assert.Equal(expectedState, recovered.State);
        Assert.Equal(foreignProcess ? (int?)null : 600, recovered.Tick);
        Assert.Equal(foreignProcess ? (DateTimeOffset?)null : ObservedAt, recovered.ObservedAtUtc);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));

        WriteMarker(temporary, Process(), observedAt: now);
        AlwaysOnStatusReport fresh = AlwaysOnStatusReader.Read(path, "launch-1", Process(), now);
        Assert.Equal("active", fresh.State);
        Assert.Equal(now, fresh.ObservedAtUtc);
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
    public void FreshRuntimeSnapshotRoundTripsWithWorldIdentity()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new RuntimeSnapshotMarker(
            RuntimeSnapshotContract.SchemaVersion,
            true,
            "fall",
            14,
            2,
            1810,
            "FarmHouse",
            12,
            8,
            true,
            ObservedAt);
        string statusPath = WriteMarker(temporary, Process(), runtime: runtime);

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt.AddSeconds(2));

        Assert.Equal("ready", report.Runtime?.State);
        Assert.True(report.Runtime?.WorldReady);
        Assert.Equal("fall", report.Runtime?.Season);
        Assert.Equal(14, report.Runtime?.DayOfMonth);
        Assert.Equal(2, report.Runtime?.Year);
        Assert.Equal(1810, report.Runtime?.TimeOfDay);
        Assert.Equal("FarmHouse", report.Runtime?.LocationId);
        Assert.Equal(12, report.Runtime?.TileX);
        Assert.Equal(8, report.Runtime?.TileY);
        Assert.True(report.Runtime?.MenuOpen);
        Assert.Equal(ObservedAt, report.Runtime?.ObservedAtUtc);
    }

    [Fact]
    public void RuntimeSnapshotWithoutWorldRejectsLeakedWorldValues()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new RuntimeSnapshotMarker(
            RuntimeSnapshotContract.SchemaVersion,
            false,
            "spring",
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            ObservedAt);
        string statusPath = WriteMarker(temporary, Process(), runtime: runtime);

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt);

        Assert.Equal("invalid", report.Runtime?.State);
        Assert.Null(report.Runtime?.WorldReady);
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

    [Fact]
    public void MatchingTestSaveMarkerIsBoundToTheExpectedFixtureAndLog()
    {
        using TemporaryDirectory temporary = new();
        TestSaveLaunchState expected = TestSaveLaunch(temporary);
        string path = WriteMarker(
            temporary,
            Process(),
            TestSaveMarker(expected, "passed", identityVerified: true, waitedTicks: 120));

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt,
            expected);

        Assert.Equal("active", report.State);
        Assert.Equal("ready", report.TestSave?.State);
        Assert.Equal("passed", report.TestSave?.Phase);
        Assert.True(report.TestSave?.IdentityVerified);
        Assert.Equal(120, report.TestSave?.WaitedTicks);
    }

    [Fact]
    public void MissingTestSavePayloadIsPendingForAnExpectedFixture()
    {
        using TemporaryDirectory temporary = new();
        string path = WriteMarker(temporary, Process());

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt,
            TestSaveLaunch(temporary));

        Assert.Equal("active", report.State);
        Assert.Equal("pending", report.TestSave?.State);
    }

    [Fact]
    public void TestSaveIdentityOrPhaseDriftIsRejectedInsideTheValidLifecycleMarker()
    {
        using TemporaryDirectory temporary = new();
        TestSaveLaunchState expected = TestSaveLaunch(temporary);
        TestSaveStatusMarker wrongFixture = TestSaveMarker(
            expected,
            "passed",
            identityVerified: true,
            waitedTicks: 120) with
        {
            FixtureId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        };
        string path = WriteMarker(temporary, Process(), wrongFixture);

        AlwaysOnStatusReport mismatch = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt,
            expected);

        Assert.Equal("active", mismatch.State);
        Assert.Equal("mismatch", mismatch.TestSave?.State);

        WriteMarker(
            temporary,
            Process(),
            TestSaveMarker(expected, "created", identityVerified: true, waitedTicks: 0));
        AlwaysOnStatusReport wrongModePhase = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt,
            expected);

        Assert.Equal("invalid", wrongModePhase.TestSave?.State);
    }

    [Fact]
    public void PassedScenarioRequiresAllObservedGameTicks()
    {
        using TemporaryDirectory temporary = new();
        TestSaveLaunchState expected = TestSaveLaunch(temporary);
        string path = WriteMarker(
            temporary,
            Process(),
            TestSaveMarker(
                expected,
                "passed",
                identityVerified: true,
                waitedTicks: TestSaveContract.RequiredScenarioTicks - 1));

        AlwaysOnStatusReport shortWait = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt,
            expected);

        Assert.Equal("invalid", shortWait.TestSave?.State);

        WriteMarker(
            temporary,
            Process(),
            TestSaveMarker(
                expected,
                "passed",
                identityVerified: true,
                waitedTicks: TestSaveContract.RequiredScenarioTicks));
        AlwaysOnStatusReport completeWait = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt,
            expected);

        Assert.Equal("ready", completeWait.TestSave?.State);
    }

    [Fact]
    public void PassedReviewRequiresIdentityButNotTheBoundedScenarioWait()
    {
        using TemporaryDirectory temporary = new();
        TestSaveLaunchState expected = TestSaveLaunch(temporary) with
        {
            Mode = TestSaveContract.ReviewMode,
        };
        string path = WriteMarker(
            temporary,
            Process(),
            TestSaveMarker(expected, "passed", identityVerified: true, waitedTicks: 0));

        AlwaysOnStatusReport passed = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt,
            expected);

        Assert.Equal("ready", passed.TestSave?.State);
        Assert.Equal(TestSaveContract.ReviewMode, passed.TestSave?.Mode);

        WriteMarker(
            temporary,
            Process(),
            TestSaveMarker(expected, "created", identityVerified: true, waitedTicks: 0));
        AlwaysOnStatusReport wrongPhase = AlwaysOnStatusReader.Read(
            path,
            "launch-1",
            Process(),
            ObservedAt,
            expected);

        Assert.Equal("invalid", wrongPhase.TestSave?.State);
    }

    private static OwnedProcessIdentity Process() =>
        new(4242, StartedAt, @"E:\Games\StardewModdingAPI.exe");

    private static string WriteMarker(
        TemporaryDirectory temporary,
        OwnedProcessIdentity process,
        TestSaveStatusMarker? testSave = null,
        RuntimeSnapshotMarker? runtime = null,
        DateTimeOffset? observedAt = null)
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
            observedAt ?? ObservedAt,
            testSave,
            Runtime: runtime);
        File.WriteAllText(path, JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
        return path;
    }

    private static TestSaveLaunchState TestSaveLaunch(TemporaryDirectory temporary)
    {
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            "11111111111111111111111111111111",
            "22222222222222222222222222222222",
            123456789L,
            "SDVKit_123456789",
            TestSaveContract.PlayerName,
            TestSaveContract.FarmName,
            TestSaveContract.FavoriteThing);
        return new TestSaveLaunchState(
            TestSaveContract.ScenarioMode,
            identity,
            Path.Combine(temporary.Path, identity.SaveId),
            Path.Combine(temporary.Path, "work"),
            Path.Combine(temporary.Path, "scenario.log"));
    }

    private static TestSaveStatusMarker TestSaveMarker(
        TestSaveLaunchState launch,
        string phase,
        bool identityVerified,
        int waitedTicks) =>
        new(
            TestSaveContract.SchemaVersion,
            launch.Mode,
            phase,
            launch.Identity.FixtureId,
            launch.Identity.SaveId,
            identityVerified,
            waitedTicks,
            "test status",
            launch.ScenarioLogPath);
}
