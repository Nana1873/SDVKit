using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace SdvKit.Cli.LiveLab;

internal static class StatusPipeContract
{
    public const int MaximumSnapshotBytes = 256 * 1024;
    public const int MaximumServerInstances = 1;
    public const int FrameHeaderBytes = sizeof(int);
    public const byte ReceiptAcknowledgement = 0x06;
    private const string PipePrefix = "sdvkit-status-v1-";

    public static string GetPipeName(string launchId)
    {
        if (!Guid.TryParseExact(launchId, "N", out Guid parsed))
        {
            throw new ArgumentException("The status-pipe launch ID must be an N-format GUID.", nameof(launchId));
        }

        return PipePrefix + parsed.ToString("N");
    }
}

internal sealed class StatusPipeServer : IDisposable
{
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private byte[] _snapshot;
    private ExceptionDispatchInfo? _listenerFailure;
    private int _disposed;

    public StatusPipeServer(string launchId, byte[] initialSnapshot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The experimental status pipe is supported only on Windows.");
        }

        _pipeName = StatusPipeContract.GetPipeName(launchId);
        ValidateSnapshot(initialSnapshot);
        _snapshot = initialSnapshot;
        NamedPipeServerStream listener = CreateListener();
        _worker = Task.Run(() => RunWorkerAsync(listener, _shutdown.Token));
    }

    public void Publish(byte[] snapshot)
    {
        ValidateSnapshot(snapshot);
#if NET8_0_OR_GREATER
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
#else
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(StatusPipeServer));
        }
#endif
        ExceptionDispatchInfo? failure = Volatile.Read(ref _listenerFailure);
        if (failure is not null)
        {
            throw new IOException("The lab status pipe listener failed.", failure.SourceException);
        }

        Volatile.Write(ref _snapshot, snapshot);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (AggregateException)
            {
                // Process-exit cleanup must not fail because a cancellation callback failed.
            }

            _ = _worker.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _shutdown,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunWorkerAsync(NamedPipeServerStream listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var accepted = false;
                try
                {
                    await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    accepted = true;
                    byte[] snapshot = Volatile.Read(ref _snapshot);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(ClientWriteTimeout);
                    byte[] header = new byte[StatusPipeContract.FrameHeaderBytes];
                    BinaryPrimitives.WriteInt32LittleEndian(header, snapshot.Length);
                    await listener.WriteAsync(header.AsMemory(), timeout.Token).ConfigureAwait(false);
                    await listener.WriteAsync(snapshot.AsMemory(), timeout.Token).ConfigureAwait(false);
                    byte[] acknowledgement = new byte[1];
                    int acknowledged = await listener.ReadAsync(
                        acknowledgement.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (acknowledged != 1
                        || acknowledgement[0] != StatusPipeContract.ReceiptAcknowledgement)
                    {
                        continue;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (accepted)
                {
                    // A client that doesn't consume its bounded response can't occupy this worker indefinitely.
                }
                catch (IOException) when (accepted)
                {
                    // A disconnected client affects only its one response.
                }
                finally
                {
                    if (accepted)
                    {
                        listener.Disconnect();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(
                ref _listenerFailure,
                ExceptionDispatchInfo.Capture(exception),
                null);
        }
        finally
        {
            listener.Dispose();
        }
    }

    private NamedPipeServerStream CreateListener()
    {
        PipeOptions options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            StatusPipeContract.MaximumServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 4096);
    }

    private static void ValidateSnapshot(byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Length is <= 0 or > StatusPipeContract.MaximumSnapshotBytes)
        {
            throw new IOException(
                $"The lab status snapshot must contain between 1 and {StatusPipeContract.MaximumSnapshotBytes} bytes.");
        }
    }
}

internal enum StatusPipeReadState
{
    Success,
    NotConnected,
    Invalid,
}

internal readonly record struct StatusPipeReadResult(StatusPipeReadState State, byte[]? Snapshot);

internal static partial class StatusPipeClient
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(1);

    public static StatusPipeReadResult Read(
        string launchId,
        int expectedProcessId,
        DateTimeOffset expectedProcessStartTimeUtc)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new StatusPipeReadResult(StatusPipeReadState.NotConnected, null);
        }

        string pipeName;
        try
        {
            pipeName = StatusPipeContract.GetPipeName(launchId);
        }
        catch (ArgumentException)
        {
            return new StatusPipeReadResult(StatusPipeReadState.Invalid, null);
        }

        return ReadAsync(pipeName, expectedProcessId, expectedProcessStartTimeUtc)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task<StatusPipeReadResult> ReadAsync(
        string pipeName,
        int expectedProcessId,
        DateTimeOffset expectedProcessStartTimeUtc)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or TimeoutException
            or UnauthorizedAccessException
            or OperationCanceledException)
        {
            return new StatusPipeReadResult(StatusPipeReadState.NotConnected, null);
        }

        try
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverProcessId)
                || serverProcessId != checked((uint)expectedProcessId)
                || !MatchesProcessStartTime(expectedProcessId, expectedProcessStartTimeUtc))
            {
                return new StatusPipeReadResult(StatusPipeReadState.Invalid, null);
            }

            byte[] header = new byte[StatusPipeContract.FrameHeaderBytes];
            if (!await ReadExactlyAsync(pipe, header, timeout.Token).ConfigureAwait(false))
            {
                return new StatusPipeReadResult(StatusPipeReadState.Invalid, null);
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > StatusPipeContract.MaximumSnapshotBytes)
            {
                return new StatusPipeReadResult(StatusPipeReadState.Invalid, null);
            }

            byte[] content = new byte[length];
            if (!await ReadExactlyAsync(pipe, content, timeout.Token).ConfigureAwait(false))
            {
                return new StatusPipeReadResult(StatusPipeReadState.Invalid, null);
            }

            await pipe.WriteAsync(
                new[] { StatusPipeContract.ReceiptAcknowledgement },
                timeout.Token).ConfigureAwait(false);
            return new StatusPipeReadResult(StatusPipeReadState.Success, content);
        }
        catch (Exception exception) when (exception is IOException
            or TimeoutException
            or UnauthorizedAccessException
            or OperationCanceledException)
        {
            return new StatusPipeReadResult(StatusPipeReadState.Invalid, null);
        }
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static bool MatchesProcessStartTime(int processId, DateTimeOffset expectedStartTimeUtc)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtc.UtcTicks;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint serverProcessId);
}
