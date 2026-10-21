using SdvKit.Cli.LiveLab;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;

namespace SdvKit.GameSide;

internal sealed partial class StardewReviewFixtureRuntime
{
    // FishingRod.DoFunction tags the "Hit!" overlay whose end opens the bobber bar with this ID.
    private const int FishHitOverlayId = 987654321;

    public ReviewFixtureResult CatchFish(ReviewFixtureAccess access)
    {
        // Operation dispatch has just verified the owned disposable save and main-player role.
        Farmer player = Game1.player;
        GameLocation? location = player.currentLocation;
        if (location is null || Game1.activeClickableMenu is not null || Game1.eventUp || Game1.isFestival()
            || Game1.fadeToBlack)
        {
            return Refused("fishingCatchBlocked", "The fishing catch requires a loaded location with no menu, event, festival or fade; nothing changed.");
        }
        if (player.CurrentTool is not FishingRod { isFishing: true } rod)
        {
            return Refused("fishingCatchNotFishing", "The main player is not fishing with a cast bobber; cast first and read localPlayer.data.fishing.fishing=true. Nothing changed.");
        }
        if (rod.castedButBobberStillInAir || rod.isCasting || rod.isTimingCast || rod.hit || rod.isReeling
            || rod.pullingOutOfWater || rod.fishCaught || rod.showingTreasure || rod.lastUser != player)
        {
            return Refused("fishingCatchRodState", "The rod is not waiting for a bite (casting, hooked, reeling, pulling out or holding a catch); nothing changed.");
        }
        int bobberX = (int)(rod.bobber.X / 64f);
        int bobberY = (int)(rod.bobber.Y / 64f);
        if (!location.canFishHere() || !location.isTileFishable(bobberX, bobberY))
        {
            return Refused("fishingCatchBobberNotInWater", $"The bobber at {bobberX},{bobberY} is not in fishable water; nothing changed.");
        }
        if (location.isTileBuildingFishable(bobberX, bobberY))
        {
            return Refused("fishingCatchFishPond", "Catching from a fish pond is not supported; nothing changed.");
        }
        int capacity = Math.Min(player.Items.Count, player.MaxItems);
        if (!Enumerable.Range(0, capacity).Any(index => player.Items[index] is null))
        {
            return Refused("fishingCatchBackpackFull", "The backpack has no empty slot, so the game would open a menu for the catch; nothing changed.");
        }

        // The bite exactly as FishingRod.tickUpdate starts it, then the hook the auto-hook enchantment uses:
        // DoFunction picks the fish for this bobber and either pulls junk out at once or shows "Hit!".
        rod.fishingBiteAccumulator = 0f;
        rod.timeUntilFishingBite = -1f;
        rod.isNibbling = true;
        rod.timePerBobberBob = 1f;
        rod.timeUntilFishingNibbleDone = FishingRod.maxTimeToNibble;
        rod.DoFunction(location, (int)rod.bobber.X, (int)rod.bobber.Y, 1, player);

        bool bossFish = false;
        if (rod.hit)
        {
            TemporaryAnimatedSprite? hitOverlay = Game1.screenOverlayTempSprites.FirstOrDefault(sprite => sprite.id == FishHitOverlayId);
            if (hitOverlay?.endFunction is null)
            {
                return Refused("fishingCatchNotHooked", "The game marked a hit but showed no minigame start; the rod stays hooked until the review is stopped and reset.");
            }
            Game1.screenOverlayTempSprites.Remove(hitOverlay);
            hitOverlay.endFunction(0);
            if (Game1.activeClickableMenu is not BobberBar bar)
            {
                return Refused("fishingCatchNotHooked", "The game did not open the bobber bar after the hit; the rod stays reeling until the review is stopped and reset.");
            }

            // Only the minigame is skipped: finish it as BobberBar.update does for a won catch that was not perfect
            // and found no treasure, so the fish data, size and quality stay the game's own.
            if (bar.fishSize == bar.maxFishSize)
            {
                bar.fishSize--;
            }
            string? bait = rod.GetBait()?.QualifiedItemId;
            int numCaught = bar.bossFish || bait != "(O)774" || Game1.random.NextDouble() >= 0.25 + player.DailyLuck / 2.0 ? 1 : 2;
            // A non-perfect win missed at least once, and each miss costs challenge bait one fish.
            if (bar.challengeBaitFishes > 1)
            {
                numCaught = bar.challengeBaitFishes - 1;
            }
            bar.handledFishResult = true;
            bossFish = bar.bossFish;
            rod.pullFishFromWater(bar.whichFish, bar.fishSize, bar.fishQuality, (int)bar.difficulty, treasureCaught: false,
                wasPerfect: false, bar.fromFishPond, bar.setFlagOnCatch, bar.bossFish, numCaught);
            Game1.exitActiveMenu();
        }
        if (!rod.pullingOutOfWater || rod.whichFish is null)
        {
            return Refused("fishingCatchNotHooked", "The game did not hook anything at the forced bite; read localPlayer.data.fishing before retrying.");
        }

        // The flight animation's end calls playerCaughtFishEndFunction; call it now and drop the animation so it
        // cannot run twice. That records the catch, then doneHoldingFish adds the fish like the click would.
        rod.animations.Clear();
        rod.playerCaughtFishEndFunction(bossFish);
        if (!rod.fishCaught || rod.whichFish is null)
        {
            return Refused("fishingCatchNotHooked", "The game did not reach the holding-fish state; read localPlayer.data.fishing.");
        }
        string qualifiedItemId = rod.whichFish.QualifiedItemId;
        int quality = rod.fishQuality;
        int size = rod.fishSize;
        int before = CountInBackpack(player, qualifiedItemId);
        rod.doneHoldingFish(player);
        int added = CountInBackpack(player, qualifiedItemId) - before;
        if (added < 1)
        {
            return Refused("fishingCatchNotInBackpack", $"The game recorded the catch of {qualifiedItemId}, but nothing arrived in the backpack.");
        }

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
            return Refused("fishingCatchReadBackFailed", $"The game caught {added} x {qualifiedItemId}, but the backpack could not be read back ({reason}).");
        }

        return new ReviewFixtureResult(true,
            $"Caught {added} x {qualifiedItemId} (quality {quality}, size {size}) through the game's catch path at bobber {bobberX},{bobberY}. "
            + "Test setup: the bite was forced and the minigame skipped; not proof of either.",
            Catch: new ReviewFixtureCatchReport(qualifiedItemId, added, quality, size, revision));
    }

    private static int CountInBackpack(Farmer player, string qualifiedItemId) =>
        player.Items.Where(item => item?.QualifiedItemId == qualifiedItemId).Sum(item => item.Stack);

    private static ReviewFixtureResult Refused(string code, string message) =>
        new(false, message, ProblemCode: code);
}
