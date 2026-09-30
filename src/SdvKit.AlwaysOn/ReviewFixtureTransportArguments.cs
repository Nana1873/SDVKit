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

internal static class ReviewFixtureTransportArguments
{
    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out string? requestId,
        out ReviewFixtureRequestBinding? binding,
        out ReviewFixtureQuery? query,
        out ReviewFixtureRequest? request,
        out ReviewFixtureProblem? problem)
    {
        requestId = null;
        binding = null;
        query = null;
        request = null;
        problem = null;
        if (arguments is null
            || arguments.Count < 8
            || !string.Equals(arguments[0], "fixture", StringComparison.Ordinal)
            || !ReviewTransportToken.IsRequestId(arguments[1]))
        {
            problem = Problem("fixtureTransportInvalid", "The bounded review-fixture transport request is invalid.");
            return false;
        }

        requestId = arguments[1];
        string launchId = arguments[2];
        string topology = arguments[3];
        string roleToken = arguments[4];
        bool single = string.Equals(topology, "single", StringComparison.Ordinal)
            && string.Equals(
                roleToken,
                ReviewFixtureTransportContract.SingleRoleToken,
                StringComparison.Ordinal);
        bool network = string.Equals(
                topology,
                NetworkTwoContract.Topology,
                StringComparison.Ordinal)
            && NetworkTwoContract.IsRole(roleToken);
        if (!ReviewTransportToken.IsRequestId(launchId)
            || (!single && !network)
            || !TryDecode(arguments[5], out string fixtureId)
            || !TryDecode(arguments[6], out string saveId))
        {
            problem = Problem(
                "fixtureBindingInvalid",
                "The bounded review-fixture identity binding is invalid.");
            return false;
        }

        binding = new ReviewFixtureRequestBinding(
            launchId,
            topology,
            network ? roleToken : null,
            fixtureId,
            saveId);
        string operation = arguments[7];
        if (operation == ReviewFixtureTransportContract.StatusOperation
            && arguments.Count == 8)
        {
            query = new ReviewFixtureQuery(operation);
            request = new ReviewFixtureStatusRequest();
            return true;
        }

        if (operation == ReviewFixtureTransportContract.FarmOperation
            && arguments.Count == 8)
        {
            query = new ReviewFixtureQuery(operation);
            request = new ReviewFixtureFarmRequest();
            return true;
        }

        if (operation == ReviewFixtureTransportContract.SaveOperation
            && arguments.Count == 8)
        {
            query = new ReviewFixtureQuery(operation);
            return true;
        }

        if (operation == ReviewFixtureTransportContract.EnterOperation
            && arguments.Count == 9
            && TryDecode(arguments[8], out string building)
            && ReviewFixtureArguments.IsValidBuildingToken(building))
        {
            query = new ReviewFixtureQuery(operation, Building: building);
            request = new ReviewFixtureEnterRequest(building);
            return true;
        }

        if (operation == ReviewFixtureTransportContract.BuildingEnsureOperation
            && arguments.Count == 12
            && TryDecode(arguments[8], out string alias)
            && TryDecode(arguments[9], out string buildingKind)
            && ReviewFixtureArguments.IsValidAlias(alias)
            && IsValidKind(buildingKind)
            && int.TryParse(arguments[10], NumberStyles.None, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(arguments[11], NumberStyles.None, CultureInfo.InvariantCulture, out int y)
            && x >= 0
            && y >= 0)
        {
            query = new ReviewFixtureQuery(
                operation,
                Alias: alias,
                Kind: buildingKind,
                X: x,
                Y: y);
            request = new ReviewFixtureBuildingEnsureRequest(alias, buildingKind, x, y);
            return true;
        }

        if (operation == ReviewFixtureTransportContract.AnimalEnsureOperation
            && arguments.Count == 10
            && TryDecode(arguments[8], out string animalBuilding)
            && TryDecode(arguments[9], out string animalKind)
            && ReviewFixtureArguments.IsValidBuildingToken(animalBuilding)
            && IsValidKind(animalKind))
        {
            query = new ReviewFixtureQuery(
                operation,
                Building: animalBuilding,
                Kind: animalKind);
            request = new ReviewFixtureAnimalEnsureRequest(animalBuilding, animalKind);
            return true;
        }

        problem = Problem(
            "fixtureTransportInvalid",
            "The bounded review-fixture operation or its encoded operands are invalid.");
        return false;
    }

    private static bool TryDecode(string token, out string value) =>
        ReviewTransportToken.TryDecode(
            token,
            ReviewFixtureTransportContract.MaximumTokenLength,
            out value);

    private static bool IsValidKind(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= ReviewFixtureTransportContract.MaximumTokenLength
        && !value.Any(char.IsControl);

    private static ReviewFixtureProblem Problem(string code, string message) =>
        new(code, message);
}
