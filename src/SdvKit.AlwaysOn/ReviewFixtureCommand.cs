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

internal static class ReviewFixtureContract
{
    internal const string FixtureIdMarkerKey = "SDVKit.AlwaysOn/FixtureId";
    internal const string BuildingAliasMarkerKey = "SDVKit.AlwaysOn/FixtureBuildingAlias";
    internal const string ObjectMarkerKey = "SDVKit.AlwaysOn/FixtureObject";
    internal const string AnimalKindMarkerKey = "SDVKit.AlwaysOn/FixtureAnimalKind";
    internal const string GreenhouseTarget = "greenhouse";
    internal const string GreenhouseBuildingType = "Greenhouse";
    internal const string ReviewEnvironmentName = "SDVKIT_PROJECT_REVIEW";
    internal const string ReviewEnvironmentValue = "1";
}

internal abstract record ReviewFixtureRequest(bool RequiresMainPlayer);

internal sealed record ReviewFixtureStatusRequest()
    : ReviewFixtureRequest(RequiresMainPlayer: false);

internal sealed record ReviewFixtureBuildingEnsureRequest(
    string Alias,
    string Kind,
    int X,
    int Y)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal sealed record ReviewFixtureObjectEnsureRequest(
    string Building,
    string QualifiedItemId)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal sealed record ReviewFixtureObjectClearOwnedRequest(string Building)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal sealed record ReviewFixtureAnimalEnsureRequest(
    string Building,
    string Kind)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal sealed record ReviewFixtureEnterRequest(string Building)
    : ReviewFixtureRequest(RequiresMainPlayer: false);

internal sealed record ReviewFixtureFarmRequest()
    : ReviewFixtureRequest(RequiresMainPlayer: false);

internal static class ReviewFixtureArguments
{
    internal const string Usage =
        "Usage: sdvkit fixture status | "
        + "building ensure <alias> <building-kind> <x> <y> | "
        + "object ensure <alias-or-id> <qualified-item-id> | "
        + "object clear-owned <alias-or-id> | "
        + "animal ensure <alias-or-id> <animal-kind> | "
        + "enter <alias-or-id> | enter greenhouse | farm";
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
            && string.Equals(arguments[2], "ensure", StringComparison.Ordinal))
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

            if (!IsValidKindInput(arguments[4]))
            {
                error = "A fixture building kind must be one bounded non-empty token.";
                return false;
            }

            request = new ReviewFixtureBuildingEnsureRequest(
                arguments[3],
                arguments[4],
                x,
                y);
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
            && string.Equals(arguments[2], "ensure", StringComparison.Ordinal))
        {
            if (!IsValidBuildingToken(arguments[3]))
            {
                error = BuildingError;
                return false;
            }

            if (!IsValidKindInput(arguments[4]))
            {
                error = "A fixture animal kind must be one bounded non-empty token.";
                return false;
            }

            request = new ReviewFixtureAnimalEnsureRequest(arguments[3], arguments[4]);
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

    public static bool IsGreenhouseNavigationTarget(string? value) =>
        string.Equals(value, ReviewFixtureContract.GreenhouseTarget, StringComparison.Ordinal);

    private static bool IsValidKindInput(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !value.Any(char.IsControl);

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

internal sealed record ReviewFixtureKindResolution(
    string CanonicalId,
    string CanonicalToken);

internal static class ReviewFixtureKindResolver
{
    private const int CandidateLimit = 5;

    public static bool TryResolve(
        string input,
        IEnumerable<string> canonicalIds,
        string kindDescription,
        out ReviewFixtureKindResolution? resolution,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(canonicalIds);
        if (string.IsNullOrWhiteSpace(kindDescription))
        {
            throw new ArgumentException(
                "A kind description is required.",
                nameof(kindDescription));
        }

        resolution = null;
        string normalizedInput = Normalize(input);
        if (normalizedInput.Length == 0)
        {
            error = $"The {kindDescription} kind '{input}' has no stable token characters.";
            return false;
        }

        (string CanonicalId, string Token)[] candidates = canonicalIds
            .Where(id => !string.IsNullOrWhiteSpace(id)
                && id.Length <= 128
                && !id.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal)
            .Select(id => (CanonicalId: id, Token: Normalize(id)))
            .Where(candidate => candidate.Token.Length > 0)
            .ToArray();
        (string CanonicalId, string Token)[] matches = candidates
            .Where(candidate => string.Equals(
                candidate.Token,
                normalizedInput,
                StringComparison.Ordinal))
            .Take(CandidateLimit + 1)
            .ToArray();
        if (matches.Length == 1)
        {
            resolution = new ReviewFixtureKindResolution(
                matches[0].CanonicalId,
                matches[0].Token);
            error = string.Empty;
            return true;
        }

        if (matches.Length > 1)
        {
            error = $"The {kindDescription} kind '{input}' is ambiguous: "
                + string.Join(
                    ", ",
                    matches.Take(CandidateLimit).Select(DescribeCandidate))
                + ".";
            return false;
        }

        string[] suggestions = candidates
            .OrderBy(candidate => EditDistance(normalizedInput, candidate.Token))
            .ThenBy(candidate => candidate.Token, StringComparer.Ordinal)
            .Take(CandidateLimit)
            .Select(DescribeCandidate)
            .ToArray();
        error = $"Stardew's loaded data has no unambiguous {kindDescription} kind '{input}'."
            + (suggestions.Length == 0
                ? string.Empty
                : $" Canonical candidates: {string.Join(", ", suggestions)}.");
        return false;
    }

    public static string Normalize(string value) =>
        StableIdentityNormalizer.Normalize(value);

    private static string DescribeCandidate((string CanonicalId, string Token) candidate) =>
        string.Equals(candidate.CanonicalId, candidate.Token, StringComparison.Ordinal)
            ? candidate.Token
            : $"{candidate.Token} ('{candidate.CanonicalId}')";

    private static int EditDistance(string left, string right)
    {
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                int substitution = previous[rightIndex - 1]
                    + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

internal sealed record ReviewFixtureAccess(
    bool Succeeded,
    bool CanMutate,
    string? FixtureId,
    string? Role,
    string Message,
    string? LaunchId = null,
    string? Topology = null,
    string? SaveId = null);

internal sealed record ReviewFixtureExecution(
    ReviewFixtureAccess Access,
    ReviewFixtureResult Result,
    ReviewFixtureProblem? Problem = null);

internal sealed record ReviewFixtureResult(
    bool Succeeded,
    string Message,
    ReviewFixtureStatusReport? Status = null,
    ReviewFixtureNavigationReport? Navigation = null,
    ReviewFixtureBuildingReport? Building = null,
    ReviewFixtureAnimalReport? Animal = null);

internal interface IReviewFixtureRuntime
{
    ReviewFixtureAccess VerifyExactReviewFixture();

    ReviewFixtureResult Status(ReviewFixtureAccess access);

    ReviewFixtureResult EnsureBuilding(
        ReviewFixtureAccess access,
        string alias,
        string kind,
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
        string building,
        string kind);

    ReviewFixtureResult Enter(
        ReviewFixtureAccess access,
        string building);

    ReviewFixtureResult Farm(ReviewFixtureAccess access);
}

internal static class ReviewFixtureOperation
{
    public static ReviewFixtureResult Execute(
        ReviewFixtureRequest request,
        IReviewFixtureRuntime runtime) =>
        ExecuteBound(request, runtime, expected: null).Result;

    public static ReviewFixtureExecution ExecuteBound(
        ReviewFixtureRequest request,
        IReviewFixtureRuntime runtime,
        ReviewFixtureRequestBinding? expected)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        ReviewFixtureExecution verification = VerifyBound(
            request.RequiresMainPlayer,
            runtime,
            expected);
        if (!verification.Result.Succeeded)
        {
            return verification;
        }

        ReviewFixtureAccess access = verification.Access;
        ReviewFixtureResult result = request switch
        {
            ReviewFixtureStatusRequest => runtime.Status(access),
            ReviewFixtureBuildingEnsureRequest building => runtime.EnsureBuilding(
                access,
                building.Alias,
                building.Kind,
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
                animal.Building,
                animal.Kind),
            ReviewFixtureEnterRequest enter => runtime.Enter(access, enter.Building),
            ReviewFixtureFarmRequest => runtime.Farm(access),
            _ => new ReviewFixtureResult(false, ReviewFixtureArguments.Usage),
        };
        return new ReviewFixtureExecution(access, result);
    }

    public static ReviewFixtureExecution VerifyBound(
        bool requiresMainPlayer,
        IReviewFixtureRuntime runtime,
        ReviewFixtureRequestBinding? expected)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        ReviewFixtureAccess access = runtime.VerifyExactReviewFixture();
        if (!access.Succeeded)
        {
            return Failed(access, access.Message);
        }

        if (string.IsNullOrWhiteSpace(access.FixtureId)
            || string.IsNullOrWhiteSpace(access.Role))
        {
            return Failed(
                access,
                "The freshly verified review fixture returned an incomplete identity.");
        }

        if (expected is not null && !MatchesBinding(access, expected))
        {
            const string message =
                "The live review fixture identity changed after preflight; no fixture action was run.";
            return Failed(
                access,
                message,
                new ReviewFixtureProblem("fixtureBindingChanged", message));
        }

        if (requiresMainPlayer && !access.CanMutate)
        {
            return Failed(
                access,
                "Only the freshly verified main player or network-2 host may mutate the fixture.");
        }

        return new ReviewFixtureExecution(
            access,
            new ReviewFixtureResult(true, access.Message));
    }

    private static bool MatchesBinding(
        ReviewFixtureAccess access,
        ReviewFixtureRequestBinding expected) =>
        string.Equals(access.LaunchId, expected.LaunchId, StringComparison.Ordinal)
        && string.Equals(access.Topology, expected.Topology, StringComparison.Ordinal)
        && string.Equals(
            string.Equals(access.Role, ReviewFixtureTransportContract.SingleRoleToken, StringComparison.Ordinal)
                ? null
                : access.Role,
            expected.Role,
            StringComparison.Ordinal)
        && string.Equals(access.FixtureId, expected.FixtureId, StringComparison.Ordinal)
        && string.Equals(access.SaveId, expected.SaveId, StringComparison.Ordinal);

    private static ReviewFixtureExecution Failed(
        ReviewFixtureAccess access,
        string message,
        ReviewFixtureProblem? problem = null) =>
        new(access, new ReviewFixtureResult(false, message), problem);
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

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewFixtureCommand
{
    public static void Handle(
        string[] arguments,
        IReviewFixtureRuntime runtime,
        IMonitor monitor,
        string? runtimePath = null,
        Func<TestSaveAutomation?>? testSave = null)
    {
        if (arguments.Length > 1
            && ReviewTransportToken.IsRequestId(arguments[1]))
        {
            if (string.IsNullOrWhiteSpace(runtimePath) || testSave is null)
            {
                monitor.Log(
                    "SDVKit review-fixture transport is unavailable.",
                    LogLevel.Error);
                return;
            }

            ReviewFixtureTransportCommand.Handle(
                arguments,
                runtime,
                testSave,
                runtimePath,
                monitor);
            return;
        }

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

internal static class ReviewFixtureTransportCommand
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Handle(
        string[] arguments,
        IReviewFixtureRuntime runtime,
        Func<TestSaveAutomation?> testSave,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(testSave);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-fixture runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        _ = ReviewFixtureTransportArguments.TryParse(
            arguments,
            out string? requestId,
            out ReviewFixtureRequestBinding? binding,
            out ReviewFixtureQuery? query,
            out ReviewFixtureRequest? request,
            out ReviewFixtureProblem? parseProblem);
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log(
                "SDVKit review-fixture rejected an invalid request ID.",
                LogLevel.Error);
            return;
        }

        if (binding is null)
        {
            monitor.Log(
                "SDVKit review-fixture rejected an invalid identity binding.",
                LogLevel.Error);
            return;
        }

        string operation = query?.Operation
            ?? (arguments.Length > 7 ? arguments[7] : "unknown");
        if (parseProblem is not null)
        {
            ReviewFixtureAccess parseAccess = runtime.VerifyExactReviewFixture();
            Publish(
                runtimePath,
                requestId!,
                binding,
                operation,
                parseAccess,
                new ReviewFixtureResult(false, parseProblem.Message),
                [parseProblem],
                save: null,
                monitor);
            return;
        }

        if (operation == ReviewFixtureTransportContract.SaveOperation)
        {
            ReviewFixtureExecution verification = ReviewFixtureOperation.VerifyBound(
                requiresMainPlayer: true,
                runtime,
                binding);
            ReviewFixtureAccess access = verification.Access;
            if (!verification.Result.Succeeded)
            {
                ReviewFixtureProblem problem = verification.Problem
                    ?? new ReviewFixtureProblem(
                        "fixtureIdentityRejected",
                        verification.Result.Message);
                Publish(
                    runtimePath,
                    requestId!,
                    binding,
                    operation,
                    access,
                    verification.Result,
                    [problem],
                    save: null,
                    monitor);
                return;
            }

            TestSaveAutomation? automation = testSave();
            string reason =
                "The exact disposable test-save automation is unavailable.";
            bool started = automation is not null
                && automation.TryStartReviewSave(
                    (succeeded, message) => Publish(
                        runtimePath,
                        requestId!,
                        binding,
                        operation,
                        access,
                        new ReviewFixtureResult(succeeded, message),
                        succeeded
                            ? []
                            : [new ReviewFixtureProblem("fixtureSaveFailed", message)],
                        succeeded
                            ? new ReviewFixtureSaveReport(
                                Constants.SaveFolderName!,
                                DateTimeOffset.UtcNow)
                            : null,
                        monitor),
                    out reason);
            if (!started)
            {
                string message = automation is null
                    ? "The exact disposable test-save automation is unavailable."
                    : reason;
                Publish(
                    runtimePath,
                    requestId!,
                    binding,
                    operation,
                    access,
                    new ReviewFixtureResult(false, message),
                    [new ReviewFixtureProblem("fixtureSaveRejected", message)],
                    save: null,
                    monitor);
            }

            return;
        }

        ReviewFixtureExecution execution;
        try
        {
            execution = ReviewFixtureOperation.ExecuteBound(request!, runtime, binding);
        }
        catch (Exception exception)
        {
            ReviewFixtureAccess failedAccess = runtime.VerifyExactReviewFixture();
            execution = new ReviewFixtureExecution(
                failedAccess,
                new ReviewFixtureResult(
                    false,
                    $"The fixture action failed closed ({exception.GetType().Name})."));
        }

        ReviewFixtureResult result = execution.Result;
        Publish(
            runtimePath,
            requestId!,
            binding,
            operation,
            execution.Access,
            result,
            result.Succeeded
                ? []
                : [execution.Problem
                    ?? new ReviewFixtureProblem("fixtureActionRejected", result.Message)],
            save: null,
            monitor);
    }

    private static void Publish(
        string runtimePath,
        string requestId,
        ReviewFixtureRequestBinding binding,
        string operation,
        ReviewFixtureAccess access,
        ReviewFixtureResult result,
        IReadOnlyList<ReviewFixtureProblem> problems,
        ReviewFixtureSaveReport? save,
        IMonitor monitor)
    {
        string? networkRole = Environment
            .GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE")
            ?.Trim();
        bool network = networkRole is not null
            && NetworkTwoContract.IsRole(networkRole);
        string topology = network
            ? NetworkTwoContract.Topology
            : "single";
        string? role = network ? networkRole : null;
        string launchId = access.LaunchId
            ?? Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")?.Trim()
            ?? string.Empty;
        topology = access.Topology ?? topology;
        role = string.Equals(
                access.Role,
                ReviewFixtureTransportContract.SingleRoleToken,
                StringComparison.Ordinal)
            ? null
            : access.Role ?? role;
        var report = new ReviewFixtureReport(
            ReviewFixtureTransportContract.SchemaVersion,
            result.Succeeded ? "ready" : "blocked",
            operation,
            launchId,
            topology,
            role,
            DateTimeOffset.UtcNow,
            access.FixtureId,
            access.SaveId,
            result.Message,
            problems,
            result.Status,
            result.Navigation,
            result.Building,
            result.Animal,
            save);
        var envelope = new ReviewFixtureResponseEnvelope(
            ReviewFixtureTransportContract.SchemaVersion,
            requestId,
            binding,
            report);
        try
        {
            WriteResponse(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-fixture completed '{operation}' with state '{report.State}'.",
                result.Succeeded ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit review-fixture could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

    private static void WriteResponse(
        string runtimePath,
        ReviewFixtureResponseEnvelope envelope)
    {
        string absoluteRuntimePath = Path.GetFullPath(runtimePath);
        FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
        if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
            || (runtimeAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The review runtime response root is not a regular directory.");
        }

        string responsePath = ReviewFixtureTransportContract.ResponsePath(
            absoluteRuntimePath,
            envelope.RequestId);
        string temporaryPath = responsePath + ".tmp";
        if (File.Exists(responsePath) || File.Exists(temporaryPath))
        {
            throw new InvalidDataException(
                "The review-fixture response target already exists.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ResponseJsonOptions);
        if (bytes.Length > ReviewFixtureTransportContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-fixture response exceeds its maximum size.");
        }

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, responsePath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

internal sealed class StardewReviewFixtureRuntime(
    Func<TestSaveAutomation?> testSave,
    Func<NetworkTwoAutomation?> networkTwo,
    Func<long> getNewMultiplayerId) : IReviewFixtureRuntime
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
            "The exact live single-player review fixture was freshly verified.",
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

    public ReviewFixtureResult Enter(
        ReviewFixtureAccess access,
        string building)
    {
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

        Game1.warpFarmer(indoors.NameOrUniqueName, (int)entry.X, (int)entry.Y, false);
        return SuccessNavigation(
            $"Warped through the natural entry of {targetDescription} at {entry.X},{entry.Y}.",
            indoors.NameOrUniqueName,
            (int)entry.X,
            (int)entry.Y,
            changed: true);
    }

    public ReviewFixtureResult Farm(ReviewFixtureAccess access)
    {
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

        Game1.warpFarmer("Farm", exit.TargetX, exit.TargetY, false);
        string sourceDescription = parent is not null
            ? $"fixture building {parent.id.Value:D}"
            : greenhouse is not null
                ? $"Greenhouse {greenhouse.id.Value:D}"
                : $"review FarmHouse '{current.NameOrUniqueName}'";
        return SuccessNavigation(
            $"Warped through the natural Farm exit of {sourceDescription} at {target.X},{target.Y}.",
            farm.NameOrUniqueName,
            (int)target.X,
            (int)target.Y,
            changed: true);
    }

    private static ReviewFixtureAccess Denied(string message) =>
        new(false, false, null, null, message);

    private static ReviewFixtureResult Success(string message) => new(true, message);

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
