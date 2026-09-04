namespace SdvKit.Tests;

public sealed class TestSaveAutomationSourceTests
{
    [Fact]
    public void PassedReviewKeepsVerifyingTheExactLiveWorld()
    {
        string source = ReadAutomationSource();
        int update = source.IndexOf(
            "public void OnUpdateTicked()",
            StringComparison.Ordinal);
        int passedReview = source.IndexOf(
            "if (IsPassedReview)",
            update,
            StringComparison.Ordinal);
        int verification = source.IndexOf(
            "VerifyExactWorld(allowMultiplayer: _allowMultiplayer);",
            passedReview,
            StringComparison.Ordinal);
        int terminalGuard = source.IndexOf(
            "if (IsTerminal)",
            verification,
            StringComparison.Ordinal);

        Assert.True(update >= 0);
        Assert.True(passedReview > update);
        Assert.True(verification > passedReview);
        Assert.True(terminalGuard > verification);
        Assert.DoesNotContain(
            "VerifyExactWorld(allowMultiplayer: true);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReturningToTitleInvalidatesAPassedReview()
    {
        string source = ReadAutomationSource();
        int returnedToTitle = source.IndexOf(
            "public void OnReturnedToTitle()",
            StringComparison.Ordinal);
        int passedReview = source.IndexOf(
            "if (IsPassedReview)",
            returnedToTitle,
            StringComparison.Ordinal);
        int failure = source.IndexOf(
            "Stardew returned to title after the exact fixture was loaded for review.",
            passedReview,
            StringComparison.Ordinal);
        int terminalGuard = source.IndexOf(
            "if (!IsTerminal)",
            failure,
            StringComparison.Ordinal);

        Assert.True(returnedToTitle >= 0);
        Assert.True(passedReview > returnedToTitle);
        Assert.True(failure > passedReview);
        Assert.True(terminalGuard > failure);
    }

    [Fact]
    public void FixtureCommandAuthorizationFreshlyVerifiesPassedReviewWorld()
    {
        string source = ReadAutomationSource();
        int authorization = source.IndexOf(
            "public bool TryVerifyReviewFixture(",
            StringComparison.Ordinal);
        int passedReview = source.IndexOf(
            "if (!IsPassedReview)",
            authorization,
            StringComparison.Ordinal);
        int verification = source.IndexOf(
            "VerifyExactWorld(allowMultiplayer: _allowMultiplayer);",
            passedReview,
            StringComparison.Ordinal);

        Assert.True(authorization >= 0);
        Assert.True(passedReview > authorization);
        Assert.True(verification > passedReview);
    }

    [Fact]
    public void PassedReviewRequiresReviewModeAndPassedPhase()
    {
        string source = ReadAutomationSource();
        int definition = source.IndexOf(
            "private bool IsPassedReview =>",
            StringComparison.Ordinal);
        int reviewMode = source.IndexOf(
            "TestSaveContract.ReviewMode",
            definition,
            StringComparison.Ordinal);
        int passedPhase = source.IndexOf(
            "_phase, \"passed\"",
            reviewMode,
            StringComparison.Ordinal);
        int authorization = source.IndexOf(
            "public bool TryVerifyReviewFixture(",
            passedPhase,
            StringComparison.Ordinal);

        Assert.True(definition >= 0);
        Assert.True(reviewMode > definition);
        Assert.True(passedPhase > reviewMode);
        Assert.True(authorization > passedPhase);
    }

    [Fact]
    public void MultiplayerReviewSynchronizesFarmhandsBeforeSaveSerialization()
    {
        string source = ReadAutomationSource();
        int durableSave = source.IndexOf(
            "private void StartDurableSave()",
            StringComparison.Ordinal);
        int multiplayerGuard = source.IndexOf(
            "if (_allowMultiplayer)",
            durableSave,
            StringComparison.Ordinal);
        int activeHost = source.IndexOf(
            "if (!Context.IsMultiplayer || !Game1.IsServer)",
            multiplayerGuard,
            StringComparison.Ordinal);
        int farmhandSync = source.IndexOf(
            "Game1.Multiplayer.saveFarmhands();",
            activeHost,
            StringComparison.Ordinal);
        int serialization = source.IndexOf(
            "_saveIterator = SaveGame.Save()",
            farmhandSync,
            StringComparison.Ordinal);

        Assert.True(durableSave >= 0);
        Assert.True(multiplayerGuard > durableSave);
        Assert.True(activeHost > multiplayerGuard);
        Assert.True(farmhandSync > activeHost);
        Assert.True(serialization > farmhandSync);
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
                "TestSaveAutomation.cs");
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
