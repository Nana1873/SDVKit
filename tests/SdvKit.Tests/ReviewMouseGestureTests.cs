using System.Globalization;
using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewMouseGestureTests
{
    private static readonly string Revision = new('a', 64);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static ReviewInputQuery Click(int count = 1) => new("click", "MouseLeft", null, 10, 20, UiRevision: Revision, Count: count);
    private static ReviewInputQuery Scroll(int count = 1) => new("scroll", null, null, 10, 20, UiRevision: Revision, Notches: count);
    private static ReviewInputQuery Drag(int duration = 1) => new("drag", "MouseLeft", null, 10, 20, DurationTicks: duration, UiRevision: Revision, EndX: 110, EndY: 220);

    private static void PrepareCursor(ReviewChordProgress progress)
    {
        Assert.True(progress.PreparingCursor);
        progress.ObserveGestureSample(-1, true, true, preparingCursor: true);
        Assert.True(progress.PreparingCursor);
        Assert.True(progress.AwaitingCursorUpdate);
        Assert.False(progress.CompleteGameUpdate(out bool released));
        Assert.False(released);
        Assert.False(progress.PreparingCursor);
        Assert.Null(progress.StartTick);
        Assert.Equal(0, progress.EdgeSamples);
        Assert.Equal(0, progress.CompletedSteps);
    }

    [Theory]
    [InlineData("MouseLeft")]
    [InlineData("MouseRight")]
    [InlineData("MouseMiddle")]
    [InlineData("MouseX1")]
    [InlineData("MouseX2")]
    public void MouseNamesAndDistinctModifiersRoundTripInOneCommand(string button)
    {
        RoundTrip(Click(2) with { Button = button, Modifiers = ["LeftShift", "RightControl", "LeftAlt"] });
        RoundTrip(Drag(120) with { Button = button });
        RoundTrip(Scroll(-20));
        RoundTrip(Scroll(20));
    }

    private static void RoundTrip(ReviewInputQuery query)
    {
        const string id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string command = ProjectReviewInputService.BuildCommand(id, query);
        Assert.True(ProjectReviewConsoleLine.CanRunBeforeScenarioReady(command));
        Assert.True(ReviewInputArguments.TryParse(command.Split(' ').Skip(1).ToArray(), out var parsed, out string error), error);
        Assert.Equal(ReviewInputKind.Gesture, parsed!.Kind);
        Assert.Equal(query.Action, parsed.Gesture!.Action);
        Assert.Equal(query.X, parsed.Gesture.X);
        Assert.Equal(query.Y, parsed.Gesture.Y);
        Assert.Equal(query.EndX, parsed.Gesture.EndX);
        Assert.Equal(query.EndY, parsed.Gesture.EndY);
        Assert.Equal(query.Count, parsed.Gesture.Count);
        Assert.Equal(query.Notches, parsed.Gesture.Notches);
        Assert.Equal(query.DurationTicks, parsed.Gesture.DurationTicks);
        Assert.Equal(query.Modifiers ?? [], parsed.Gesture.Modifiers ?? []);
    }

    [Fact]
    public void ClosedContractsRejectMixedUnboundedAndDuplicateValues()
    {
        ReviewInputQuery[] invalid = [Click(0), Click(3), Scroll(0), Scroll(-21), Scroll(21), Drag(0), Drag(121),
            Click() with { Button = "F8" }, Click() with { X = -1 }, Drag() with { EndX = -1 },
            Click() with { Modifiers = ["LeftShift", "leftshift"] }, Click() with { Modifiers = ["F8"] },
            Scroll() with { Modifiers = [] }, Drag() with { Count = 1 }, Click() with { DurationTicks = 1 },
            Click() with { Notches = 1 }, Scroll() with { Button = "MouseLeft" }, Click() with { UiRevision = "old" }];
        foreach (ReviewInputQuery query in invalid) Assert.NotNull(ProjectReviewInputService.Validate(query));
        Assert.Null(ProjectReviewInputService.Validate(Click()));
        Assert.Null(ProjectReviewInputService.Validate(Click() with { Modifiers = [] }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ClicksRequireSeparatePressReleaseAndGameUpdateBoundaries(int count)
    {
        var progress = new ReviewChordProgress(1, Click(count));
        PrepareCursor(progress);
        for (int click = 0; click < count; click++)
        {
            Assert.Equal(0, progress.EdgeSamples);
            Assert.Equal(1, progress.Remaining);
            progress.ObserveGestureSample(click * 2, false, true);
            Assert.Equal(click, progress.CompletedSteps);
            Assert.False(progress.CompleteGameUpdate(out _));
            Assert.Equal(0, progress.Remaining);
            Assert.Equal(click + 1 < count, progress.MoreActions);
            progress.ObserveGestureSample(click * 2 + 1, true, true);
            Assert.False(progress.Finished);
            Assert.Equal(click + 1 == count, progress.CompleteGameUpdate(out bool released));
            Assert.True(released);
        }
        Assert.Equal(count, progress.CompletedSteps);
        Assert.True(progress.Finished);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(120)]
    public void DragHasInitialPressThenMovementAndHeldEndpointUpdates(int duration)
    {
        var progress = new ReviewChordProgress(1, Drag(duration));
        PrepareCursor(progress);
        Assert.Equal((10, 20), progress.Position);
        for (int sample = 0; sample <= duration; sample++)
        {
            progress.ObserveGestureSample(sample, false, true);
            Assert.False(progress.CompleteGameUpdate(out _));
        }
        Assert.Equal(duration, progress.CompletedSteps);
        Assert.Equal((110, 220), progress.Position);
        Assert.Equal(1, progress.Remaining);
        Assert.True(progress.MoreActions);
        progress.ObserveGestureSample(duration + 1, false, true);
        Assert.False(progress.CompleteGameUpdate(out _));
        Assert.Equal(duration, progress.CompletedSteps);
        Assert.Equal((110, 220), progress.Position);
        Assert.Equal(0, progress.Remaining);
        Assert.False(progress.MoreActions);
        progress.ObserveGestureSample(duration + 2, true, true);
        Assert.True(progress.CompleteGameUpdate(out bool released));
        Assert.True(released);
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(20)]
    public void ScrollCountsOnlyObservedNotchesAfterGameUpdate(int count)
    {
        var progress = new ReviewChordProgress(1, Scroll(count));
        for (int step = 0; step < Math.Abs(count); step++)
        {
            progress.ObserveGestureSample(step, false, true);
            Assert.Equal(step, progress.CompletedSteps);
            progress.CompleteGameUpdate(out _);
        }
        Assert.Equal(Math.Abs(count), progress.CompletedSteps);
        Assert.False(progress.Finished);
        progress.ObserveGestureSample(21, true, true);
        Assert.True(progress.CompleteGameUpdate(out bool released));
        Assert.True(released);
    }

    [Fact]
    public void CanceledProgressRetainsEndpointUntilReleaseCompletion()
    {
        var progress = new ReviewChordProgress(1, Drag(3));
        PrepareCursor(progress);
        progress.ObserveGestureSample(1, false, true);
        progress.CompleteGameUpdate(out _);
        progress.Cancel();
        Assert.Equal(0, progress.Remaining);
        Assert.False(progress.Finished);
        progress.ObserveGestureSample(2, true, true);
        Assert.False(progress.Finished);
        Assert.True(progress.CompleteGameUpdate(out bool released));
        Assert.True(released);
        Assert.Equal(0, progress.CompletedSteps);
    }

    [Fact]
    public void MissingGameCallbackStopsFurtherStepsAndHasBoundedReleaseFallback()
    {
        var progress = new ReviewChordProgress(1, Drag(120));
        PrepareCursor(progress);
        progress.ObserveGestureSample(10, false, true);
        Assert.False(progress.MissingGameUpdate());
        Assert.True(progress.Canceled);
        Assert.Equal(0, progress.CompletedSteps);
        Assert.Equal(0, progress.Remaining);
        Assert.Equal(10, progress.StartTick);
        progress.ObserveGestureSample(11, true, true);
        Assert.True(progress.MissingGameUpdate());
        Assert.False(progress.AwaitingGameUpdate);
        Assert.False(progress.Finished); // caller reports unconfirmed callback completion, not successful release.
    }

    [Fact]
    public void FailedReleaseDoesNotCountACompleteClick()
    {
        var progress = new ReviewChordProgress(1, Click());
        PrepareCursor(progress);
        progress.ObserveGestureSample(1, false, true);
        progress.CompleteGameUpdate(out _);
        progress.Cancel();
        progress.ObserveGestureSample(2, true, false);
        Assert.True(progress.CompleteGameUpdate(out bool released));
        Assert.False(released);
        Assert.Equal(0, progress.CompletedSteps);
    }

    [Fact]
    public void RepeatedFailedSmapiSamplesHaveOnlyOneReleaseRecoveryAttempt()
    {
        var progress = new ReviewChordProgress(1, Drag());
        progress.ObserveFailedInputSample(10);
        Assert.False(progress.CompleteGameUpdate(out _));
        Assert.Equal(0, progress.Remaining);
        progress.ObserveFailedInputSample(11);
        Assert.True(progress.CompleteGameUpdate(out bool released));
        Assert.False(released);
        Assert.Equal(0, progress.CompletedSteps);
        Assert.Null(progress.StartTick);
    }

    [Fact]
    public void FailedSamplesAndSkippedCallbacksAlsoTerminateUnconfirmed()
    {
        var progress = new ReviewChordProgress(1, Scroll());
        progress.ObserveFailedInputSample(10);
        Assert.False(progress.MissingGameUpdate());
        progress.ObserveFailedInputSample(11);
        Assert.True(progress.MissingGameUpdate());
        Assert.False(progress.AwaitingGameUpdate);
        Assert.Equal(0, progress.CompletedSteps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalCoordinateConsumerSeesPreparedClickAndFinalDragPosition(bool drag)
    {
        var progress = new ReviewChordProgress(1, drag ? Drag(3) : Click());
        (int X, int Y) previousPosition = (900, 900);
        bool previousDown = false, dragging = false;
        int presses = 0, releases = 0;
        (int X, int Y)? value = null;
        for (int tick = 0; !progress.Finished && tick < 12; tick++)
        {
            bool preparing = progress.PreparingCursor;
            bool down = !preparing && progress.Remaining > 0;
            var position = progress.Position!.Value;
            progress.ObserveGestureSample(tick, !down, true, preparing);
            // The selected native control hit-tests the previous position, then
            // keeps dragging only while the current raw left button is down.
            if (down && !previousDown)
            {
                presses++;
                dragging = previousPosition == (10, 20);
            }
            if (!down) dragging = false;
            if (dragging) value = previousPosition;
            if (previousDown && !down) releases++;
            previousPosition = position;
            previousDown = down;
            progress.CompleteGameUpdate(out _);
        }
        Assert.True(progress.Finished);
        Assert.Equal(1, presses);
        Assert.Equal(1, releases);
        Assert.Equal(drag ? (110, 220) : (10, 20), value);
        Assert.Equal(drag ? 3 : 1, progress.CompletedSteps);
        Assert.Equal(1, progress.StartTick);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationCannotEmitDownBeforeItsCompletedGameUpdate(bool drag)
    {
        var progress = new ReviewChordProgress(1, drag ? Drag() : Click());
        Assert.Throws<InvalidOperationException>(() => progress.ObserveGestureSample(10, false, true));
        Assert.Throws<InvalidOperationException>(() => progress.Consumed(10));
        progress.ObserveGestureSample(10, true, true, preparingCursor: true);
        Assert.Throws<InvalidOperationException>(() => progress.ObserveGestureSample(10, false, true));
        Assert.Throws<InvalidOperationException>(() => progress.Consumed(10));
        Assert.False(progress.CompleteGameUpdate(out _));
        progress.ObserveGestureSample(11, false, true);
        Assert.False(progress.CompleteGameUpdate(out _));
        Assert.Equal(1, progress.EdgeSamples);
        Assert.Equal(11, progress.StartTick);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CancellationOrContextLossAcrossPreparationDrainsWithoutAnyPress(int boundary)
    {
        var progress = new ReviewChordProgress(1, Drag());
        if (boundary >= 1) progress.ObserveGestureSample(10, true, true, preparingCursor: true);
        if (boundary == 2) progress.CompleteGameUpdate(out _);
        // The runtime's ownership/menu/viewport/EOF guards all cancel this same
        // progression; no first down may remain after any preparation boundary.
        progress.Cancel();
        if (boundary == 1) Assert.False(progress.CompleteGameUpdate(out _));
        Assert.False(progress.PreparingCursor);
        Assert.Equal(0, progress.Remaining);
        progress.ObserveGestureSample(11, true, true);
        Assert.True(progress.CompleteGameUpdate(out bool released));
        Assert.True(released);
        Assert.True(progress.Canceled);
        Assert.Null(progress.StartTick);
        Assert.Equal(0, progress.EdgeSamples);
        Assert.Equal(0, progress.CompletedSteps);
    }

    [Fact]
    public void MissingPreparationGameUpdateCancelsWithoutPromotingItToAPress()
    {
        var progress = new ReviewChordProgress(1, Click());
        progress.ObserveGestureSample(10, true, true, preparingCursor: true);
        Assert.False(progress.MissingGameUpdate());
        Assert.True(progress.Canceled);
        Assert.Null(progress.StartTick);
        Assert.Equal(0, progress.Remaining);
        progress.ObserveGestureSample(11, true, true);
        Assert.True(progress.CompleteGameUpdate(out bool released));
        Assert.True(released);
        Assert.Equal(0, progress.CompletedSteps);
    }

    [Fact]
    public void UnrelatedMouseReadCannotConsumeOrReplayGestureNotch()
    {
        var mouse = new ReviewVirtualMouseState();
        mouse.Set(10, 20);
        mouse.Apply(3, 4, 0, 100, 100, 1f, out _);
        Assert.True(mouse.TryQueueWheel(120));
        Assert.Equal(0, mouse.Apply(3, 4, 0, 100, 100, 1f, out _, consumeWheel: false).Wheel);
        Assert.True(mouse.HasPendingWheel);
        Assert.Equal(120, mouse.Apply(3, 4, 0, 100, 100, 1f, out _).Wheel);
        Assert.Equal(1, mouse.WheelSample);
        mouse.CancelWheel();
        mouse.Clear();
        Assert.Equal(120, mouse.Apply(3, 4, 0, 100, 100, 1f, out _).Wheel);
        Assert.Equal(1, mouse.WheelSample);
    }

    [Fact]
    public void ResponseBindsPartialStepsAndHonestFinalCursor()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const string id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ReviewInputQuery query = Drag(3);
        var response = new ReviewInputResponseEnvelope(1, id, now, 15, "drag", true, "MouseLeft", null, 10, 20,
            true, true, null, DurationTicks: 3, StartTick: 10, EndTick: 14, Released: true, Modifiers: [],
            EndX: 110, EndY: 220, CompletedSteps: 3, FinalX: 110, FinalY: 220);
        bool Matches(ReviewInputResponseEnvelope value) => ProjectReviewInputService.MatchesResponse(value, id, query, now, now);
        Assert.True(Matches(response));
        Assert.False(Matches(response with { Released = false }));
        Assert.False(Matches(response with { CompletedSteps = 2 }));
        Assert.False(Matches(response with { FinalX = 10 }));
        Assert.False(Matches(response with { CursorSet = false }));
        var failure = response with
        {
            Succeeded = false,
            Problem = new("inputGestureInterrupted", "Canceled."),
            CompletedSteps = 1,
            CursorSet = false,
            FinalX = null,
            FinalY = null
        };
        Assert.True(Matches(failure));
        Assert.True(Matches(ProjectReviewInputService.DeserializeResponse(JsonSerializer.SerializeToUtf8Bytes(failure, JsonOptions))!));
    }
}
