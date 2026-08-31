using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class NetworkTwoStateTests
{
    private const string BuildIdentity =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Theory]
    [InlineData(true, true, 8)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    public void PairTicksAdvanceOnlyForAnExactVerifiedUnfocusedPair(
        bool exactPairVerified,
        bool verifiedUnfocused,
        int expected)
    {
        int actual = NetworkTwoContract.NextVerifiedUnfocusedTickCount(
            currentCount: 7,
            exactPairVerified,
            verifiedUnfocused);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole, null)]
    [InlineData(NetworkTwoContract.FarmhandRole, 987654321L)]
    [InlineData(NetworkTwoContract.FarmhandRole, -987654321L)]
    public void StateStoreRoundTripsOnlyTheExactRoleIdentityContract(
        string role,
        long? expectedFarmhandId)
    {
        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(singlePaths, role);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        TestSaveLaunchState? testSave = string.Equals(
                role,
                NetworkTwoContract.HostRole,
                StringComparison.Ordinal)
            ? TestSave(paths)
            : null;
        NetworkTwoLaunchState launch = Launch(paths, role, expectedFarmhandId);
        LiveLabState state = State(paths, launch, testSave);

        store.Write(state);

        LiveLabState restored = Assert.IsType<LiveLabState>(store.Read());
        Assert.Equal(NetworkTwoContract.Topology, restored.Topology);
        Assert.Equal(launch, restored.NetworkTwo);
        Assert.Equal(expectedFarmhandId, restored.NetworkTwo?.ExpectedFarmhandId);
        Assert.Equal(testSave, restored.TestSave);
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole, 123L)]
    [InlineData(NetworkTwoContract.FarmhandRole, null)]
    [InlineData(NetworkTwoContract.FarmhandRole, 0L)]
    public void StateStoreRejectsAnInvalidExpectedFarmhandIdentity(
        string role,
        long? expectedFarmhandId)
    {
        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(singlePaths, role);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        NetworkTwoLaunchState invalid = Launch(paths, role, expectedFarmhandId);
        TestSaveLaunchState? testSave = string.Equals(
                role,
                NetworkTwoContract.HostRole,
                StringComparison.Ordinal)
            ? TestSave(paths)
            : null;
        LiveLabState state = State(paths, invalid, testSave);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => store.Write(state));

        Assert.Contains("network-2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.StatePath));
    }

    [Fact]
    public void FarmhandStateCannotOwnTheDisposableFixture()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        NetworkTwoLaunchState launch = Launch(
            paths,
            NetworkTwoContract.FarmhandRole,
            expectedFarmhandId: 987654321L);
        LiveLabState invalid = State(paths, launch, TestSave(paths));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => store.Write(invalid));

        Assert.Contains("topology", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.StatePath));
    }

    [Fact]
    public void NetworkTopologyRequiresItsLaunchPayload()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        LiveLabState invalid = State(
            paths,
            Launch(paths, NetworkTwoContract.HostRole, null)) with
        {
            NetworkTwo = null,
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => store.Write(invalid));

        Assert.Contains("topology", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.StatePath));
    }

    [Fact]
    public void HostStateRequiresTheExactMatchingDisposableFixture()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths paths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        NetworkTwoLaunchState launch = Launch(
            paths,
            NetworkTwoContract.HostRole,
            expectedFarmhandId: null);

        LiveLabState missingFixture = State(paths, launch);
        Assert.Throws<InvalidDataException>(() => store.Write(missingFixture));

        TestSaveLaunchState wrongFixture = TestSave(paths) with
        {
            Identity = TestSave(paths).Identity with
            {
                FixtureId = "dddddddddddddddddddddddddddddddd",
            },
        };
        LiveLabState mismatchedFixture = State(paths, launch, wrongFixture);
        Assert.Throws<InvalidDataException>(() => store.Write(mismatchedFixture));

        Assert.False(File.Exists(paths.StatePath));
    }

    private static NetworkTwoLaunchState Launch(
        LiveLabPaths paths,
        string role,
        long? expectedFarmhandId) =>
        new(
            role,
            BuildIdentity,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "SDVKit_123456789",
            Path.Combine(paths.RuntimePath, "network-2.log"),
            expectedFarmhandId);

    private static LiveLabState State(
        LiveLabPaths paths,
        NetworkTwoLaunchState launch,
        TestSaveLaunchState? testSave = null) =>
        new(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            new OwnedProcessIdentity(
                4242,
                new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero),
                Path.Combine(paths.ProjectRoot, "StardewModdingAPI.exe")),
            paths.ModsPath,
            paths.StatusPath,
            paths.StopRequestPath,
            testSave,
            launch);

    private static TestSaveLaunchState TestSave(LiveLabPaths paths)
    {
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            "cccccccccccccccccccccccccccccccc",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
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
}
