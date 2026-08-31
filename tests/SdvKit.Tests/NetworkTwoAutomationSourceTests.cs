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
