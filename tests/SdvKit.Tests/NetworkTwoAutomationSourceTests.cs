namespace SdvKit.Tests;

public sealed class NetworkTwoAutomationSourceTests
{
    [Fact]
    public void PassedProofFreezesBeforeAnotherForegroundObservation()
    {
        string source = ReadAutomationSource();
        int update = source.IndexOf(
            "public void OnUpdateTicked()",
            StringComparison.Ordinal);
        int terminalGuard = source.IndexOf(
            "if (IsTerminal)",
            update,
            StringComparison.Ordinal);
        int foregroundObservation = source.IndexOf(
            "_foregroundWindow = WindowsForegroundWindowProbe.Observe();",
            terminalGuard,
            StringComparison.Ordinal);

        Assert.True(update >= 0);
        Assert.True(terminalGuard > update);
        Assert.True(foregroundObservation > terminalGuard);
    }

    [Fact]
    public void JoinedTicksUseTheExactPairAndWin32UnfocusedProof()
    {
        string source = ReadAutomationSource();
        int observation = source.IndexOf(
            "private void ObserveJoinedTicks()",
            StringComparison.Ordinal);
        int pairVerification = source.IndexOf(
            "bool exactPairVerified = TryVerifyJoinedPair();",
            observation,
            StringComparison.Ordinal);
        int counter = source.IndexOf(
            "NetworkTwoContract.NextVerifiedUnfocusedTickCount(",
            pairVerification,
            StringComparison.Ordinal);
        int foregroundProof = source.IndexOf(
            "_foregroundWindow.IsVerifiedUnfocused",
            counter,
            StringComparison.Ordinal);

        Assert.True(observation >= 0);
        Assert.True(pairVerification > observation);
        Assert.True(counter > pairVerification);
        Assert.True(foregroundProof > counter);
        Assert.DoesNotContain("Game1.ticks - _joinedAtTick", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CabinBuildUsesOnlyTheVerifiedStartingLocation()
    {
        string source = ReadAutomationSource();

        Assert.Contains("startingCabinLocations.Add(candidates[0]);", source);
        Assert.Contains("Game1.getFarm().BuildStartingCabins();", source);
        Assert.DoesNotContain("Game1.startingCabins", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.cabinsSeparate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewResumeUsesTheTestSaveModeAndOnlyTheExactSavedFarmhand()
    {
        string source = ReadAutomationSource();
        int statusMode = source.IndexOf(
            "VerifyHostFixtureBeforeHosting(testSave.Mode);",
            StringComparison.Ordinal);
        int saveIdentity = source.IndexOf(
            "Constants.SaveFolderName, _launch.SaveId",
            statusMode,
            StringComparison.Ordinal);
        int preparation = source.IndexOf(
            "Farmer farmhand = PrepareHostFarmhand(testSaveMode);",
            saveIdentity,
            StringComparison.Ordinal);
        int reviewMode = source.IndexOf(
            "TestSaveContract.ReviewMode",
            preparation,
            StringComparison.Ordinal);
        int exactCount = source.IndexOf(
            "farmhands.Count == 1",
            reviewMode,
            StringComparison.Ordinal);
        int customized = source.IndexOf(
            "if (savedFarmhand.isUnclaimedFarmhand",
            exactCount,
            StringComparison.Ordinal);
        int stableId = source.IndexOf(
            "savedFarmhand.UniqueMultiplayerID == 0",
            customized,
            StringComparison.Ordinal);
        int exactIdentity = source.IndexOf(
            "!MatchesPlayer(",
            stableId,
            StringComparison.Ordinal);
        int farmhandRole = source.IndexOf(
            "NetworkTwoContract.FarmhandRole",
            exactIdentity,
            StringComparison.Ordinal);
        int farmhandName = source.IndexOf(
            "NetworkTwoContract.FarmhandName",
            farmhandRole,
            StringComparison.Ordinal);
        int playerMatcher = source.IndexOf(
            "private bool MatchesPlayer(Farmer player, string role, string name)",
            farmhandName,
            StringComparison.Ordinal);
        int exactName = source.IndexOf(
            "string.Equals(player.Name, name, StringComparison.Ordinal)",
            playerMatcher,
            StringComparison.Ordinal);
        int fixtureMarker = source.IndexOf(
            "TestSaveContract.FixtureMarkerKey",
            exactName,
            StringComparison.Ordinal);
        int exactFixture = source.IndexOf(
            "string.Equals(fixtureId, _launch.FixtureId, StringComparison.Ordinal)",
            fixtureMarker,
            StringComparison.Ordinal);
        int buildMarker = source.IndexOf(
            "NetworkTwoContract.BuildMarkerKey",
            exactFixture,
            StringComparison.Ordinal);
        int exactBuild = source.IndexOf(
            "string.Equals(buildIdentity, _loadedBuildIdentity, StringComparison.Ordinal)",
            buildMarker,
            StringComparison.Ordinal);
        int roleMarker = source.IndexOf(
            "NetworkTwoContract.RoleMarkerKey",
            exactBuild,
            StringComparison.Ordinal);
        int exactRole = source.IndexOf(
            "string.Equals(observedRole, role, StringComparison.Ordinal)",
            roleMarker,
            StringComparison.Ordinal);

        Assert.True(statusMode >= 0);
        Assert.True(saveIdentity > statusMode);
        Assert.True(preparation > saveIdentity);
        Assert.True(reviewMode > preparation);
        Assert.True(exactCount > reviewMode);
        Assert.True(customized > exactCount);
        Assert.True(stableId > customized);
        Assert.True(exactIdentity > stableId);
        Assert.True(farmhandRole > exactIdentity);
        Assert.True(farmhandName > farmhandRole);
        Assert.True(playerMatcher > farmhandName);
        Assert.True(exactName > playerMatcher);
        Assert.True(fixtureMarker > exactName);
        Assert.True(exactFixture > fixtureMarker);
        Assert.True(buildMarker > exactFixture);
        Assert.True(exactBuild > buildMarker);
        Assert.True(roleMarker > exactBuild);
        Assert.True(exactRole > roleMarker);
    }

    [Fact]
    public void FarmhandSelectionAcceptsOnlyFreshOrExactPersistedReviewIdentity()
    {
        string source = ReadAutomationSource();
        int connecting = source.IndexOf(
            "if (string.Equals(_phase, \"connecting\", StringComparison.Ordinal))",
            StringComparison.Ordinal);
        int stableId = source.IndexOf(
            "farmhand.UniqueMultiplayerID != _launch.ExpectedFarmhandId",
            connecting,
            StringComparison.Ordinal);
        int fresh = source.IndexOf(
            "bool isFreshFarmhand = farmhand.isUnclaimedFarmhand;",
            stableId,
            StringComparison.Ordinal);
        int persisted = source.IndexOf(
            "bool isPersistedReviewFarmhand = !isFreshFarmhand",
            fresh,
            StringComparison.Ordinal);
        int exactIdentity = source.IndexOf(
            "&& MatchesPlayer(",
            persisted,
            StringComparison.Ordinal);
        int farmhandRole = source.IndexOf(
            "NetworkTwoContract.FarmhandRole",
            exactIdentity,
            StringComparison.Ordinal);
        int farmhandName = source.IndexOf(
            "NetworkTwoContract.FarmhandName",
            farmhandRole,
            StringComparison.Ordinal);
        int rejection = source.IndexOf(
            "if (!isFreshFarmhand && !isPersistedReviewFarmhand)",
            farmhandName,
            StringComparison.Ordinal);
        int configuration = source.IndexOf(
            "ConfigureFarmhand(farmhand);",
            rejection,
            StringComparison.Ordinal);

        Assert.True(connecting >= 0);
        Assert.True(stableId > connecting);
        Assert.True(fresh > stableId);
        Assert.True(persisted > fresh);
        Assert.True(exactIdentity > persisted);
        Assert.True(farmhandRole > exactIdentity);
        Assert.True(farmhandName > farmhandRole);
        Assert.True(rejection > farmhandName);
        Assert.True(configuration > rejection);
    }

    [Fact]
    public void NonReviewModesKeepTheStrictEmptyFarmhandBaseline()
    {
        string source = ReadAutomationSource();
        int preparation = source.IndexOf(
            "private Farmer PrepareHostFarmhand(string testSaveMode)",
            StringComparison.Ordinal);
        int reviewMode = source.IndexOf(
            "TestSaveContract.ReviewMode",
            preparation,
            StringComparison.Ordinal);
        int savedReturn = source.IndexOf(
            "return savedFarmhand;",
            reviewMode,
            StringComparison.Ordinal);
        int strictEmpty = source.IndexOf(
            "if (farmhands.Count != 0)",
            savedReturn,
            StringComparison.Ordinal);
        int cabinBuild = source.IndexOf(
            "Game1.getFarm().BuildStartingCabins();",
            strictEmpty,
            StringComparison.Ordinal);

        Assert.True(preparation >= 0);
        Assert.True(reviewMode > preparation);
        Assert.True(savedReturn > reviewMode);
        Assert.True(strictEmpty > savedReturn);
        Assert.True(cabinBuild > strictEmpty);
    }

    [Fact]
    public void FixtureCommandAuthorizationFreshlyReverifiesThePassedPairAndSave()
    {
        string source = ReadAutomationSource();
        int authorization = source.IndexOf(
            "public bool TryVerifyReviewFixture(",
            StringComparison.Ordinal);
        int passed = source.IndexOf(
            "!string.Equals(_phase, \"passed\", StringComparison.Ordinal)",
            authorization,
            StringComparison.Ordinal);
        int pair = source.IndexOf(
            "if (!TryVerifyJoinedPair())",
            passed,
            StringComparison.Ordinal);
        int pairMethod = source.IndexOf(
            "private bool TryVerifyJoinedPair()",
            pair,
            StringComparison.Ordinal);
        int save = source.IndexOf(
            "NetworkTwoContract.MatchesReviewSaveIdentity(",
            pairMethod,
            StringComparison.Ordinal);

        Assert.True(authorization >= 0);
        Assert.True(passed > authorization);
        Assert.True(pair > passed);
        Assert.True(pairMethod > pair);
        Assert.True(save > pairMethod);
    }

    private static string ReadAutomationSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                directory.FullName,
                "src",
                "SdvKit.AlwaysOn",
                "NetworkTwoAutomation.cs");
            if (File.Exists(path))
            {
                return File.ReadAllText(path).ReplaceLineEndings("\n");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }
}
