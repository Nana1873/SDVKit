using SdvKit.AlwaysOn;

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
}
