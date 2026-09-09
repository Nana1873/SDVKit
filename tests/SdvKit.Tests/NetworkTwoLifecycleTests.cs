using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class NetworkTwoLifecycleTests
{
    private const string BuildIdentity =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Theory]
    [InlineData("departureArmed", true, true, "departureArmed")]
    [InlineData("departureArmed", false, false, "waitingForFarmhand")]
    [InlineData("waitingForFarmhand", true, true, "rejoined")]
    [InlineData("waitingForFarmhand", true, false, "waitingForFarmhand")]
    [InlineData("waitingForFarmhand", false, false, "waitingForFarmhand")]
    [InlineData("passed", true, true, null)]
    public void HostLifecycleTransitionsRequireObservedDepartureBeforeRejoin(
        string phase,
        bool farmhandOnline,
        bool exactPairVerified,
        string? expected) =>
        Assert.Equal(
            expected,
            NetworkTwoContract.NextHostLifecyclePhase(
                phase,
                farmhandOnline,
                exactPairVerified));

    [Theory]
    [InlineData(NetworkTwoContract.FarmhandRole, "leaving", true)]
    [InlineData(NetworkTwoContract.FarmhandRole, "passed", false)]
    [InlineData(NetworkTwoContract.HostRole, "leaving", false)]
    public void OnlyApprovedFarmhandDeparturePreservesTheFixtureAutomation(
        string role,
        string phase,
        bool expected) =>
        Assert.Equal(
            expected,
            NetworkTwoContract.IsExpectedFarmhandReturnToTitle(role, phase));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void HostLifecycleReadinessRequiresTheOriginalLanConfiguration(
        bool enableServer,
        bool ipConnectionsEnabled,
        bool expected)
    {
        AlwaysOnStatusReport host = Role(
            NetworkTwoContract.HostRole,
            "departureArmed",
            101,
            202,
            enableServer,
            ipConnectionsEnabled);
        AlwaysOnStatusReport farmhand = Role(
            NetworkTwoContract.FarmhandRole,
            "leaveRequested",
            202,
            101);

        Assert.Equal(
            expected,
            NetworkTwoPairVerifier.IsHostLifecycleReady(
                host,
                farmhand,
                BuildIdentity));
    }

    [Theory]
    [InlineData("departureArmed", "leaveRequested", true)]
    [InlineData("departureArmed", "joining", false)]
    [InlineData("waitingForFarmhand", "waitingForRejoin", true)]
    [InlineData("waitingForFarmhand", "joining", true)]
    [InlineData("waitingForFarmhand", "rejoined", true)]
    [InlineData("waitingForFarmhand", "passed", false)]
    [InlineData("rejoined", "rejoined", true)]
    [InlineData("rejoined", "passed", true)]
    [InlineData("passed", "rejoined", true)]
    [InlineData("joined", "joined", false)]
    public void HostLifecycleReadinessRequiresAnExactCoordinatedPhasePair(
        string hostPhase,
        string farmhandPhase,
        bool expected)
    {
        AlwaysOnStatusReport host = Role(
            NetworkTwoContract.HostRole,
            hostPhase,
            101,
            202,
            enableServer: true,
            ipConnectionsEnabled: true);
        AlwaysOnStatusReport farmhand = Role(
            NetworkTwoContract.FarmhandRole,
            farmhandPhase,
            202,
            101);

        Assert.Equal(
            expected,
            NetworkTwoPairVerifier.IsHostLifecycleReady(
                host,
                farmhand,
                BuildIdentity));
    }

    private static AlwaysOnStatusReport Role(
        string role,
        string phase,
        long localPlayerId,
        long remotePlayerId,
        bool? enableServer = null,
        bool? ipConnectionsEnabled = null) =>
        new(
            "active",
            600,
            IsActive: false,
            PauseWhenOutOfFocus: false,
            DateTimeOffset.UtcNow,
            EnableServer: enableServer,
            IpConnectionsEnabled: ipConnectionsEnabled,
            NetworkTwo: new NetworkTwoStatusReport(
                "ready",
                role,
                phase,
                BuildIdentity,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "SDVKit_123456789",
                IdentityVerified: true,
                NetworkTwoContract.RequiredJoinedTicks,
                localPlayerId,
                role == NetworkTwoContract.HostRole
                    ? TestSaveContract.PlayerName
                    : NetworkTwoContract.FarmhandName,
                remotePlayerId,
                role == NetworkTwoContract.HostRole
                    ? NetworkTwoContract.FarmhandName
                    : TestSaveContract.PlayerName,
                "lifecycle",
                @"E:\SDVKit\network.log",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
}
