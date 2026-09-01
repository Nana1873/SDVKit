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
