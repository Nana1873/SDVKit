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
        if (item is null || item.QualifiedItemId != qualifiedItemId)
        {
            return new ReviewFixtureResult(false, $"Item {qualifiedItemId} could not be created exactly; backpack unchanged.");
        }
        // Only objects keep a quality; tools, rings, clothing and the like would silently ignore it.
        if (quality != 0 && (item is not StardewValley.Object || item.Quality != quality))
        {
            return new ReviewFixtureResult(false, $"Item {qualifiedItemId} does not take quality {quality}; backpack unchanged.");
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
        string? revision = null;
        string reason = "an exception";
        try
        {
            ReviewInventoryReport inventory = ReviewInventoryCommand.Capture(Guid.NewGuid().ToString("N"),
                access.LaunchId ?? "", access.Topology ?? "single", role);
            reason = inventory.ErrorCode ?? "no revision";
            revision = inventory.State == "ready" ? inventory.Data?.InventoryRevision : null;
        }
        catch (Exception exception)
        {
            reason = exception.GetType().Name;
        }
        if (revision is null)
        {
            // Undo in the same tick, so a refusal never leaves the item behind.
            player.Items[slot] = null;
            return new ReviewFixtureResult(false, $"The backpack could not be read back after adding {qualifiedItemId} ({reason}); the item was removed again and the backpack is unchanged.");
        }

        return new ReviewFixtureResult(true,
            $"Added {stack} x {qualifiedItemId} (quality {quality}) to backpack slot {slot}. Test setup, not proof of game behaviour.",
            Item: new ReviewFixtureItemReport(qualifiedItemId, stack, quality, slot, revision));
    }
}
