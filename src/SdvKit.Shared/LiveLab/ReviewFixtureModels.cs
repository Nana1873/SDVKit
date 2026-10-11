using System.Text.Json.Serialization;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewFixtureTransportContract
{
    public const int SchemaVersion = 1;
    public const int MaximumResponseBytes = 256 * 1024;
    public const int MaximumTokenLength = 128;
    public const string StatusOperation = "status";
    public const string EnterOperation = "enter";
    public const string FarmOperation = "farm";
    public const string BuildingEnsureOperation = "buildingEnsure";
    public const string AnimalEnsureOperation = "animalEnsure";
    public const string SaveOperation = "save";
    public const string ObjectEnsureOperation = "objectEnsure";
    public const string ObjectClearOperation = "objectClear";
    public const string FishingPrepareOperation = "fishingPrepare";
    public const string SetTimeOperation = "setTime";
    public const string SetWeatherOperation = "setWeather";
    public const string SetSeasonOperation = "setSeason";
    public const string WarpOperation = "warp";
    public const string SetZoomOperation = "setZoom";
    public const string SetWindowOperation = "setWindow";
    public const string SingleRoleToken = "single";
    public const double MinimumZoom = 0.75;
    public const double MaximumZoom = 2.0;
    public const int MinimumWindowSize = 320;
    public const int MaximumWindowSize = 7680;

    public static readonly IReadOnlyList<string> Weathers = ["Sun", "Rain", "Storm", "Snow", "Wind", "GreenRain"];
    public static readonly IReadOnlyList<string> Seasons = ["spring", "summer", "fall", "winter"];

    /// <summary>Game time 6:00 to 25:50 in ten-minute steps; 26:00 is excluded because passing out saves the day.</summary>
    public static bool IsValidTime(int time) =>
        time is >= 600 and <= 2550 && time % 100 < 60 && time % 10 == 0;

    public static bool IsLabOperation(string? operation) => operation is
        SetTimeOperation
        or SetWeatherOperation
        or SetSeasonOperation
        or WarpOperation
        or SetZoomOperation
        or SetWindowOperation;

    /// <summary>Operations that change the shared world and are limited to single-player or the network-2 host.</summary>
    public static bool RequiresWorldAuthority(string? operation) => operation is
        BuildingEnsureOperation
        or AnimalEnsureOperation
        or SaveOperation
        or ObjectEnsureOperation
        or ObjectClearOperation
        or FishingPrepareOperation
        or SetTimeOperation
        or SetWeatherOperation
        or SetSeasonOperation;

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-fixture runtime path is required.",
                nameof(runtimePath));
        }
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-fixture request ID is invalid.",
                nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-fixture-{requestId}.json");
    }

    public static bool IsOperation(string? value) => value is
        StatusOperation
        or EnterOperation
        or FarmOperation
        or BuildingEnsureOperation
        or AnimalEnsureOperation
        or SaveOperation
        or ObjectEnsureOperation
        or ObjectClearOperation
        or FishingPrepareOperation
        || IsLabOperation(value);
}

internal sealed record ReviewFixtureQuery(
    string Operation,
    string? Building = null,
    string? Alias = null,
    string? Kind = null,
    int? X = null,
    int? Y = null,
    string? Value = null,
    bool SkipTutorial = false,
    double? Zoom = null);

/// <summary>World, player and window state observed right after a lab action.</summary>
internal sealed record ReviewFixtureLabReport(
    int TimeOfDay,
    string Season,
    string Weather,
    string LocationId,
    int TileX,
    int TileY,
    double Zoom,
    int WindowWidth,
    int WindowHeight);

internal sealed record ReviewFixtureRequestBinding(
    string LaunchId,
    string Topology,
    string? Role,
    string FixtureId,
    string SaveId);

internal sealed record ReviewFixtureProblem(string Code, string Message);

internal sealed record ReviewFixtureBuildingReport(
    string Alias,
    string BuildingId,
    string CanonicalKind,
    string CanonicalToken,
    int X,
    int Y,
    string? InteriorLocationId,
    string? MapAsset,
    int OwnedObjects,
    int OwnedAnimals,
    bool Changed);

internal sealed record ReviewFixtureAnimalReport(
    long AnimalId,
    string CanonicalKind,
    string CanonicalToken,
    string HomeBuildingId,
    bool Assigned,
    bool Changed);

internal sealed record ReviewFixtureNavigationReport(
    string LocationId,
    int TileX,
    int TileY,
    bool Changed);

internal sealed record ReviewFixtureStatusReport(
    string LocationId,
    long PlayerId,
    bool MainPlayer,
    bool Multiplayer,
    IReadOnlyList<ReviewFixtureBuildingReport> Buildings);

internal sealed record ReviewFixtureSaveReport(
    string SaveId,
    DateTimeOffset PersistedAtUtc);

internal sealed record ReviewFixtureReport(
    int SchemaVersion,
    string State,
    string Operation,
    string LaunchId,
    string Topology,
    string? Role,
    DateTimeOffset CompletedAtUtc,
    string? FixtureId,
    string? SaveId,
    string Message,
    IReadOnlyList<ReviewFixtureProblem> Problems,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReviewFixtureStatusReport? Status = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReviewFixtureNavigationReport? Navigation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReviewFixtureBuildingReport? Building = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReviewFixtureAnimalReport? Animal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReviewFixtureSaveReport? Save = null,
    bool CommandWritten = false,
    bool MayHaveRun = false,
    bool CancellationRequested = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReviewFixtureLabReport? Lab = null);

internal sealed record ReviewFixtureResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    ReviewFixtureRequestBinding Binding,
    ReviewFixtureReport Report);
