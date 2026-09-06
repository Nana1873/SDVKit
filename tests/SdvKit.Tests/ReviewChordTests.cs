using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewChordTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Request = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Revision = new('a', 64);

    [Theory]
    [InlineData(1, "F8")]
    [InlineData(120, "LeftShift", "F8")]
    [InlineData(2, "LeftShift", "MouseLeft")]
    [InlineData(5, "ControllerA", "ControllerB")]
    public void TypedChordRoundTripsToOneAtomicCommand(int duration, params string[] buttons)
    {
        var query = new ReviewInputQuery("chord", null, null, null, null, buttons, duration, Revision);
        string command = ProjectReviewInputService.BuildCommand(Request, query);
        Assert.True(ReviewInputArguments.TryParse(command.Split(' ').Skip(1).ToArray(), out var parsed, out var error), error);
        Assert.Equal(ReviewInputKind.Chord, parsed!.Kind);
        Assert.Equal(buttons, parsed.Buttons);
        Assert.Equal(duration, parsed.DurationTicks);
        Assert.Equal(Revision, parsed.UiRevision);
        Assert.Equal(Request, parsed.RequestId);
    }

    [Theory]
    [InlineData(0, "F8")]
    [InlineData(121, "F8")]
    [InlineData(2, "F8", "f8")]
    [InlineData(2, "None")]
    [InlineData(2, "MouseWheelUp")]
    [InlineData(2, "A+B")]
    public void RejectsUnboundedOrAmbiguousChords(int duration, params string[] buttons)
    {
        var query = new ReviewInputQuery("chord", null, null, null, null, buttons, duration, Revision);
        Assert.NotNull(ProjectReviewInputService.Validate(query));
        Assert.False(ReviewInputArguments.TryParse(["input", "chord", duration.ToString(System.Globalization.CultureInfo.InvariantCulture), Revision, .. buttons], out _, out _));
    }

    [Fact]
    public void RejectsEmptyOversizedStaleAndMixedQueries()
    {
        var query = new ReviewInputQuery("chord", null, null, null, null, ["F8"], 1, Revision);
        Assert.NotNull(ProjectReviewInputService.Validate(query with { Buttons = [] }));
        Assert.NotNull(ProjectReviewInputService.Validate(query with { Buttons = Enumerable.Range(1, 9).Select(i => $"F{i}").ToArray() }));
        Assert.NotNull(ProjectReviewInputService.Validate(query with { UiRevision = "old" }));
        Assert.NotNull(ProjectReviewInputService.Validate(query with { Button = "F8" }));
        Assert.NotNull(ProjectReviewInputService.Validate(query with { Action = "press" }));
    }

    [Fact]
    public void SuccessRequiresCanonicalMembersDurationAndObservedRelease()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var query = new ReviewInputQuery("chord", null, null, null, null, ["leftshift", "f8"], 5, Revision);
        var response = new ReviewInputResponseEnvelope(1, Request, now, 105, "chord", true,
            null, null, null, null, false, false, null, ["LeftShift", "F8"], 5, 100, 105, true);
        bool Matches(ReviewInputResponseEnvelope value) => ProjectReviewInputService.MatchesResponse(value, Request, query, now, now);
        Assert.True(Matches(response));
        Assert.False(Matches(response with { Released = false }));
        Assert.False(Matches(response with { StartTick = null }));
        Assert.False(Matches(response with { EndTick = 100 }));
        Assert.False(Matches(response with { DurationTicks = 4 }));
        Assert.False(Matches(response with { Buttons = ["F8", "LeftShift"] }));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        Assert.True(Matches(ProjectReviewInputService.DeserializeResponse(bytes)!));
    }

    [Fact]
    public void RejectedChordDoesNotInventStartAndReleaseTicks()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var query = new ReviewInputQuery("chord", null, null, null, null, ["F8"], 5, Revision);
        var response = new ReviewInputResponseEnvelope(1, Request, now, 10, "chord", false,
            null, null, null, null, false, false, new("inputChordRejected", "Stale UI."), ["F8"], 5, Released: false);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        Assert.True(ProjectReviewInputService.MatchesResponse(ProjectReviewInputService.DeserializeResponse(bytes), Request, query, now, now));
        Assert.DoesNotContain("startTick", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void PartialPressFailureRollsBackOnlyPrefixOwnedAdditions()
    {
        var queue = new HashSet<int> { 99 };
        var sample = new ReviewOwnedButtonSample();
        Assert.Throws<InvalidOperationException>(() => sample.TryInject([1, 2, 3], queue.Contains, b =>
        {
            queue.Add(b);
            if (b == 2) throw new InvalidOperationException("injection failed");
        }));
        sample.Rollback(b => queue.Remove(b));
        Assert.Equal([99], queue);
        Assert.False(sample.Injected);
    }

    [Fact]
    public void ExternallyQueuedMemberRejectsEntireChordWithoutAnyPress()
    {
        var sample = new ReviewOwnedButtonSample();
        var queue = new HashSet<int> { 2 };
        Assert.False(sample.TryInject([1, 2], queue.Contains, _ => Assert.Fail("Atomic validation must precede all presses.")));
        sample.Rollback(_ => Assert.Fail("No external entry can be owned."));
        Assert.Equal([2], queue);
    }

    [Fact]
    public void FailedSmapiConsumptionRetainsOwnershipUntilNarrowRollback()
    {
        var sample = new ReviewOwnedButtonSample();
        var queue = new HashSet<int> { 99 };
        Assert.True(sample.TryInject([1, 2], queue.Contains, b => queue.Add(b)));
        Assert.False(sample.IsConsumed(queue.Contains));
        queue.Remove(1); // SMAPI may fail after consuming only part of its queue.
        sample.Rollback(b => queue.Remove(b));
        Assert.Equal([99], queue);
        Assert.True(sample.IsConsumed(queue.Contains));
    }
}
