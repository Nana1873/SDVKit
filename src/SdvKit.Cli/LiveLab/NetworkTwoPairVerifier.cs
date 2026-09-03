namespace SdvKit.Cli.LiveLab;

internal static class NetworkTwoPairVerifier
{
    public static bool IsPassed(
        AlwaysOnStatusReport? host,
        AlwaysOnStatusReport? farmhand,
        string expectedBuildIdentity) =>
        IsReady(host, farmhand)
        && HasExactIdentity(host!.NetworkTwo!, farmhand!.NetworkTwo!, expectedBuildIdentity);

    public static bool IsReady(
        AlwaysOnStatusReport? host,
        AlwaysOnStatusReport? farmhand)
    {
        NetworkTwoStatusReport? hostNetwork = host?.NetworkTwo;
        NetworkTwoStatusReport? farmhandNetwork = farmhand?.NetworkTwo;
        return host is not null
            && farmhand is not null
            && hostNetwork is not null
            && farmhandNetwork is not null
            && string.Equals(host.State, "active", StringComparison.Ordinal)
            && string.Equals(farmhand.State, "active", StringComparison.Ordinal)
            && host.PauseWhenOutOfFocus == false
            && farmhand.PauseWhenOutOfFocus == false
            && host.EnableServer == true
            && host.IpConnectionsEnabled == true
            && string.Equals(hostNetwork.State, "ready", StringComparison.Ordinal)
            && string.Equals(farmhandNetwork.State, "ready", StringComparison.Ordinal)
            && string.Equals(hostNetwork.Phase, "passed", StringComparison.Ordinal)
            && string.Equals(farmhandNetwork.Phase, "passed", StringComparison.Ordinal)
            && hostNetwork.IdentityVerified == true
            && farmhandNetwork.IdentityVerified == true
            && hostNetwork.JoinedTicks >= NetworkTwoContract.RequiredJoinedTicks
            && farmhandNetwork.JoinedTicks >= NetworkTwoContract.RequiredJoinedTicks;
    }

    public static bool HasExactIdentity(
        NetworkTwoStatusReport host,
        NetworkTwoStatusReport farmhand,
        string? expectedBuildIdentity) =>
        string.Equals(host.BuildIdentity, expectedBuildIdentity, StringComparison.Ordinal)
        && string.Equals(farmhand.BuildIdentity, expectedBuildIdentity, StringComparison.Ordinal)
        && string.Equals(host.FixtureId, farmhand.FixtureId, StringComparison.Ordinal)
        && string.Equals(host.SaveId, farmhand.SaveId, StringComparison.Ordinal)
        && host.LocalPlayerId is not (null or 0)
        && host.RemotePlayerId is not (null or 0)
        && host.LocalPlayerId == farmhand.RemotePlayerId
        && host.RemotePlayerId == farmhand.LocalPlayerId
        && string.Equals(
            host.LocalPlayerName,
            TestSaveContract.PlayerName,
            StringComparison.Ordinal)
        && string.Equals(
            host.RemotePlayerName,
            NetworkTwoContract.FarmhandName,
            StringComparison.Ordinal)
        && string.Equals(
            farmhand.LocalPlayerName,
            NetworkTwoContract.FarmhandName,
            StringComparison.Ordinal)
        && string.Equals(
            farmhand.RemotePlayerName,
            TestSaveContract.PlayerName,
            StringComparison.Ordinal);
}
