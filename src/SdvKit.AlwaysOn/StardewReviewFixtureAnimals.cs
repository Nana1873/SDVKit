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
    public ReviewFixtureResult EnsureAnimal(
        ReviewFixtureAccess access,
        string building,
        string kind)
    {
        Farm farm = Game1.getFarm();
        string fixtureId = RequiredFixtureId(access);
        Dictionary<string, FarmAnimalData> animalKinds = DataLoader.FarmAnimals(Game1.content);
        if (!ReviewFixtureKindResolver.TryResolve(
                kind,
                animalKinds.Keys,
                "animal",
                out ReviewFixtureKindResolution? resolved,
                out string resolutionError)
            || resolved is null)
        {
            return Failure(resolutionError);
        }

        if (!animalKinds.TryGetValue(
                resolved.CanonicalId,
                out FarmAnimalData? animalData))
        {
            return Failure(
                $"Stardew's loaded animal data changed while resolving '{kind}'.");
        }

        if (!TryResolveOwnedBuilding(building, fixtureId, out Building target, out GameLocation indoors, out string error))
        {
            return Failure(error);
        }

        if (indoors is not AnimalHouse animalHouse)
        {
            return Failure($"Fixture building {target.id.Value:D} is not an AnimalHouse.");
        }

        BuildingData? targetData = target.GetData();
        if (targetData is null
            || !ReviewFixturePolicy.IsAnimalHouseCompatible(
                animalData.House,
                targetData.ValidOccupantTypes))
        {
            return Failure(
                $"Canonical animal kind '{resolved.CanonicalId}' requires occupant type "
                + $"'{animalData.House}', which fixture building {target.id.Value:D} "
                + $"type='{target.buildingType.Value}' doesn't accept.");
        }

        FarmAnimal[] ownedAnimals = GetAllFarmAnimals(farm)
            .Where(animal => IsOwnedAnimal(animal, fixtureId, target.id.Value))
            .ToArray();
        ReviewFixtureEnsureDecision decision = ReviewFixturePolicy.DecideAnimalEnsure(
            ownedAnimals.Select(existing => new ReviewFixtureAnimalState(
                GetOwnedAnimalKind(existing, target.id.Value) ?? string.Empty,
                existing.type.Value,
                existing.home?.id.Value == target.id.Value,
                animalHouse.animalsThatLiveHere.Contains(existing.myID.Value))).ToArray(),
            resolved.CanonicalToken,
            resolved.CanonicalId,
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
            return SuccessAnimal(
                $"Owned fixture animal {existing.myID.Value} already exists "
                + $"type='{resolved.CanonicalId}' token={resolved.CanonicalToken} "
                + $"home={target.id.Value:D} assigned=true.",
                existing,
                target,
                resolved,
                changed: false);
        }

        var animal = new FarmAnimal(
            resolved.CanonicalId,
            getNewMultiplayerId(),
            Game1.player.UniqueMultiplayerID);
        if (!animal.CanLiveIn(target))
        {
            return Failure(
                $"Stardew rejected canonical animal kind '{resolved.CanonicalId}' "
                + $"for fixture building {target.id.Value:D} before adoption.");
        }

        animal.modData[ReviewFixtureContract.FixtureIdMarkerKey] = fixtureId;
        animal.modData[ReviewFixtureContract.AnimalKindMarkerKey] =
            GetAnimalMarker(resolved.CanonicalToken, target.id.Value);
        try
        {
            animalHouse.adoptAnimal(animal);
        }
        catch (Exception exception)
        {
            bool rollbackConfirmed = TryRollbackFailedAnimal(animalHouse, animal);
            return Failure(
                $"Stardew failed while adopting canonical animal kind '{resolved.CanonicalId}': "
                + exception.GetBaseException().Message
                + (rollbackConfirmed
                    ? ". No partial animal remains."
                    : ". The exact partial animal couldn't be removed; reset the disposable fixture."));
        }

        bool hasExactAssignment = animalHouse.animalsThatLiveHere.Contains(animal.myID.Value);
        bool hasExactHome = animal.home?.id.Value == target.id.Value;
        if (!hasExactAssignment || !hasExactHome)
        {
            bool rollbackConfirmed = TryRollbackFailedAnimal(animalHouse, animal);
            return Failure(
                $"Stardew didn't retain the exact home and assignment for animal {animal.myID.Value}. "
                + (rollbackConfirmed
                    ? "No partial animal remains."
                    : "The exact partial animal couldn't be removed; reset the disposable fixture."));
        }

        return SuccessAnimal(
            $"Created owned fixture animal {animal.myID.Value} "
            + $"type='{resolved.CanonicalId}' token={resolved.CanonicalToken} "
            + $"home={target.id.Value:D} assigned=true.",
            animal,
            target,
            resolved,
            changed: true);
    }

    private static ReviewFixtureResult SuccessAnimal(
        string message,
        FarmAnimal animal,
        Building building,
        ReviewFixtureKindResolution resolution,
        bool changed) =>
        new(
            true,
            message,
            Animal: new ReviewFixtureAnimalReport(
                animal.myID.Value,
                resolution.CanonicalId,
                resolution.CanonicalToken,
                building.id.Value.ToString("D"),
                animal.home?.id.Value == building.id.Value
                    && building.GetIndoors() is AnimalHouse animalHouse
                    && animalHouse.animalsThatLiveHere.Contains(animal.myID.Value),
                changed));

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
        && TryParseAnimalMarker(observedKind, out _, out Guid observedBuildingId)
        && observedBuildingId == buildingId;

    private static string GetAnimalMarker(string animalKind, Guid buildingId) =>
        $"{animalKind}:{buildingId:D}";

    private static string? GetOwnedAnimalKind(FarmAnimal animal, Guid buildingId) =>
        animal.modData.TryGetValue(
                ReviewFixtureContract.AnimalKindMarkerKey,
                out string observedKind)
            && TryParseAnimalMarker(
                observedKind,
                out string animalKind,
                out Guid observedBuildingId)
            && observedBuildingId == buildingId
                ? animalKind
                : null;

    private static bool TryParseAnimalMarker(
        string marker,
        out string animalKind,
        out Guid buildingId)
    {
        int separator = marker.LastIndexOf(':');
        animalKind = separator > 0 ? marker[..separator] : string.Empty;
        buildingId = Guid.Empty;
        return animalKind.Length > 0
            && string.Equals(
                animalKind,
                ReviewFixtureKindResolver.Normalize(animalKind),
                StringComparison.Ordinal)
            && Guid.TryParseExact(marker[(separator + 1)..], "D", out buildingId)
            && buildingId != Guid.Empty;
    }

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

    private static bool TryRollbackFailedAnimal(
        AnimalHouse animalHouse,
        FarmAnimal animal)
    {
        try
        {
            animalHouse.animals.Remove(animal.myID.Value);
            animalHouse.animalsThatLiveHere.Remove(animal.myID.Value);
            return !animalHouse.animals.ContainsKey(animal.myID.Value)
                && !animalHouse.animalsThatLiveHere.Contains(animal.myID.Value);
        }
        catch
        {
            return false;
        }
    }
}

#endif
