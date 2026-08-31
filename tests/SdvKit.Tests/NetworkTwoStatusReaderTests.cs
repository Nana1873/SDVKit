using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class NetworkTwoStatusReaderTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);

    private const string BuildIdentity =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Theory]
    [InlineData(NetworkTwoContract.HostRole, 101L, 202L)]
    [InlineData(NetworkTwoContract.FarmhandRole, 202L, 101L)]
    [InlineData(NetworkTwoContract.FarmhandRole, -202L, 101L)]
    public void MatchingPassedMarkerBindsRoleBuildFixturePathAndPairIdentities(
        string role,
        long localPlayerId,
        long remotePlayerId)
    {
        using TemporaryDirectory temporary = new();
        NetworkTwoLaunchState expected = Launch(
            temporary,
            role,
            string.Equals(role, NetworkTwoContract.FarmhandRole, StringComparison.Ordinal)
                ? localPlayerId
                : null);
        NetworkTwoStatusMarker network = Passed(
            expected,
            localPlayerId,
            remotePlayerId);
        string path = WriteMarker(temporary, network);

        AlwaysOnStatusReport report = Read(path, expected);

        Assert.Equal("active", report.State);
        NetworkTwoStatusReport networkReport = Assert.IsType<NetworkTwoStatusReport>(
            report.NetworkTwo);
        Assert.Equal("ready", networkReport.State);
        Assert.Equal(role, networkReport.Role);
        Assert.Equal("passed", networkReport.Phase);
        Assert.Equal(BuildIdentity, networkReport.BuildIdentity);
        Assert.Equal(expected.FixtureId, networkReport.FixtureId);
        Assert.Equal(expected.SaveId, networkReport.SaveId);
        Assert.True(networkReport.IdentityVerified);
        Assert.Equal(NetworkTwoContract.RequiredJoinedTicks, networkReport.JoinedTicks);
        Assert.Equal(localPlayerId, networkReport.LocalPlayerId);
        Assert.Equal(remotePlayerId, networkReport.RemotePlayerId);
        Assert.Equal(expected.NetworkLogPath, networkReport.NetworkLogPath);
        Assert.Equal(12345L, report.ForegroundWindowHandle);
        Assert.Equal(9001, report.ForegroundProcessId);
        Assert.False(report.IsActive);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("build")]
    [InlineData("fixture")]
    [InlineData("save")]
    [InlineData("path")]
    public void LaunchBoundMarkerRejectsEveryIdentityMismatch(string mismatch)
    {
        using TemporaryDirectory temporary = new();
        NetworkTwoLaunchState expected = Launch(
            temporary,
            NetworkTwoContract.FarmhandRole,
            expectedFarmhandId: 202L);
        NetworkTwoStatusMarker marker = Passed(expected, 202L, 101L);
        marker = mismatch switch
        {
            "role" => marker with { Role = NetworkTwoContract.HostRole },
            "build" => marker with
            {
                BuildIdentity =
                    "sha256:2222222222222222222222222222222222222222222222222222222222222222",
            },
            "fixture" => marker with
            {
                FixtureId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            },
            "save" => marker with { SaveId = "SDVKit_987654321" },
            "path" => marker with
            {
                NetworkLogPath = Path.Combine(temporary.Path, "other.log"),
            },
            _ => throw new InvalidOperationException("Unknown mismatch."),
        };
        string path = WriteMarker(temporary, marker);

        AlwaysOnStatusReport report = Read(path, expected);

        Assert.Equal("mismatch", report.NetworkTwo?.State);
    }

    [Fact]
    public void FarmhandMarkerMustUseTheExpectedFarmhandIdentityBeforeAndAfterJoin()
    {
        using TemporaryDirectory temporary = new();
        NetworkTwoLaunchState expected = Launch(
            temporary,
            NetworkTwoContract.FarmhandRole,
            expectedFarmhandId: 202L);
        NetworkTwoStatusMarker wrong = Passed(expected, 303L, 101L);
        string path = WriteMarker(temporary, wrong);

        AlwaysOnStatusReport report = Read(path, expected);

        Assert.Equal("invalid", report.NetworkTwo?.State);
    }

    [Theory]
    [InlineData(NetworkTwoContract.RequiredJoinedTicks - 1, 101L, 202L, "host", "farmhand")]
    [InlineData(NetworkTwoContract.RequiredJoinedTicks, null, 202L, "host", "farmhand")]
    [InlineData(NetworkTwoContract.RequiredJoinedTicks, 101L, null, "host", "farmhand")]
    [InlineData(NetworkTwoContract.RequiredJoinedTicks, 101L, 202L, "", "farmhand")]
    [InlineData(NetworkTwoContract.RequiredJoinedTicks, 101L, 202L, "host", "")]
    public void PassedMarkerRequiresTheWholeVerifiedPair(
        int joinedTicks,
        long? localPlayerId,
        long? remotePlayerId,
        string localPlayerName,
        string remotePlayerName)
    {
        using TemporaryDirectory temporary = new();
        NetworkTwoLaunchState expected = Launch(
            temporary,
            NetworkTwoContract.HostRole,
            expectedFarmhandId: null);
        NetworkTwoStatusMarker invalid = Passed(expected, 101L, 202L) with
        {
            JoinedTicks = joinedTicks,
            LocalPlayerId = localPlayerId,
            RemotePlayerId = remotePlayerId,
            LocalPlayerName = localPlayerName,
            RemotePlayerName = remotePlayerName,
        };
        string path = WriteMarker(temporary, invalid);

        AlwaysOnStatusReport report = Read(path, expected);

        Assert.Equal("invalid", report.NetworkTwo?.State);
    }

    [Theory]
    [InlineData(null, 9001, false)]
    [InlineData(12345L, null, false)]
    [InlineData(0L, 9001, false)]
    [InlineData(12345L, 0, false)]
    [InlineData(12345L, 4242, true)]
    [InlineData(12345L, 9001, true)]
    public void PassedMarkerRequiresAConsistentDifferentForegroundWindow(
        long? foregroundWindowHandle,
        int? foregroundProcessId,
        bool isActive)
    {
        using TemporaryDirectory temporary = new();
        NetworkTwoLaunchState expected = Launch(
            temporary,
            NetworkTwoContract.HostRole,
            expectedFarmhandId: null);
        string path = WriteMarker(
            temporary,
            Passed(expected, 101L, 202L),
            foregroundWindowHandle,
            foregroundProcessId,
            isActive);

        AlwaysOnStatusReport report = Read(path, expected);

        Assert.Equal("invalid", report.NetworkTwo?.State);
    }

    [Fact]
    public void MissingExpectedNetworkPayloadIsPendingAndUnexpectedPayloadIsRejected()
    {
        using TemporaryDirectory temporary = new();
        NetworkTwoLaunchState expected = Launch(
            temporary,
            NetworkTwoContract.HostRole,
            expectedFarmhandId: null);
        string withoutNetwork = WriteMarker(temporary, network: null);

        AlwaysOnStatusReport pending = Read(withoutNetwork, expected);

        Assert.Equal("pending", pending.NetworkTwo?.State);
        Assert.Equal(expected.Role, pending.NetworkTwo?.Role);
        Assert.Equal(expected.BuildIdentity, pending.NetworkTwo?.BuildIdentity);
        Assert.Equal(expected.NetworkLogPath, pending.NetworkTwo?.NetworkLogPath);

        string withNetwork = WriteMarker(temporary, Passed(expected, 101L, 202L));
        AlwaysOnStatusReport unexpected = AlwaysOnStatusReader.Read(
            withNetwork,
            "11111111111111111111111111111111",
            Process(),
            StartedAt.AddSeconds(11));

        Assert.Equal("unexpected", unexpected.NetworkTwo?.State);
    }

    private static AlwaysOnStatusReport Read(
        string statusPath,
        NetworkTwoLaunchState expected) =>
        AlwaysOnStatusReader.Read(
            statusPath,
            "11111111111111111111111111111111",
            Process(),
            StartedAt.AddSeconds(11),
            expectedNetworkTwo: expected);

    private static NetworkTwoLaunchState Launch(
        TemporaryDirectory temporary,
        string role,
        long? expectedFarmhandId) =>
        new(
            role,
            BuildIdentity,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "SDVKit_123456789",
            Path.Combine(temporary.Path, $"{role}.network-2.log"),
            expectedFarmhandId);

    private static NetworkTwoStatusMarker Passed(
        NetworkTwoLaunchState expected,
        long localPlayerId,
        long remotePlayerId) =>
        new(
            NetworkTwoContract.SchemaVersion,
            expected.Role,
            "passed",
            expected.BuildIdentity,
            expected.FixtureId,
            expected.SaveId,
            IdentityVerified: true,
            NetworkTwoContract.RequiredJoinedTicks,
            localPlayerId,
            string.Equals(expected.Role, NetworkTwoContract.HostRole, StringComparison.Ordinal)
                ? TestSaveContract.PlayerName
                : NetworkTwoContract.FarmhandName,
            remotePlayerId,
            string.Equals(expected.Role, NetworkTwoContract.HostRole, StringComparison.Ordinal)
                ? NetworkTwoContract.FarmhandName
                : TestSaveContract.PlayerName,
            "verified pair",
            expected.NetworkLogPath);

    private static string WriteMarker(
        TemporaryDirectory temporary,
        NetworkTwoStatusMarker? network,
        long? foregroundWindowHandle = 12345L,
        int? foregroundProcessId = 9001,
        bool isActive = false)
    {
        string path = Path.Combine(temporary.Path, "always-on-status.json");
        var marker = new AlwaysOnStatusMarker(
            1,
            "11111111111111111111111111111111",
            Process().ProcessId,
            Process().StartTimeUtc,
            "active",
            600,
            IsActive: isActive,
            PauseWhenOutOfFocus: false,
            StartedAt.AddSeconds(10),
            NetworkTwo: network,
            ForegroundWindowHandle: foregroundWindowHandle,
            ForegroundProcessId: foregroundProcessId);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
        return path;
    }

    private static OwnedProcessIdentity Process() =>
        new(4242, StartedAt, @"E:\Games\StardewModdingAPI.exe");
}
