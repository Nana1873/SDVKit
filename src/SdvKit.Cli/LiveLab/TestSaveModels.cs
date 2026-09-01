namespace SdvKit.Cli.LiveLab;

internal static class TestSaveContract
{
    public const int SchemaVersion = 1;
    public const string CreateMode = "create";
    public const string ScenarioMode = "scenario";
    public const string ReviewMode = "review";
    public const string PlayerName = "SDVKit";
    public const string FarmName = "SDVKit";
    public const string FavoriteThing = "Tests";
    public const string WorkspaceOwnerMarkerKey = "SDVKit/WorkspaceOwnerId";
    public const string FixtureMarkerKey = "SDVKit/FixtureId";
    public const string ScenarioMarkerKey = "SDVKit/TestSaveScenario";
    public const string FixtureMarkerFileName = ".sdvkit-fixture.json";
    public const int RequiredScenarioTicks = 120;

    public static string GetSaveId(long uniqueGameId) =>
        $"{PlayerName}_{uniqueGameId}";
}

internal sealed record TestSaveIdentity(
    int SchemaVersion,
    string WorkspaceOwnerId,
    string FixtureId,
    long UniqueGameId,
    string SaveId,
    string PlayerName,
    string FarmName,
    string FavoriteThing)
{
    public void Validate()
    {
        if (SchemaVersion != TestSaveContract.SchemaVersion
            || !Guid.TryParseExact(WorkspaceOwnerId, "N", out _)
            || !Guid.TryParseExact(FixtureId, "N", out _)
            || UniqueGameId <= 0
            || !string.Equals(
                SaveId,
                TestSaveContract.GetSaveId(UniqueGameId),
                StringComparison.Ordinal)
            || !string.Equals(PlayerName, TestSaveContract.PlayerName, StringComparison.Ordinal)
            || !string.Equals(FarmName, TestSaveContract.FarmName, StringComparison.Ordinal)
            || !string.Equals(FavoriteThing, TestSaveContract.FavoriteThing, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The SDVKit test-save identity is invalid.");
        }
    }
}

internal sealed record TestSaveLaunchState(
    string Mode,
    TestSaveIdentity Identity,
    string SlotPath,
    string WorkPath,
    string ScenarioLogPath)
{
    public void Validate()
    {
        if (Mode is not (TestSaveContract.CreateMode
            or TestSaveContract.ScenarioMode
            or TestSaveContract.ReviewMode))
        {
            throw new InvalidDataException("The SDVKit test-save launch mode is invalid.");
        }

        Identity?.Validate();
        if (Identity is null
            || string.IsNullOrWhiteSpace(SlotPath)
            || string.IsNullOrWhiteSpace(WorkPath)
            || string.IsNullOrWhiteSpace(ScenarioLogPath)
            || !Path.IsPathFullyQualified(SlotPath)
            || !Path.IsPathFullyQualified(WorkPath)
            || !Path.IsPathFullyQualified(ScenarioLogPath))
        {
            throw new InvalidDataException("The SDVKit test-save launch paths are invalid.");
        }
    }
}

internal sealed record TestSaveStatusMarker(
    int SchemaVersion,
    string Mode,
    string Phase,
    string FixtureId,
    string SaveId,
    bool IdentityVerified,
    int WaitedTicks,
    string? Message,
    string ScenarioLogPath);

internal sealed record TestSaveStatusReport(
    string State,
    string? Mode,
    string? Phase,
    string? FixtureId,
    string? SaveId,
    bool? IdentityVerified,
    int? WaitedTicks,
    string? Message,
    string? ScenarioLogPath);
