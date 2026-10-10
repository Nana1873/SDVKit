#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Tools;
#endif

namespace SdvKit.GameSide;

internal sealed record FishingBank(int X, int Y, int Facing, int WaterX, int WaterY);

internal static class FishingBankSearch
{
    // Bound a synchronous main-thread scan and keep every game query inside the map.
    public const int MaximumDimension = 512;

    /// <summary>
    /// Finds the standing tile closest to the origin from which natural water is two tiles away.
    /// The search grows in square rings around the origin and stops once no closer bank can exist.
    /// </summary>
    public static FishingBank? Find(
        int width, int height,
        Func<int, int, bool> isNaturalWater,
        Func<int, int, bool> canStand,
        int originX = 0,
        int originY = 0)
    {
        if (width is < 3 or > MaximumDimension || height is < 3 or > MaximumDimension
            || originX < 0 || originY < 0 || originX >= width || originY >= height)
        {
            return null;
        }

        (int X, int Y, int Facing)[] offsets = [(2, 0, 1), (-2, 0, 3), (0, 2, 2), (0, -2, 0)];
        FishingBank? best = null;
        long bestDistance = long.MaxValue;
        int maximumRadius = Math.Max(width, height);
        for (int radius = 0; radius <= maximumRadius; radius++)
        {
            if (best is not null && (long)radius * radius > bestDistance)
            {
                break;
            }

            // Walk only the ring's perimeter.
            for (int x = originX - radius; x <= originX + radius; x++)
            {
                Visit(x, originY - radius);
                if (radius > 0)
                {
                    Visit(x, originY + radius);
                }
            }

            for (int y = originY - radius + 1; y <= originY + radius - 1; y++)
            {
                Visit(originX - radius, y);
                Visit(originX + radius, y);
            }
        }

        return best;

        void Visit(int x, int y)
        {
            if (x < 1 || y < 1 || x >= width - 1 || y >= height - 1)
            {
                return;
            }

            long distance = (long)(x - originX) * (x - originX) + (long)(y - originY) * (y - originY);
            if (distance >= bestDistance)
            {
                return;
            }

            foreach (var offset in offsets)
            {
                int waterX = x + offset.X, waterY = y + offset.Y;
                if (waterX < 0 || waterY < 0 || waterX >= width || waterY >= height
                    || !isNaturalWater(waterX, waterY) || !canStand(x, y))
                {
                    continue;
                }

                best = new FishingBank(x, y, offset.Facing, waterX, waterY);
                bestDistance = distance;
                return;
            }
        }
    }
}

#if SDVKIT_GAME_AVAILABLE
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
            $"Fishing prepared: location={location.NameOrUniqueName}, searchStart={startX},{startY}, bank={bank.X},{bank.Y}, "
            + $"facing={bank.Facing}, minimumCastWater={bank.WaterX},{bank.WaterY}, "
            + $"addedBambooRod={addedRod}, seededTutorialCollectionEntry={seededTutorial}. "
            + "No cast, bite, catch callback or save was triggered. Use normal review input to cast.");
    }
}
#endif
