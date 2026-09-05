using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;

namespace SdvKit.AlwaysOn;

internal sealed class StatusWriter
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

    public StatusWriter(string launchId, string statusPath)
    {
        _launchId = launchId;
        _statusPath = statusPath;
        _processId = Environment.ProcessId;
        using Process process = Process.GetCurrentProcess();
        _processStartTimeUtc = process.StartTime.ToUniversalTime();
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
        string directory = Path.GetDirectoryName(_statusPath)
            ?? throw new IOException("The lab status path has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = _statusPath + $".{_processId}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json, Utf8WithoutBom);
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
