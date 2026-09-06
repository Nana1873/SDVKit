using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewPendingPressesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(120)]
    public void DurationCountsCompletedInputSamplesAndRequiresSeparateRelease(int duration)
    {
        var progress = new ReviewChordProgress(duration);
        for (int i = 0; i < duration; i++)
        {
            Assert.False(progress.Finished);
            progress.Consumed(100 + i);
            Assert.Equal(100, progress.StartTick);
        }
        Assert.Equal(0, progress.Remaining);
        Assert.Null(progress.EndTick);
        Assert.Throws<InvalidOperationException>(() => progress.Consumed(500));
        progress.Released(500);
        Assert.True(progress.Finished);
        Assert.Equal(500, progress.EndTick);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void RejectsUnboundedDuration(int duration) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReviewChordProgress(duration));

    [Fact]
    public void CancellationStopsReinjectionAndRetainsObservedStart()
    {
        var progress = new ReviewChordProgress(120);
        progress.Consumed(7);
        progress.Cancel();
        Assert.True(progress.Canceled);
        Assert.Equal(0, progress.Remaining);
        Assert.Equal(7, progress.StartTick);
        Assert.False(progress.Finished);
        progress.Released(8);
        Assert.True(progress.Finished);
    }

    [Fact]
    public void CancellationBeforeDispatchDoesNotInventStart()
    {
        var progress = new ReviewChordProgress(1);
        progress.Cancel();
        Assert.Null(progress.StartTick);
        progress.Released(8);
        Assert.Equal(8, progress.EndTick);
    }

    [Fact]
    public void ReleaseBeforeRequestedSamplesIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => new ReviewChordProgress(2).Released(10));
}
