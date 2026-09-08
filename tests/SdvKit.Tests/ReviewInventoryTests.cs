using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewInventoryTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Capture = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void SharedItemAndSlotContractDistinguishesEmptyOccupiedAndUnavailable()
    {
        Assert.True(ReviewItemSlotContract.SlotValid(new(0, "empty", null, null), 0));
        Assert.True(ReviewItemSlotContract.SlotValid(new(1, "occupied", null, new("(O)388", 5, 0)), 1));
        Assert.True(ReviewItemSlotContract.SlotValid(new(2, "unavailable", "itemDataUnavailable", null), 2));
        Assert.False(ReviewItemSlotContract.SlotValid(new(0, "empty", null, new("(O)388", 1, 0)), 0));
        Assert.False(ReviewItemSlotContract.ItemValid(new("(O)388", 0, 0)));
        Assert.False(ReviewItemSlotContract.ItemValid(new("invalid", 1, null)));
    }

    [Fact]
    public void RevisionChangesWithLaunchPlayerSelectionSlotsAndQuantity()
    {
        IReadOnlyList<ReviewItemSlot> slots = [new(0, "occupied", null, new("(O)388", 5, 0)), new(1, "empty", null, null)];
        string revision = ReviewInventoryContract.Revision(Launch, "123", 2, 0, slots);
        Assert.True(ReviewInventoryContract.IsRevision(revision));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(new string('c', 32), "123", 2, 0, slots));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(Launch, "124", 2, 0, slots));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(Launch, "123", 2, 1, slots));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(Launch, "123", 2, 0,
            [slots[0] with { Item = slots[0].Item! with { Stack = 4 } }, slots[1]]));
    }

    [Fact]
    public void CompleteAndPartialCapturesRequireEveryBoundedSlot()
    {
        ReviewInventoryValues complete = Values([
            new(0, "occupied", null, new("(O)388", 5, 0)),
            new(1, "empty", null, null),
        ]);
        Assert.True(ReviewInventoryContract.DataValid(complete, Launch));

        ReviewInventoryValues partial = Values([
            complete.Slots[0],
            new(1, "unavailable", "itemDataUnavailable", null),
        ], complete: false);
        Assert.True(ReviewInventoryContract.DataValid(partial, Launch));

        Assert.False(ReviewInventoryContract.DataValid(complete with { Capacity = 3 }, Launch));
        Assert.False(ReviewInventoryContract.DataValid(complete with { SelectedSlot = 2 }, Launch));
        Assert.False(ReviewInventoryContract.DataValid(complete with { Slots = [complete.Slots[1], complete.Slots[0]] }, Launch));
        Assert.False(ReviewInventoryContract.DataValid(partial with { Complete = true, Limitations = [] }, Launch));
    }

    private static ReviewInventoryValues Values(IReadOnlyList<ReviewItemSlot> slots, bool complete = true)
    {
        var value = new ReviewInventoryValues(Capture, string.Empty, "123", slots.Count, 0, complete,
            complete ? [] : ["itemDataUnavailable"], slots);
        return value with { InventoryRevision = ReviewInventoryContract.Revision(Launch, value.PlayerId,
            value.Capacity, value.SelectedSlot, value.Slots) };
    }
}
