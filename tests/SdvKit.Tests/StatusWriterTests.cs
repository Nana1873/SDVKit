using System.Diagnostics;
using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class StatusWriterTests
{
    private const string LaunchId = "11111111111111111111111111111111";

    [Fact]
    public void ProductionWriterPreservesRequiredPlayerNullsThroughWorldAndTitleTransitions()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "status.json");
        var writer = new StatusWriter(LaunchId, path);
        LocalPlayerSnapshot player = LocalPlayerSnapshotTests.Player();
        LocalPlayerSnapshot[] snapshots =
        [
            player,
            player with { Data = player.Data! with { SelectedItem = null } },
            player with { Data = player.Data! with { SelectedSlot = null, SelectedItem = null } },
            player with { Data = player.Data! with { SelectedItem = new SelectedItemValues("(T)Axe", 1, null) } },
            LocalPlayerSnapshotContract.WithoutData("worldNotReady"),
        ];
        foreach (LocalPlayerSnapshot snapshot in snapshots)
        {
            bool ready = snapshot.Availability == "available";
            var runtime = new RuntimeSnapshotMarker(1, ready,
                ready ? "spring" : null, ready ? 1 : null, ready ? 1 : null,
                ready ? 600 : null, ready ? "Farm" : null, ready ? 10 : null,
                ready ? 20 : null, !ready, DateTimeOffset.UtcNow, snapshot);
            writer.Write("active", 600, false, false, runtime: runtime);
            AlwaysOnStatusReport result = Read(path);
            Assert.Equal("ready", result.Runtime?.State);
            Assert.Equal(snapshot, result.Runtime?.LocalPlayer);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement published = document.RootElement.GetProperty("runtime").GetProperty("localPlayer");
            Assert.True(published.TryGetProperty("reason", out _));
            Assert.True(published.TryGetProperty("data", out _));
        }
    }

    [Fact]
    public void LongNestedStatusPathSupportsCreationReplacementAndHeldReaders()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, ".sdvkit",
            new string('a', 70), new string('b', 70), new string('c', 70), "status.json");
        Assert.True(path.Length > 260);
        var writer = new StatusWriter(LaunchId, path);
        writer.Write("active", 1, false, false);
        using var old = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("active", 2, false, false);
        Assert.Equal("active", Read(path).State);
        Assert.Equal(2, Read(path).Tick);
        using JsonDocument oldJson = JsonDocument.Parse(old);
        Assert.Equal(1, oldJson.RootElement.GetProperty("tick").GetInt32());
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData(FileShare.Read | FileShare.Delete)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public void PublicationPreservesHeldOldSnapshotAndMakesNewSnapshotReadable(FileShare sharing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "status ü.json");
        var writer = new StatusWriter(LaunchId, path);
        writer.Write("active", 1, false, false);
        using (var oldSnapshot = new FileStream(path, FileMode.Open, FileAccess.Read, sharing))
        {
            for (int tick = 2; tick <= 5; tick++)
            {
                writer.Write("active", tick, false, false);
                AlwaysOnStatusReport current = Read(path);
                Assert.Equal("active", current.State);
                Assert.Equal(tick, current.Tick);
            }

            using JsonDocument old = JsonDocument.Parse(oldSnapshot);
            Assert.Equal(1, old.RootElement.GetProperty("tick").GetInt32());
            Assert.Equal(LaunchId, old.RootElement.GetProperty("launchId").GetString());
        }

        Assert.Equal([path], Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.ReadWrite)]
    public void NonDeleteSharingReaderKeepsFailuresVisibleUntilReleased(FileShare sharing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "status.json");
        var writer = new StatusWriter(LaunchId, path);
        writer.Write("active", 1, false, false);
        byte[] original = File.ReadAllBytes(path);
        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, sharing))
        {
            for (int tick = 2; tick <= 4; tick++)
            {
                IOException error = Assert.Throws<IOException>(() => writer.Write("active", tick, false, false));
                Assert.Equal(unchecked((int)0x80070020), error.HResult);
                Assert.Contains("rename", error.Message, StringComparison.Ordinal);
                Assert.Equal(original, File.ReadAllBytes(path));
                Assert.Equal([path], Directory.GetFiles(directory.Path));
            }

            AlwaysOnStatusReport stale = Read(path, DateTimeOffset.UtcNow.AddSeconds(6));
            Assert.Equal("stale", stale.State);
            Assert.Equal(1, stale.Tick);
        }

        writer.Write("active", 5, false, false);
        Assert.Equal("active", Read(path).State);
        Assert.Equal(5, Read(path).Tick);
        Assert.Equal([path], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void ReadOnlyDestinationRejectsPublicationWithoutLosingOldSnapshotAndRecovers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "status.json");
        var writer = new StatusWriter(LaunchId, path);
        writer.Write("active", 1, false, false);
        byte[] original = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            IOException error = Assert.Throws<IOException>(() => writer.Write("active", 2, false, false));
            Assert.Equal(unchecked((int)0x80070005), error.HResult);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFiles(directory.Path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        writer.Write("active", 3, false, false);
        Assert.Equal(3, Read(path).Tick);
    }

    [Fact]
    public void PartialAndReplacedLaunchMarkersAreRejectedAndFreshPublicationRecovers()
    {
        using TemporaryDirectory directory = new();
        string path = directory.WriteFile("status.json", "{\"schemaVersion\":1,");
        Assert.Equal("invalid", Read(path).State);
        var writer = new StatusWriter(LaunchId, path);
        writer.Write("active", 1, false, false);
        Assert.Equal("active", Read(path).State);

        new StatusWriter("22222222222222222222222222222222", path).Write("active", 2, false, false);
        Assert.Equal("mismatch", Read(path).State);
        Assert.Null(Read(path).Tick);

        writer.Write("active", 3, false, false);
        Assert.Equal("active", Read(path).State);
        Assert.Equal(3, Read(path).Tick);
        Assert.Equal("stale", Read(path, DateTimeOffset.UtcNow.AddSeconds(6)).State);
        Assert.Equal([path], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ConcurrentReaderSeesCompleteExactSnapshotsWithContractFreshness()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "status.json");
        var writer = new StatusWriter(LaunchId, path);
        writer.Write("active", 0, false, false);
        OwnedProcessIdentity process = CurrentProcess();
        using var start = new Barrier(2);
        Task writes = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int tick = 1; tick <= 500; tick++)
            {
                writer.Write("active", tick, false, false);
            }
        });

        start.SignalAndWait();
        try
        {
            int previousTick = 0;
            for (int index = 0; index < 1_000; index++)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(path, LaunchId, process, now);
                DateTimeOffset observed = Assert.IsType<DateTimeOffset>(report.ObservedAtUtc);
                string expectedState = observed > now.AddSeconds(1) || now - observed > TimeSpan.FromSeconds(5)
                    ? "stale"
                    : "active";
                Assert.Equal(expectedState, report.State);
                Assert.InRange(report.Tick!.Value, previousTick, 500);
                previousTick = report.Tick.Value;
            }
        }
        finally
        {
            await writes;
        }

        Assert.Equal(500, Read(path).Tick);
        Assert.Equal([path], Directory.GetFiles(directory.Path));
    }

    private static AlwaysOnStatusReport Read(string path, DateTimeOffset? now = null) =>
        AlwaysOnStatusReader.Read(path, LaunchId, CurrentProcess(), now ?? DateTimeOffset.UtcNow);

    private static OwnedProcessIdentity CurrentProcess()
    {
        using Process process = Process.GetCurrentProcess();
        return new OwnedProcessIdentity(process.Id, process.StartTime.ToUniversalTime(), Environment.ProcessPath!);
    }
}
