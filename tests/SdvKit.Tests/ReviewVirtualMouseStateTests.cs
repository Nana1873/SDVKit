using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewVirtualMouseStateTests
{
    [Fact]
    public void CursorUsesScaledClampedCoordinatesAndClearRestoresPhysicalCoordinates()
    {
        var state = new ReviewVirtualMouseState();
        state.Set(200, 100);
        Assert.Equal((149, 74, 0), state.Apply(8, 9, 0, 100, 50, 1.5f, out _));
        state.Clear();
        Assert.False(state.IsSet);
        Assert.Equal((8, 9, 0), state.Apply(8, 9, 0, 100, 50, 1.5f, out _));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(-120)]
    public void NotchIsConsumedOnceAndClearDoesNotReverseIt(int direction)
    {
        var state = new ReviewVirtualMouseState();
        state.Apply(8, 9, 240, 100, 100, 1, out _);
        state.Set(20, 30);
        Assert.True(state.TryQueueWheel(direction));
        Assert.Equal(240 + direction, state.Apply(8, 9, 240, 100, 100, 1, out _).Wheel);
        Assert.Equal(240 + direction, state.Apply(8, 9, 240, 100, 100, 1, out _).Wheel);
        state.Clear();
        Assert.Equal((8, 9, 240 + direction), state.Apply(8, 9, 240, 100, 100, 1, out _));
        Assert.Equal(360 + direction, state.Apply(8, 9, 360, 100, 100, 1, out _).Wheel);
    }

    [Fact]
    public void ClearCancelsOnlyUnconsumedWheel()
    {
        var state = new ReviewVirtualMouseState();
        state.Apply(8, 9, 0, 100, 100, 1, out _);
        state.Set(20, 30);
        Assert.True(state.TryQueueWheel(120));
        state.Apply(8, 9, 0, 100, 100, 1, out _);
        Assert.True(state.TryQueueWheel(-120));
        state.Clear();
        Assert.Equal((8, 9, 120), state.Apply(8, 9, 0, 100, 100, 1, out _));
        state.Set(20, 30);
        Assert.True(state.TryQueueWheel(-120));
        Assert.Equal(0, state.Apply(8, 9, 0, 100, 100, 1, out _).Wheel);
    }

    [Fact]
    public void RejectedAdditionalNotchDoesNotReplacePendingDirection()
    {
        var state = new ReviewVirtualMouseState();
        Assert.False(state.TryQueueWheel(120));
        state.Apply(8, 9, 0, 100, 100, 1, out _);
        state.Set(20, 30);
        Assert.False(state.TryQueueWheel(1));
        Assert.True(state.TryQueueWheel(120));
        Assert.False(state.TryQueueWheel(-120));
        Assert.Equal(120, state.Apply(8, 9, 0, 100, 100, 1, out _).Wheel);
    }

    [Theory]
    [InlineData(int.MaxValue, 120, -120)]
    [InlineData(int.MinValue, -120, 120)]
    public void CounterOverflowIsRejectedBeforeMutation(int physical, int rejected, int accepted)
    {
        var state = new ReviewVirtualMouseState();
        state.Apply(8, 9, physical, 100, 100, 1, out _);
        state.Set(20, 30);
        Assert.False(state.TryQueueWheel(rejected));
        Assert.Equal(physical, state.Apply(8, 9, physical, 100, 100, 1, out _).Wheel);
        Assert.True(state.TryQueueWheel(accepted));
        Assert.Equal(physical + accepted, state.Apply(8, 9, physical, 100, 100, 1, out _).Wheel);
    }

    [Fact]
    public void WheelNeedsAnActualPhysicalSample()
    {
        var state = new ReviewVirtualMouseState();
        state.Set(20, 30);
        Assert.False(state.TryQueueWheel(120));
        state.Apply(8, 9, 240, 100, 100, 1, out _);
        Assert.True(state.TryQueueWheel(120));
    }

    [Theory]
    [InlineData(int.MaxValue, 120)]
    [InlineData(int.MinValue, -120)]
    public void PhysicalChangeAfterQueueRejectsWholeNotchWithoutChangingOrigin(int physical, int direction)
    {
        var state = new ReviewVirtualMouseState();
        state.Apply(8, 9, 0, 100, 100, 1, out _);
        state.Set(20, 30);
        Assert.True(state.TryQueueWheel(direction));
        Assert.Equal(physical, state.Apply(8, 9, physical, 100, 100, 1, out bool rejected).Wheel);
        Assert.True(rejected);
        Assert.Equal(0, state.Apply(8, 9, 0, 100, 100, 1, out rejected).Wheel);
        Assert.False(rejected);
    }

    [Fact]
    public void UnrepresentableConsumedOriginRetainsLastOutputAndReportsFailure()
    {
        var state = new ReviewVirtualMouseState();
        state.Apply(8, 9, 0, 100, 100, 1, out _);
        state.Set(20, 30);
        Assert.True(state.TryQueueWheel(120));
        Assert.Equal(120, state.Apply(8, 9, 0, 100, 100, 1, out _).Wheel);
        Assert.Equal(120, state.Apply(8, 9, int.MaxValue, 100, 100, 1, out bool rejected).Wheel);
        Assert.True(rejected);
        state.Clear();
        Assert.Equal(120, state.Apply(8, 9, 0, 100, 100, 1, out rejected).Wheel);
        Assert.False(rejected);
    }
}
