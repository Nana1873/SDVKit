using SdvKit.GameSide;

namespace SdvKit.Tests;

public sealed class FishingBankSearchTests
{
    [Fact]
    public void SkipsBlockedBanksAndReturnsReachableWaterAndFacing()
    {
        FishingBank? bank = FishingBankSearch.Find(10, 10,
            (x, y) => x is 4 or 5 && y == 5,
            (x, y) => x == 7 && y == 5);
        Assert.Equal(new FishingBank(7, 5, 3, 5, 5), bank);
    }

    [Fact]
    public void ChoosesTheBankNearestToTheSearchStart()
    {
        // Both banks have water two and three tiles out.
        bool Water(int x, int y) => y == 5 && x is 1 or 2 or 17 or 18;
        bool Stand(int x, int y) => y == 5 && x is 4 or 15;

        Assert.Equal(new FishingBank(4, 5, 3, 2, 5), FishingBankSearch.Find(20, 10, Water, Stand, 5, 5));
        Assert.Equal(new FishingBank(15, 5, 1, 17, 5), FishingBankSearch.Find(20, 10, Water, Stand, 14, 5));
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(7, 3)]
    public void SkipsACloserDownFacingBankForASideCast(int sideBankX, int facing)
    {
        // The origin (5,3) has water two and three tiles down, but preparation must use a side bank.
        int beyondX = sideBankX == 3 ? 6 : 4;
        FishingBank? bank = FishingBankSearch.Find(10, 10,
            (x, y) => (x == 5 && y is 5 or 6) || (x == beyondX && y == 5),
            (x, y) => (x == 5 && y == 3) || (x == sideBankX && y == 5),
            5, 3);

        Assert.Equal(new FishingBank(sideBankX, 5, facing, 5, 5), bank);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void OnlyVerticalBanksFindNothing(int bankY)
    {
        Assert.Null(FishingBankSearch.Find(10, 10,
            (x, y) => x == 5 && y is 4 or 5 or 6,
            (x, y) => x == 5 && y == bankY,
            5, bankY));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsSideBankWhenOnlyMinimumLandingTileIsWater(int direction)
    {
        Assert.Null(FishingBankSearch.Find(10, 10,
            (x, y) => x == 5 + direction * 2 && y == 5,
            (x, y) => x == 5 && y == 5,
            5, 5));
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(1, 1)]
    public void SkipsShallowSideBankForBankWithWaterTwoAndThreeTilesOut(int direction, int facing)
    {
        int minimumX = 5 + direction * 2;
        int beyondX = 5 + direction * 3;
        FishingBank? bank = FishingBankSearch.Find(10, 10,
            (x, y) => (x == minimumX && y is 3 or 6) || (x == beyondX && y == 6),
            (x, y) => x == 5 && y is 3 or 6,
            5, 3);

        Assert.Equal(new FishingBank(5, 6, facing, minimumX, 6), bank);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void WaterBeyondMapEdgeRejectsBankWithoutOutOfBoundsQueries(int waterX)
    {
        Assert.Null(FishingBankSearch.Find(5, 5,
            (x, y) =>
            {
                Assert.InRange(x, 0, 4);
                Assert.InRange(y, 0, 4);
                return x == waterX && y == 2;
            },
            (x, y) => x == 2 && y == 2,
            2, 2));
    }

    [Fact]
    public void SearchStartOutsideTheMapFindsNothing() =>
        Assert.Null(FishingBankSearch.Find(10, 10, (_, _) => true, (_, _) => true, 10, 0));

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
