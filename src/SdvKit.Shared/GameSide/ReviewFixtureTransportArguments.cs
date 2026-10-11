using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;


namespace SdvKit.GameSide;

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

        if (TryParseExtended(operation, arguments, out query, out request))
        {
            return true;
        }

        problem = Problem(
            "fixtureTransportInvalid",
            "The bounded review-fixture operation or its encoded operands are invalid.");
        return false;
    }

    private static bool TryParseExtended(
        string operation,
        IReadOnlyList<string> arguments,
        out ReviewFixtureQuery? query,
        out ReviewFixtureRequest? request)
    {
        query = null;
        request = null;
        switch (operation)
        {
            case ReviewFixtureTransportContract.ObjectEnsureOperation
                when arguments.Count == 10
                    && TryDecode(arguments[8], out string building)
                    && TryDecode(arguments[9], out string item)
                    && ReviewFixtureArguments.IsValidBuildingToken(building)
                    && IsValidKind(item):
                query = new ReviewFixtureQuery(operation, Building: building, Kind: item);
                request = new ReviewFixtureObjectEnsureRequest(building, item);
                return true;
            case ReviewFixtureTransportContract.ObjectClearOperation
                when arguments.Count == 9
                    && TryDecode(arguments[8], out string clearBuilding)
                    && ReviewFixtureArguments.IsValidBuildingToken(clearBuilding):
                query = new ReviewFixtureQuery(operation, Building: clearBuilding);
                request = new ReviewFixtureObjectClearOwnedRequest(clearBuilding);
                return true;
            case ReviewFixtureTransportContract.FishingPrepareOperation
                when arguments.Count is 9 or 11 && arguments[8] is "skip" or "keep":
                bool skip = arguments[8] == "skip";
                if (arguments.Count == 9)
                {
                    query = new ReviewFixtureQuery(operation, SkipTutorial: skip);
                    request = new ReviewFixtureFishingRequest(skip);
                    return true;
                }

                if (int.TryParse(arguments[9], NumberStyles.None, CultureInfo.InvariantCulture, out int fishX)
                    && int.TryParse(arguments[10], NumberStyles.None, CultureInfo.InvariantCulture, out int fishY))
                {
                    query = new ReviewFixtureQuery(operation, X: fishX, Y: fishY, SkipTutorial: skip);
                    request = new ReviewFixtureFishingRequest(skip, fishX, fishY);
                    return true;
                }

                return false;
            case ReviewFixtureTransportContract.SetTimeOperation
                when arguments.Count == 9
                    && int.TryParse(arguments[8], NumberStyles.None, CultureInfo.InvariantCulture, out int time)
                    && ReviewFixtureTransportContract.IsValidTime(time):
                query = new ReviewFixtureQuery(operation, X: time);
                request = new ReviewFixtureSetTimeRequest(time);
                return true;
            case ReviewFixtureTransportContract.SetWeatherOperation
                when arguments.Count == 9
                    && TryDecode(arguments[8], out string weather)
                    && ReviewFixtureTransportContract.Weathers.Contains(weather):
                query = new ReviewFixtureQuery(operation, Value: weather);
                request = new ReviewFixtureSetWeatherRequest(weather);
                return true;
            case ReviewFixtureTransportContract.SetSeasonOperation
                when arguments.Count == 9
                    && TryDecode(arguments[8], out string season)
                    && ReviewFixtureTransportContract.Seasons.Contains(season):
                query = new ReviewFixtureQuery(operation, Value: season);
                request = new ReviewFixtureSetSeasonRequest(season);
                return true;
            case ReviewFixtureTransportContract.WarpOperation
                when arguments.Count is 9 or 11
                    && TryDecode(arguments[8], out string location)
                    && IsValidKind(location):
                if (arguments.Count == 9)
                {
                    query = new ReviewFixtureQuery(operation, Value: location);
                    request = new ReviewFixtureWarpRequest(location, null, null);
                    return true;
                }

                if (int.TryParse(arguments[9], NumberStyles.None, CultureInfo.InvariantCulture, out int warpX)
                    && int.TryParse(arguments[10], NumberStyles.None, CultureInfo.InvariantCulture, out int warpY))
                {
                    query = new ReviewFixtureQuery(operation, Value: location, X: warpX, Y: warpY);
                    request = new ReviewFixtureWarpRequest(location, warpX, warpY);
                    return true;
                }

                return false;
            case ReviewFixtureTransportContract.SetZoomOperation
                when arguments.Count == 9
                    && double.TryParse(arguments[8], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double zoom)
                    && zoom is >= ReviewFixtureTransportContract.MinimumZoom and <= ReviewFixtureTransportContract.MaximumZoom:
                query = new ReviewFixtureQuery(operation, Zoom: zoom);
                request = new ReviewFixtureSetZoomRequest(zoom);
                return true;
            case ReviewFixtureTransportContract.SetWindowOperation
                when arguments.Count == 10
                    && int.TryParse(arguments[8], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
                    && int.TryParse(arguments[9], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
                    && width is >= ReviewFixtureTransportContract.MinimumWindowSize and <= ReviewFixtureTransportContract.MaximumWindowSize
                    && height is >= ReviewFixtureTransportContract.MinimumWindowSize and <= ReviewFixtureTransportContract.MaximumWindowSize:
                query = new ReviewFixtureQuery(operation, X: width, Y: height);
                request = new ReviewFixtureSetWindowRequest(width, height);
                return true;
            default:
                return false;
        }
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
