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
    public const string SingleRoleToken = "single";

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
        or SaveOperation;
}

internal sealed record ReviewFixtureQuery(
    string Operation,
    string? Building = null,
    string? Alias = null,
    string? Kind = null,
    int? X = null,
    int? Y = null);

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
    bool CancellationRequested = false);

internal sealed record ReviewFixtureResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    ReviewFixtureRequestBinding Binding,
    ReviewFixtureReport Report);
