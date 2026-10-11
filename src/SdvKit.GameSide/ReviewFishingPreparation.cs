using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Tools;

namespace SdvKit.GameSide;

internal sealed partial class StardewReviewFixtureRuntime
{
    public ReviewFixtureResult PrepareFishing(ReviewFixtureAccess access, bool skipTutorial, int? originX, int? originY)
    {
        // Operation dispatch has just verified the owned disposable save and main-player role.
        Farmer player = Game1.player;
        GameLocation location = player.currentLocation;
        if (Game1.activeClickableMenu is not null || Game1.eventUp || Game1.isFestival()
            || Game1.fadeToBlack || player.UsingTool || !player.CanMove
            || player.CurrentTool is FishingRod { isFishing: true } or FishingRod { fishCaught: true })
        {
            return new ReviewFixtureResult(false, "Fishing preparation requires an idle, movable player with no menu, event, fade or active catch.");
        }
        if (!location.IsOutdoors || location.Map?.GetLayer("Back") is not { } layer)
        {
            return new ReviewFixtureResult(false, "Fishing preparation requires the current outdoor location with a loaded Back layer.");
        }

        FishingRod? rod = player.CurrentTool as FishingRod;
        int[] emptySlots = Enumerable.Range(0, player.Items.Count).Where(i => player.Items[i] is null).ToArray();
        if (emptySlots.Length < (rod is null ? 2 : 1))
        {
            return new ReviewFixtureResult(false, "Fishing preparation needs one empty catch slot, plus one rod slot if no rod is selected; no items were replaced.");
        }
        int startX = originX ?? player.TilePoint.X;
        int startY = originY ?? player.TilePoint.Y;
        if (startX < 0 || startY < 0 || startX >= layer.LayerWidth || startY >= layer.LayerHeight)
        {
            return new ReviewFixtureResult(false, $"The search start {startX},{startY} is outside the current map; fixture unchanged.");
        }

        FishingBank? bank = FishingBankSearch.Find(layer.LayerWidth, layer.LayerHeight,
            (x, y) => location.isTileFishable(x, y) && !location.isTileBuildingFishable(x, y),
            (x, y) => !location.isWaterTile(x, y) && !location.isCollidingPosition(
                new Rectangle(x * 64 + 8, y * 64 + 16, 48, 32), Game1.viewport,
                isFarmer: true, damagesFarmer: 0, glider: false, character: player,
                pathfinding: true, projectile: false, ignoreCharacterRequirement: false,
                skipCollisionEffects: true),
            startX,
            startY);
        if (bank is null)
        {
            return new ReviewFixtureResult(false, "No collision-free side-facing bank with natural water two tiles away was found (map limit: 512x512); fixture unchanged.");
        }

        bool addedRod = rod is null;
        if (addedRod)
        {
            player.Items[emptySlots[0]] = new FishingRod(0);
            player.CurrentToolIndex = emptySlots[0];
        }
        bool seededTutorial = skipTutorial && player.fishCaught.Length == 0;
        if (seededTutorial)
        {
            // An explicit test prerequisite, never credited as a real catch or inventory item.
            player.fishCaught["(O)145"] = [1, 5];
        }
        player.Halt();
        // Center StandingPixel on the bank: the rod's 120px minimum side cast then lands inside the reported tile.
        player.setTileLocation(new Vector2(bank.X, bank.Y));
        player.faceDirection(bank.Facing);
        player.netItemStowed.Value = false;
        return new ReviewFixtureResult(true,
            $"Fishing prepared: location={location.NameOrUniqueName}, searchStart={startX},{startY}, bank={bank.X},{bank.Y}, "
            + $"facing={bank.Facing}, minimumCastWater={bank.WaterX},{bank.WaterY}, "
            + $"addedBambooRod={addedRod}, seededTutorialCollectionEntry={seededTutorial}. "
            + "No cast, bite, catch callback or save was triggered. Use normal review input to cast.");
    }
}
