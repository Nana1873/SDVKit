using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.GameData.FarmAnimals;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
#endif

namespace SdvKit.AlwaysOn;

#if SDVKIT_GAME_AVAILABLE
internal sealed partial class StardewReviewFixtureRuntime
{
    public ReviewFixtureResult EnsureObject(
        ReviewFixtureAccess access,
        string building,
        string qualifiedItemId)
    {
        string fixtureId = RequiredFixtureId(access);
        if (!TryResolveOwnedBuilding(building, fixtureId, out Building target, out GameLocation indoors, out string error))
        {
            return Failure(error);
        }

        KeyValuePair<Vector2, StardewValley.Object>[] owned = indoors.Objects.Pairs
            .Where(pair => IsOwnedObject(pair.Value, fixtureId, target.id.Value))
            .ToArray();
        ReviewFixtureEnsureDecision decision = ReviewFixturePolicy.DecideObjectEnsure(
            owned.Select(pair => pair.Value.QualifiedItemId).ToArray(),
            qualifiedItemId);
        if (decision == ReviewFixtureEnsureDecision.Reject)
        {
            return Failure(
                $"Fixture building {target.id.Value:D} has an owned object conflict or different item.");
        }

        if (decision == ReviewFixtureEnsureDecision.Confirm)
        {
            return Success(
                $"Fixture object '{qualifiedItemId}' already exists in {target.id.Value:D} at "
                + $"{owned[0].Key.X},{owned[0].Key.Y}.");
        }

        if (!ItemRegistry.IsQualifiedItemId(qualifiedItemId)
            || !ItemRegistry.Exists(qualifiedItemId))
        {
            return Failure(
                $"Stardew has no exact qualified item ID '{qualifiedItemId}'.");
        }

        StardewValley.Object item;
        try
        {
            item = ItemRegistry.Create<StardewValley.Object>(qualifiedItemId, 1, 0, false);
        }
        catch (Exception exception)
        {
            return Failure(
                $"Stardew rejected qualified object ID '{qualifiedItemId}': {exception.GetBaseException().Message}");
        }

        Vector2 tile = FindValidObjectTile(indoors);
        item.modData[ReviewFixtureContract.FixtureIdMarkerKey] = fixtureId;
        item.modData[ReviewFixtureContract.ObjectMarkerKey] = target.id.Value.ToString("D");
        if (!indoors.tryPlaceObject(tile, item))
        {
            return Failure(
                $"Stardew rejected the valid free object tile {tile.X},{tile.Y} in {target.id.Value:D}.");
        }

        return Success(
            $"Created owned fixture object '{qualifiedItemId}' in {target.id.Value:D} at {tile.X},{tile.Y}.");
    }

    public ReviewFixtureResult ClearOwnedObjects(
        ReviewFixtureAccess access,
        string building)
    {
        string fixtureId = RequiredFixtureId(access);
        if (!TryResolveOwnedBuilding(building, fixtureId, out Building target, out GameLocation indoors, out string error))
        {
            return Failure(error);
        }

        Vector2[] ownedTiles = indoors.Objects.Pairs
            .Where(pair => IsOwnedObject(pair.Value, fixtureId, target.id.Value))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (Vector2 tile in ownedTiles)
        {
            indoors.Objects.Remove(tile);
        }

        return Success(
            $"Removed {ownedTiles.Length} owned fixture object(s) from {target.id.Value:D}; other interior objects were untouched.");
    }

    private static bool IsOwnedObject(
        StardewValley.Object item,
        string fixtureId,
        Guid buildingId) =>
        ReviewFixturePolicy.IsOwnedObject(
            item.modData.TryGetValue(
                ReviewFixtureContract.FixtureIdMarkerKey,
                out string observedFixtureId)
                ? observedFixtureId
                : null,
            item.modData.TryGetValue(
                ReviewFixtureContract.ObjectMarkerKey,
                out string observedBuildingId)
                ? observedBuildingId
                : null,
            fixtureId,
            buildingId.ToString("D"));

    private static Vector2 FindValidObjectTile(GameLocation indoors)
    {
        xTile.Layers.Layer? back = indoors.Map.GetLayer("Back");
        if (back is null)
        {
            throw new InvalidOperationException("The fixture interior has no Back layer.");
        }

        Warp naturalExit = indoors.GetFirstPlayerWarp();
        var warpSource = naturalExit is null
            ? new Vector2(-1, -1)
            : new Vector2(naturalExit.X, naturalExit.Y);
        var naturalEntry = naturalExit is null
            ? new Vector2(-1, -1)
            : new Vector2(naturalExit.X, naturalExit.Y - 1);

        for (var y = 1; y < back.LayerHeight - 1; y++)
        {
            for (var x = 1; x < back.LayerWidth - 1; x++)
            {
                var tile = new Vector2(x, y);
                if (!indoors.Objects.ContainsKey(tile)
                    && tile != warpSource
                    && tile != naturalEntry
                    && indoors.isTileOnMap(tile)
                    && indoors.isTilePassable(tile)
                    && indoors.isTileLocationOpen(tile)
                    && indoors.isTilePlaceable(tile, itemIsPassable: false))
                {
                    return tile;
                }
            }
        }

        throw new InvalidOperationException(
            "No valid free object tile exists in the fixture interior.");
    }
}

#endif
