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
internal sealed partial class StardewReviewFixtureRuntime(
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
                    out string networkSaveId,
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
                "The exact live network-2 review pair was freshly verified.",
                Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")?.Trim(),
                NetworkTwoContract.Topology,
                networkSaveId);
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
            "The exact live review fixture and current local player were freshly verified.",
            Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")?.Trim(),
            "single",
            Constants.SaveFolderName);
    }

    public ReviewFixtureResult Status(ReviewFixtureAccess access)
    {
        Farm farm = Game1.getFarm();
        string fixtureId = RequiredFixtureId(access);
        Building[] ownedBuildings = GetOwnedBuildings(farm, fixtureId).ToArray();
        var lines = new List<string>
        {
            $"SDVKit fixture status fixture={fixtureId} role={access.Role} "
            + $"save={Constants.SaveFolderName} player={Game1.player.Name} "
            + $"playerId={Game1.player.UniqueMultiplayerID} "
            + $"location={Game1.currentLocation?.NameOrUniqueName ?? "<none>"} "
            + $"mainPlayer={Context.IsMainPlayer} multiplayer={Context.IsMultiplayer}",
        };

        foreach (Building building in ownedBuildings)
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
                + $"ownedObjects={ownedObjects} ownedAnimals={ownedAnimals}");
        }

        return new ReviewFixtureResult(
            true,
            string.Join(Environment.NewLine, lines),
            Status: new ReviewFixtureStatusReport(
                Game1.currentLocation?.NameOrUniqueName ?? "<none>",
                Game1.player.UniqueMultiplayerID,
                Context.IsMainPlayer,
                Context.IsMultiplayer,
                ownedBuildings
                    .Select(building => DescribeBuilding(
                        building,
                        fixtureId,
                        building.buildingType.Value,
                        ReviewFixtureKindResolver.Normalize(building.buildingType.Value),
                        changed: false))
                    .ToArray()));
    }

    private static ReviewFixtureAccess Denied(string message) =>
        new(false, false, null, null, message);

    private static ReviewFixtureResult Success(string message) => new(true, message);

    private static ReviewFixtureBuildingReport DescribeBuilding(
        Building building,
        string fixtureId,
        string canonicalKind,
        string canonicalToken,
        bool changed)
    {
        GameLocation? indoors = building.GetIndoors();
        Farm farm = Game1.getFarm();
        return new ReviewFixtureBuildingReport(
            building.modData[ReviewFixtureContract.BuildingAliasMarkerKey],
            building.id.Value.ToString("D"),
            canonicalKind,
            canonicalToken,
            building.tileX.Value,
            building.tileY.Value,
            indoors?.NameOrUniqueName,
            indoors?.mapPath.Value,
            indoors?.Objects.Pairs.Count(pair =>
                IsOwnedObject(pair.Value, fixtureId, building.id.Value)) ?? 0,
            GetAllFarmAnimals(farm).Count(animal =>
                IsOwnedAnimal(animal, fixtureId, building.id.Value)),
            changed);
    }

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
}

#endif
