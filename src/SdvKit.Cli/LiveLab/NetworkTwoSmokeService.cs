using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed class NetworkTwoSmokeService
{
    private sealed class UnfocusedObservation
    {
        public int? FirstTick { get; private set; }

        public int? LastTick { get; private set; }

        public bool Continued => FirstTick is not null
            && LastTick > FirstTick;

        public void Observe(AlwaysOnStatusReport? status)
        {
            if (status?.State != "active"
                || status.IsActive != false
                || status.PauseWhenOutOfFocus != false
                || status.Tick is not int tick)
            {
                return;
            }

            FirstTick ??= tick;
            if (tick > (LastTick ?? int.MinValue))
            {
                LastTick = tick;
            }
        }
    }

    private const int Success = 0;
    private const int OperationFailed = 3;

    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly string[] Warnings =
    [
        "The smoke uses exactly one local host and one local farmhand against SDVKit's disposable fixture; it does not expose a general multiplayer topology.",
        "Stardew AppData preferences and standard SMAPI logs remain shared. Personal saves and the normal Mods directory are never selected or modified.",
    ];

    private readonly LiveLabPaths _singlePaths;
    private readonly LiveLabPaths _hostPaths;
    private readonly LiveLabPaths _farmhandPaths;
    private readonly JsonLiveLabStateStore _singleStateStore;
    private readonly JsonLiveLabStateStore _hostStateStore;
    private readonly JsonLiveLabStateStore _farmhandStateStore;
    private readonly TestSaveFixtureStore _fixtureStore;
    private readonly NetworkTwoModBuildPreparer _buildPreparer;
    private readonly LiveLabService _hostService;
    private readonly LiveLabService _farmhandService;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<TimeSpan> _delay;
    private readonly Func<DoctorReport> _discoverInstallations;
    private readonly string _smokeId;
    private readonly ProjectModLaunchState? _projectMod;
    private readonly UnfocusedObservation _hostUnfocused = new();
    private readonly UnfocusedObservation _farmhandUnfocused = new();
    private readonly List<LiveLabProblem> _problems = [];

    private TestSaveLaunchState? _fixtureLaunch;
    private LiveLabReport? _hostReport;
    private LiveLabReport? _farmhandReport;
    private AlwaysOnBuildResult? _hostBuild;
    private string? _buildIdentity;
    private bool _fixturePrepared;
    private bool _fixtureReset;
    private bool _hostLaunchedThisRun;
    private bool _farmhandLaunchedThisRun;
    private bool _hostUnverifiedChildPossible;
    private bool _farmhandUnverifiedChildPossible;
    private DoctorReport? _doctor;

    private NetworkTwoSmokeService(
        LiveLabPaths singlePaths,
        Func<DoctorReport> discoverInstallations,
        Func<DateTimeOffset>? utcNow = null,
        Action<TimeSpan>? delay = null,
        Func<string>? createSmokeId = null,
        ProjectModLaunchState? projectMod = null)
    {
        _singlePaths = singlePaths;
        _hostPaths = LiveLabPaths.ResolveNetworkRole(singlePaths, NetworkTwoContract.HostRole);
        _farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        _singleStateStore = new JsonLiveLabStateStore(singlePaths.StatePath);
        _hostStateStore = new JsonLiveLabStateStore(_hostPaths.StatePath);
        _farmhandStateStore = new JsonLiveLabStateStore(_farmhandPaths.StatePath);
        _fixtureStore = new TestSaveFixtureStore(_hostPaths);
        _buildPreparer = new NetworkTwoModBuildPreparer();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Thread.Sleep;
        _discoverInstallations = discoverInstallations;
        _smokeId = (createSmokeId ?? (() => Guid.NewGuid().ToString("N")))();
        projectMod?.Validate();
        _projectMod = projectMod;

        var processHost = new WindowsLabProcessHost();
        var unusedPreparedBuilder = new AlwaysOnBuilder();
        _hostService = new LiveLabService(
            _hostPaths,
            _hostStateStore,
            unusedPreparedBuilder,
            processHost,
            DiscoverOnce,
            utcNow: _utcNow,
            testSaveStore: _fixtureStore,
            delay: _delay,
            reportTopology: NetworkTwoContract.Topology);
        _farmhandService = new LiveLabService(
            _farmhandPaths,
            _farmhandStateStore,
            unusedPreparedBuilder,
            processHost,
            DiscoverOnce,
            utcNow: _utcNow,
            delay: _delay,
            reportTopology: NetworkTwoContract.Topology);
    }

    public static LiveLabCommandResult Execute(
        string projectRoot,
        Func<DoctorReport> discoverInstallations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        try
        {
            LiveLabPaths paths = LiveLabPaths.Resolve(projectRoot);
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(paths.ProjectRoot);
            if (operationLock is null)
            {
                return new LiveLabCommandResult(
                    OperationFailed,
                    EmptyReport(
                        "blocked",
                        [Problem(
                            "labBusy",
                            "Another live-lab operation is still running for this project.")]));
            }

            var service = new NetworkTwoSmokeService(paths, discoverInstallations);
            try
            {
                return service.Run();
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                return service.HandleUnexpectedFailure(exception);
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new LiveLabCommandResult(
                OperationFailed,
                EmptyReport(
                    "blocked",
                    [Problem("networkTwoOperationFailed", exception.Message)]));
        }
    }

    internal static LiveLabCommandResult ExecuteWithinLock(
        string projectRoot,
        Func<DoctorReport> discoverInstallations,
        ProjectModLaunchState projectMod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(discoverInstallations);
        ArgumentNullException.ThrowIfNull(projectMod);

        try
        {
            LiveLabPaths paths = LiveLabPaths.Resolve(projectRoot);
            var service = new NetworkTwoSmokeService(
                paths,
                discoverInstallations,
                projectMod: projectMod);
            try
            {
                return service.Run();
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                return service.HandleUnexpectedFailure(exception);
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new LiveLabCommandResult(
                OperationFailed,
                EmptyReport(
                    "blocked",
                    [Problem("networkTwoOperationFailed", exception.Message)]));
        }
    }

    private LiveLabCommandResult Run()
    {
        if (!Guid.TryParseExact(_smokeId, "N", out _))
        {
            return Fail("smokeIdInvalid", "The generated network-2 smoke ID is invalid.");
        }

        if (_singleStateStore.Read() is not null)
        {
            return Fail(
                "singleLabNotStopped",
                "The single live lab must be stopped before the network-2 smoke starts.");
        }

        if (!RecoverRetainedNetworkRun())
        {
            return FailureResult("blocked");
        }

        if (!File.Exists(_singlePaths.TestSaveManifestPath)
            || !Directory.Exists(_singlePaths.TestSaveBaselinePath))
        {
            return Fail(
                "testSaveBaselineMissing",
                "The #5 disposable test-save baseline is missing; run the single test-save workflow first.");
        }

        DoctorReport doctor = DiscoverOnce();
        if (!string.Equals(doctor.Status, DoctorReport.Ready, StringComparison.Ordinal)
            || doctor.Installations.Count != 1)
        {
            return Fail(
                "installationNotReady",
                "The network-2 smoke requires exactly one ready Stardew Valley + SMAPI installation from doctor.");
        }

        NetworkTwoModBuildResult prepared = _buildPreparer.Prepare(
            doctor.Installations[0].GamePath,
            _hostPaths,
            _farmhandPaths);
        _hostBuild = prepared.HostBuild;
        _buildIdentity = prepared.BuildIdentity;
        if (!prepared.Succeeded || prepared.BuildIdentity is null)
        {
            return Fail(
                "networkTwoBuildFailed",
                prepared.Error ?? "The shared network-2 AlwaysOn build could not be prepared.");
        }

        try
        {
            TestSavePreparation fixture = _fixtureStore.PrepareForStart();
            _fixtureLaunch = fixture.LaunchState;
            _fixturePrepared = true;
            if (!string.Equals(
                    _fixtureLaunch.Mode,
                    TestSaveContract.ScenarioMode,
                    StringComparison.Ordinal))
            {
                return Fail(
                    "testSaveBaselineNotReady",
                    "The disposable fixture does not yet have the reusable #5 scenario baseline.");
            }

            File.Delete(NetworkLogPath(_hostPaths));
            File.Delete(NetworkLogPath(_farmhandPaths));
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Fail("testSavePreparationFailed", exception.Message);
        }

        var hostLaunch = new NetworkTwoLaunchState(
            NetworkTwoContract.HostRole,
            prepared.BuildIdentity,
            _fixtureLaunch.Identity.FixtureId,
            _fixtureLaunch.Identity.SaveId,
            NetworkLogPath(_hostPaths));
        LiveLabCommandResult hostStarted = _hostService.StartNetwork(
            _fixtureLaunch,
            hostLaunch,
            prepared.HostBuild,
            _projectMod);
        _hostReport = RequireReport(hostStarted);
        _hostUnverifiedChildPossible = hostStarted.ExitCode != Success
            && MayHaveUncontrolledProcess(_hostReport);
        if (hostStarted.ExitCode != Success || !HasProcessIdentity(_hostReport))
        {
            return FailFromReport(
                _hostReport,
                "networkHostStartFailed",
                "The exact network-2 host did not start.");
        }

        _hostLaunchedThisRun = true;

        long? expectedFarmhandId = WaitForHostToOfferFarmhand();
        if (expectedFarmhandId is null or 0)
        {
            return FailureResult("failed");
        }

        var farmhandLaunch = new NetworkTwoLaunchState(
            NetworkTwoContract.FarmhandRole,
            prepared.BuildIdentity,
            _fixtureLaunch.Identity.FixtureId,
            _fixtureLaunch.Identity.SaveId,
            NetworkLogPath(_farmhandPaths),
            expectedFarmhandId);
        LiveLabCommandResult farmhandStarted = _farmhandService.StartNetwork(
            testSave: null,
            farmhandLaunch,
            prepared.HostBuild,
            _projectMod);
        _farmhandReport = RequireReport(farmhandStarted);
        _farmhandUnverifiedChildPossible = farmhandStarted.ExitCode != Success
            && MayHaveUncontrolledProcess(_farmhandReport);
        if (farmhandStarted.ExitCode != Success || !HasProcessIdentity(_farmhandReport))
        {
            return FailFromReport(
                _farmhandReport,
                "networkFarmhandStartFailed",
                "The exact network-2 farmhand did not start.");
        }

        _farmhandLaunchedThisRun = true;

        if (!WaitForJoinedPair())
        {
            return FailureResult("failed");
        }

        LiveLabCommandResult farmhandStopped = _farmhandService.StopNetwork();
        _farmhandReport = RequireReport(farmhandStopped);
        if (farmhandStopped.ExitCode != Success || _farmhandStateStore.Read() is not null)
        {
            AddReportProblems(
                _farmhandReport,
                "networkFarmhandStopFailed",
                "The exact farmhand did not complete its clean stop.");
            return FailureResult("blocked");
        }

        _farmhandUnverifiedChildPossible = false;

        LiveLabCommandResult hostStopped = _hostService.StopNetwork();
        _hostReport = RequireReport(hostStopped);
        if (hostStopped.ExitCode != Success || _hostStateStore.Read() is not null)
        {
            AddReportProblems(
                _hostReport,
                "networkHostStopFailed",
                "The exact host did not complete its clean stop and fixture reset.");
            return FailureResult("blocked");
        }

        _fixtureReset = true;
        IReadOnlyList<string> hostLogs;
        IReadOnlyList<string> farmhandLogs;
        try
        {
            hostLogs = ArchiveRoleLogs(_hostPaths, NetworkTwoContract.HostRole, required: true);
            farmhandLogs = ArchiveRoleLogs(
                _farmhandPaths,
                NetworkTwoContract.FarmhandRole,
                required: true);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            _problems.Add(Problem("networkTwoLogArchiveFailed", exception.Message));
            return FailureResult("failed");
        }

        return new LiveLabCommandResult(
            Success,
            CreateReport("passed", hostLogs, farmhandLogs));
    }

    private bool RecoverRetainedNetworkRun()
    {
        bool farmhandStopped = RecoverRole(
            _farmhandStateStore,
            _farmhandService,
            NetworkTwoContract.FarmhandRole,
            out _farmhandReport);
        if (!farmhandStopped)
        {
            return false;
        }

        bool hostStopped = RecoverRole(
            _hostStateStore,
            _hostService,
            NetworkTwoContract.HostRole,
            out _hostReport);
        if (!hostStopped)
        {
            return false;
        }

        if (_hostReport is not null || _farmhandReport is not null)
        {
            _problems.Clear();
            _hostReport = null;
            _farmhandReport = null;
        }

        return true;
    }

    private bool RecoverRole(
        JsonLiveLabStateStore stateStore,
        LiveLabService service,
        string role,
        out LiveLabReport? report)
    {
        report = null;
        if (stateStore.Read() is null)
        {
            return true;
        }

        LiveLabCommandResult stopped = service.StopNetwork();
        report = RequireReport(stopped);
        if (stateStore.Read() is null)
        {
            return true;
        }

        AddReportProblems(
            report,
            "retainedNetworkRoleNotStopped",
            $"The retained network-2 {role} could not be confirmed stopped.");
        return false;
    }

    private long? WaitForHostToOfferFarmhand()
    {
        DateTimeOffset deadline = _utcNow().ToUniversalTime() + SmokeTimeout;
        bool retriedInvalidStatus = false;
        while (_utcNow().ToUniversalTime() <= deadline)
        {
            LiveLabCommandResult status = _hostService.StatusNetwork();
            _hostReport = RequireReport(status);
            _hostUnfocused.Observe(_hostReport.AlwaysOn);
            if (status.ExitCode != Success)
            {
                if (!retriedInvalidStatus && IsAtomicStatusReadRace(_hostReport))
                {
                    retriedInvalidStatus = true;
                    _delay(PollInterval);
                    continue;
                }

                AddReportProblems(
                    _hostReport,
                    "networkHostStatusFailed",
                    "The exact network-2 host status failed before join.");
                return null;
            }

            retriedInvalidStatus = false;

            NetworkTwoStatusReport? network = _hostReport.AlwaysOn?.NetworkTwo;
            if (network is not null
                && string.Equals(network.State, "ready", StringComparison.Ordinal)
                && string.Equals(network.Phase, "hosting", StringComparison.Ordinal)
                && network.IdentityVerified == true
                && network.RemotePlayerId is not (null or 0))
            {
                return network.RemotePlayerId;
            }

            _delay(PollInterval);
        }

        _problems.Add(Problem(
            "networkHostTimedOut",
            "The exact host did not expose one unclaimed farmhand within two minutes."));
        return null;
    }

    private bool WaitForJoinedPair()
    {
        DateTimeOffset deadline = _utcNow().ToUniversalTime() + SmokeTimeout;
        bool retriedInvalidHostStatus = false;
        bool retriedInvalidFarmhandStatus = false;
        while (_utcNow().ToUniversalTime() <= deadline)
        {
            LiveLabCommandResult hostStatus = _hostService.StatusNetwork();
            LiveLabCommandResult farmhandStatus = _farmhandService.StatusNetwork();
            _hostReport = RequireReport(hostStatus);
            _farmhandReport = RequireReport(farmhandStatus);
            _hostUnfocused.Observe(_hostReport.AlwaysOn);
            _farmhandUnfocused.Observe(_farmhandReport.AlwaysOn);

            if (hostStatus.ExitCode != Success)
            {
                if (!retriedInvalidHostStatus && IsAtomicStatusReadRace(_hostReport))
                {
                    retriedInvalidHostStatus = true;
                    _delay(PollInterval);
                    continue;
                }

                AddReportProblems(
                    _hostReport,
                    "networkHostStatusFailed",
                    "The exact host status failed during the join smoke.");
                return false;
            }
            retriedInvalidHostStatus = false;

            if (farmhandStatus.ExitCode != Success)
            {
                if (!retriedInvalidFarmhandStatus
                    && IsAtomicStatusReadRace(_farmhandReport))
                {
                    retriedInvalidFarmhandStatus = true;
                    _delay(PollInterval);
                    continue;
                }

                if (IsSynchronousJoinLoad(_farmhandReport))
                {
                    _delay(PollInterval);
                    continue;
                }

                AddReportProblems(
                    _farmhandReport,
                    "networkFarmhandStatusFailed",
                    "The exact farmhand status failed during the join smoke.");
                return false;
            }
            retriedInvalidFarmhandStatus = false;

            if (PairPassed(_hostReport.AlwaysOn, _farmhandReport.AlwaysOn)
                && _hostUnfocused.Continued
                && _farmhandUnfocused.Continued)
            {
                return true;
            }

            _delay(PollInterval);
        }

        _problems.Add(Problem(
            "networkTwoTimedOut",
            "The exact host/farmhand pair did not complete the live join and unfocused tick proof within two minutes."));
        return false;
    }

    private static bool IsSynchronousJoinLoad(LiveLabReport report) =>
        report.Problems.Count == 1
        && string.Equals(
            report.Problems[0].Code,
            "alwaysOnStale",
            StringComparison.Ordinal)
        && string.Equals(
            report.AlwaysOn?.NetworkTwo?.Phase,
            "joining",
            StringComparison.Ordinal);

    private static bool IsAtomicStatusReadRace(LiveLabReport report) =>
        report.Problems.Count == 1
        && report.Problems[0].Code is "alwaysOnInvalid" or "alwaysOnPending";

    private bool PairPassed(
        AlwaysOnStatusReport? host,
        AlwaysOnStatusReport? farmhand)
    {
        NetworkTwoStatusReport? hostNetwork = host?.NetworkTwo;
        NetworkTwoStatusReport? farmhandNetwork = farmhand?.NetworkTwo;
        if (host?.State != "active"
            || farmhand?.State != "active"
            || host.PauseWhenOutOfFocus != false
            || farmhand.PauseWhenOutOfFocus != false
            || host.EnableServer != true
            || host.IpConnectionsEnabled != true
            || hostNetwork?.State != "ready"
            || farmhandNetwork?.State != "ready"
            || hostNetwork.Phase != "passed"
            || farmhandNetwork.Phase != "passed"
            || hostNetwork.IdentityVerified != true
            || farmhandNetwork.IdentityVerified != true
            || hostNetwork.JoinedTicks < NetworkTwoContract.RequiredJoinedTicks
            || farmhandNetwork.JoinedTicks < NetworkTwoContract.RequiredJoinedTicks)
        {
            return false;
        }

        bool matches = string.Equals(
                hostNetwork.BuildIdentity,
                _buildIdentity,
                StringComparison.Ordinal)
            && string.Equals(
                farmhandNetwork.BuildIdentity,
                _buildIdentity,
                StringComparison.Ordinal)
            && string.Equals(
                hostNetwork.FixtureId,
                farmhandNetwork.FixtureId,
                StringComparison.Ordinal)
            && string.Equals(hostNetwork.SaveId, farmhandNetwork.SaveId, StringComparison.Ordinal)
            && hostNetwork.LocalPlayerId is not (null or 0)
            && hostNetwork.RemotePlayerId is not (null or 0)
            && hostNetwork.LocalPlayerId == farmhandNetwork.RemotePlayerId
            && hostNetwork.RemotePlayerId == farmhandNetwork.LocalPlayerId
            && string.Equals(
                hostNetwork.LocalPlayerName,
                TestSaveContract.PlayerName,
                StringComparison.Ordinal)
            && string.Equals(
                hostNetwork.RemotePlayerName,
                NetworkTwoContract.FarmhandName,
                StringComparison.Ordinal)
            && string.Equals(
                farmhandNetwork.LocalPlayerName,
                NetworkTwoContract.FarmhandName,
                StringComparison.Ordinal)
            && string.Equals(
                farmhandNetwork.RemotePlayerName,
                TestSaveContract.PlayerName,
                StringComparison.Ordinal);
        if (_projectMod is not null)
        {
            matches = matches
                && ProjectModMatches(host?.ProjectMod, _projectMod)
                && ProjectModMatches(farmhand?.ProjectMod, _projectMod);
        }

        if (!matches)
        {
            _problems.Add(Problem(
                "networkTwoIdentityMismatch",
                "Host and farmhand did not report the same exact build, fixture, save, and reciprocal player identities."));
        }

        return matches;
    }

    private static bool ProjectModMatches(
        ProjectModStatusReport? actual,
        ProjectModLaunchState expected) =>
        actual?.State == "ready"
        && actual.Phase == ProjectModContract.LoadedPhase
        && actual.LoadConfirmed == true
        && string.Equals(actual.LoadedUniqueId, expected.UniqueId, StringComparison.Ordinal)
        && string.Equals(actual.LoadedVersion, expected.Version, StringComparison.Ordinal)
        && string.Equals(actual.BuildIdentity, expected.BuildIdentity, StringComparison.Ordinal);

    private LiveLabCommandResult FailFromReport(
        LiveLabReport report,
        string fallbackCode,
        string fallbackMessage)
    {
        AddReportProblems(report, fallbackCode, fallbackMessage);
        return FailureResult("failed");
    }

    private LiveLabCommandResult Fail(string code, string message)
    {
        _problems.Add(Problem(code, message));
        return FailureResult("failed");
    }

    private LiveLabCommandResult HandleUnexpectedFailure(Exception exception)
    {
        _problems.Add(Problem(
            "networkTwoOperationFailed",
            $"The bounded network-2 operation failed: {exception.Message}"));
        try
        {
            return FailureResult("blocked");
        }
        catch (Exception cleanupException) when (IsControlledFailure(cleanupException))
        {
            _problems.Add(Problem(
                "networkTwoCleanupFailed",
                $"Cleanup after the failed network-2 operation could not be confirmed: {cleanupException.Message}"));
            return new LiveLabCommandResult(
                OperationFailed,
                CreateReport("blocked", [], []));
        }
    }

    private LiveLabCommandResult FailureResult(string requestedState)
    {
        bool farmhandSafe = CleanupRoleAfterFailure(
            _farmhandStateStore,
            _farmhandService,
            ref _farmhandReport,
            _farmhandUnverifiedChildPossible,
            NetworkTwoContract.FarmhandRole);
        bool hostSafe = false;
        if (farmhandSafe)
        {
            hostSafe = CleanupRoleAfterFailure(
                _hostStateStore,
                _hostService,
                ref _hostReport,
                _hostUnverifiedChildPossible,
                NetworkTwoContract.HostRole);

            if (hostSafe && _fixturePrepared && !_fixtureReset)
            {
                if (_hostStateStore.Read() is null && !_hostUnverifiedChildPossible)
                {
                    try
                    {
                        _fixtureStore.AbortStopped(_fixtureLaunch!, _smokeId);
                        _fixtureReset = true;
                    }
                    catch (Exception exception) when (IsControlledFailure(exception))
                    {
                        hostSafe = false;
                        _problems.Add(Problem(
                            "testSaveCleanupFailed",
                            $"The exact fixture could not be reset after the stopped host: {exception.Message}"));
                    }
                }
            }
        }

        IReadOnlyList<string> hostLogs = TryArchiveRoleLogs(
            _hostPaths,
            NetworkTwoContract.HostRole,
            _hostLaunchedThisRun);
        IReadOnlyList<string> farmhandLogs = TryArchiveRoleLogs(
            _farmhandPaths,
            NetworkTwoContract.FarmhandRole,
            _farmhandLaunchedThisRun);
        string state = requestedState == "blocked" || !farmhandSafe || !hostSafe
            ? "blocked"
            : "failed";
        return new LiveLabCommandResult(
            OperationFailed,
            CreateReport(state, hostLogs, farmhandLogs));
    }

    private bool CleanupRoleAfterFailure(
        JsonLiveLabStateStore stateStore,
        LiveLabService service,
        ref LiveLabReport? report,
        bool unverifiedChildPossible,
        string role)
    {
        if (stateStore.Read() is null)
        {
            return !unverifiedChildPossible;
        }

        LiveLabCommandResult stopped = service.StopNetwork();
        report = RequireReport(stopped);
        if (stateStore.Read() is null)
        {
            if (string.Equals(role, NetworkTwoContract.HostRole, StringComparison.Ordinal)
                && _fixturePrepared)
            {
                _fixtureReset = true;
            }

            return true;
        }

        _problems.Add(Problem(
            $"network{char.ToUpperInvariant(role[0])}{role[1..]}CleanupUnconfirmed",
            $"The exact {role} could not be confirmed stopped; no later fixture reset was attempted."));
        return false;
    }

    private List<string> ArchiveRoleLogs(
        LiveLabPaths paths,
        string role,
        bool required)
    {
        string networkRoot = Path.GetDirectoryName(paths.SingleRoot)
            ?? throw new InvalidOperationException("The network-2 role root has no parent.");
        string logsRoot = Path.Combine(networkRoot, "logs");
        Directory.CreateDirectory(logsRoot);
        LiveLabPaths.RejectReparsePointsBelow(logsRoot);

        (string Source, string Suffix)[] sources =
        [
            (paths.StandardOutputPath, "smapi.stdout.log"),
            (paths.StandardErrorPath, "smapi.stderr.log"),
            (paths.StatusPath, "status.json"),
            (NetworkLogPath(paths), "network-2.log"),
        ];
        var archived = new List<string>();
        foreach ((string source, string suffix) in sources)
        {
            if (!File.Exists(source))
            {
                if (required)
                {
                    throw new IOException($"The exact {role} log is missing: {source}");
                }

                continue;
            }

            string destination = Path.Combine(logsRoot, $"{_smokeId}.{role}.{suffix}");
            File.Copy(source, destination, overwrite: false);
            archived.Add(destination);
        }

        if (string.Equals(role, NetworkTwoContract.HostRole, StringComparison.Ordinal)
            && _hostBuild is not null
            && File.Exists(_hostBuild.LogPath))
        {
            string destination = Path.Combine(
                logsRoot,
                $"{_smokeId}.{role}.always-on-build.log");
            File.Copy(_hostBuild.LogPath, destination, overwrite: false);
            archived.Add(destination);
        }

        return archived;
    }

    private List<string> TryArchiveRoleLogs(
        LiveLabPaths paths,
        string role,
        bool launchedThisRun)
    {
        if (!launchedThisRun)
        {
            return [];
        }

        try
        {
            return ArchiveRoleLogs(paths, role, required: false);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            _problems.Add(Problem(
                "networkTwoLogArchiveFailed",
                $"The available {role} logs could not be archived: {exception.Message}"));
            return [];
        }
    }

    private NetworkTwoSmokeReport CreateReport(
        string state,
        IReadOnlyList<string> hostLogs,
        IReadOnlyList<string> farmhandLogs)
    {
        return new NetworkTwoSmokeReport(
            NetworkTwoContract.SchemaVersion,
            NetworkTwoContract.Topology,
            state,
            _fixtureLaunch?.Identity.FixtureId,
            _fixtureLaunch?.Identity.SaveId,
            _buildIdentity,
            _fixtureReset,
            RoleReport(
                NetworkTwoContract.HostRole,
                _hostReport,
                _hostUnfocused,
                hostLogs),
            RoleReport(
                NetworkTwoContract.FarmhandRole,
                _farmhandReport,
                _farmhandUnfocused,
                farmhandLogs),
            _problems,
            Warnings);
    }

    private static NetworkTwoRoleReport RoleReport(
        string role,
        LiveLabReport? report,
        UnfocusedObservation unfocused,
        IReadOnlyList<string> logs) =>
        new(
            role,
            report?.State ?? "notStarted",
            report?.LaunchId,
            report?.ProcessId,
            report?.ProcessStartTimeUtc,
            report?.ExecutablePath,
            report?.AlwaysOn,
            unfocused.Continued,
            unfocused.FirstTick,
            unfocused.LastTick,
            (report?.TestSaveLogPaths ?? [])
                .Concat(logs)
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
                .ToArray());

    private static NetworkTwoSmokeReport EmptyReport(
        string state,
        IReadOnlyList<LiveLabProblem> problems) =>
        new(
            NetworkTwoContract.SchemaVersion,
            NetworkTwoContract.Topology,
            state,
            null,
            null,
            null,
            false,
            EmptyRole(NetworkTwoContract.HostRole),
            EmptyRole(NetworkTwoContract.FarmhandRole),
            problems,
            Warnings);

    private static NetworkTwoRoleReport EmptyRole(string role) =>
        new(role, "notStarted", null, null, null, null, null, false, null, null, []);

    private static LiveLabReport RequireReport(LiveLabCommandResult result) =>
        result.Report as LiveLabReport
        ?? throw new InvalidDataException("The live-lab role returned an unexpected report type.");

    private void AddReportProblems(
        LiveLabReport report,
        string fallbackCode,
        string fallbackMessage)
    {
        if (report.Problems.Count == 0)
        {
            _problems.Add(Problem(fallbackCode, fallbackMessage));
            return;
        }

        _problems.AddRange(report.Problems);
    }

    private static bool MayHaveUncontrolledProcess(LiveLabReport report) =>
        report.Problems.Any(problem => problem.Code is
            "unverifiedChildAbortUnconfirmed" or "ownershipRecordLost")
        || (report.ProcessId is not null
            && report.State is "blocked" or "running" or "ownershipMismatch");

    private static bool HasProcessIdentity(LiveLabReport report) =>
        report.ProcessId is > 0
        && report.ProcessStartTimeUtc is not null
        && !string.IsNullOrWhiteSpace(report.ExecutablePath);

    private static string NetworkLogPath(LiveLabPaths paths) =>
        Path.Combine(paths.RuntimePath, "network-2.log");

    private DoctorReport DiscoverOnce() =>
        _doctor ??= _discoverInstallations();

    private static LiveLabProblem Problem(string code, string message) => new(code, message);

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
