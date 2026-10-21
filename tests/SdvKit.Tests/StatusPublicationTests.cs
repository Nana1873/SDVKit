using System.Text.Json;
using SdvKit.GameSide;

namespace SdvKit.Tests;

public sealed class StatusPublicationTests
{
    [Theory]
    [InlineData("exiting", false)]
    [InlineData("exiting", true)]
    [InlineData("restoreFailed", false)]
    [InlineData("restoreFailed", true)]
    public void FinalFailureIsReportedEvenAfterActiveFailure(string phase, bool unauthorized)
    {
        var publication = new StatusPublication();
        var errors = new List<string>();
        Exception denial = unauthorized ? new UnauthorizedAccessException("denied") : new IOException("denied");
        Assert.False(publication.TryWrite("active", () => throw denial, errors.Add));
        Assert.False(publication.TryWrite("active", () => throw denial, errors.Add));
        Assert.Single(errors);

        int attempts = 0;
        Assert.False(publication.TryWrite(phase, () =>
        {
            attempts++;
            throw denial;
        }, errors.Add));

        Assert.Equal(1, attempts);
        Assert.Equal(2, errors.Count);
        Assert.Contains($"final '{phase}'", errors[1], StringComparison.Ordinal);
        Assert.Contains("denied", errors[1], StringComparison.Ordinal);
        Assert.Contains("game will still exit", errors[1], StringComparison.Ordinal);
        Assert.Contains("cannot confirm the final lab status", errors[1], StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulPublicationResetsActiveFailureReporting()
    {
        var publication = new StatusPublication();
        var errors = new List<string>();
        Assert.False(publication.TryWrite("active", () => throw new IOException("first denial"), errors.Add));
        Assert.True(publication.TryWrite("active", () => { }, errors.Add));
        Assert.False(publication.TryWrite("active", () => throw new IOException("later denial"), errors.Add));
        Assert.Equal(2, errors.Count);
        Assert.Contains("later denial", errors[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exiting")]
    [InlineData("restoreFailed")]
    public void SuccessfulFinalPublicationIsNotReportedAsFailure(string phase)
    {
        var publication = new StatusPublication();
        var errors = new List<string>();
        Assert.True(publication.TryWrite(phase, () => { }, errors.Add));
        Assert.Empty(errors);
    }

    [Fact]
    public void UnexpectedFailuresAreNotHidden()
    {
        var publication = new StatusPublication();
        Assert.Throws<InvalidOperationException>(() => publication.TryWrite(
            "exiting", () => throw new InvalidOperationException("unexpected"), _ => { }));
    }

    [Fact]
    public void FallbackUseIsLoggedOnceAtInfoLevel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "status.json");
        var writer = new StatusWriter("11111111111111111111111111111111", path);
        var publication = new StatusPublication();
        var info = new List<string>();
        var errors = new List<string>();
        Assert.True(publication.TryWrite("active", () => writer.Write("active", 1, false, false), errors.Add, info.Add));
        Assert.Empty(info);

        for (int tick = 2; tick <= 3; tick++)
        {
            int current = tick;
            WindowsStatusFile.SimulatedHeldDestinationDenials = 1;
            try
            {
                Assert.True(publication.TryWrite("active", () => writer.Write("active", current, false, false), errors.Add, info.Add));
            }
            finally
            {
                WindowsStatusFile.SimulatedHeldDestinationDenials = 0;
            }
        }

        Assert.Single(info);
        Assert.Contains("held-destination fallback", info[0], StringComparison.Ordinal);
        Assert.Contains("#136", info[0], StringComparison.Ordinal);
        Assert.Empty(errors);
    }

    [Fact]
    public void FailedAsideRemovalKeepsThePublicationAndIsLoggedOncePerPeriod()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "status.json");
        string aside = path + WindowsStatusFile.ReplacedSuffix;
        var writer = new StatusWriter("11111111111111111111111111111111", path);
        var publication = new StatusPublication();
        var warnings = new List<string>();
        var errors = new List<string>();
        var observed = new List<(int Error, string Path)>();
        Assert.True(publication.TryWrite("active", () => writer.Write("active", 1, false, false), errors.Add, null, warnings.Add));

        Action<int, string>? previous = WindowsStatusFile.AsideCleanupObserver;
        WindowsStatusFile.AsideCleanupObserver = (error, observedPath) => observed.Add((error, observedPath));
        try
        {
            for (int tick = 2; tick <= 3; tick++)
            {
                int current = tick;
                WindowsStatusFile.SimulatedHeldDestinationDenials = 1;
                WindowsStatusFile.SimulatedAsideDeleteFailures = 1;
                Assert.True(publication.TryWrite("active", () => writer.Write("active", current, false, false), errors.Add, null, warnings.Add));
                Assert.Equal(current, TickOf(path));
                Assert.True(File.Exists(aside));
            }

            Assert.Single(warnings);
            Assert.Contains("aside name", warnings[0], StringComparison.Ordinal);
            Assert.Equal(2, observed.Count);

            // An ordinary publication neither fails because of the leftover nor reports it again.
            Assert.True(publication.TryWrite("active", () => writer.Write("active", 4, false, false), errors.Add, null, warnings.Add));
            Assert.Equal(4, TickOf(path));
            Assert.True(File.Exists(aside));
            Assert.Single(warnings);
            Assert.Empty(errors);

            // A later successful fallback replaces the leftover.
            WindowsStatusFile.SimulatedHeldDestinationDenials = 1;
            Assert.True(publication.TryWrite("active", () => writer.Write("active", 5, false, false), errors.Add, null, warnings.Add));
            Assert.False(File.Exists(aside));
            Assert.Single(warnings);
        }
        finally
        {
            WindowsStatusFile.AsideCleanupObserver = previous;
            WindowsStatusFile.SimulatedHeldDestinationDenials = 0;
            WindowsStatusFile.SimulatedAsideDeleteFailures = 0;
        }
    }

    private static int TickOf(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("tick").GetInt32();
    }
}
