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

internal enum ReviewFixtureEnsureDecision
{
    Create,
    Confirm,
    Reject,
}

internal sealed record ReviewFixtureBuildingState(
    string? FixtureId,
    string Type,
    int X,
    int Y);

internal sealed record ReviewFixtureAnimalState(
    string Kind,
    string Type,
    bool HasExactHome,
    bool HasExactAssignment);

internal readonly record struct ReviewFixtureTile(int X, int Y);

internal sealed record ReviewFixtureAdditionalPlacementArea(
    int X,
    int Y,
    int Width,
    int Height,
    bool OnlyNeedsToBePassable);

internal sealed class ReviewFixtureBuildingPlacementArea(
    IEnumerable<ReviewFixtureTile> footprintTiles,
    IEnumerable<ReviewFixtureTile> buildableTiles,
    IEnumerable<ReviewFixtureTile> passableTiles,
    IEnumerable<ReviewFixtureTile> tiles)
{
    private readonly HashSet<ReviewFixtureTile> _footprintTiles = [.. footprintTiles];
    private readonly HashSet<ReviewFixtureTile> _buildableTiles = [.. buildableTiles];
    private readonly HashSet<ReviewFixtureTile> _passableTiles = [.. passableTiles];

    public IReadOnlyList<ReviewFixtureTile> Tiles { get; } = tiles
        .OrderBy(tile => tile.Y)
        .ThenBy(tile => tile.X)
        .ToArray();

    public IReadOnlyList<ReviewFixtureTile> SelectOccupiedTiles(
        Func<ReviewFixtureTile, bool> isOccupied)
    {
        ArgumentNullException.ThrowIfNull(isOccupied);
        return Tiles.Where(isOccupied).ToArray();
    }

    public bool IsFootprint(ReviewFixtureTile tile) => _footprintTiles.Contains(tile);

    public bool MustBeBuildable(ReviewFixtureTile tile) => _buildableTiles.Contains(tile);

    public bool MustBePassable(ReviewFixtureTile tile) => _passableTiles.Contains(tile);
}

internal sealed record ReviewFixtureWarpState(
    int X,
    int Y,
    string TargetName,
    int TargetX,
    int TargetY,
    bool NpcOnly);

internal static class ReviewFixturePolicy
{
    public static ReviewFixtureEnsureDecision DecideBuildingEnsure(
        IReadOnlyList<ReviewFixtureBuildingState> aliasMatches,
        string fixtureId,
        string buildingType,
        int x,
        int y)
    {
        if (aliasMatches.Count == 0)
        {
            return ReviewFixtureEnsureDecision.Create;
        }

        if (aliasMatches.Count != 1)
        {
            return ReviewFixtureEnsureDecision.Reject;
        }

        ReviewFixtureBuildingState existing = aliasMatches[0];
        return string.Equals(existing.FixtureId, fixtureId, StringComparison.Ordinal)
            && string.Equals(
                existing.Type,
                buildingType,
                StringComparison.Ordinal)
            && existing.X == x
            && existing.Y == y
                ? ReviewFixtureEnsureDecision.Confirm
                : ReviewFixtureEnsureDecision.Reject;
    }

    public static ReviewFixtureEnsureDecision DecideObjectEnsure(
        IReadOnlyList<string> ownedQualifiedItemIds,
        string qualifiedItemId)
    {
        if (ownedQualifiedItemIds.Count == 0)
        {
            return ReviewFixtureEnsureDecision.Create;
        }

        return ownedQualifiedItemIds.Count == 1
            && string.Equals(
                ownedQualifiedItemIds[0],
                qualifiedItemId,
                StringComparison.Ordinal)
            ? ReviewFixtureEnsureDecision.Confirm
            : ReviewFixtureEnsureDecision.Reject;
    }

    public static ReviewFixtureEnsureDecision DecideAnimalEnsure(
        IReadOnlyList<ReviewFixtureAnimalState> ownedForBuilding,
        string animalKind,
        string animalType,
        int assignedAnimalCount,
        int animalCapacity)
    {
        if (ownedForBuilding.Count == 0)
        {
            return assignedAnimalCount < animalCapacity
                ? ReviewFixtureEnsureDecision.Create
                : ReviewFixtureEnsureDecision.Reject;
        }

        if (ownedForBuilding.Count != 1)
        {
            return ReviewFixtureEnsureDecision.Reject;
        }

        ReviewFixtureAnimalState existing = ownedForBuilding[0];
        return string.Equals(existing.Kind, animalKind, StringComparison.Ordinal)
            && string.Equals(
                existing.Type,
                animalType,
                StringComparison.Ordinal)
            && existing.HasExactHome
            && existing.HasExactAssignment
                ? ReviewFixtureEnsureDecision.Confirm
                : ReviewFixtureEnsureDecision.Reject;
    }

    public static bool IsAnimalHouseCompatible(
        string? animalHouse,
        IReadOnlyList<string>? validOccupantTypes) =>
        !string.IsNullOrWhiteSpace(animalHouse)
        && validOccupantTypes?.Contains(animalHouse, StringComparer.Ordinal) == true;

    public static bool IsOwnedObject(
        string? fixtureMarker,
        string? objectMarker,
        string fixtureId,
        string buildingId) =>
        string.Equals(fixtureMarker, fixtureId, StringComparison.Ordinal)
        && string.Equals(objectMarker, buildingId, StringComparison.Ordinal);

    public static bool TryCreateBuildingPlacementArea(
        int x,
        int y,
        int width,
        int height,
        IReadOnlyList<ReviewFixtureAdditionalPlacementArea>? additionalAreas,
        ReviewFixtureTile? humanDoor,
        int mapWidth,
        int mapHeight,
        out ReviewFixtureBuildingPlacementArea? placementArea,
        out string error)
    {
        placementArea = null;
        var footprintTiles = new HashSet<ReviewFixtureTile>();
        var buildableTiles = new HashSet<ReviewFixtureTile>();
        var passableTiles = new HashSet<ReviewFixtureTile>();
        var allTiles = new HashSet<ReviewFixtureTile>();

        if (width <= 0
            || height <= 0
            || mapWidth <= 0
            || mapHeight <= 0
            || !TryAddArea(
                x,
                y,
                width,
                height,
                mapWidth,
                mapHeight,
                footprintTiles,
                out error))
        {
            error = "Stardew's exact building footprint is invalid or outside the Farm map.";
            return false;
        }

        buildableTiles.UnionWith(footprintTiles);
        allTiles.UnionWith(footprintTiles);
        foreach (ReviewFixtureAdditionalPlacementArea additional in additionalAreas ?? [])
        {
            var areaTiles = new HashSet<ReviewFixtureTile>();
            if (!TryAddArea(
                    (long)x + additional.X,
                    (long)y + additional.Y,
                    additional.Width,
                    additional.Height,
                    mapWidth,
                    mapHeight,
                    areaTiles,
                    out error))
            {
                error = "Stardew's additional building placement area is invalid or outside the Farm map.";
                return false;
            }

            allTiles.UnionWith(areaTiles);
            (additional.OnlyNeedsToBePassable ? passableTiles : buildableTiles)
                .UnionWith(areaTiles);
        }

        if (humanDoor is ReviewFixtureTile door)
        {
            var accessTiles = new HashSet<ReviewFixtureTile>();
            if (!TryAddArea(
                    (long)x + door.X,
                    (long)y + door.Y + 1,
                    1,
                    1,
                    mapWidth,
                    mapHeight,
                    accessTiles,
                    out error))
            {
                error = "Stardew's human-door access tile is outside the Farm map.";
                return false;
            }

            allTiles.UnionWith(accessTiles);
            passableTiles.UnionWith(accessTiles);
        }

        placementArea = new ReviewFixtureBuildingPlacementArea(
            footprintTiles,
            buildableTiles,
            passableTiles,
            allTiles);
        error = string.Empty;
        return true;
    }

    private static bool TryAddArea(
        long x,
        long y,
        int width,
        int height,
        int mapWidth,
        int mapHeight,
        HashSet<ReviewFixtureTile> target,
        out string error)
    {
        long right = x + width;
        long bottom = y + height;
        if (width < 0
            || height < 0
            || x < 0
            || y < 0
            || right > mapWidth
            || bottom > mapHeight)
        {
            error = "The area is invalid or outside the map.";
            return false;
        }

        for (long tileY = y; tileY < bottom; tileY++)
        {
            for (long tileX = x; tileX < right; tileX++)
            {
                target.Add(new ReviewFixtureTile((int)tileX, (int)tileY));
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool IsBuildableMapTile(
        string? buildableProperty,
        bool hasDiggableProperty) =>
        string.Equals(buildableProperty, "t", StringComparison.OrdinalIgnoreCase)
        || string.Equals(buildableProperty, "true", StringComparison.OrdinalIgnoreCase)
        || (hasDiggableProperty
            && !string.Equals(buildableProperty, "f", StringComparison.OrdinalIgnoreCase));

    public static bool TrySelectNaturalFarmWarp(
        IReadOnlyList<ReviewFixtureWarpState>? warps,
        out ReviewFixtureWarpState? selected)
    {
        ReviewFixtureWarpState[] matches = warps?
            .Where(warp =>
                !warp.NpcOnly
                && string.Equals(warp.TargetName, "Farm", StringComparison.Ordinal))
            .Take(2)
            .ToArray()
            ?? [];
        selected = matches.Length == 1 ? matches[0] : null;
        return selected is not null;
    }
}
