using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewNativeMousePublicationTests
{
    private static readonly ReviewMouseValues Hardware = new(8, 9, 240, ReviewMouseButtons.None);

    private static (ReviewVirtualMouseState Mouse, ReviewNativeMousePublication Publication) Create()
    {
        var mouse = new ReviewVirtualMouseState();
        mouse.Set(20, 30);
        mouse.Apply(8, 9, 240, 100, 100, 1, out _);
        return (mouse, new(mouse));
    }

    [Theory]
    [InlineData((int)ReviewMouseButtons.Left)]
    [InlineData((int)ReviewMouseButtons.Middle)]
    [InlineData((int)ReviewMouseButtons.Right)]
    [InlineData((int)ReviewMouseButtons.X1)]
    [InlineData((int)ReviewMouseButtons.X2)]
    [InlineData((int)(ReviewMouseButtons.Left | ReviewMouseButtons.Right))]
    public void CompletedSamplePublishesOnlyOwnedButtonsAndRetainsOtherHardware(int ownedValue)
    {
        ReviewMouseButtons owned = (ReviewMouseButtons)ownedValue;
        var (_, publication) = Create();
        const ReviewMouseButtons all = ReviewMouseButtons.Left | ReviewMouseButtons.Middle | ReviewMouseButtons.Right
            | ReviewMouseButtons.X1 | ReviewMouseButtons.X2;
        var sample = new ReviewMouseValues(20, 30, 240, all);
        Assert.Equal(Hardware, publication.Read(Hardware, 0, true, out _));
        publication.Publish(sample, owned);
        Assert.Equal(sample with { Buttons = owned }, publication.Read(Hardware, 0, true, out bool overlap));
        Assert.False(overlap);
        var physicalPeer = Hardware with { Buttons = all & ~owned };
        Assert.Equal(sample, publication.Read(physicalPeer, 0, true, out overlap));
        Assert.False(overlap);
        Assert.Equal(Hardware, publication.Read(Hardware, 0, false, out _));
    }

    [Fact]
    public void RepeatedNativeReadsDoNotAdvanceWheelOrReplayItThroughAnotherSample()
    {
        var (mouse, publication) = Create();
        Assert.True(mouse.TryQueueWheel(120));
        var consumed = mouse.Apply(8, 9, 240, 100, 100, 1, out _);
        publication.Publish(new(consumed.X, consumed.Y, consumed.Wheel, ReviewMouseButtons.Left), ReviewMouseButtons.Left);
        for (int i = 0; i < 8; i++) Assert.Equal(360, publication.Read(Hardware, 0, true, out _).Wheel);
        Assert.Equal(1, mouse.WheelSample);
        Assert.True(mouse.TryQueueWheel(-120));
        for (int i = 0; i < 8; i++) Assert.Equal(360, publication.Read(Hardware, 0, true, out _).Wheel);
        Assert.True(mouse.HasPendingWheel);
        Assert.Equal(1, mouse.WheelSample);
        publication.Invalidate();
        mouse.Clear();
        Assert.Equal(Hardware with { Wheel = 360 }, publication.Read(Hardware, 0, true, out _));
        Assert.Equal(Hardware with { Wheel = 480 }, publication.Read(Hardware with { Wheel = 360 }, 0, true, out _));
    }

    [Fact]
    public void CancellationBeforeConfirmationCannotRepublishAndNextReleaseDropsOwnedButtons()
    {
        var (_, publication) = Create();
        var pressed = new ReviewMouseValues(20, 30, 240, ReviewMouseButtons.Left);
        publication.BeginInputSample();
        publication.Invalidate();
        publication.Publish(pressed, ReviewMouseButtons.Left);
        Assert.False(publication.Published);
        Assert.Equal(Hardware, publication.Read(Hardware, 0, true, out _));
        publication.BeginInputSample();
        publication.Publish(pressed, ReviewMouseButtons.Left);
        Assert.Equal(ReviewMouseButtons.Left, publication.Read(Hardware, 0, true, out _).Buttons);
        publication.BeginInputSample();
        Assert.Equal(Hardware, publication.Read(Hardware, 0, true, out _));
        publication.Publish(pressed with { Buttons = 0 }, 0);
        Assert.Equal(ReviewMouseButtons.None, publication.Read(Hardware, 0, true, out _).Buttons);
    }

    [Fact]
    public void SharedClearCancelEofRevocationImmediatelyDropsAllOwnedFields()
    {
        var (_, publication) = Create();
        publication.Publish(new(20, 30, 240, ReviewMouseButtons.Left | ReviewMouseButtons.X2),
            ReviewMouseButtons.Left | ReviewMouseButtons.X2);
        publication.Invalidate();
        Assert.Equal(Hardware, publication.Read(Hardware, 0, true, out _));
        publication.Publish(new(50, 60, 240, ReviewMouseButtons.Left), ReviewMouseButtons.Left);
        Assert.Equal(Hardware, publication.Read(Hardware, 0, true, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicalOverlapOrSuppressionRevokesWithoutSuppressingHardware(bool suppression)
    {
        var (_, publication) = Create();
        publication.Publish(new(20, 30, 240, ReviewMouseButtons.Left), ReviewMouseButtons.Left);
        var hardware = Hardware with { Buttons = suppression ? 0 : ReviewMouseButtons.Left };
        Assert.Equal(hardware, publication.Read(hardware, suppression ? ReviewMouseButtons.Left : 0, true, out bool overlap));
        Assert.True(overlap);
        Assert.Equal(Hardware, publication.Read(Hardware, 0, true, out overlap));
        Assert.False(overlap);
    }

    [Fact]
    public void OriginalHardwareGetterRunsAndCannotObserveOverlayEvenWithNestedReadsOrException()
    {
        var (_, publication) = Create();
        publication.Publish(new(20, 30, 240, ReviewMouseButtons.Left), ReviewMouseButtons.Left);
        int reads = 0;
        ReviewMouseValues Getter()
        {
            reads++;
            return publication.Read(Hardware, 0, true, out _);
        }
        Assert.Equal(Hardware, ReviewNativeMousePublication.ReadHardware(() =>
        {
            Assert.Equal(Hardware, ReviewNativeMousePublication.ReadHardware(Getter));
            return Getter();
        }));
        Assert.Equal(2, reads);
        Assert.Throws<InvalidOperationException>(() => ReviewNativeMousePublication.ReadHardware(
            () => throw new InvalidOperationException("Original getter failed.")));
        Assert.False(ReviewNativeMousePublication.ReadingHardware);
        Assert.Equal(ReviewMouseButtons.Left, Getter().Buttons);
    }
}
