using System.Globalization;

#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
#endif

namespace SdvKit.AlwaysOn;

internal static class ReviewFixtureContract
{
    internal const string FixtureIdMarkerKey = "SDVKit.AlwaysOn/FixtureId";
    internal const string BuildingAliasMarkerKey = "SDVKit.AlwaysOn/FixtureBuildingAlias";
    internal const string ObjectMarkerKey = "SDVKit.AlwaysOn/FixtureObject";
    internal const string AnimalKindMarkerKey = "SDVKit.AlwaysOn/FixtureAnimalKind";
    internal const string DeluxeBarnKind = "deluxe-barn";
    internal const string DeluxeBarnBuildingType = "Deluxe Barn";
    internal const string WhiteCowKind = "white-cow";
    internal const string WhiteCowType = "White Cow";
    internal const string ReviewEnvironmentName = "SDVKIT_PROJECT_REVIEW";
    internal const string ReviewEnvironmentValue = "1";
}

internal abstract record ReviewFixtureRequest(bool RequiresMainPlayer);

internal sealed record ReviewFixtureStatusRequest()
    : ReviewFixtureRequest(RequiresMainPlayer: false);

internal sealed record ReviewFixtureBuildingEnsureRequest(
    string Alias,
    int X,
    int Y)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal sealed record ReviewFixtureObjectEnsureRequest(
    string Building,
    string QualifiedItemId)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal sealed record ReviewFixtureObjectClearOwnedRequest(string Building)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal sealed record ReviewFixtureAnimalEnsureRequest(string Building)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal sealed record ReviewFixtureEnterRequest(string Building)
    : ReviewFixtureRequest(RequiresMainPlayer: false);

internal sealed record ReviewFixtureFarmRequest()
    : ReviewFixtureRequest(RequiresMainPlayer: false);

internal static class ReviewFixtureArguments
{
    internal const string Usage =
        "Usage: sdvkit fixture status | "
        + "building ensure <alias> deluxe-barn <x> <y> | "
        + "object ensure <alias-or-id> <qualified-item-id> | "
        + "object clear-owned <alias-or-id> | "
        + "animal ensure <alias-or-id> white-cow | "
        + "enter <alias-or-id> | farm";
    internal const string AliasError =
        "A fixture alias must contain 1-32 lowercase ASCII letters, digits, '-' or '_' and start with a letter.";
    internal const string BuildingError =
        "A fixture building must be identified by a valid alias or exact GUID.";

    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out ReviewFixtureRequest? request,
        out string error)
    {
        request = null;
        error = Usage;
        if (arguments is null
            || arguments.Count < 2
            || !string.Equals(arguments[0], "fixture", StringComparison.Ordinal))
        {
            return false;
        }

        if (arguments.Count == 2
            && string.Equals(arguments[1], "status", StringComparison.Ordinal))
        {
            request = new ReviewFixtureStatusRequest();
        }
        else if (arguments.Count == 7
            && string.Equals(arguments[1], "building", StringComparison.Ordinal)
            && string.Equals(arguments[2], "ensure", StringComparison.Ordinal)
            && string.Equals(arguments[4], ReviewFixtureContract.DeluxeBarnKind, StringComparison.Ordinal))
        {
            if (!IsValidAlias(arguments[3]))
            {
                error = AliasError;
                return false;
            }

            if (!TryParseCoordinate(arguments[5], out int x)
                || !TryParseCoordinate(arguments[6], out int y))
            {
                error = "Fixture building coordinates must be non-negative integers.";
                return false;
            }

            request = new ReviewFixtureBuildingEnsureRequest(arguments[3], x, y);
        }
        else if (arguments.Count == 5
            && string.Equals(arguments[1], "object", StringComparison.Ordinal)
            && string.Equals(arguments[2], "ensure", StringComparison.Ordinal))
        {
            if (!IsValidBuildingToken(arguments[3]))
            {
                error = BuildingError;
                return false;
            }

            if (!IsValidQualifiedItemId(arguments[4]))
            {
                error = "A qualified item ID must be one non-empty token such as '(O)388'.";
                return false;
            }

            request = new ReviewFixtureObjectEnsureRequest(arguments[3], arguments[4]);
        }
        else if (arguments.Count == 4
            && string.Equals(arguments[1], "object", StringComparison.Ordinal)
            && string.Equals(arguments[2], "clear-owned", StringComparison.Ordinal))
        {
            if (!IsValidBuildingToken(arguments[3]))
            {
                error = BuildingError;
                return false;
            }

            request = new ReviewFixtureObjectClearOwnedRequest(arguments[3]);
        }
        else if (arguments.Count == 5
            && string.Equals(arguments[1], "animal", StringComparison.Ordinal)
            && string.Equals(arguments[2], "ensure", StringComparison.Ordinal)
            && string.Equals(arguments[4], ReviewFixtureContract.WhiteCowKind, StringComparison.Ordinal))
        {
            if (!IsValidBuildingToken(arguments[3]))
            {
                error = BuildingError;
                return false;
            }

            request = new ReviewFixtureAnimalEnsureRequest(arguments[3]);
        }
        else if (arguments.Count == 3
            && string.Equals(arguments[1], "enter", StringComparison.Ordinal))
        {
            if (!IsValidBuildingToken(arguments[2]))
            {
                error = BuildingError;
                return false;
            }

            request = new ReviewFixtureEnterRequest(arguments[2]);
        }
        else if (arguments.Count == 2
            && string.Equals(arguments[1], "farm", StringComparison.Ordinal))
        {
            request = new ReviewFixtureFarmRequest();
        }

        return request is not null;
    }

    public static bool IsValidAlias(string? alias)
    {
        if (alias is null
            || alias.Length is < 1 or > 32
            || alias[0] is < 'a' or > 'z')
        {
            return false;
        }

        return alias.All(character =>
            (character >= 'a' && character <= 'z')
            || (character >= '0' && character <= '9')
            || character is '-' or '_');
    }

    public static bool IsValidBuildingToken(string? value) =>
        IsValidAlias(value)
        || (Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty);

    private static bool IsValidQualifiedItemId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !value.Any(char.IsWhiteSpace);

    private static bool TryParseCoordinate(string value, out int coordinate) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out coordinate);
}

internal sealed record ReviewFixtureAccess(
    bool Succeeded,
    bool CanMutate,
    string? FixtureId,
    string? Role,
    string Message);

internal sealed record ReviewFixtureResult(bool Succeeded, string Message);

internal interface IReviewFixtureRuntime
{
    ReviewFixtureAccess VerifyExactReviewFixture();

    ReviewFixtureResult Status(ReviewFixtureAccess access);

    ReviewFixtureResult EnsureBuilding(
        ReviewFixtureAccess access,
        string alias,
        int x,
        int y);

    ReviewFixtureResult EnsureObject(
        ReviewFixtureAccess access,
        string building,
        string qualifiedItemId);

    ReviewFixtureResult ClearOwnedObjects(
        ReviewFixtureAccess access,
        string building);

    ReviewFixtureResult EnsureAnimal(
        ReviewFixtureAccess access,
        string building);

    ReviewFixtureResult Enter(
        ReviewFixtureAccess access,
        string building);

    ReviewFixtureResult Farm(ReviewFixtureAccess access);
}

internal static class ReviewFixtureOperation
{
    public static ReviewFixtureResult Execute(
        ReviewFixtureRequest request,
        IReviewFixtureRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        ReviewFixtureAccess access = runtime.VerifyExactReviewFixture();
        if (!access.Succeeded)
        {
            return new ReviewFixtureResult(false, access.Message);
        }

        if (string.IsNullOrWhiteSpace(access.FixtureId)
            || string.IsNullOrWhiteSpace(access.Role))
        {
            return new ReviewFixtureResult(
                false,
                "The freshly verified review fixture returned an incomplete identity.");
        }

        if (request.RequiresMainPlayer && !access.CanMutate)
        {
            return new ReviewFixtureResult(
                false,
                "Only the freshly verified main player or network-2 host may mutate the fixture.");
        }

        return request switch
        {
            ReviewFixtureStatusRequest => runtime.Status(access),
            ReviewFixtureBuildingEnsureRequest building => runtime.EnsureBuilding(
                access,
                building.Alias,
                building.X,
                building.Y),
            ReviewFixtureObjectEnsureRequest item => runtime.EnsureObject(
                access,
                item.Building,
                item.QualifiedItemId),
            ReviewFixtureObjectClearOwnedRequest clear => runtime.ClearOwnedObjects(
                access,
                clear.Building),
            ReviewFixtureAnimalEnsureRequest animal => runtime.EnsureAnimal(
                access,
                animal.Building),
            ReviewFixtureEnterRequest enter => runtime.Enter(access, enter.Building),
            ReviewFixtureFarmRequest => runtime.Farm(access),
            _ => new ReviewFixtureResult(false, ReviewFixtureArguments.Usage),
        };
    }
}

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
    string Type,
    bool HasExactHome,
    bool HasExactAssignment);

internal sealed record ReviewFixtureObjectClutterState(
    string ItemId,
    string Name,
    string Type,
    int Stack,
    bool CanBeSetDown,
    bool CanBeGrabbed,
    bool IsSpawnedObject,
    bool IsQuestItem,
    bool IsBigCraftable,
    bool HasHeldObject,
    int Fragility,
    int Price,
    bool HasModData);

internal enum ReviewFixtureTerrainKind
{
    Grass,
    Tree,
    Other,
}

internal sealed record ReviewFixtureTerrainClutterState(
    ReviewFixtureTerrainKind Kind,
    bool IsTapped,
    bool IsStump,
    bool HasModData);

internal sealed record ReviewFixtureResourceClumpState(
    int ParentSheetIndex,
    int Width,
    int Height,
    bool HasModData);

internal static class ReviewFixturePolicy
{
    private static readonly Dictionary<string, (string Name, string Type, int Fragility)>
        DisposableObjectKinds = new Dictionary<string, (string Name, string Type, int Fragility)>(
            StringComparer.Ordinal)
        {
            ["295"] = ("Twig", "Litter", 2),
            ["343"] = ("Stone", "Litter", 0),
            ["450"] = ("Stone", "Litter", 0),
            ["590"] = ("Artifact Spot", "asdf", 0),
            ["674"] = ("Weeds", "Litter", 2),
            ["784"] = ("Weeds", "Litter", 2),
        };

    public static ReviewFixtureEnsureDecision DecideBuildingEnsure(
        IReadOnlyList<ReviewFixtureBuildingState> aliasMatches,
        string fixtureId,
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
                ReviewFixtureContract.DeluxeBarnBuildingType,
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
        return string.Equals(
                existing.Type,
                ReviewFixtureContract.WhiteCowType,
                StringComparison.Ordinal)
            && existing.HasExactHome
            && existing.HasExactAssignment
                ? ReviewFixtureEnsureDecision.Confirm
                : ReviewFixtureEnsureDecision.Reject;
    }

    public static bool IsOwnedObject(
        string? fixtureMarker,
        string? objectMarker,
        string fixtureId,
        string buildingId) =>
        string.Equals(fixtureMarker, fixtureId, StringComparison.Ordinal)
        && string.Equals(objectMarker, buildingId, StringComparison.Ordinal);

    public static bool IsDisposableObjectClutter(ReviewFixtureObjectClutterState item) =>
        DisposableObjectKinds.TryGetValue(
            item.ItemId,
            out (string Name, string Type, int Fragility) kind)
        && string.Equals(item.Name, kind.Name, StringComparison.Ordinal)
        && string.Equals(item.Type, kind.Type, StringComparison.Ordinal)
        && item.Stack == 1
        && item.CanBeSetDown
        && item.CanBeGrabbed
        && !item.IsSpawnedObject
        && !item.IsQuestItem
        && !item.IsBigCraftable
        && !item.HasHeldObject
        && item.Fragility == kind.Fragility
        && item.Price == 0
        && !item.HasModData;

    public static bool IsDisposableTerrainClutter(
        ReviewFixtureTerrainClutterState terrain) =>
        !terrain.HasModData
        && (terrain.Kind == ReviewFixtureTerrainKind.Grass
            || (terrain.Kind == ReviewFixtureTerrainKind.Tree
                && !terrain.IsTapped
                && !terrain.IsStump));

    public static bool IsDisposableResourceClump(
        ReviewFixtureResourceClumpState clump) =>
        clump.ParentSheetIndex == 600
        && clump.Width == 2
        && clump.Height == 2
        && !clump.HasModData;

    public static bool IsBuildableMapTile(
        string? buildableProperty,
        bool hasDiggableProperty) =>
        string.Equals(buildableProperty, "t", StringComparison.OrdinalIgnoreCase)
        || string.Equals(buildableProperty, "true", StringComparison.OrdinalIgnoreCase)
        || (hasDiggableProperty
            && !string.Equals(buildableProperty, "f", StringComparison.OrdinalIgnoreCase));
}

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewFixtureCommand
{
    public static void Handle(
        string[] arguments,
        IReviewFixtureRuntime runtime,
        IMonitor monitor)
    {
        if (!ReviewFixtureArguments.TryParse(arguments, out ReviewFixtureRequest? request, out string error))
        {
            monitor.Log(error, LogLevel.Error);
            return;
        }

        try
        {
            ReviewFixtureResult result = ReviewFixtureOperation.Execute(request!, runtime);
            monitor.Log(result.Message, result.Succeeded ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit fixture command failed closed: {exception.GetBaseException().Message}",
                LogLevel.Error);
        }
    }
}

internal sealed class StardewReviewFixtureRuntime(
    Func<TestSaveAutomation?> testSave,
    Func<NetworkTwoAutomation?> networkTwo,
    Func<long> getNewMultiplayerId) : IReviewFixtureRuntime
{
    public ReviewFixtureAccess VerifyExactReviewFixture()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(ReviewFixtureContract.ReviewEnvironmentName)?.Trim(),
                ReviewFixtureContract.ReviewEnvironmentValue,
                StringComparison.Ordinal))
        {
            return Denied(
                "Fixture commands are available only in an SDVKit project review launch.");
        }

        NetworkTwoAutomation? network = networkTwo();
        if (network is not null)
        {
            if (!network.TryVerifyReviewFixture(
                    out string networkFixtureId,
                    out string role,
                    out string reason))
            {
                return Denied(reason);
            }

            if (network.IsHost)
            {
                TestSaveAutomation? hostTestSave = testSave();
                if (hostTestSave is null)
                {
                    return Denied(
                        "The network-2 host has no disposable test-save automation.");
                }

                if (!hostTestSave.TryVerifyReviewFixture(
                        out string hostFixtureId,
                        out string hostReason))
                {
                    return Denied(hostReason);
                }

                if (!string.Equals(
                        hostFixtureId,
                        networkFixtureId,
                        StringComparison.Ordinal))
                {
                    return Denied(
                        "The network-2 host and live pair fixture IDs differ.");
                }
            }

            return new ReviewFixtureAccess(
                true,
                network.IsHost && Context.IsMainPlayer,
                networkFixtureId,
                role,
                "The exact live network-2 review pair was freshly verified.");
        }

        TestSaveAutomation? single = testSave();
        if (single is null)
        {
            return Denied("The project review has no disposable test-save automation.");
        }

        if (!single.TryVerifyReviewFixture(out string fixtureId, out string singleReason))
        {
            return Denied(singleReason);
        }

        return new ReviewFixtureAccess(
            true,
            Context.IsMainPlayer,
            fixtureId,
            "single",
            "The exact live single-player review fixture was freshly verified.");
    }

    public ReviewFixtureResult Status(ReviewFixtureAccess access)
    {
        Farm farm = Game1.getFarm();
        string fixtureId = RequiredFixtureId(access);
        var lines = new List<string>
        {
            $"SDVKit fixture status fixture={fixtureId} role={access.Role} "
            + $"save={Constants.SaveFolderName} player={Game1.player.Name} "
            + $"playerId={Game1.player.UniqueMultiplayerID} "
            + $"location={Game1.currentLocation?.NameOrUniqueName ?? "<none>"} "
            + $"mainPlayer={Context.IsMainPlayer} multiplayer={Context.IsMultiplayer}",
        };

        foreach (Building building in GetOwnedBuildings(farm, fixtureId))
        {
            GameLocation? indoors = building.GetIndoors();
            string alias = building.modData[ReviewFixtureContract.BuildingAliasMarkerKey];
            int ownedObjects = indoors?.Objects.Pairs.Count(pair =>
                IsOwnedObject(pair.Value, fixtureId, building.id.Value)) ?? 0;
            int ownedAnimals = GetAllFarmAnimals(farm).Count(animal =>
                IsOwnedAnimal(animal, fixtureId, building.id.Value));
            int players = indoors?.farmers.Count ?? 0;
            int objects = indoors?.Objects.Count() ?? 0;
            int animals = indoors?.getAllFarmAnimals().Count ?? 0;
            lines.Add(
                $"  alias={alias} id={building.id.Value:D} type={building.buildingType.Value} "
                + $"tile={building.tileX.Value},{building.tileY.Value} "
                + $"interior={indoors?.NameOrUniqueName ?? "<none>"} "
                + $"map={indoors?.mapPath.Value ?? "<none>"} "
                + $"players={players} objects={objects} animals={animals} "
                + $"ownedObjects={ownedObjects} ownedWhiteCows={ownedAnimals}");
        }

        return Success(string.Join(Environment.NewLine, lines));
    }

    public ReviewFixtureResult EnsureBuilding(
        ReviewFixtureAccess access,
        string alias,
        int x,
        int y)
    {
        Farm farm = Game1.getFarm();
        string fixtureId = RequiredFixtureId(access);
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
            if (existing.isUnderConstruction(ignoreUpgrades: false))
            {
                existing.FinishConstruction(onGameStart: false);
            }

            return Success(
                $"Fixture building '{alias}' already exists as {existing.id.Value:D} at {x},{y}.");
        }

        if (!TryValidateBuildingFootprint(farm, x, y, out string footprintError))
        {
            return Failure(footprintError);
        }

        // The registered disposable baseline has ordinary debris in these exact
        // acceptance-test footprints. The narrow preflight permits only that
        // explicit disposable clutter and fails closed on every other world state.
        // Stardew may retain allowed objects or clumps beneath the building; the
        // final fixture reset restores the entire baseline either way.
        if (!farm.buildStructure(
                ReviewFixtureContract.DeluxeBarnBuildingType,
                new Vector2(x, y),
                Game1.player,
                out Building constructed,
                magicalConstruction: false,
                skipSafetyChecks: true))
        {
            return Failure(
                $"Stardew rejected the safe Deluxe Barn placement for '{alias}' at {x},{y}.");
        }

        constructed.modData[ReviewFixtureContract.FixtureIdMarkerKey] = fixtureId;
        constructed.modData[ReviewFixtureContract.BuildingAliasMarkerKey] = alias;
        constructed.FinishConstruction(onGameStart: false);
        return Success(
            $"Created finished fixture building '{alias}' as {constructed.id.Value:D} at {x},{y}.");
    }

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

    public ReviewFixtureResult EnsureAnimal(
        ReviewFixtureAccess access,
        string building)
    {
        Farm farm = Game1.getFarm();
        string fixtureId = RequiredFixtureId(access);
        if (!TryResolveOwnedBuilding(building, fixtureId, out Building target, out GameLocation indoors, out string error))
        {
            return Failure(error);
        }

        if (indoors is not AnimalHouse animalHouse)
        {
            return Failure($"Fixture building {target.id.Value:D} is not an AnimalHouse.");
        }

        FarmAnimal[] ownedAnimals = GetAllFarmAnimals(farm)
            .Where(animal => IsOwnedAnimal(animal, fixtureId, target.id.Value))
            .ToArray();
        ReviewFixtureEnsureDecision decision = ReviewFixturePolicy.DecideAnimalEnsure(
            ownedAnimals.Select(existing => new ReviewFixtureAnimalState(
                existing.type.Value,
                existing.home?.id.Value == target.id.Value,
                animalHouse.animalsThatLiveHere.Contains(existing.myID.Value))).ToArray(),
            animalHouse.animalsThatLiveHere.Count,
            animalHouse.animalLimit.Value);
        if (decision == ReviewFixtureEnsureDecision.Reject)
        {
            return Failure(
                $"Fixture building {target.id.Value:D} has an owned-animal conflict, invalid home, or no capacity.");
        }

        if (decision == ReviewFixtureEnsureDecision.Confirm)
        {
            FarmAnimal existing = ownedAnimals[0];
            return Success(
                $"Owned White Cow {existing.myID.Value} already belongs to {target.id.Value:D}.");
        }

        var animal = new FarmAnimal(
            ReviewFixtureContract.WhiteCowType,
            getNewMultiplayerId(),
            Game1.player.UniqueMultiplayerID);
        animal.modData[ReviewFixtureContract.FixtureIdMarkerKey] = fixtureId;
        animal.modData[ReviewFixtureContract.AnimalKindMarkerKey] =
            GetAnimalMarker(target.id.Value);
        animalHouse.adoptAnimal(animal);
        return Success(
            $"Created owned White Cow {animal.myID.Value} in {target.id.Value:D}.");
    }

    public ReviewFixtureResult Enter(
        ReviewFixtureAccess access,
        string building)
    {
        string fixtureId = RequiredFixtureId(access);
        if (!TryResolveOwnedBuilding(building, fixtureId, out Building target, out GameLocation indoors, out string error))
        {
            return Failure(error);
        }

        if (ReferenceEquals(Game1.currentLocation, indoors))
        {
            return Success($"Player is already inside fixture building {target.id.Value:D}.");
        }

        if (!ReferenceEquals(Game1.currentLocation, Game1.getFarm()))
        {
            return Failure("Enter is allowed only from the Farm or the requested fixture interior.");
        }

        if (!TryGetNaturalExit(indoors, out Warp exit, out Vector2 entry, out error))
        {
            return Failure(error);
        }

        Game1.warpFarmer(indoors.NameOrUniqueName, (int)entry.X, (int)entry.Y, false);
        return Success(
            $"Warped through the natural entry of fixture building {target.id.Value:D} at {entry.X},{entry.Y}.");
    }

    public ReviewFixtureResult Farm(ReviewFixtureAccess access)
    {
        string fixtureId = RequiredFixtureId(access);
        Farm farm = Game1.getFarm();
        if (ReferenceEquals(Game1.currentLocation, farm))
        {
            return Success("Player is already on the Farm.");
        }

        Building? parent = GetOwnedBuildings(farm, fixtureId).SingleOrDefault(building =>
            ReferenceEquals(building.GetIndoors(), Game1.currentLocation));
        if (parent?.GetIndoors() is not GameLocation indoors)
        {
            return Failure("Farm is allowed only from the Farm or an owned fixture interior.");
        }

        if (!TryGetNaturalExit(indoors, out Warp exit, out _, out string error))
        {
            return Failure(error);
        }

        var target = new Vector2(exit.TargetX, exit.TargetY);
        if (!farm.isTileOnMap(target) || !farm.isTilePassable(target))
        {
            return Failure("The fixture interior's natural Farm warp target is not passable.");
        }

        Game1.warpFarmer("Farm", exit.TargetX, exit.TargetY, false);
        return Success(
            $"Warped through fixture building {parent.id.Value:D}'s natural Farm exit at {target.X},{target.Y}.");
    }

    private static ReviewFixtureAccess Denied(string message) =>
        new(false, false, null, null, message);

    private static ReviewFixtureResult Success(string message) => new(true, message);

    private static ReviewFixtureResult Failure(string message) => new(false, message);

    private static string RequiredFixtureId(ReviewFixtureAccess access) =>
        access.FixtureId
        ?? throw new InvalidOperationException("The verified fixture ID is unavailable.");

    private static IEnumerable<Building> GetOwnedBuildings(Farm farm, string fixtureId) =>
        farm.buildings
            .Where(building =>
                IsOwnedBuilding(building, fixtureId)
                && building.modData.TryGetValue(
                    ReviewFixtureContract.BuildingAliasMarkerKey,
                    out string alias)
                && ReviewFixtureArguments.IsValidAlias(alias))
            .OrderBy(building =>
                building.modData[ReviewFixtureContract.BuildingAliasMarkerKey],
                StringComparer.Ordinal);

    private static bool IsOwnedBuilding(Building building, string fixtureId) =>
        building.modData.TryGetValue(
            ReviewFixtureContract.FixtureIdMarkerKey,
            out string observedFixtureId)
        && string.Equals(observedFixtureId, fixtureId, StringComparison.Ordinal);

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

    private static bool IsOwnedAnimal(
        FarmAnimal animal,
        string fixtureId,
        Guid buildingId) =>
        animal.modData.TryGetValue(
            ReviewFixtureContract.FixtureIdMarkerKey,
            out string observedFixtureId)
        && string.Equals(observedFixtureId, fixtureId, StringComparison.Ordinal)
        && animal.modData.TryGetValue(
            ReviewFixtureContract.AnimalKindMarkerKey,
            out string observedKind)
        && string.Equals(
            observedKind,
            GetAnimalMarker(buildingId),
            StringComparison.Ordinal);

    private static string GetAnimalMarker(Guid buildingId) =>
        $"{ReviewFixtureContract.WhiteCowKind}:{buildingId:D}";

    private static FarmAnimal[] GetAllFarmAnimals(Farm farm)
    {
        var animals = new Dictionary<long, FarmAnimal>();
        foreach (FarmAnimal animal in farm.getAllFarmAnimals())
        {
            animals[animal.myID.Value] = animal;
        }

        foreach (Building building in farm.buildings)
        {
            if (building.GetIndoors() is not GameLocation indoors)
            {
                continue;
            }

            foreach (FarmAnimal animal in indoors.getAllFarmAnimals())
            {
                animals[animal.myID.Value] = animal;
            }
        }

        return animals.Values.ToArray();
    }

    private static bool TryResolveOwnedBuilding(
        string token,
        string fixtureId,
        out Building building,
        out GameLocation indoors,
        out string error)
    {
        Farm farm = Game1.getFarm();
        Building[] matches;
        if (Guid.TryParseExact(token, "D", out Guid id))
        {
            matches = farm.buildings
                .Where(candidate => candidate.id.Value == id)
                .ToArray();
        }
        else
        {
            matches = farm.buildings
                .Where(candidate => candidate.modData.TryGetValue(
                    ReviewFixtureContract.BuildingAliasMarkerKey,
                    out string alias)
                    && string.Equals(alias, token, StringComparison.Ordinal))
                .ToArray();
        }

        if (matches.Length != 1
            || !IsOwnedBuilding(matches[0], fixtureId)
            || !matches[0].modData.TryGetValue(
                ReviewFixtureContract.BuildingAliasMarkerKey,
                out string ownedAlias)
            || !ReviewFixtureArguments.IsValidAlias(ownedAlias))
        {
            building = null!;
            indoors = null!;
            error = matches.Length > 1
                ? $"Fixture building token '{token}' is ambiguous."
                : $"No exact owned fixture building matches '{token}'.";
            return false;
        }

        building = matches[0];
        indoors = building.GetIndoors()!;
        if (indoors is null)
        {
            error = $"Fixture building {building.id.Value:D} has no loaded interior.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateBuildingFootprint(
        Farm farm,
        int x,
        int y,
        out string error)
    {
        if (!Game1.buildingData.TryGetValue(
                ReviewFixtureContract.DeluxeBarnBuildingType,
                out StardewValley.GameData.Buildings.BuildingData? buildingData)
            || buildingData.Size.X <= 0
            || buildingData.Size.Y <= 0)
        {
            error = "Stardew's exact Deluxe Barn footprint is unavailable.";
            return false;
        }

        xTile.Layers.Layer? back = farm.Map.GetLayer("Back");
        if (back is null
            || (long)x + buildingData.Size.X > back.LayerWidth
            || (long)y + buildingData.Size.Y > back.LayerHeight)
        {
            error = $"The Deluxe Barn footprint at {x},{y} is outside the Farm map.";
            return false;
        }

        var requested = new Rectangle(
            x,
            y,
            buildingData.Size.X,
            buildingData.Size.Y);
        var footprintTiles = new HashSet<Point>();
        AddTiles(footprintTiles, requested);
        var buildableTiles = new HashSet<Point>(footprintTiles);
        var passableTiles = new HashSet<Point>();
        var protectedTiles = new HashSet<Point>(footprintTiles);
        foreach (StardewValley.GameData.Buildings.BuildingPlacementTile placement
            in buildingData.AdditionalPlacementTiles ?? [])
        {
            Rectangle area = placement.TileArea;
            var absoluteArea = new Rectangle(
                x + area.X,
                y + area.Y,
                area.Width,
                area.Height);
            AddTiles(protectedTiles, absoluteArea);
            AddTiles(
                placement.OnlyNeedsToBePassable ? passableTiles : buildableTiles,
                absoluteArea);
        }

        if (buildingData.HumanDoor.X >= 0 && buildingData.HumanDoor.Y >= 0)
        {
            var landing = new Point(
                x + buildingData.HumanDoor.X,
                y + buildingData.HumanDoor.Y + 1);
            protectedTiles.Add(landing);
            passableTiles.Add(landing);
        }

        // Farm.isBuildable also evaluates current occupancy, which would reject
        // the explicitly allowed baseline clutter. Validate its occupancy-neutral
        // map placement and Buildable/Diggable rules here; stricter occupancy
        // checks follow.
        Rectangle buildableArea = farm.GetBuildableRectangle();
        foreach (Point tile in protectedTiles)
        {
            var vector = new Vector2(tile.X, tile.Y);
            if (tile.X < 0
                || tile.Y < 0
                || tile.X >= back.LayerWidth
                || tile.Y >= back.LayerHeight)
            {
                error = $"The Deluxe Barn placement tile {tile.X},{tile.Y} is outside the Farm map.";
                return false;
            }

            if (buildableTiles.Contains(tile)
                && ((buildableArea != Rectangle.Empty
                        && !buildableArea.Contains(tile))
                    || !farm.isTilePlaceable(vector, itemIsPassable: false)
                    || !ReviewFixturePolicy.IsBuildableMapTile(
                        farm.doesTileHavePropertyNoNull(
                            tile.X,
                            tile.Y,
                            "Buildable",
                            "Back"),
                        farm.doesTileHaveProperty(
                            tile.X,
                            tile.Y,
                            "Diggable",
                            "Back",
                            ignoreTileSheetProperties: false) is not null)))
            {
                error = $"The Deluxe Barn footprint tile {tile.X},{tile.Y} is water, NoBuild, NoFurniture, or otherwise not buildable.";
                return false;
            }
        }

        Building? overlap = farm.buildings.FirstOrDefault(existing =>
            protectedTiles.Any(tile =>
                existing.occupiesTile(tile.X, tile.Y, applyTilePropertyRadius: false)));
        if (overlap is not null)
        {
            error = $"The Deluxe Barn footprint at {x},{y} overlaps existing building {overlap.id.Value:D}.";
            return false;
        }

        foreach (Point tile in protectedTiles)
        {
            var vector = new Vector2(tile.X, tile.Y);
            bool inFootprint = footprintTiles.Contains(tile);
            if (farm.GetFurnitureAt(vector) is not null)
            {
                error = $"The Deluxe Barn placement at {x},{y} overlaps furniture at {tile.X},{tile.Y}.";
                return false;
            }

            if (farm.Objects.TryGetValue(vector, out StardewValley.Object? item)
                && (!inFootprint || !IsDisposableObjectClutter(item)))
            {
                error = $"The Deluxe Barn placement at {x},{y} would overwrite non-disposable object '{item.QualifiedItemId}' at {tile.X},{tile.Y}.";
                return false;
            }

            if (farm.terrainFeatures.TryGetValue(vector, out TerrainFeature? terrain)
                && (!inFootprint
                    ? terrain is not Grass || terrain.modData.Count() > 0
                    : !IsDisposableTerrainClutter(terrain)))
            {
                error = $"The Deluxe Barn placement at {x},{y} would overwrite non-disposable terrain at {tile.X},{tile.Y}.";
                return false;
            }

            if (passableTiles.Contains(tile)
                && !buildableTiles.Contains(tile)
                && !farm.isTilePassable(vector))
            {
                error = $"The Deluxe Barn access tile {tile.X},{tile.Y} is not passable.";
                return false;
            }

            Rectangle tileBounds = GetTileBounds(tile);
            if (farm.farmers.Any(farmer => farmer.GetBoundingBox().Intersects(tileBounds))
                || farm.characters.Any(character => character.GetBoundingBox().Intersects(tileBounds))
                || farm.getAllFarmAnimals().Any(animal => animal.GetBoundingBox().Intersects(tileBounds)))
            {
                error = $"The Deluxe Barn placement at {x},{y} is occupied by a player, character, or animal at {tile.X},{tile.Y}.";
                return false;
            }

            if (farm.largeTerrainFeatures.Any(feature =>
                feature.getBoundingBox().Intersects(tileBounds)))
            {
                error = $"The Deluxe Barn placement at {x},{y} overlaps a large terrain feature at {tile.X},{tile.Y}.";
                return false;
            }
        }

        foreach (ResourceClump clump in farm.resourceClumps)
        {
            bool overlapsFootprint = footprintTiles.Any(tile =>
                clump.occupiesTile(tile.X, tile.Y));
            bool overlapsProtected = overlapsFootprint || protectedTiles.Any(tile =>
                clump.occupiesTile(tile.X, tile.Y));
            if (overlapsProtected
                && (!overlapsFootprint || !IsDisposableResourceClump(clump)))
            {
                error = $"The Deluxe Barn placement at {x},{y} overlaps a non-disposable resource clump.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static void AddTiles(HashSet<Point> target, Rectangle area)
    {
        for (int tileX = area.Left; tileX < area.Right; tileX++)
        {
            for (int tileY = area.Top; tileY < area.Bottom; tileY++)
            {
                target.Add(new Point(tileX, tileY));
            }
        }
    }

    private static Rectangle GetTileBounds(Point tile) => new(
        tile.X * Game1.tileSize,
        tile.Y * Game1.tileSize,
        Game1.tileSize,
        Game1.tileSize);

    private static bool IsDisposableObjectClutter(StardewValley.Object item) =>
        ReviewFixturePolicy.IsDisposableObjectClutter(new ReviewFixtureObjectClutterState(
            item.ItemId,
            item.Name,
            item.Type,
            item.Stack,
            item.CanBeSetDown,
            item.CanBeGrabbed,
            item.IsSpawnedObject,
            item.questItem.Value,
            item.bigCraftable.Value,
            item.heldObject.Value is not null,
            item.Fragility,
            item.Price,
            item.modData.Count() > 0));

    private static bool IsDisposableTerrainClutter(TerrainFeature terrain) =>
        ReviewFixturePolicy.IsDisposableTerrainClutter(
            terrain switch
            {
                Grass grass => new ReviewFixtureTerrainClutterState(
                    ReviewFixtureTerrainKind.Grass,
                    IsTapped: false,
                    IsStump: false,
                    grass.modData.Count() > 0),
                Tree tree => new ReviewFixtureTerrainClutterState(
                    ReviewFixtureTerrainKind.Tree,
                    tree.tapped.Value,
                    tree.stump.Value,
                    tree.modData.Count() > 0),
                _ => new ReviewFixtureTerrainClutterState(
                    ReviewFixtureTerrainKind.Other,
                    IsTapped: false,
                    IsStump: false,
                    terrain.modData.Count() > 0),
            });

    private static bool IsDisposableResourceClump(ResourceClump clump) =>
        ReviewFixturePolicy.IsDisposableResourceClump(new ReviewFixtureResourceClumpState(
            clump.parentSheetIndex.Value,
            clump.width.Value,
            clump.height.Value,
            clump.modData.Count() > 0));

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

    private static bool TryGetNaturalExit(
        GameLocation indoors,
        out Warp exit,
        out Vector2 entry,
        out string error)
    {
        exit = indoors.GetFirstPlayerWarp();
        entry = exit is null
            ? Vector2.Zero
            : new Vector2(exit.X, exit.Y - 1);
        if (exit is null
            || !string.Equals(exit.TargetName, "Farm", StringComparison.Ordinal)
            || !indoors.isTileOnMap(entry)
            || !indoors.isTilePassable(entry))
        {
            error = "The fixture interior has no usable natural Farm warp and entry tile.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
#endif
