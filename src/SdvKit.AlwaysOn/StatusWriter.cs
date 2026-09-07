using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;

namespace SdvKit.AlwaysOn;

internal sealed class StatusWriter : IDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _launchId;
    private readonly string _statusPath;
    private readonly int _processId;
    private readonly DateTimeOffset _processStartTimeUtc;
    private readonly bool _useStatusPipe;
    private StatusPipeServer? _statusPipe;

    public StatusWriter(string launchId, string statusPath, bool useStatusPipe = false)
    {
        _launchId = launchId;
        _statusPath = statusPath;
        _processId = Environment.ProcessId;
        using Process process = Process.GetCurrentProcess();
        _processStartTimeUtc = process.StartTime.ToUniversalTime();
        _useStatusPipe = useStatusPipe;
    }

    public void Write(
        string phase,
        int tick,
        bool isActive,
        bool? pauseWhenOutOfFocus,
        TestSaveStatusMarker? testSave = null,
        bool? enableServer = null,
        bool? ipConnectionsEnabled = null,
        NetworkTwoStatusMarker? networkTwo = null,
        long? foregroundWindowHandle = null,
        int? foregroundProcessId = null,
        ProjectModStatusMarker? projectMod = null,
        RuntimeSnapshotMarker? runtime = null,
        LoadedModsStatusMarker? loadedMods = null)
    {
        var marker = new
        {
            schemaVersion = 1,
            launchId = _launchId,
            processId = _processId,
            processStartTimeUtc = _processStartTimeUtc,
            phase,
            tick,
            isActive,
            pauseWhenOutOfFocus,
            enableServer,
            ipConnectionsEnabled,
            foregroundWindowHandle,
            foregroundProcessId,
            testSave,
            networkTwo,
            projectMod,
            runtime,
            loadedMods,
            observedAtUtc = DateTimeOffset.UtcNow,
        };
        string json = JsonSerializer.Serialize(marker, JsonOptions) + Environment.NewLine;
        if (_useStatusPipe)
        {
            byte[] snapshot = Utf8WithoutBom.GetBytes(json);
            if (snapshot.Length > StatusPipeContract.MaximumSnapshotBytes)
            {
                throw new IOException(
                    $"The lab status snapshot exceeds {StatusPipeContract.MaximumSnapshotBytes} bytes.");
            }

            if (string.Equals(phase, "active", StringComparison.Ordinal))
            {
                if (_statusPipe is null)
                {
                    _statusPipe = new StatusPipeServer(_launchId, snapshot);
                }
                else
                {
                    _statusPipe.Publish(snapshot);
                }

                return;
            }

            if (phase is not ("exiting" or "restoreFailed"))
            {
                throw new IOException($"The lab status phase '{phase}' can't be published through the status pipe.");
            }

            WriteFile(snapshot);
            _statusPipe?.Dispose();
            return;
        }

        WriteFile(Utf8WithoutBom.GetBytes(json));
    }

    public void Dispose() => _statusPipe?.Dispose();

    private void WriteFile(byte[] snapshot)
    {
        string directory = Path.GetDirectoryName(_statusPath)
            ?? throw new IOException("The lab status path has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = _statusPath + $".{_processId}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, snapshot);
            if (OperatingSystem.IsWindows())
            {
                WindowsStatusFile.Publish(temporaryPath, _statusPath);
            }
            else
            {
                File.Move(temporaryPath, _statusPath, overwrite: true);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not hide the original status-write result.
        }
    }
}
