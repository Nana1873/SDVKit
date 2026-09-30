using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed partial class LiveLabService
{
    private const int Success = 0;
    private const int OperationFailed = 3;

    private static readonly TimeSpan CleanStopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupRollbackSignalGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AlwaysOnStartupGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TestSaveTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TestSavePollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly string[] IsolationWarnings =
    [
        "The controlled process resolves Stardew's own preferences, saves, startup preferences, and standard SMAPI logs to a project-owned data root below .sdvkit; SDVKit does not select or modify their normal counterparts or the normal Mods directory.",
        "This is process-level data isolation, not a Windows sandbox; tested mods and external services can still access shared machine resources.",
    ];

    private static readonly string[] TestSaveWarnings =
    [
        "The controlled process resolves Stardew's own preferences, saves, startup preferences, and standard SMAPI logs to a project-owned data root below .sdvkit; SDVKit does not select personal data or the normal Mods directory.",
        "Only one exact SDVKit-owned direct-child save junction is exposed inside that project-owned data root.",
    ];

    private static readonly string[] RestoreUnconfirmedWarnings =
    [
        "AlwaysOn could not confirm restoration of the isolated profile options before normal exit. The exact lab process still stopped safely; the next start reapplies the required lab values.",
    ];

    private static readonly string[] TestSaveEnvironmentNames =
    [
        "SDVKIT_TEST_SAVE_MODE",
        "SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID",
        "SDVKIT_TEST_SAVE_FIXTURE_ID",
        "SDVKIT_TEST_SAVE_UNIQUE_GAME_ID",
        "SDVKIT_TEST_SAVE_ID",
        "SDVKIT_TEST_SAVE_PLAYER_NAME",
        "SDVKIT_TEST_SAVE_FARM_NAME",
        "SDVKIT_TEST_SAVE_FAVORITE_THING",
        "SDVKIT_TEST_SAVE_LOG_PATH",
    ];

    private static readonly string[] NetworkTwoEnvironmentNames =
    [
        "SDVKIT_NETWORK_TWO_ROLE",
        "SDVKIT_NETWORK_TWO_BUILD_ID",
        "SDVKIT_NETWORK_TWO_FIXTURE_ID",
        "SDVKIT_NETWORK_TWO_SAVE_ID",
        "SDVKIT_NETWORK_TWO_LOG_PATH",
        "SDVKIT_NETWORK_TWO_EXPECTED_FARMHAND_ID",
    ];

    private static readonly string[] ProjectModEnvironmentNames =
    [
        "SDVKIT_PROJECT_MOD_UNIQUE_ID",
        "SDVKIT_PROJECT_MOD_VERSION",
        "SDVKIT_PROJECT_MOD_BUILD_IDENTITY",
        "SDVKIT_PROJECT_REVIEW",
    ];

    private readonly LiveLabPaths _paths;
    private readonly ILiveLabStateStore _stateStore;
    private readonly IAlwaysOnBuilder _alwaysOnBuilder;
    private readonly ILabProcessHost _processHost;
    private readonly Func<DoctorReport> _discoverInstallations;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _createLaunchId;
    private readonly ITestSaveFixtureStore _testSaveStore;
    private readonly Action<TimeSpan> _delay;
    private readonly string _reportTopology;
    private IReadOnlyList<string> _lastTestSaveLogPaths = [];

    internal AlwaysOnStatusReport? LastAlwaysOn { get; private set; }

    internal LiveLabService(
        LiveLabPaths paths,
        ILiveLabStateStore stateStore,
        IAlwaysOnBuilder alwaysOnBuilder,
        ILabProcessHost processHost,
        Func<DoctorReport> discoverInstallations,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? createLaunchId = null,
        ITestSaveFixtureStore? testSaveStore = null,
        Action<TimeSpan>? delay = null,
        string reportTopology = LiveLabState.SingleTopology)
    {
        _paths = paths;
        _stateStore = stateStore;
        _alwaysOnBuilder = alwaysOnBuilder;
        _processHost = processHost;
        _discoverInstallations = discoverInstallations;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _createLaunchId = createLaunchId ?? (() => Guid.NewGuid().ToString("N"));
        _testSaveStore = testSaveStore ?? new TestSaveFixtureStore(paths);
        _delay = delay ?? Thread.Sleep;
        _reportTopology = reportTopology;
    }

    public static LiveLabCommandResult Execute(
        string action,
        string projectRoot,
        Func<DoctorReport> discoverInstallations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(projectRoot);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Result(
                OperationFailed,
                new LiveLabReport(
                    1,
                    LiveLabState.SingleTopology,
                    "blocked",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [Problem("labPathInvalid", exception.Message)],
                    IsolationWarnings));
        }

        var service = new LiveLabService(
            paths,
            new JsonLiveLabStateStore(paths.StatePath),
            new AlwaysOnBuilder(),
            new WindowsLabProcessHost(),
            discoverInstallations);
        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(paths.ProjectRoot);
            if (operationLock is null)
            {
                return Result(
                    OperationFailed,
                    service.Report(
                        "blocked",
                        null,
                        [Problem(
                            "labBusy",
                            "Another live-lab operation is still running for this project.")]));
            }

            return service.Execute(action);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Result(
                OperationFailed,
                service.Report(
                    "blocked",
                    null,
                    [Problem("labOperationFailed", exception.Message)]));
        }
    }

    internal LiveLabCommandResult Execute(string action)
    {
        return action switch
        {
            "start" => Start(),
            "status" => Status(),
            "stop" => Stop(),
            "test-save" => TestSave(projectMod: null),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private LiveLabState? ReadState()
    {
        LiveLabState? state = _stateStore.Read();
        if (state is null)
        {
            return null;
        }

        if (!PathEquals(state.ModsPath, _paths.ModsPath)
            || !PathEquals(state.StatusPath, _paths.StatusPath)
            || !PathEquals(state.StopRequestPath, _paths.StopRequestPath)
            || (state.TestSave is not null
                && (!PathEquals(state.TestSave.WorkPath, _paths.TestSaveWorkPath)
                    || !PathEquals(
                        state.TestSave.ScenarioLogPath,
                        _paths.TestSaveScenarioLogPath)))
            || (state.NetworkTwo is not null
                && !PathEquals(
                    state.NetworkTwo.NetworkLogPath,
                    Path.Combine(_paths.RuntimePath, "network-2.log"))))
        {
            throw new InvalidDataException(
                "The retained live-lab paths do not match this project-local live-lab instance.");
        }

        return state;
    }

    private AlwaysOnStatusReport ReadAlwaysOn(LiveLabState state)
    {
        return AlwaysOnStatusReader.Read(
            state.StatusPath,
            state.LaunchId,
            state.OwnedProcessIdentity,
            _utcNow().ToUniversalTime(),
            state.TestSave,
            state.NetworkTwo,
            state.ProjectMod);
    }

    private LiveLabCommandResult Failure(
        string stateName,
        LiveLabState? state,
        string code,
        string message,
        string? buildLogPath = null,
        AlwaysOnStatusReport? alwaysOn = null)
    {
        return Result(
            OperationFailed,
            Report(
                stateName,
                state,
                [Problem(code, message)],
                buildLogPath,
                alwaysOn,
                additionalWarnings: string.Equals(
                    alwaysOn?.State,
                    "restoreFailed",
                    StringComparison.Ordinal)
                        ? RestoreUnconfirmedWarnings
                        : null));
    }

    private LiveLabReport Report(
        string stateName,
        LiveLabState? state,
        IReadOnlyList<LiveLabProblem> problems,
        string? buildLogPath = null,
        AlwaysOnStatusReport? alwaysOn = null,
        IReadOnlyList<string>? additionalWarnings = null)
    {
        OwnedProcessIdentity? process = state?.OwnedProcessIdentity;
        IReadOnlyList<string> baseWarnings = state?.TestSave is null
            ? IsolationWarnings
            : TestSaveWarnings;
        IReadOnlyList<string> warnings = additionalWarnings is null
            or { Count: 0 }
            ? baseWarnings
            : [.. baseWarnings, .. additionalWarnings];
        return new LiveLabReport(
            1,
            state?.Topology ?? _reportTopology,
            stateName,
            state?.LaunchId,
            process?.ProcessId,
            process?.StartTimeUtc,
            process?.ExecutablePath,
            _paths.ModsPath,
            buildLogPath,
            alwaysOn,
            problems,
            warnings,
            state?.TestSave is null ? [] : _lastTestSaveLogPaths);
    }

    private static LiveLabProblem Problem(string code, string message) =>
        new(code, message);

    private static LiveLabCommandResult Result(int exitCode, object report) =>
        new(exitCode, report);

    private static bool PathEquals(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), comparison);
    }

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or PlatformNotSupportedException
            or JsonException
            or SecurityException
            or UnauthorizedAccessException;
}
