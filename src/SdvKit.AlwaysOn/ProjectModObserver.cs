#if SDVKIT_GAME_AVAILABLE
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;

namespace SdvKit.AlwaysOn;

internal sealed class ProjectModObserver
{
    private readonly ProjectModLaunchState _expected;
    private readonly IModRegistry _modRegistry;
    private readonly IMonitor _monitor;

    private string _phase = ProjectModContract.WaitingForGameLaunchPhase;
    private string? _loadedUniqueId;
    private string? _loadedVersion;
    private bool _loadConfirmed;
    private string? _message;

    private ProjectModObserver(
        ProjectModLaunchState expected,
        IModRegistry modRegistry,
        IMonitor monitor)
    {
        _expected = expected;
        _modRegistry = modRegistry;
        _monitor = monitor;
    }

    public ProjectModStatusMarker Snapshot => new(
        ProjectModContract.SchemaVersion,
        _phase,
        _expected.UniqueId,
        _expected.Version,
        _loadedUniqueId,
        _loadedVersion,
        _expected.BuildIdentity,
        _loadConfirmed,
        _message);

    public static bool TryCreate(
        IModHelper helper,
        IMonitor monitor,
        out ProjectModObserver? observer,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);

        string uniqueId = ReadEnvironment("SDVKIT_PROJECT_MOD_UNIQUE_ID");
        string version = ReadEnvironment("SDVKIT_PROJECT_MOD_VERSION");
        string buildIdentity = ReadEnvironment("SDVKIT_PROJECT_MOD_BUILD_IDENTITY");
        if (uniqueId.Length == 0
            && version.Length == 0
            && buildIdentity.Length == 0)
        {
            observer = null;
            reason = string.Empty;
            return true;
        }

        var expected = new ProjectModLaunchState(uniqueId, version, buildIdentity);
        observer = new ProjectModObserver(expected, helper.ModRegistry, monitor);
        try
        {
            expected.Validate();
            reason = string.Empty;
            return true;
        }
        catch (InvalidDataException exception)
        {
            reason = exception.Message;
            observer.Fail(reason);
            return false;
        }
    }

    public void ObserveLoadedMod()
    {
        if (!string.Equals(
                _phase,
                ProjectModContract.WaitingForGameLaunchPhase,
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            IManifest? manifest = _modRegistry.Get(_expected.UniqueId)?.Manifest;
            if (manifest is null)
            {
                Fail($"SMAPI did not report project mod '{_expected.UniqueId}' as loaded.");
                return;
            }

            _loadedUniqueId = manifest.UniqueID;
            _loadedVersion = manifest.Version.ToString();
            if (!string.Equals(
                    _loadedUniqueId,
                    _expected.UniqueId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    _loadedVersion,
                    _expected.Version,
                    StringComparison.Ordinal))
            {
                Fail(
                    $"SMAPI loaded project mod '{_loadedUniqueId}' version "
                    + $"'{_loadedVersion}' instead of expected "
                    + $"'{_expected.UniqueId}' version '{_expected.Version}'.");
                return;
            }

            _phase = ProjectModContract.LoadedPhase;
            _loadConfirmed = true;
            _message =
                $"SMAPI reported project mod '{_loadedUniqueId}' version '{_loadedVersion}' as loaded.";
            _monitor.Log($"SDVKit project mod [loaded] {_message}", LogLevel.Info);
        }
        catch (Exception exception)
        {
            Fail(
                $"SMAPI project-mod observation failed: "
                + exception.GetBaseException().Message);
        }
    }

    private void Fail(string message)
    {
        _phase = ProjectModContract.FailedPhase;
        _loadConfirmed = false;
        _message = message;
        _monitor.Log($"SDVKit project mod [failed] {message}", LogLevel.Error);
    }

    private static string ReadEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;
}
#endif
