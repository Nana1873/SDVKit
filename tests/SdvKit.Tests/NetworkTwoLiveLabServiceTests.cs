using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class NetworkTwoLiveLabServiceTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);

    private const string LaunchId = "11111111111111111111111111111111";

    private const string BuildIdentity =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public void HostStartUsesPreparedBuildFixtureAndItsOwnIsolatedLifecyclePaths()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = Path.GetDirectoryName(temporary.WriteFile("game/.keep"))!;
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        TestSaveLaunchState testSave = TestSave(paths);
        NetworkTwoLaunchState launch = Launch(
            paths,
            NetworkTwoContract.HostRole,
            testSave.Identity,
            expectedFarmhandId: null);
        var stateStore = new FakeStateStore();
        var builder = new FakeBuilder();
        var process = new FakeProcessHost
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
        };
        LiveLabService service = Service(paths, stateStore, builder, process, gamePath);
        var prepared = new AlwaysOnBuildResult(
            true,
            Path.Combine(paths.BuildPath, "always-on-build.log"),
            null);

        LiveLabCommandResult result = service.StartNetwork(testSave, launch, prepared);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, builder.CallCount);
        LiveLabState state = Assert.IsType<LiveLabState>(stateStore.State);
        Assert.Equal(NetworkTwoContract.Topology, state.Topology);
        Assert.Equal(testSave, state.TestSave);
        Assert.Equal(launch, state.NetworkTwo);
        LabProcessStartSpec specification = Assert.IsType<LabProcessStartSpec>(
            process.Specification);
        Assert.Equal(["--mods-path", paths.ModsPath], specification.Arguments);
        Assert.Equal(paths.StandardOutputPath, specification.StandardOutputPath);
        Assert.Equal(paths.StandardErrorPath, specification.StandardErrorPath);
        Assert.True(specification.StartMinimizedWithoutActivation);
        Assert.Equal(NetworkTwoContract.HostRole, specification.Environment[
            "SDVKIT_NETWORK_TWO_ROLE"]);
        Assert.Equal(BuildIdentity, specification.Environment[
            "SDVKIT_NETWORK_TWO_BUILD_ID"]);
        Assert.Equal(testSave.Identity.FixtureId, specification.Environment[
            "SDVKIT_NETWORK_TWO_FIXTURE_ID"]);
        Assert.Equal(testSave.Identity.SaveId, specification.Environment[
            "SDVKIT_NETWORK_TWO_SAVE_ID"]);
        Assert.Equal(launch.NetworkLogPath, specification.Environment[
            "SDVKIT_NETWORK_TWO_LOG_PATH"]);
        Assert.Equal(paths.UserProfilePath, specification.Environment["USERPROFILE"]);
        Assert.Equal(paths.RoamingAppDataPath, specification.Environment["APPDATA"]);
        Assert.Equal(paths.LocalAppDataPath, specification.Environment["LOCALAPPDATA"]);
        Assert.Equal(paths.StardewDataPath, specification.Environment[
            "SDVKIT_LAB_DATA_PATH"]);
        Assert.Equal(string.Empty, specification.Environment[
            "SDVKIT_NETWORK_TWO_EXPECTED_FARMHAND_ID"]);
        Assert.Equal(TestSaveContract.ScenarioMode, specification.Environment[
            "SDVKIT_TEST_SAVE_MODE"]);
        Assert.Equal(string.Empty, specification.Environment["SDVKIT_PROJECT_REVIEW"]);
        Assert.StartsWith(
            Path.Combine(temporary.Path, ".sdvkit", "lab", "network-2", "host"),
            paths.ModsPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(singlePaths.ModsPath, paths.ModsPath);
    }

    [Fact]
    public void FarmhandStartUsesTheSamePreparedBuildButSeparatePathsAndNoFixtureBinding()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = Path.GetDirectoryName(temporary.WriteFile("game/.keep"))!;
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        TestSaveIdentity identity = TestSave(paths).Identity;
        NetworkTwoLaunchState launch = Launch(
            paths,
            NetworkTwoContract.FarmhandRole,
            identity,
            expectedFarmhandId: 202L);
        var stateStore = new FakeStateStore();
        var builder = new FakeBuilder();
        var process = new FakeProcessHost
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
        };
        LiveLabService service = Service(paths, stateStore, builder, process, gamePath);
        var prepared = new AlwaysOnBuildResult(
            true,
            Path.Combine(hostPaths.BuildPath, "always-on-build.log"),
            null);

        LiveLabCommandResult result = service.StartNetwork(null, launch, prepared);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, builder.CallCount);
        LiveLabState state = Assert.IsType<LiveLabState>(stateStore.State);
        Assert.Null(state.TestSave);
        Assert.Equal(launch, state.NetworkTwo);
        LabProcessStartSpec specification = Assert.IsType<LabProcessStartSpec>(
            process.Specification);
        Assert.Equal(["--mods-path", paths.ModsPath], specification.Arguments);
        Assert.True(specification.StartMinimizedWithoutActivation);
        Assert.Equal("202", specification.Environment[
            "SDVKIT_NETWORK_TWO_EXPECTED_FARMHAND_ID"]);
        Assert.Equal(paths.UserProfilePath, specification.Environment["USERPROFILE"]);
        Assert.Equal(paths.RoamingAppDataPath, specification.Environment["APPDATA"]);
        Assert.Equal(paths.LocalAppDataPath, specification.Environment["LOCALAPPDATA"]);
        Assert.Equal(paths.StardewDataPath, specification.Environment[
            "SDVKIT_LAB_DATA_PATH"]);
        Assert.All(
            specification.Environment.Where(pair => pair.Key.StartsWith(
                "SDVKIT_TEST_SAVE_",
                StringComparison.Ordinal)),
            pair => Assert.Equal(string.Empty, pair.Value));
        Assert.Equal(string.Empty, specification.Environment["SDVKIT_PROJECT_REVIEW"]);
        Assert.NotEqual(hostPaths.ModsPath, paths.ModsPath);
        Assert.NotEqual(hostPaths.StatusPath, paths.StatusPath);
        Assert.NotEqual(hostPaths.StopRequestPath, paths.StopRequestPath);
        Assert.NotEqual(hostPaths.StandardOutputPath, paths.StandardOutputPath);
        Assert.NotEqual(hostPaths.StandardErrorPath, paths.StandardErrorPath);
        Assert.NotEqual(hostPaths.UserProfilePath, paths.UserProfilePath);
        Assert.NotEqual(hostPaths.StardewDataPath, paths.StardewDataPath);
        Assert.Equal(hostPaths.TestSaveWorkPath, paths.TestSaveWorkPath);
    }

    [Fact]
    public void StartBindsProjectTargetEnvironmentAndPersistsItsExactPayload()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = Path.GetDirectoryName(temporary.WriteFile("game/.keep"))!;
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        TestSaveIdentity identity = TestSave(paths).Identity;
        NetworkTwoLaunchState launch = Launch(
            paths,
            NetworkTwoContract.FarmhandRole,
            identity,
            expectedFarmhandId: 202L);
        var projectMod = new ProjectModLaunchState(
            "Example.ProjectMod",
            "1.2.3",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
        var stateStore = new FakeStateStore();
        var process = new FakeProcessHost
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            gamePath);
        var prepared = new AlwaysOnBuildResult(
            true,
            Path.Combine(hostPaths.BuildPath, "always-on-build.log"),
            null);

        LiveLabCommandResult result = service.StartNetwork(
            testSave: null,
            launch,
            prepared,
            projectMod);

        Assert.Equal(0, result.ExitCode);
        LiveLabState state = Assert.IsType<LiveLabState>(stateStore.State);
        Assert.Equal(projectMod, state.ProjectMod);
        LabProcessStartSpec specification = Assert.IsType<LabProcessStartSpec>(
            process.Specification);
        Assert.Equal(projectMod.UniqueId, specification.Environment[
            "SDVKIT_PROJECT_MOD_UNIQUE_ID"]);
        Assert.Equal(projectMod.Version, specification.Environment[
            "SDVKIT_PROJECT_MOD_VERSION"]);
        Assert.Equal(projectMod.BuildIdentity, specification.Environment[
            "SDVKIT_PROJECT_MOD_BUILD_IDENTITY"]);
        Assert.Equal(string.Empty, specification.Environment["SDVKIT_PROJECT_REVIEW"]);
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole)]
    [InlineData(NetworkTwoContract.FarmhandRole)]
    public void ReviewRolesUseSeparateInteractiveConsolesWithAlwaysOnAndProjectBinding(
        string role)
    {
        using TemporaryDirectory temporary = new();
        string gamePath = Path.GetDirectoryName(temporary.WriteFile("game/.keep"))!;
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(singlePaths, role);
        TestSaveLaunchState review = TestSave(hostPaths) with
        {
            Mode = TestSaveContract.ReviewMode,
        };
        NetworkTwoLaunchState launch = Launch(
            paths,
            role,
            review.Identity,
            role == NetworkTwoContract.FarmhandRole ? 202L : null);
        var projectMod = new ProjectModLaunchState(
            "Example.ProjectMod",
            "1.2.3",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
        var stateStore = new FakeStateStore();
        var process = new FakeProcessHost
        {
            StartResult = new LabProcessStartResult(
                LabProcessStartStatus.Started,
                Identity(gamePath)),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            gamePath);
        var prepared = new AlwaysOnBuildResult(
            true,
            Path.Combine(hostPaths.BuildPath, "always-on-build.log"),
            null);

        LiveLabCommandResult result = service.StartNetwork(
            role == NetworkTwoContract.HostRole ? review : null,
            launch,
            prepared,
            projectMod,
            interactiveConsole: true);

        Assert.Equal(0, result.ExitCode);
        LiveLabState state = Assert.IsType<LiveLabState>(stateStore.State);
        Assert.Equal(projectMod, state.ProjectMod);
        Assert.Equal(role, state.NetworkTwo?.Role);
        Assert.Equal(
            role == NetworkTwoContract.HostRole ? review : null,
            state.TestSave);
        LabProcessStartSpec specification = Assert.IsType<LabProcessStartSpec>(
            process.Specification);
        Assert.True(specification.InteractiveConsole);
        Assert.False(specification.StartMinimizedWithoutActivation);
        Assert.True(specification.StartVisibleWithoutActivation);
        Assert.Equal(paths.ModsPath, specification.Arguments[1]);
        Assert.Equal(paths.UserProfilePath, specification.Environment["USERPROFILE"]);
        Assert.Equal(paths.StardewDataPath, specification.Environment[
            "SDVKIT_LAB_DATA_PATH"]);
        Assert.Equal(projectMod.BuildIdentity, specification.Environment[
            "SDVKIT_PROJECT_MOD_BUILD_IDENTITY"]);
        Assert.Equal("1", specification.Environment["SDVKIT_PROJECT_REVIEW"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void HostCleanStopReportsConfirmedRestoredNetworkOptions(
        bool restoredEnableServer,
        bool restoredIpConnectionsEnabled)
    {
        using TemporaryDirectory temporary = new();
        string gamePath = Path.GetDirectoryName(temporary.WriteFile("game/.keep"))!;
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            LiveLabPaths.Resolve(temporary.Path),
            NetworkTwoContract.HostRole);
        paths.EnsureDirectories();
        var fixtureStore = new FakeTestSaveStore(paths);
        TestSaveLaunchState testSave = fixtureStore.Launch;
        NetworkTwoLaunchState launch = Launch(
            paths,
            NetworkTwoContract.HostRole,
            testSave.Identity,
            expectedFarmhandId: null);
        LiveLabState state = State(paths, gamePath, launch, testSave);
        WriteTerminalMarker(
            paths,
            state,
            restoredEnableServer,
            restoredIpConnectionsEnabled);
        var stateStore = new FakeStateStore { State = state };
        var process = new FakeProcessHost
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.Exited),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            gamePath,
            fixtureStore);

        LiveLabCommandResult result = service.StopNetwork();

        Assert.Equal(0, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("stopped", report.State);
        Assert.Equal(restoredEnableServer, report.AlwaysOn?.EnableServer);
        Assert.Equal(restoredIpConnectionsEnabled, report.AlwaysOn?.IpConnectionsEnabled);
        Assert.Equal("passed", report.AlwaysOn?.NetworkTwo?.Phase);
        Assert.Equal(1, fixtureStore.CompleteCount);
        Assert.Null(stateStore.State);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, null)]
    public void HostStopTreatsMissingNetworkOptionRestoreReadbackAsBestEffort(
        bool? restoredEnableServer,
        bool? restoredIpConnectionsEnabled)
    {
        using TemporaryDirectory temporary = new();
        string gamePath = Path.GetDirectoryName(temporary.WriteFile("game/.keep"))!;
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            LiveLabPaths.Resolve(temporary.Path),
            NetworkTwoContract.HostRole);
        paths.EnsureDirectories();
        var fixtureStore = new FakeTestSaveStore(paths);
        TestSaveLaunchState testSave = fixtureStore.Launch;
        NetworkTwoLaunchState launch = Launch(
            paths,
            NetworkTwoContract.HostRole,
            testSave.Identity,
            expectedFarmhandId: null);
        LiveLabState state = State(paths, gamePath, launch, testSave);
        WriteTerminalMarker(
            paths,
            state,
            restoredEnableServer,
            restoredIpConnectionsEnabled,
            "restoreFailed");
        var stateStore = new FakeStateStore { State = state };
        var process = new FakeProcessHost
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
            WaitResult = new LabProcessWaitResult(LabProcessWaitStatus.Exited),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            gamePath,
            fixtureStore);

        LiveLabCommandResult result = service.StopNetwork();

        Assert.Equal(0, result.ExitCode);
        LiveLabReport report = Assert.IsType<LiveLabReport>(result.Report);
        Assert.Equal("stopped", report.State);
        Assert.Empty(report.Problems);
        Assert.Equal("restoreFailed", report.AlwaysOn?.State);
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains(
                "could not confirm restoration of the isolated profile options",
                StringComparison.Ordinal));
        Assert.Equal(1, fixtureStore.CompleteCount);
        Assert.Null(stateStore.State);
    }

    [Fact]
    public void HostRunningStatusRequiresAlwaysOnToApplyBothNetworkOptions()
    {
        using TemporaryDirectory temporary = new();
        string gamePath = Path.GetDirectoryName(temporary.WriteFile("game/.keep"))!;
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            LiveLabPaths.Resolve(temporary.Path),
            NetworkTwoContract.HostRole);
        paths.EnsureDirectories();
        TestSaveLaunchState testSave = TestSave(paths);
        NetworkTwoLaunchState launch = Launch(
            paths,
            NetworkTwoContract.HostRole,
            testSave.Identity,
            expectedFarmhandId: null);
        LiveLabState state = State(paths, gamePath, launch, testSave);
        WriteActiveHostingMarker(paths, state, enableServer: true, ipConnectionsEnabled: false);
        var stateStore = new FakeStateStore { State = state };
        var process = new FakeProcessHost
        {
            InspectResult = new LabProcessInspectResult(LabProcessInspectStatus.Running),
        };
        LiveLabService service = Service(
            paths,
            stateStore,
            new FakeBuilder(),
            process,
            gamePath);

        LiveLabCommandResult result = service.StatusNetwork();

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(
            "networkHostOptionsNotApplied",
            Assert.Single(Assert.IsType<LiveLabReport>(result.Report).Problems).Code);
    }

    private static LiveLabService Service(
        LiveLabPaths paths,
        FakeStateStore stateStore,
        FakeBuilder builder,
        FakeProcessHost process,
        string gamePath,
        ITestSaveFixtureStore? fixtureStore = null) =>
        new(
            paths,
            stateStore,
            builder,
            process,
            () => new DoctorReport(
                1,
                DoctorReport.Ready,
                [new DetectedInstallation(gamePath)]),
            () => StartedAt.AddSeconds(10),
            () => LaunchId,
            fixtureStore,
            reportTopology: NetworkTwoContract.Topology);

    private static NetworkTwoLaunchState Launch(
        LiveLabPaths paths,
        string role,
        TestSaveIdentity identity,
        long? expectedFarmhandId) =>
        new(
            role,
            BuildIdentity,
            identity.FixtureId,
            identity.SaveId,
            Path.Combine(paths.RuntimePath, "network-2.log"),
            expectedFarmhandId);

    private static TestSaveLaunchState TestSave(LiveLabPaths paths)
    {
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            123456789L,
            "SDVKit_123456789",
            TestSaveContract.PlayerName,
            TestSaveContract.FarmName,
            TestSaveContract.FavoriteThing);
        return new TestSaveLaunchState(
            TestSaveContract.ScenarioMode,
            identity,
            Path.Combine(paths.ProjectRoot, "source-saves", identity.SaveId),
            paths.TestSaveWorkPath,
            paths.TestSaveScenarioLogPath);
    }

    private static LiveLabState State(
        LiveLabPaths paths,
        string gamePath,
        NetworkTwoLaunchState network,
        TestSaveLaunchState? testSave) =>
        new(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            LaunchId,
            Identity(gamePath),
            paths.ModsPath,
            paths.StatusPath,
            paths.StopRequestPath,
            testSave,
            network);

    private static OwnedProcessIdentity Identity(string gamePath) =>
        new(
            4242,
            StartedAt,
            Path.Combine(gamePath, "StardewModdingAPI.exe"));

    private static void WriteTerminalMarker(
        LiveLabPaths paths,
        LiveLabState state,
        bool? enableServer,
        bool? ipConnectionsEnabled,
        string phase = "exiting")
    {
        TestSaveLaunchState testSave = Assert.IsType<TestSaveLaunchState>(state.TestSave);
        NetworkTwoLaunchState network = Assert.IsType<NetworkTwoLaunchState>(
            state.NetworkTwo);
        WriteMarker(
            paths,
            state,
            phase,
            new TestSaveStatusMarker(
                TestSaveContract.SchemaVersion,
                testSave.Mode,
                "passed",
                testSave.Identity.FixtureId,
                testSave.Identity.SaveId,
                IdentityVerified: true,
                TestSaveContract.RequiredScenarioTicks,
                "fixture passed",
                testSave.ScenarioLogPath),
            PassedNetwork(network),
            enableServer,
            ipConnectionsEnabled);
    }

    private static void WriteActiveHostingMarker(
        LiveLabPaths paths,
        LiveLabState state,
        bool enableServer,
        bool ipConnectionsEnabled)
    {
        TestSaveLaunchState testSave = Assert.IsType<TestSaveLaunchState>(state.TestSave);
        NetworkTwoLaunchState network = Assert.IsType<NetworkTwoLaunchState>(
            state.NetworkTwo);
        WriteMarker(
            paths,
            state,
            "active",
            new TestSaveStatusMarker(
                TestSaveContract.SchemaVersion,
                testSave.Mode,
                "passed",
                testSave.Identity.FixtureId,
                testSave.Identity.SaveId,
                IdentityVerified: true,
                TestSaveContract.RequiredScenarioTicks,
                "fixture passed",
                testSave.ScenarioLogPath),
            PassedNetwork(network) with
            {
                Phase = "hosting",
                JoinedTicks = 0,
                LocalPlayerId = 101L,
                LocalPlayerName = TestSaveContract.PlayerName,
                RemotePlayerId = 202L,
                RemotePlayerName = NetworkTwoContract.FarmhandName,
            },
            enableServer,
            ipConnectionsEnabled);
    }

    private static NetworkTwoStatusMarker PassedNetwork(NetworkTwoLaunchState launch) =>
        new(
            NetworkTwoContract.SchemaVersion,
            launch.Role,
            "passed",
            launch.BuildIdentity,
            launch.FixtureId,
            launch.SaveId,
            IdentityVerified: true,
            NetworkTwoContract.RequiredJoinedTicks,
            LocalPlayerId: 101L,
            TestSaveContract.PlayerName,
            RemotePlayerId: 202L,
            NetworkTwoContract.FarmhandName,
            "verified pair",
            launch.NetworkLogPath);

    private static void WriteMarker(
        LiveLabPaths paths,
        LiveLabState state,
        string phase,
        TestSaveStatusMarker testSave,
        NetworkTwoStatusMarker network,
        bool? enableServer,
        bool? ipConnectionsEnabled)
    {
        var marker = new AlwaysOnStatusMarker(
            1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            phase,
            600,
            IsActive: false,
            PauseWhenOutOfFocus: phase == "active" ? false : true,
            StartedAt.AddSeconds(10),
            testSave,
            enableServer,
            ipConnectionsEnabled,
            network,
            ForegroundWindowHandle: 12345L,
            ForegroundProcessId: 9001);
        File.WriteAllText(
            paths.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    private sealed class FakeStateStore : ILiveLabStateStore
    {
        public LiveLabState? State { get; set; }

        public int VerifyWritableCount { get; private set; }

        public LiveLabState? Read() => State;

        public void VerifyWritable() => VerifyWritableCount++;

        public void Write(LiveLabState state) => State = state;

        public void Delete() => State = null;
    }

    private sealed class FakeBuilder : IAlwaysOnBuilder
    {
        public int CallCount { get; private set; }

        public AlwaysOnBuildResult BuildAndInstall(string gamePath, LiveLabPaths paths)
        {
            CallCount++;
            return new AlwaysOnBuildResult(
                true,
                Path.Combine(paths.BuildPath, "unexpected-build.log"),
                null);
        }
    }

    private sealed class FakeProcessHost : ILabProcessHost
    {
        public LabProcessStartResult StartResult { get; init; } =
            new(LabProcessStartStatus.Failed, Error: "not configured");

        public LabProcessInspectResult InspectResult { get; init; } =
            new(LabProcessInspectStatus.Exited);

        public LabProcessWaitResult WaitResult { get; init; } =
            new(LabProcessWaitStatus.TimedOut);

        public LabProcessStartSpec? Specification { get; private set; }

        public LabProcessStartResult Start(LabProcessStartSpec specification)
        {
            Specification = specification;
            return StartResult;
        }

        public LabProcessInspectResult Inspect(OwnedProcessIdentity expected) =>
            InspectResult;

        public LabProcessWaitResult WaitForExit(
            OwnedProcessIdentity expected,
            TimeSpan timeout) =>
            WaitResult;

        public LabProcessCloseResult RequestCloseAndWait(
            OwnedProcessIdentity expected,
            TimeSpan timeout) =>
            new(LabProcessCloseStatus.CloseRequestFailed);
    }

    private sealed class FakeTestSaveStore : ITestSaveFixtureStore
    {
        public FakeTestSaveStore(LiveLabPaths paths)
        {
            Launch = TestSave(paths);
        }

        public TestSaveLaunchState Launch { get; }

        public int CompleteCount { get; private set; }

        public TestSavePreparation PrepareForStart() =>
            new(Launch);

        public TestSavePreparation PrepareReviewForStart(bool resetFromBaseline) =>
            new(Launch with { Mode = TestSaveContract.ReviewMode });

        public TestSaveCleanupResult CompleteStopped(
            TestSaveLaunchState launch,
            string launchId)
        {
            CompleteCount++;
            return new TestSaveCleanupResult([], ScenarioLogArchived: true);
        }

        public TestSaveCleanupResult AbortStopped(
            TestSaveLaunchState launch,
            string launchId) =>
            new([], ScenarioLogArchived: false);
    }
}
