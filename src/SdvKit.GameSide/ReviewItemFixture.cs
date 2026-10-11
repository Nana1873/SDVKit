using SdvKit.Cli.LiveLab;
using StardewValley;

namespace SdvKit.GameSide;

internal sealed partial class StardewReviewFixtureRuntime
{
    public ReviewFixtureResult AddItem(ReviewFixtureAccess access, string qualifiedItemId, int stack, int quality)
    {
        // Operation dispatch has just verified the owned disposable save and main-player role.
        Farmer player = Game1.player;
        if (Game1.activeClickableMenu is not null || Game1.eventUp || player.UsingTool
            || player.CursorSlotItem is not null)
        {
            return new ReviewFixtureResult(false, "Item add requires no open menu or event, no tool in use and no item held by the cursor; backpack unchanged.");
        }
        if (!ItemRegistry.Exists(qualifiedItemId))
        {
            return new ReviewFixtureResult(false, $"Item {qualifiedItemId} does not exist in the loaded item data; backpack unchanged.");
        }

        Item? item = ItemRegistry.Create(qualifiedItemId, 1, quality, allowNull: true);
        if (item is null || item.QualifiedItemId != qualifiedItemId || item.Quality != quality)
        {
            return new ReviewFixtureResult(false, $"Item {qualifiedItemId} could not be created exactly with quality {quality}; backpack unchanged.");
        }
        if (stack > item.maximumStackSize())
        {
            return new ReviewFixtureResult(false, $"Item {qualifiedItemId} stacks to at most {item.maximumStackSize()}; backpack unchanged.");
        }

        int capacity = Math.Min(player.Items.Count, player.MaxItems);
        int slot = Enumerable.Range(0, capacity).FirstOrDefault(index => player.Items[index] is null, -1);
        if (slot < 0)
        {
            return new ReviewFixtureResult(false, "The backpack has no empty slot; nothing was added or merged.");
        }

        item.Stack = stack;
        player.Items[slot] = item;
        string? role = string.Equals(access.Role, ReviewFixtureTransportContract.SingleRoleToken, StringComparison.Ordinal)
            ? null : access.Role;
        ReviewInventoryReport inventory = ReviewInventoryCommand.Capture(Guid.NewGuid().ToString("N"),
            access.LaunchId ?? "", access.Topology ?? "single", role);
        if (inventory.State != "ready" || inventory.Data?.InventoryRevision is not { } revision)
        {
            return new ReviewFixtureResult(false, $"Item {qualifiedItemId} was added to slot {slot}, but the backpack could not be read back ({inventory.ErrorCode}).");
        }

        return new ReviewFixtureResult(true,
            $"Added {stack} x {qualifiedItemId} (quality {quality}) to backpack slot {slot}. Test setup, not proof of game behaviour.",
            Item: new ReviewFixtureItemReport(qualifiedItemId, stack, quality, slot, revision));
    }
}
