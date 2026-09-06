using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewTextInputTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Request = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static ReviewInputQuery Query(string text) => new(ReviewInputContract.TextAction, null, null, null, null,
        UiRevision: new string('b', 64), Text: text, FieldId: 2);

    [Theory]
    [InlineData("")]
    [InlineData("abc\n")]
    [InlineData("\t")]
    [InlineData("\u0000")]
    [InlineData("\u007f")]
    [InlineData("abc\U0001f600")]
    public void InvalidTextCannotReachTransport(string text)
    {
        Assert.NotNull(ProjectReviewInputService.Validate(Query(text)));
        Assert.Throws<ArgumentException>(() => ProjectReviewInputService.BuildCommand(Request, Query(text)));
        Assert.Throws<ArgumentException>(() => new ReviewTextProgress(text));
    }

    [Fact]
    public void InvalidUtf16IsRejectedWithoutAttributeSerializationReplacingSurrogates()
    {
        foreach (string text in new[] { new string((char)0xd800, 1), new string((char)0xdc00, 1), new string([(char)0xd800, 'x']) })
            InvalidTextCannotReachTransport(text);
    }

    [Fact]
    public void MaximumThreeByteBmpPayloadRoundTripsWithinConsoleBound()
    {
        string text = new('\u754c', 256);
        string command = ProjectReviewInputService.BuildCommand(Request, Query(text));
        Assert.Equal(1024, command.Split(' ')[^1].Length);
        Assert.Null(ProjectReviewConsoleLine.ValidationError(command));
        Assert.True(ReviewInputArguments.TryParse(command.Split(' ').Skip(1).ToArray(), out var parsed, out _));
        Assert.Equal(text, parsed!.TextQuery!.Text);
    }

    [Theory]
    [InlineData("ASCII")]
    [InlineData("  \u00e4\u00f6\u00fc\u00df \u00c4\u00d6\u00dc  ")]
    [InlineData("\"quoted\" ; command")]
    public void TransportPreservesCharactersWithoutConsoleSyntax(string text)
    {
        string command = ProjectReviewInputService.BuildCommand(Request, Query(text));
        Assert.DoesNotContain(text, command, StringComparison.Ordinal);
        Assert.True(ReviewInputArguments.TryParse(command.Split(' ').Skip(1).ToArray(), out var parsed, out _));
        Assert.Equal(text, parsed!.TextQuery!.Text);
        Assert.Equal(2, parsed.TextQuery.FieldId);
        Assert.Equal(Query(text).UiRevision, parsed.TextQuery.UiRevision);
        Assert.False(ProjectReviewConsoleLine.CanRunBeforeScenarioReady(command));
    }

    [Fact]
    public void LimitCountsScalarsAndRejectsSupplementaryBeforeTheFirstCharacter()
    {
        Assert.Null(ReviewInputContract.ValidateText(new string('a', 256)));
        Assert.Equal("inputTextTooLong", ReviewInputContract.ValidateText(new string('a', 257))!.Code);
        Assert.Equal("inputTextSupplementaryUnsupported", ReviewInputContract.ValidateText(new string('a', 255) + "\U0001f600")!.Code);
        Assert.Equal("inputTextTooLong", ReviewInputContract.ValidateText(new string('a', 256) + "\U0001f600")!.Code);
    }

    [Fact]
    public void RejectsMissingTargetAndForeignActionArguments()
    {
        var query = Query("abc");
        foreach (var invalid in new[] { query with { FieldId = 0 }, query with { UiRevision = null },
            query with { X = 1 }, query with { Button = "Enter" }, query with { Modifiers = [] },
            query with { Action = ReviewInputContract.PressAction, Button = "F8" } })
            Assert.NotNull(ProjectReviewInputService.Validate(invalid));
    }

    [Fact]
    public void PollsOneCharacterAtATimeAndStopsRemainingTextOnLostIdentityOrConcurrency()
    {
        foreach (bool concurrent in new[] { false, true })
        {
            var progress = new ReviewTextProgress("abc");
            Assert.True(progress.TryNext(true, false, out char first));
            Assert.Equal('a', first);
            Assert.False(progress.TryNext(true, false, out _));
            Assert.Equal(0, progress.Delivered);
            progress.ObservePoll(true);
            Assert.Equal(1, progress.Delivered);
            Assert.False(progress.TryNext(concurrent, concurrent, out _));
            Assert.False(progress.TryNext(true, false, out _));
            Assert.Equal(1, progress.Delivered);
        }
    }

    [Fact]
    public void CancellationAndFailedPollNeverQueueRemainder()
    {
        var progress = new ReviewTextProgress("abc");
        progress.Stop();
        Assert.False(progress.TryNext(true, false, out _));
        Assert.Equal(0, progress.Delivered);
        progress = new ReviewTextProgress("abc");
        Assert.True(progress.TryNext(true, false, out _));
        progress.ObservePoll(false);
        Assert.False(progress.TryNext(true, false, out _));
        Assert.Equal(0, progress.Delivered);
    }

    [Fact]
    public void AcknowledgementCarriesOnlyDeliveryCountAndRejectsFalseCompletion()
    {
        var now = DateTimeOffset.UtcNow;
        var query = Query("abc");
        var response = new ReviewInputResponseEnvelope(1, Request, now, 1, "text", true, null, null, null, null,
            false, true, null, DeliveredScalars: 3);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        Assert.Equal(response, ProjectReviewInputService.DeserializeResponse(json));
        Assert.True(ProjectReviewInputService.MatchesResponse(response, Request, query, now, now));
        Assert.False(ProjectReviewInputService.MatchesResponse(response with { DeliveredScalars = 2 }, Request, query, now, now));
        Assert.True(ProjectReviewInputService.MatchesResponse(response with
        {
            Succeeded = false,
            DeliveredScalars = 1,
            Problem = new("inputTextInterrupted", "The target changed.")
        }, Request, query, now, now));
        Assert.DoesNotContain("abc", System.Text.Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }
}
