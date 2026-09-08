#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Tools;
#endif

namespace SdvKit.AlwaysOn;

internal sealed record FishingBank(int X, int Y, int Facing, int WaterX, int WaterY);

internal static class FishingBankSearch
{
    // Bound a synchronous main-thread scan and keep every game query inside the map.
    public const int MaximumDimension = 512;

    public static FishingBank? Find(
        int width, int height,
        Func<int, int, bool> isNaturalWater,
        Func<int, int, bool> canStand)
    {
        if (width is < 3 or > MaximumDimension || height is < 3 or > MaximumDimension)
        {
            return null;
        }

        (int X, int Y, int Facing)[] offsets = [(2, 0, 1), (-2, 0, 3), (0, 2, 2), (0, -2, 0)];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                foreach (var offset in offsets)
                {
                    int waterX = x + offset.X, waterY = y + offset.Y;
                    if (waterX < 0 || waterY < 0 || waterX >= width || waterY >= height
                        || !isNaturalWater(waterX, waterY) || !canStand(x, y))
                    {
                        continue;
                    }
                    return new FishingBank(x, y, offset.Facing, waterX, waterY);
                }
            }
        }
        return null;
    }
}

#if SDVKIT_GAME_AVAILABLE
internal sealed partial class StardewReviewFixtureRuntime
{
    public ReviewFixtureResult PrepareFishing(ReviewFixtureAccess access, bool skipTutorial)
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
        FishingBank? bank = FishingBankSearch.Find(layer.LayerWidth, layer.LayerHeight,
            (x, y) => location.isTileFishable(x, y) && !location.isTileBuildingFishable(x, y),
            (x, y) => !location.isWaterTile(x, y) && !location.isCollidingPosition(
                new Rectangle(x * 64 + 8, y * 64 + 16, 48, 32), Game1.viewport,
                isFarmer: true, damagesFarmer: 0, glider: false, character: player,
                pathfinding: true, projectile: false, ignoreCharacterRequirement: false,
                skipCollisionEffects: true));
        if (bank is null)
        {
            return new ReviewFixtureResult(false, "No collision-free bank with natural water two tiles away was found (map limit: 512x512); fixture unchanged.");
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
        player.setTileLocation(new Vector2(bank.X, bank.Y));
        player.faceDirection(bank.Facing);
        player.netItemStowed.Value = false;
        return new ReviewFixtureResult(true,
            $"Fishing prepared: location={location.NameOrUniqueName}, bank={bank.X},{bank.Y}, "
            + $"facing={bank.Facing}, minimumCastWater={bank.WaterX},{bank.WaterY}, "
            + $"addedBambooRod={addedRod}, seededTutorialCollectionEntry={seededTutorial}. "
            + "No cast, bite, catch callback or save was triggered. Use normal review input to cast.");
    }
}
#endif
