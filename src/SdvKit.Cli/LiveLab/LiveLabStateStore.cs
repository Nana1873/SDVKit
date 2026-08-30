using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed record LiveLabState(
    int SchemaVersion,
    string Topology,
    string LaunchId,
    OwnedProcessIdentity OwnedProcessIdentity,
    string ModsPath,
    string StatusPath,
    string StopRequestPath)
{
    public const int CurrentSchemaVersion = 1;
    public const string SingleTopology = "single";
}

internal interface ILiveLabStateStore
{
    LiveLabState? Read();

    void VerifyWritable();

    void Write(LiveLabState state);

    void Delete();
}

internal sealed class JsonLiveLabStateStore(string statePath) : ILiveLabStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _statePath = Path.GetFullPath(statePath);

    public LiveLabState? Read()
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        LiveLabState state;
        try
        {
            using FileStream stream = new(
                _statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            state = JsonSerializer.Deserialize<LiveLabState>(stream, JsonOptions)
                ?? throw new InvalidDataException("The live-lab state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The live-lab state is not valid JSON.", exception);
        }

        Validate(state);
        return state;
    }

    public void VerifyWritable()
    {
        string directory = RequireRuntimeDirectory();
        string identity = Guid.NewGuid().ToString("N");
        string stagingPath = Path.Combine(directory, $".state-write-probe.{identity}.tmp");
        string renamedPath = Path.Combine(directory, $".state-write-probe.{identity}.ready");
        try
        {
            using (FileStream stream = new(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            File.Move(stagingPath, renamedPath);
        }
        finally
        {
            File.Delete(stagingPath);
            File.Delete(renamedPath);
        }
    }

    public void Write(LiveLabState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);

        string directory = RequireRuntimeDirectory();

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(_statePath);
        }
        catch (DirectoryNotFoundException)
        {
            // An absent project-local runtime directory is already a deleted state.
        }
    }

    private string RequireRuntimeDirectory()
    {
        string? directory = Path.GetDirectoryName(_statePath);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Live-lab runtime directory was not found: {directory}");
        }

        return directory;
    }

    private static void Validate(LiveLabState state)
    {
        if (state.SchemaVersion != LiveLabState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported live-lab state schema: {state.SchemaVersion}");
        }

        if (!string.Equals(
            state.Topology,
            LiveLabState.SingleTopology,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported live-lab topology: {state.Topology}");
        }

        if (!Guid.TryParseExact(state.LaunchId, "N", out _))
        {
            throw new InvalidDataException("The live-lab launch ID is invalid.");
        }

        if (string.IsNullOrWhiteSpace(state.ModsPath)
            || string.IsNullOrWhiteSpace(state.StatusPath)
            || string.IsNullOrWhiteSpace(state.StopRequestPath)
            || !Path.IsPathFullyQualified(state.ModsPath)
            || !Path.IsPathFullyQualified(state.StatusPath)
            || !Path.IsPathFullyQualified(state.StopRequestPath))
        {
            throw new InvalidDataException("Live-lab state paths must be absolute.");
        }

        if (state.OwnedProcessIdentity is null
            || state.OwnedProcessIdentity.ProcessId <= 0
            || state.OwnedProcessIdentity.StartTimeUtc == default
            || state.OwnedProcessIdentity.StartTimeUtc.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(state.OwnedProcessIdentity.ExecutablePath)
            || !Path.IsPathFullyQualified(state.OwnedProcessIdentity.ExecutablePath))
        {
            throw new InvalidDataException("The owned process identity is invalid.");
        }
    }
}
