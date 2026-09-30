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
    private sealed record BuildingPlacementPreparation(
        IReadOnlyList<ReviewFixtureTile> ObjectTiles,
        IReadOnlyList<ReviewFixtureTile> TerrainFeatureTiles,
        IReadOnlyList<ResourceClump> ResourceClumps,
        IReadOnlyList<Furniture> Furniture);

    private readonly record struct BuildingPreparationCounts(
        int Objects,
        int TerrainFeatures,
        int ResourceClumps,
        int Furniture)
    {
        public override string ToString() =>
            $"objects={Objects} terrainFeatures={TerrainFeatures} "
            + $"resourceClumps={ResourceClumps} furniture={Furniture}";
    }

    public ReviewFixtureResult EnsureBuilding(
        ReviewFixtureAccess access,
        string alias,
        string kind,
        int x,
        int y)
    {
        Farm farm = Game1.getFarm();
        string fixtureId = RequiredFixtureId(access);
        if (!ReviewFixtureKindResolver.TryResolve(
                kind,
                Game1.buildingData.Keys,
                "building",
                out ReviewFixtureKindResolution? resolved,
                out string resolutionError)
            || resolved is null)
        {
            return Failure(resolutionError);
        }

        if (!Game1.buildingData.TryGetValue(
                resolved.CanonicalId,
                out BuildingData? buildingData))
        {
            return Failure(
                $"Stardew's loaded building data changed while resolving '{kind}'.");
        }

        Building[] aliases = farm.buildings
            .Where(building => building.modData.TryGetValue(
                ReviewFixtureContract.BuildingAliasMarkerKey,
                out string observedAlias)
                && string.Equals(observedAlias, alias, StringComparison.Ordinal))
            .ToArray();
        ReviewFixtureEnsureDecision decision = ReviewFixturePolicy.DecideBuildingEnsure(
            aliases.Select(existing => new ReviewFixtureBuildingState(
                existing.modData.TryGetValue(
                    ReviewFixtureContract.FixtureIdMarkerKey,
                    out string observedFixtureId)
                    ? observedFixtureId
                    : null,
                existing.buildingType.Value,
                existing.tileX.Value,
                existing.tileY.Value)).ToArray(),
            fixtureId,
            resolved.CanonicalId,
            x,
            y);
        if (decision == ReviewFixtureEnsureDecision.Reject)
        {
            return Failure(
                $"Fixture alias '{alias}' already identifies a different, ambiguous, or non-owned building.");
        }

        if (decision == ReviewFixtureEnsureDecision.Confirm)
        {
            Building existing = aliases[0];
            bool finishedConstruction = existing.isUnderConstruction(ignoreUpgrades: false);
            if (finishedConstruction)
            {
                existing.FinishConstruction(onGameStart: false);
            }

            return SuccessBuilding(
                $"Fixture building '{alias}' "
                + (finishedConstruction ? "was finished" : "already exists")
                + $" as {existing.id.Value:D} "
                + $"type='{resolved.CanonicalId}' token={resolved.CanonicalToken} at {x},{y}.",
                existing,
                fixtureId,
                resolved.CanonicalId,
                resolved.CanonicalToken,
                changed: finishedConstruction);
        }

        if (!ReferenceEquals(Game1.currentLocation, farm))
        {
            return Failure(
                "A new fixture building can be prepared only while the main player is on the Farm. "
                + "Run 'sdvkit fixture farm' first; no placement content was changed.");
        }

        if (!GameStateQuery.CheckConditions(
                buildingData.BuildCondition,
                farm,
                Game1.player))
        {
            return Failure(
                $"Canonical building kind '{resolved.CanonicalId}' isn't currently buildable in this review world.");
        }

        try
        {
            Building candidate = Building.CreateInstanceFromId(
                resolved.CanonicalId,
                new Vector2(x, y));
            if (candidate is null
                || !string.Equals(
                    candidate.buildingType.Value,
                    resolved.CanonicalId,
                    StringComparison.Ordinal))
            {
                return Failure(
                    $"Stardew can't instantiate canonical building kind '{resolved.CanonicalId}'.");
            }
        }
        catch (Exception exception)
        {
            return Failure(
                $"Stardew can't instantiate canonical building kind '{resolved.CanonicalId}': "
                + exception.GetBaseException().Message);
        }

        if (!TryPlanBuildingPlacement(
                farm,
                resolved.CanonicalId,
                buildingData,
                x,
                y,
                out BuildingPlacementPreparation? preparation,
                out string footprintError))
        {
            return Failure(footprintError);
        }

        if (!TryApplyBuildingPlacementPreparation(
                farm,
                preparation!,
                out BuildingPreparationCounts removed,
                out string preparationError))
        {
            return Failure(
                $"Prepared '{resolved.CanonicalId}' placement at {x},{y}: removed {removed}. "
                + $"{preparationError} Reset the disposable fixture before retrying.");
        }

        Building? constructed = null;
        try
        {
            if (!farm.buildStructure(
                    resolved.CanonicalId,
                    buildingData,
                    new Vector2(x, y),
                    Game1.player,
                    out Building placed,
                    magicalConstruction: false,
                    skipSafetyChecks: false))
            {
                constructed = placed;
                bool rollbackConfirmed = TryRollbackFailedBuilding(farm, constructed);
                return Failure(
                    $"Prepared '{resolved.CanonicalId}' placement at {x},{y}: removed {removed}. "
                    + $"Stardew rejected the placement for '{alias}'. "
                    + (rollbackConfirmed
                        ? "No partial building remains. "
                        : "The exact partial building couldn't be removed. ")
                    + "Reset the disposable fixture before retrying.");
            }

            constructed = placed;
            constructed.modData[ReviewFixtureContract.FixtureIdMarkerKey] = fixtureId;
            constructed.modData[ReviewFixtureContract.BuildingAliasMarkerKey] = alias;
            constructed.FinishConstruction(onGameStart: false);
            return SuccessBuilding(
                $"Created finished fixture building '{alias}' as {constructed.id.Value:D} "
                + $"type='{resolved.CanonicalId}' token={resolved.CanonicalToken} at {x},{y}; "
                + $"removed {removed} from the exact placement area.",
                constructed,
                fixtureId,
                resolved.CanonicalId,
                resolved.CanonicalToken,
                changed: true);
        }
        catch (Exception exception)
        {
            bool rollbackConfirmed = TryRollbackFailedBuilding(farm, constructed);
            return Failure(
                $"Prepared '{resolved.CanonicalId}' placement at {x},{y}: removed {removed}. "
                + $"Stardew failed while creating '{alias}': {exception.GetBaseException().Message}. "
                + (rollbackConfirmed
                    ? "No partial building remains. "
                    : "The exact partial building couldn't be removed. ")
                + "Reset the disposable fixture before retrying.");
        }
    }

    private static ReviewFixtureResult SuccessBuilding(
        string message,
        Building building,
        string fixtureId,
        string canonicalKind,
        string canonicalToken,
        bool changed) =>
        new(
            true,
            message,
            Building: DescribeBuilding(
                building,
                fixtureId,
                canonicalKind,
                canonicalToken,
                changed));

    private static bool TryPlanBuildingPlacement(
        Farm farm,
        string buildingType,
        BuildingData buildingData,
        int x,
        int y,
        out BuildingPlacementPreparation? preparation,
        out string error)
    {
        preparation = null;
        if (buildingData.Size.X <= 0
            || buildingData.Size.Y <= 0)
        {
            error = $"Stardew's exact '{buildingType}' footprint is unavailable.";
            return false;
        }

        xTile.Layers.Layer? back = farm.Map.GetLayer("Back");
        if (back is null)
        {
            error = $"The Farm map has no Back layer for '{buildingType}' placement.";
            return false;
        }

        ReviewFixtureAdditionalPlacementArea[] additionalAreas = (buildingData
                .AdditionalPlacementTiles ?? [])
            .Select(placement => new ReviewFixtureAdditionalPlacementArea(
                placement.TileArea.X,
                placement.TileArea.Y,
                placement.TileArea.Width,
                placement.TileArea.Height,
                placement.OnlyNeedsToBePassable))
            .ToArray();
        ReviewFixtureTile? humanDoor = buildingData.HumanDoor == new Point(-1, -1)
            ? null
            : new ReviewFixtureTile(buildingData.HumanDoor.X, buildingData.HumanDoor.Y);
        if (!ReviewFixturePolicy.TryCreateBuildingPlacementArea(
                x,
                y,
                buildingData.Size.X,
                buildingData.Size.Y,
                additionalAreas,
                humanDoor,
                back.LayerWidth,
                back.LayerHeight,
                out ReviewFixtureBuildingPlacementArea? placementArea,
                out string placementAreaError))
        {
            error = $"The '{buildingType}' placement at {x},{y} is invalid. {placementAreaError}";
            return false;
        }

        // Farm.isBuildable also evaluates current occupancy, which would reject
        // the dynamic contents which this explicit disposable-work-copy operation
        // prepares. Validate the occupancy-neutral map rules here, then all
        // structural blockers, before removing any content.
        Rectangle buildableArea = farm.GetBuildableRectangle();
        foreach (ReviewFixtureTile tile in placementArea!.Tiles)
        {
            var vector = new Vector2(tile.X, tile.Y);
            string buildableProperty = farm.doesTileHavePropertyNoNull(
                tile.X,
                tile.Y,
                "Buildable",
                "Back");
            if (farm.isWaterTile(tile.X, tile.Y)
                || string.Equals(buildableProperty, "f", StringComparison.OrdinalIgnoreCase)
                || (placementArea.MustBeBuildable(tile)
                && ((buildableArea != Rectangle.Empty
                        && !buildableArea.Contains(new Point(tile.X, tile.Y)))
                    || !farm.isTilePlaceable(vector, itemIsPassable: false)
                    || !ReviewFixturePolicy.IsBuildableMapTile(
                        buildableProperty,
                        farm.doesTileHaveProperty(
                            tile.X,
                            tile.Y,
                            "Diggable",
                            "Back",
                            ignoreTileSheetProperties: false) is not null))))
            {
                error = $"The '{buildingType}' footprint tile {tile.X},{tile.Y} is water, NoBuild, NoFurniture, or otherwise not buildable.";
                return false;
            }

            if (!farm.isTilePassable(vector))
            {
                error = $"The '{buildingType}' placement tile {tile.X},{tile.Y} is blocked by the Farm map collision layer.";
                return false;
            }
        }

        Building? overlap = farm.buildings.FirstOrDefault(existing =>
            placementArea.Tiles.Any(tile =>
                existing.occupiesTile(tile.X, tile.Y, applyTilePropertyRadius: false)));
        if (overlap is not null)
        {
            error = $"The '{buildingType}' footprint at {x},{y} overlaps existing building {overlap.id.Value:D}.";
            return false;
        }

        foreach (ReviewFixtureTile tile in placementArea.Tiles)
        {
            Rectangle tileBounds = GetTileBounds(tile);
            if (farm.farmers.Any(farmer => farmer.GetBoundingBox().Intersects(tileBounds))
                || farm.characters.Any(character => character.GetBoundingBox().Intersects(tileBounds))
                || farm.animals.Values.Any(animal => animal.GetBoundingBox().Intersects(tileBounds)))
            {
                error = $"The '{buildingType}' placement at {x},{y} is occupied by a player, character, or animal at {tile.X},{tile.Y}.";
                return false;
            }

            if (farm.largeTerrainFeatures.Any(feature =>
                feature.getBoundingBox().Intersects(tileBounds)))
            {
                error = $"The '{buildingType}' placement at {x},{y} overlaps a large terrain feature at {tile.X},{tile.Y}.";
                return false;
            }
        }

        IReadOnlyList<ReviewFixtureTile> objectTiles = placementArea.SelectOccupiedTiles(tile =>
            farm.Objects.ContainsKey(new Vector2(tile.X, tile.Y)));
        IReadOnlyList<ReviewFixtureTile> terrainFeatureTiles = placementArea.SelectOccupiedTiles(tile =>
            farm.terrainFeatures.ContainsKey(new Vector2(tile.X, tile.Y)));
        ResourceClump[] resourceClumps = farm.resourceClumps
            .Where(clump => placementArea.Tiles.Any(tile =>
                clump.occupiesTile(tile.X, tile.Y)))
            .OrderBy(clump => clump.Tile.Y)
            .ThenBy(clump => clump.Tile.X)
            .ToArray();
        Furniture[] selectedFurniture = farm.furniture
            .Where(item => placementArea.Tiles.Any(tile =>
                item.GetBoundingBox().Intersects(GetTileBounds(tile))))
            .OrderBy(item => item.TileLocation.Y)
            .ThenBy(item => item.TileLocation.X)
            .ToArray();
        HashSet<ReviewFixtureTile> plannedObjectTiles = [.. objectTiles];
        if (!TryOrderFurniturePreparation(
                farm,
                selectedFurniture,
                plannedObjectTiles,
                out Furniture[] orderedFurniture,
                out Furniture? unsafeFurniture))
        {
            error = $"The '{buildingType}' placement at {x},{y} overlaps furniture at "
                + $"{unsafeFurniture!.TileLocation.X},{unsafeFurniture.TileLocation.Y} "
                + "which Stardew cannot safely and synchronously remove.";
            return false;
        }

        preparation = new BuildingPlacementPreparation(
            objectTiles,
            terrainFeatureTiles,
            resourceClumps,
            orderedFurniture);
        error = string.Empty;
        return true;
    }

    private static bool TryRollbackFailedBuilding(Farm farm, Building? building)
    {
        if (building is null || !farm.buildings.Contains(building))
        {
            return true;
        }

        try
        {
            return farm.destroyStructure(building)
                && !farm.buildings.Contains(building);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryApplyBuildingPlacementPreparation(
        Farm farm,
        BuildingPlacementPreparation preparation,
        out BuildingPreparationCounts removed,
        out string error)
    {
        var objectCount = 0;
        var terrainFeatureCount = 0;
        var resourceClumpCount = 0;
        var furnitureCount = 0;
        try
        {
            foreach (ReviewFixtureTile tile in preparation.ObjectTiles)
            {
                if (!farm.Objects.Remove(new Vector2(tile.X, tile.Y)))
                {
                    return Failed(
                        $"Farm object removal drifted at {tile.X},{tile.Y}.",
                        out removed,
                        out error);
                }

                objectCount++;
            }

            foreach (ReviewFixtureTile tile in preparation.TerrainFeatureTiles)
            {
                if (!farm.terrainFeatures.Remove(new Vector2(tile.X, tile.Y)))
                {
                    return Failed(
                        $"Terrain-feature removal drifted at {tile.X},{tile.Y}.",
                        out removed,
                        out error);
                }

                terrainFeatureCount++;
            }

            foreach (ResourceClump clump in preparation.ResourceClumps)
            {
                if (!farm.resourceClumps.Contains(clump))
                {
                    return Failed(
                        $"Resource-clump removal drifted at {clump.Tile.X},{clump.Tile.Y}.",
                        out removed,
                        out error);
                }

                farm.resourceClumps.Remove(clump);
                if (farm.resourceClumps.Contains(clump))
                {
                    return Failed(
                        $"Stardew did not remove the resource clump at {clump.Tile.X},{clump.Tile.Y}.",
                        out removed,
                        out error);
                }

                resourceClumpCount++;
            }

            foreach (Furniture item in preparation.Furniture)
            {
                if (!farm.furniture.Contains(item)
                    || !item.canBeRemoved(Game1.player)
                    || !HasSynchronousFurnitureRemoval(item))
                {
                    return Failed(
                        $"Furniture removal drifted at {item.TileLocation.X},{item.TileLocation.Y}.",
                        out removed,
                        out error);
                }

                Furniture? removedFurniture = null;
                item.AttemptRemoval(candidate =>
                {
                    removedFurniture = candidate;
                    candidate.performRemoveAction();
                    farm.furniture.Remove(candidate);
                });
                if (!ReferenceEquals(removedFurniture, item)
                    || farm.furniture.Contains(item))
                {
                    return Failed(
                        $"Stardew did not remove the furniture at {item.TileLocation.X},{item.TileLocation.Y}.",
                        out removed,
                        out error);
                }

                furnitureCount++;
            }

            removed = new BuildingPreparationCounts(
                objectCount,
                terrainFeatureCount,
                resourceClumpCount,
                furnitureCount);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            return Failed(
                $"Stardew threw while preparing the placement area: {exception.GetBaseException().Message}.",
                out removed,
                out error);
        }

        bool Failed(
            string message,
            out BuildingPreparationCounts observed,
            out string failure)
        {
            observed = new BuildingPreparationCounts(
                objectCount,
                terrainFeatureCount,
                resourceClumpCount,
                furnitureCount);
            failure = message;
            return false;
        }
    }

    private static bool HasSynchronousFurnitureRemoval(Furniture item) =>
        item.GetType().GetMethod(
            nameof(Furniture.AttemptRemoval),
            [typeof(Action<Furniture>)])?.DeclaringType == typeof(Furniture);

    private static bool CanPrepareFurniture(
        Farm farm,
        Furniture item,
        HashSet<ReviewFixtureTile> plannedObjectTiles,
        IReadOnlyList<Furniture> scheduledFurniture)
    {
        if (!HasSynchronousFurnitureRemoval(item))
        {
            return false;
        }

        if (item.canBeRemoved(Game1.player))
        {
            return true;
        }

        // Base Furniture only rejects an otherwise removable passable item when
        // another object or furniture occupies its tiles. Objects already selected
        // for this exact placement area disappear before the normal removal API is
        // invoked, so account for only that scheduled state change here. Overrides
        // remain fail-closed because their additional contracts aren't predictable.
        if (item.GetType().GetMethod(
                nameof(Furniture.canBeRemoved),
                [typeof(Farmer)])?.DeclaringType != typeof(Furniture)
            || !item.AllowLocalRemoval
            || !ReferenceEquals(item.Location, farm)
            || item.HasSittingFarmers()
            || item.heldObject.Value is not null
            || !item.isPassable())
        {
            return false;
        }

        Rectangle bounds = item.GetBoundingBox();
        if (farm.furniture.Any(other =>
            !ReferenceEquals(other, item)
            && !scheduledFurniture.Any(scheduled => ReferenceEquals(scheduled, other))
            && other.GetBoundingBox().Intersects(bounds)))
        {
            return false;
        }

        for (int x = bounds.Left / Game1.tileSize; x < bounds.Right / Game1.tileSize; x++)
        {
            for (int y = bounds.Top / Game1.tileSize; y < bounds.Bottom / Game1.tileSize; y++)
            {
                var tile = new ReviewFixtureTile(x, y);
                if (farm.Objects.ContainsKey(new Vector2(x, y))
                    && !plannedObjectTiles.Contains(tile))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryOrderFurniturePreparation(
        Farm farm,
        IReadOnlyList<Furniture> selectedFurniture,
        HashSet<ReviewFixtureTile> plannedObjectTiles,
        out Furniture[] orderedFurniture,
        out Furniture? unsafeFurniture)
    {
        var remaining = new List<Furniture>(selectedFurniture);
        var scheduled = new List<Furniture>(selectedFurniture.Count);
        while (remaining.Count > 0)
        {
            Furniture? next = remaining.FirstOrDefault(item =>
                CanPrepareFurniture(farm, item, plannedObjectTiles, scheduled));
            if (next is null)
            {
                orderedFurniture = [];
                unsafeFurniture = remaining[0];
                return false;
            }

            scheduled.Add(next);
            remaining.Remove(next);
        }

        orderedFurniture = [.. scheduled];
        unsafeFurniture = null;
        return true;
    }

    private static Rectangle GetTileBounds(ReviewFixtureTile tile) => new(
        tile.X * Game1.tileSize,
        tile.Y * Game1.tileSize,
        Game1.tileSize,
        Game1.tileSize);
}

#endif
