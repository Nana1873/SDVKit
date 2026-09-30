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
{    private sealed record NavigationPlan(
        GameLocation Location,
        int TileX,
        int TileY,
        string Message);

    public ReviewFixtureResult Enter(
        ReviewFixtureAccess access,
        string building)
    {
        ReviewFixtureResult? immediate = PrepareEnter(
            access,
            building,
            out NavigationPlan? plan);
        if (immediate is not null)
        {
            return immediate;
        }

        Game1.warpFarmer(
            plan!.Location.NameOrUniqueName,
            plan.TileX,
            plan.TileY,
            false);
        return SuccessNavigation(
            plan.Message,
            plan.Location.NameOrUniqueName,
            plan.TileX,
            plan.TileY,
            changed: true);
    }

    public ReviewFixtureResult Farm(ReviewFixtureAccess access)
    {
        ReviewFixtureResult? immediate = PrepareFarm(
            access,
            out NavigationPlan? plan);
        if (immediate is not null)
        {
            return immediate;
        }

        Game1.warpFarmer(
            plan!.Location.NameOrUniqueName,
            plan.TileX,
            plan.TileY,
            false);
        return SuccessNavigation(
            plan.Message,
            plan.Location.NameOrUniqueName,
            plan.TileX,
            plan.TileY,
            changed: true);
    }

    public void BeginNavigation(
        ReviewFixtureAccess access,
        ReviewFixtureRequest request,
        Action<ReviewFixtureResult> completed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(completed);

        ReviewFixtureResult? immediate;
        NavigationPlan? plan;
        if (request is ReviewFixtureEnterRequest enter)
        {
            immediate = PrepareEnter(access, enter.Building, out plan);
        }
        else if (request is ReviewFixtureFarmRequest)
        {
            immediate = PrepareFarm(access, out plan);
        }
        else
        {
            completed(Failure(ReviewFixtureArguments.Usage));
            return;
        }

        if (immediate is not null)
        {
            completed(immediate);
            return;
        }

        BeginWarp(plan!, completed);
    }

    private static ReviewFixtureResult? PrepareEnter(
        ReviewFixtureAccess access,
        string building,
        out NavigationPlan? plan)
    {
        plan = null;
        string fixtureId = RequiredFixtureId(access);
        if (!TryResolveEnterBuilding(
                building,
                fixtureId,
                out Building target,
                out GameLocation indoors,
                out bool isGreenhouse,
                out string error))
        {
            return Failure(error);
        }

        string targetDescription = isGreenhouse
            ? $"Greenhouse {target.id.Value:D}"
            : $"fixture building {target.id.Value:D}";
        if (ReferenceEquals(Game1.currentLocation, indoors))
        {
            return SuccessNavigation(
                $"Player is already inside {targetDescription}.",
                indoors.NameOrUniqueName,
                Game1.player.TilePoint.X,
                Game1.player.TilePoint.Y,
                changed: false);
        }

        if (!ReferenceEquals(Game1.currentLocation, Game1.getFarm()))
        {
            return Failure("Enter is allowed only from the Farm or the requested fixture interior.");
        }

        if (!TryGetNaturalExit(indoors, out Warp exit, out Vector2 entry, out error))
        {
            return Failure(error);
        }

        plan = new NavigationPlan(
            indoors,
            (int)entry.X,
            (int)entry.Y,
            $"Warped through the natural entry of {targetDescription} at {entry.X},{entry.Y}.");
        return null;
    }

    private static ReviewFixtureResult? PrepareFarm(
        ReviewFixtureAccess access,
        out NavigationPlan? plan)
    {
        plan = null;
        string fixtureId = RequiredFixtureId(access);
        Farm farm = Game1.getFarm();
        if (ReferenceEquals(Game1.currentLocation, farm))
        {
            return SuccessNavigation(
                "Player is already on the Farm.",
                farm.NameOrUniqueName,
                Game1.player.TilePoint.X,
                Game1.player.TilePoint.Y,
                changed: false);
        }

        GameLocation? current = Game1.currentLocation;
        if (current is null)
        {
            return Failure("The current review location is unavailable.");
        }

        Building? parent = GetOwnedBuildings(farm, fixtureId).SingleOrDefault(building =>
            ReferenceEquals(building.GetIndoors(), current));
        Building? greenhouse = null;
        bool isGreenhouse = TryResolveGreenhouse(
                farm,
                out Building resolvedGreenhouse,
                out GameLocation greenhouseIndoors,
                out _)
            && ReferenceEquals(greenhouseIndoors, current);
        if (isGreenhouse)
        {
            greenhouse = resolvedGreenhouse;
        }

        if (parent is null
            && greenhouse is null
            && !IsExactReviewFarmHouse(farm, current))
        {
            return Failure(
                "Farm is allowed only from the Farm, a review FarmHouse, the exact Greenhouse, or an owned fixture interior.");
        }

        if (!TryGetNaturalExit(current, out Warp exit, out _, out string error))
        {
            return Failure(error);
        }

        var target = new Vector2(exit.TargetX, exit.TargetY);
        if (!farm.isTileOnMap(target) || !farm.isTilePassable(target))
        {
            return Failure("The fixture interior's natural Farm warp target is not passable.");
        }

        string sourceDescription = parent is not null
            ? $"fixture building {parent.id.Value:D}"
            : greenhouse is not null
                ? $"Greenhouse {greenhouse.id.Value:D}"
                : $"review FarmHouse '{current.NameOrUniqueName}'";
        plan = new NavigationPlan(
            farm,
            exit.TargetX,
            exit.TargetY,
            $"Warped through the natural Farm exit of {sourceDescription} at {target.X},{target.Y}.");
        return null;
    }

    private static void BeginWarp(
        NavigationPlan plan,
        Action<ReviewFixtureResult> completed)
    {
        string expectedLocationId = plan.Location.NameOrUniqueName;
        bool expectedIsStructure = plan.Location.isStructure.Value;
        var locationRequest = new LocationRequest(
            expectedLocationId,
            expectedIsStructure,
            plan.Location);
        locationRequest.OnWarp += () =>
        {
            GameLocation? actualLocation = Game1.currentLocation;
            GameLocation? requestedLocation = locationRequest.Location;
            if (actualLocation is null
                || requestedLocation is null
                || !ReferenceEquals(actualLocation, requestedLocation)
                || !string.Equals(
                    actualLocation.NameOrUniqueName,
                    expectedLocationId,
                    StringComparison.Ordinal)
                || actualLocation.isStructure.Value != expectedIsStructure)
            {
                completed(Failure(
                    "Stardew completed the fixture warp without reaching the exact requested location."));
                return;
            }

            completed(SuccessNavigation(
                plan.Message,
                actualLocation.NameOrUniqueName,
                Game1.player.TilePoint.X,
                Game1.player.TilePoint.Y,
                changed: true));
        };
        Game1.warpFarmer(
            locationRequest,
            plan.TileX,
            plan.TileY,
            Game1.player.FacingDirection);
    }

    private static ReviewFixtureResult SuccessNavigation(
        string message,
        string locationId,
        int tileX,
        int tileY,
        bool changed) =>
        new(
            true,
            message,
            Navigation: new ReviewFixtureNavigationReport(
                locationId,
                tileX,
                tileY,
                changed));

    private static bool TryResolveEnterBuilding(
        string token,
        string fixtureId,
        out Building building,
        out GameLocation indoors,
        out bool isGreenhouse,
        out string error)
    {
        isGreenhouse = ReviewFixtureArguments.IsGreenhouseNavigationTarget(token);
        if (isGreenhouse)
        {
            return TryResolveGreenhouse(Game1.getFarm(), out building, out indoors, out error);
        }

        return TryResolveOwnedBuilding(token, fixtureId, out building, out indoors, out error);
    }

    private static bool TryResolveGreenhouse(
        Farm farm,
        out Building building,
        out GameLocation indoors,
        out string error)
    {
        GameLocation? canonical = Game1.getLocationFromName(
            ReviewFixtureContract.GreenhouseBuildingType);
        Building[] matches = farm.buildings
            .Where(candidate =>
                candidate is GreenhouseBuilding
                && string.Equals(
                    candidate.buildingType.Value,
                    ReviewFixtureContract.GreenhouseBuildingType,
                    StringComparison.Ordinal)
                && ReferenceEquals(candidate.GetIndoors(), canonical))
            .ToArray();
        if (matches.Length != 1 || canonical is null)
        {
            building = null!;
            indoors = null!;
            error = matches.Length > 1
                ? "The exact review fixture has multiple canonical Greenhouse buildings."
                : "The exact review fixture has no canonical loaded Greenhouse building.";
            return false;
        }

        building = matches[0];
        indoors = canonical;
        error = string.Empty;
        return true;
    }

    private static bool IsExactReviewFarmHouse(Farm farm, GameLocation current)
    {
        if (current is not FarmHouse)
        {
            return false;
        }

        if (ReferenceEquals(Game1.getLocationFromName(current.NameOrUniqueName), current))
        {
            return true;
        }

        return farm.buildings.Count(building =>
            ReferenceEquals(building.GetIndoors(), current)) == 1;
    }

    private static bool TryGetNaturalExit(
        GameLocation indoors,
        out Warp exit,
        out Vector2 entry,
        out string error)
    {
        ReviewFixtureWarpState[] warps = indoors.warps
            .Select(warp => new ReviewFixtureWarpState(
                warp.X,
                warp.Y,
                warp.TargetName,
                warp.TargetX,
                warp.TargetY,
                warp.npcOnly.Value))
            .ToArray();
        if (!ReviewFixturePolicy.TrySelectNaturalFarmWarp(
                warps,
                out ReviewFixtureWarpState? selected)
            || selected is null)
        {
            exit = null!;
            entry = Vector2.Zero;
            error = "The review interior does not have exactly one natural player warp to the Farm.";
            return false;
        }

        ReviewFixtureWarpState selectedWarp = selected;
        exit = indoors.warps.Single(warp =>
            warp.X == selectedWarp.X
            && warp.Y == selectedWarp.Y
            && string.Equals(warp.TargetName, selectedWarp.TargetName, StringComparison.Ordinal)
            && warp.TargetX == selectedWarp.TargetX
            && warp.TargetY == selectedWarp.TargetY
            && warp.npcOnly.Value == selectedWarp.NpcOnly);
        entry = new Vector2(selectedWarp.X, selectedWarp.Y - 1);
        if (!indoors.isTileOnMap(entry)
            || !indoors.isTilePassable(entry))
        {
            error = "The review interior's natural Farm warp has no usable entry tile.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

#endif
