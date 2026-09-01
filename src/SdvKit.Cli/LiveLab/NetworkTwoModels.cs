namespace SdvKit.Cli.LiveLab;

internal static class NetworkTwoContract
{
    public const int SchemaVersion = 1;
    public const string Topology = "network-2";
    public const string HostRole = "host";
    public const string FarmhandRole = "farmhand";
    public const string FarmhandName = "SDVKitFarmhand";
    public const string BuildMarkerKey = "SDVKit/NetworkTwoBuild";
    public const string RoleMarkerKey = "SDVKit/NetworkTwoRole";
    public const int RequiredJoinedTicks = 120;

    public static bool IsRole(string role) =>
        role is HostRole or FarmhandRole;

    public static int NextVerifiedUnfocusedTickCount(
        int currentCount,
        bool exactPairVerified,
        bool verifiedUnfocused)
    {
        return exactPairVerified && verifiedUnfocused
            ? checked(currentCount + 1)
            : 0;
    }

    public static bool MatchesReviewSaveIdentity(
        string role,
        string expectedSaveId,
        string? observedSaveFolderName,
        ulong uniqueGameId)
    {
        if (!IsRole(role)
            || string.IsNullOrWhiteSpace(expectedSaveId)
            || string.IsNullOrWhiteSpace(observedSaveFolderName))
        {
            return false;
        }

        if (string.Equals(
                observedSaveFolderName,
                expectedSaveId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(role, FarmhandRole, StringComparison.Ordinal)
            && uniqueGameId is > 0 and <= long.MaxValue
            && string.Equals(
                TestSaveContract.GetSaveId((long)uniqueGameId),
                expectedSaveId,
                StringComparison.Ordinal);
    }
}

internal sealed record NetworkTwoLaunchState(
    string Role,
    string BuildIdentity,
    string FixtureId,
    string SaveId,
    string NetworkLogPath,
    long? ExpectedFarmhandId = null)
{
    public void Validate()
    {
        if (!NetworkTwoContract.IsRole(Role)
            || !ModBuildIdentity.IsValid(BuildIdentity)
            || !Guid.TryParseExact(FixtureId, "N", out _)
            || string.IsNullOrWhiteSpace(SaveId)
            || string.IsNullOrWhiteSpace(NetworkLogPath)
            || !Path.IsPathFullyQualified(NetworkLogPath)
            || (string.Equals(Role, NetworkTwoContract.HostRole, StringComparison.Ordinal)
                && ExpectedFarmhandId is not null)
            || (string.Equals(Role, NetworkTwoContract.FarmhandRole, StringComparison.Ordinal)
                && ExpectedFarmhandId is null or 0))
        {
            throw new InvalidDataException("The network-2 launch state is invalid.");
        }
    }
}

internal sealed record NetworkTwoStatusMarker(
    int SchemaVersion,
    string Role,
    string Phase,
    string BuildIdentity,
    string FixtureId,
    string SaveId,
    bool IdentityVerified,
    int JoinedTicks,
    long? LocalPlayerId,
    string? LocalPlayerName,
    long? RemotePlayerId,
    string? RemotePlayerName,
    string? Message,
    string NetworkLogPath);

internal sealed record NetworkTwoStatusReport(
    string State,
    string? Role,
    string? Phase,
    string? BuildIdentity,
    string? FixtureId,
    string? SaveId,
    bool? IdentityVerified,
    int? JoinedTicks,
    long? LocalPlayerId,
    string? LocalPlayerName,
    long? RemotePlayerId,
    string? RemotePlayerName,
    string? Message,
    string? NetworkLogPath);
