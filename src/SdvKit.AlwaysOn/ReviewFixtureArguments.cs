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

internal sealed record ReviewFixtureFishingRequest(bool SkipTutorial)
    : ReviewFixtureRequest(RequiresMainPlayer: true);

internal static class ReviewFixtureArguments
{
    internal const string Usage =
        "Usage: sdvkit fixture status | "
        + "building ensure <alias> <building-kind> <x> <y> | "
        + "object ensure <alias-or-id> <qualified-item-id> | "
        + "object clear-owned <alias-or-id> | "
        + "animal ensure <alias-or-id> <animal-kind> | "
        + "enter <alias-or-id> | enter greenhouse | farm | fishing prepare [--skip-tutorial]";
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

        if ((arguments.Count == 3 || arguments.Count == 4 && arguments[3] == "--skip-tutorial")
            && arguments[1] == "fishing" && arguments[2] == "prepare")
        {
            request = new ReviewFixtureFishingRequest(arguments.Count == 4);
        }
        else if (arguments.Count == 2
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
