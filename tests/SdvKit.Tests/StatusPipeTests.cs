using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class StatusPipeTests
{
    [Fact]
    public void ActiveStatusUsesOnlyThePipeAndPreservesTheExistingPayloadContract()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using var writer = new StatusWriter(launchId, statusPath, useStatusPipe: true);

        writer.Write("active", 17, true, false);
        AlwaysOnStatusReport report = Read(statusPath, launchId, useStatusPipe: true);

        Assert.Equal("active", report.State);
        Assert.Equal(17, report.Tick);
        Assert.True(report.IsActive);
        Assert.False(report.PauseWhenOutOfFocus);
        Assert.NotNull(report.ObservedAtUtc);
        Assert.False(File.Exists(statusPath));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task PipePublicationSurvivesTheOriginalDirectoryScannerAndServesConcurrentClients()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(directory.Path);
        paths.EnsureDirectories();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = paths.StatusPath;
        using var writer = new StatusWriter(launchId, statusPath, useStatusPipe: true);
        writer.Write("active", 0, false, false);
        using var start = new Barrier(3);
        Task writes = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int tick = 1; tick <= 750; tick++)
            {
                writer.Write("active", tick, false, false);
            }
        });
        Task<AlwaysOnStatusReport[]> overlappingReads = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            return Enumerable.Range(0, 10)
                .Select(_ => Read(statusPath, launchId, useStatusPipe: true))
                .ToArray();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        start.SignalAndWait();
        for (int scan = 0; scan < 2_500; scan++)
        {
            paths.EnsureDirectories();
        }

        await writes;
        AlwaysOnStatusReport[] overlap = await overlappingReads;
        Assert.All(overlap, report =>
        {
            Assert.Equal("active", report.State);
            Assert.InRange(report.Tick!.Value, 0, 750);
        });
        writer.Write("active", 751, false, false);

        Task<AlwaysOnStatusReport>[] reads = Enumerable.Range(0, 8)
            .Select(_ => Task.Factory.StartNew(
                () => Read(statusPath, launchId, useStatusPipe: true),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        AlwaysOnStatusReport[] reports = await Task.WhenAll(reads);

        Assert.All(reports, report =>
        {
            Assert.Equal("active", report.State);
            Assert.Equal(751, report.Tick);
            Assert.False(report.IsActive);
            Assert.NotNull(report.ObservedAtUtc);
        });
        Assert.False(File.Exists(statusPath));
        Assert.Empty(Directory.GetFiles(paths.RuntimePath));
    }

    [Fact]
    public void TerminalReceiptClosesThePipeAndRemainsReadableAfterWriterDisposal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using (var writer = new StatusWriter(launchId, statusPath, useStatusPipe: true))
        {
            writer.Write("active", 30, false, false);
            Assert.False(File.Exists(statusPath));
            writer.Write("exiting", 31, false, false);
        }
        WaitForEndpointRelease(launchId);

        AlwaysOnStatusReport report = Read(statusPath, launchId, useStatusPipe: true);

        Assert.Equal("exiting", report.State);
        Assert.Equal(31, report.Tick);
        Assert.NotNull(report.ObservedAtUtc);
        Assert.Equal([statusPath], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void PipeModeDoesNotFallBackToAnActiveStatusFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using (var legacyWriter = new StatusWriter(launchId, statusPath))
        {
            legacyWriter.Write("active", 41, false, false);
        }

        AlwaysOnStatusReport report = Read(statusPath, launchId, useStatusPipe: true);

        Assert.Equal("pending", report.State);
        Assert.Null(report.Tick);
    }

    [Fact]
    public async Task MalformedConnectedResponseCannotBeHiddenByATerminalReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using (var legacyWriter = new StatusWriter(launchId, statusPath))
        {
            legacyWriter.Write("exiting", 50, false, false);
        }

        using NamedPipeServerStream server = CreateTestServer(launchId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            byte[] malformed = "{"u8.ToArray();
            byte[] header = new byte[StatusPipeContract.FrameHeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(header, malformed.Length);
            await server.WriteAsync(header, timeout.Token);
            await server.WriteAsync(malformed, timeout.Token);
            byte[] acknowledgement = new byte[1];
            _ = await server.ReadAsync(acknowledgement, timeout.Token);
        });

        AlwaysOnStatusReport report = Read(statusPath, launchId, useStatusPipe: true);
        await serve;

        Assert.Equal("invalid", report.State);
        Assert.Null(report.Tick);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidConnectedFrameCannotBeHiddenByATerminalReceipt(bool oversized)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using (var legacyWriter = new StatusWriter(launchId, statusPath))
        {
            legacyWriter.Write("restoreFailed", 55, false, false);
        }

        using NamedPipeServerStream server = CreateTestServer(launchId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            byte[] header = new byte[StatusPipeContract.FrameHeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(
                header,
                oversized ? StatusPipeContract.MaximumSnapshotBytes + 1 : 10);
            await server.WriteAsync(header, timeout.Token);
            if (!oversized)
            {
                await server.WriteAsync(new byte[3], timeout.Token);
                byte[] acknowledgement = new byte[1];
                _ = await server.ReadAsync(acknowledgement, timeout.Token);
            }
        });

        AlwaysOnStatusReport report = Read(statusPath, launchId, useStatusPipe: true);
        await serve;

        Assert.Equal("invalid", report.State);
        Assert.Null(report.Tick);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PipeServerIdentityMismatchIsInvalid(bool wrongStartTime)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using var writer = new StatusWriter(launchId, statusPath, useStatusPipe: true);
        writer.Write("active", 61, false, false);
        OwnedProcessIdentity actual = CurrentProcess();
        OwnedProcessIdentity wrong = wrongStartTime
            ? actual with { StartTimeUtc = actual.StartTimeUtc.AddTicks(1) }
            : actual with { ProcessId = actual.ProcessId + 1 };

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            launchId,
            wrong,
            DateTimeOffset.UtcNow,
            useStatusPipe: true);

        Assert.Equal("invalid", report.State);
        Assert.Null(report.Tick);
    }

    [Fact]
    public void PayloadLaunchMismatchAndStaleTimestampRetainExistingValidation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string endpointLaunchId = Guid.NewGuid().ToString("N");
        OwnedProcessIdentity process = CurrentProcess();
        byte[] mismatched = Snapshot(
            Guid.NewGuid().ToString("N"), process, tick: 80, DateTimeOffset.UtcNow);
        using (var server = new StatusPipeServer(endpointLaunchId, mismatched))
        {
            AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
                Path.Combine(directory.Path, "missing.json"),
                endpointLaunchId,
                process,
                DateTimeOffset.UtcNow,
                useStatusPipe: true);
            Assert.Equal("mismatch", report.State);
        }
        WaitForEndpointRelease(endpointLaunchId);

        string staleLaunchId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var staleServer = new StatusPipeServer(
            staleLaunchId,
            Snapshot(staleLaunchId, process, tick: 81, now.AddSeconds(-6)));
        AlwaysOnStatusReport stale = AlwaysOnStatusReader.Read(
            Path.Combine(directory.Path, "missing.json"),
            staleLaunchId,
            process,
            now,
            useStatusPipe: true);
        Assert.Equal("stale", stale.State);
        Assert.Equal(81, stale.Tick);
    }

    [Fact]
    public async Task ClientDisconnectBeforeAcknowledgementDoesNotBreakTheNextRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using var writer = new StatusWriter(launchId, statusPath, useStatusPipe: true);
        writer.Write("active", 90, false, false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var client = CreateRawClient(launchId))
        {
            await client.ConnectAsync(timeout.Token);
            _ = await ReadFrameAsync(client, timeout.Token);
        }

        AlwaysOnStatusReport report = Read(statusPath, launchId, useStatusPipe: true);
        Assert.Equal("active", report.State);
        Assert.Equal(90, report.Tick);
    }

    [Fact]
    public async Task ClientWithoutAcknowledgementTimesOutAndTheNextReadRecovers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using var writer = new StatusWriter(launchId, statusPath, useStatusPipe: true);
        writer.Write("active", 91, false, false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (NamedPipeClientStream client = CreateRawClient(launchId))
        {
            await client.ConnectAsync(timeout.Token);
            _ = await ReadFrameAsync(client, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(1_100), timeout.Token);

            AlwaysOnStatusReport report = Read(statusPath, launchId, useStatusPipe: true);
            Assert.Equal("active", report.State);
            Assert.Equal(91, report.Tick);
        }
    }

    [Fact]
    public void FailedTerminalReceiptDoesNotReplaceTheLivePipeSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = directory.WriteFile("status.json", "old receipt");
        using var writer = new StatusWriter(launchId, statusPath, useStatusPipe: true);
        writer.Write("active", 100, false, false);
        File.SetAttributes(statusPath, FileAttributes.ReadOnly);
        try
        {
            IOException error = Assert.Throws<IOException>(() =>
                writer.Write("restoreFailed", 101, false, false));
            Assert.Equal(unchecked((int)0x80070005), error.HResult);

            AlwaysOnStatusReport report = Read(statusPath, launchId, useStatusPipe: true);
            Assert.Equal("active", report.State);
            Assert.Equal(100, report.Tick);
            Assert.Equal("old receipt", File.ReadAllText(statusPath));
        }
        finally
        {
            File.SetAttributes(statusPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void OversizedSnapshotIsRejectedBeforeTheEndpointStarts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string launchId = Guid.NewGuid().ToString("N");
        byte[] snapshot = new byte[StatusPipeContract.MaximumSnapshotBytes + 1];

        IOException error = Assert.Throws<IOException>(() => new StatusPipeServer(launchId, snapshot));

        Assert.Contains(
            StatusPipeContract.MaximumSnapshotBytes.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumSizedSnapshotCompletesTheBoundedFrameAndAcknowledgement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string launchId = Guid.NewGuid().ToString("N");
        byte[] snapshot = Enumerable.Repeat((byte)'x', StatusPipeContract.MaximumSnapshotBytes).ToArray();
        using var server = new StatusPipeServer(launchId, snapshot);
        OwnedProcessIdentity process = CurrentProcess();

        StatusPipeReadResult result = StatusPipeClient.Read(
            launchId,
            process.ProcessId,
            process.StartTimeUtc);

        Assert.Equal(StatusPipeReadState.Success, result.State);
        Assert.Equal(snapshot, result.Snapshot);
    }

    [Fact]
    public void ExistingFirstPipeInstanceRejectsStatusPublisherStartup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string launchId = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(directory.Path, "status.json");
        using NamedPipeServerStream squatter = CreateTestServer(launchId);
        using var writer = new StatusWriter(launchId, statusPath, useStatusPipe: true);

        Assert.Throws<IOException>(() => writer.Write("active", 70, false, false));
        Assert.False(File.Exists(statusPath));
    }

    private static NamedPipeServerStream CreateTestServer(string launchId) =>
        new(
            StatusPipeContract.GetPipeName(launchId),
            PipeDirection.InOut,
            StatusPipeContract.MaximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 4096);

    private static NamedPipeClientStream CreateRawClient(string launchId) =>
        new(
            ".",
            StatusPipeContract.GetPipeName(launchId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[StatusPipeContract.FrameHeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        byte[] snapshot = new byte[length];
        await stream.ReadExactlyAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private static byte[] Snapshot(
        string launchId,
        OwnedProcessIdentity process,
        int tick,
        DateTimeOffset observedAtUtc) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            launchId,
            processId = process.ProcessId,
            processStartTimeUtc = process.StartTimeUtc,
            phase = "active",
            tick,
            isActive = false,
            pauseWhenOutOfFocus = false,
            observedAtUtc,
        });

    private static void WaitForEndpointRelease(string launchId)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using NamedPipeServerStream available = CreateTestServer(launchId);
                return;
            }
            catch (IOException) when (timeout.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(10);
            }
        }
    }

    private static AlwaysOnStatusReport Read(string statusPath, string launchId, bool useStatusPipe) =>
        AlwaysOnStatusReader.Read(
            statusPath,
            launchId,
            CurrentProcess(),
            DateTimeOffset.UtcNow,
            useStatusPipe: useStatusPipe);

    private static OwnedProcessIdentity CurrentProcess()
    {
        using Process process = Process.GetCurrentProcess();
        return new OwnedProcessIdentity(
            process.Id,
            process.StartTime.ToUniversalTime(),
            Environment.ProcessPath!);
    }
}
