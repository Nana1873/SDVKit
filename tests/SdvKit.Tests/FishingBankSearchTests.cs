using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class FishingBankSearchTests
{
    [Fact]
    public void SkipsBlockedBanksAndReturnsReachableWaterAndFacing()
    {
        FishingBank? bank = FishingBankSearch.Find(10, 10,
            (x, y) => x == 5 && y == 5,
            (x, y) => x == 7 && y == 5);
        Assert.Equal(new FishingBank(7, 5, 3, 5, 5), bank);
    }

    [Fact]
    public void NeverQueriesOutsideTheMapAndDoesNotInventABank()
    {
        Assert.Null(FishingBankSearch.Find(3, 3,
            (x, y) => { Assert.InRange(x, 0, 2); Assert.InRange(y, 0, 2); return false; },
            (_, _) => throw new InvalidOperationException("No water exists.")));
        Assert.Null(FishingBankSearch.Find(10, 10, (_, _) => true, (_, _) => false));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, -1)]
    [InlineData(513, 10)]
    [InlineData(10, int.MaxValue)]
    public void UnsupportedMapsAreRejectedBeforeGameQueries(int width, int height)
    {
        Assert.Null(FishingBankSearch.Find(width, height,
            (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException()));
    }
}
