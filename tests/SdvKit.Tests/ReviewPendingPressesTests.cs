using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewPendingPressesTests
{
    private const int MouseLeft = 1000;

    [Fact]
    public void ClearCancelsQueuedClickBeforeRestoringPhysicalCoordinates()
    {
        var mouse = new ReviewVirtualMouseState();
        mouse.Set(20, 30);
        var pending = new ReviewPendingPresses();
        pending.Add(MouseLeft);
        var smapiQueue = new HashSet<int> { MouseLeft };

        pending.CancelAndClear(mouse, button =>
        {
            Assert.Equal((20, 30, 0), mouse.Apply(80, 90, 0, 100, 100, 1, out _));
            Assert.True(smapiQueue.Remove(button));
        });

        Assert.Empty(smapiQueue);
        Assert.Equal((80, 90, 0), mouse.Apply(80, 90, 0, 100, 100, 1, out _));
    }

    [Fact]
    public void ConsumedPressIsNotSuppressedEvenWithoutPublicTickEvent()
    {
        var pending = new ReviewPendingPresses();
        pending.Add(MouseLeft);
        pending.RetireConsumed(_ => false);
        pending.CancelAndClear(new ReviewVirtualMouseState(), _ => Assert.Fail("Consumed input must not be suppressed."));
    }

    [Fact]
    public void FailedUpdateKeepsOnlyActuallyQueuedOwnedPressesCancelable()
    {
        var pending = new ReviewPendingPresses();
        pending.Add(MouseLeft);
        pending.Add(42);
        var smapiQueue = new HashSet<int> { MouseLeft, 99 };
        pending.RetireConsumed(smapiQueue.Contains);
        var canceled = new List<int>();
        pending.CancelAndClear(new ReviewVirtualMouseState(), canceled.Add);
        Assert.Equal(new[] { MouseLeft }, canceled);
    }

    [Fact]
    public void FailedCancellationDoesNotClearCursorOrLosePendingOwnership()
    {
        var mouse = new ReviewVirtualMouseState();
        mouse.Set(20, 30);
        var pending = new ReviewPendingPresses();
        pending.Add(MouseLeft);
        Assert.Throws<InvalidOperationException>(() =>
            pending.CancelAndClear(mouse, _ => throw new InvalidOperationException("cancel failed")));
        Assert.True(mouse.IsSet);
        var canceled = new List<int>();
        pending.CancelAndClear(mouse, canceled.Add);
        Assert.Equal(new[] { MouseLeft }, canceled);
        Assert.False(mouse.IsSet);
    }

    [Fact]
    public void LateWheelOverflowFailureResetCancelsQueuedClickAndCursor()
    {
        var mouse = new ReviewVirtualMouseState();
        mouse.Apply(80, 90, 0, 100, 100, 1, out _);
        mouse.Set(20, 30);
        Assert.True(mouse.TryQueueWheel(120));
        var pending = new ReviewPendingPresses();
        pending.Add(MouseLeft);

        mouse.Apply(80, 90, int.MaxValue, 100, 100, 1, out bool rejected);
        Assert.True(rejected);
        var canceled = new List<int>();
        pending.CancelAndClear(mouse, canceled.Add);

        Assert.Equal(new[] { MouseLeft }, canceled);
        Assert.False(mouse.IsSet);
        Assert.Equal((80, 90, 0), mouse.Apply(80, 90, 0, 100, 100, 1, out rejected));
        Assert.False(rejected);
    }
}
