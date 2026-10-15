using System.Globalization;
using System.Text.Json.Serialization;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewWorldContract
{
    public const int MaximumSide = 32;
    public const int MaximumTiles = 256;
    public const int MaximumResponseBytes = 192 * 1024;
    public const int MaximumLocationLength = 128;
    public const int MaximumPhaseCount = 100;
    public const int MaximumMachineMinutes = 1_000_000;
    public const int MaximumTileCoordinate = 100_000;
    // Keeps a full 16x16 read within the response limit; a longer (modded) ID makes only its tile "unavailable".
    public const int MaximumFeatureIdLength = 64;

    /// <summary>Feature families named by an <c>unavailable</c> feature whose facts could not be read.</summary>
    public static readonly IReadOnlyList<string> FeatureFamilies =
        ["tree", "fruitTree", "grass", "bush", "resourceClump", "giantCrop", "furniture", "unknown"];
    public const int MaximumFeatureGrowthStage = 1_000_000;
    public const float MinimumFeatureHealth = -100;
    public const float MaximumFeatureHealth = 1_000_000;

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-world-{requestId}.json");

    public static string? QueryProblem(ReviewWorldArea? area)
    {
        if (area is null || Math.Abs((long)area.X) > MaximumTileCoordinate
            || Math.Abs((long)area.Y) > MaximumTileCoordinate)
            return "worldAreaInvalid";
        if (area.Width is < 1 or > MaximumSide || area.Height is < 1 or > MaximumSide
            || (long)area.Width * area.Height > MaximumTiles)
            return "worldAreaLimit";
        if ((long)area.X + area.Width - 1 > MaximumTileCoordinate
            || (long)area.Y + area.Height - 1 > MaximumTileCoordinate)
            return "worldAreaInvalid";
        return null;
    }

    public static bool QualifiedItemIdValid(string? value) => value is { Length: > 3 and <= 256 }
        && value[0] == '(' && value.IndexOf(')') is > 1 && value[^1] != ')'
        && !value.Any(char.IsControl);

    public static bool ItemObservationValid(ReviewWorldItemObservation? observation) => observation is not null
        && (observation.State == "absent" && observation.Reason is null && observation.Item is null
            || observation.State == "available" && observation.Reason is null
                && ReviewItemSlotContract.ItemValid(observation.Item)
            || observation.State == "unavailable" && observation.Reason == "worldItemFactsUnavailable"
                && observation.Item is null);

    public static bool DataValid(ReviewWorldValues? data)
    {
        if (data is null || QueryProblem(data.Area) is not null || !data.Complete
            || !TextValid(data.LocationName, MaximumLocationLength)
            || !ReviewTransportToken.IsRequestId(data.LocationInstanceId)
            || !long.TryParse(data.PlayerId, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out long playerId) || playerId == 0
            || data.PlayerId != playerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || data.CaptureTick < 0 || data.Tiles is null
            || data.Tiles.Count != data.Area.Width * data.Area.Height)
            return false;

        for (var index = 0; index < data.Tiles.Count; index++)
        {
            ReviewWorldTile tile = data.Tiles[index];
            int expectedX = data.Area.X + index % data.Area.Width;
            int expectedY = data.Area.Y + index / data.Area.Width;
            if (tile is null || tile.X != expectedX || tile.Y != expectedY || !SoilValid(tile.Soil)
                || !ObjectValid(tile) || !FeatureValid(tile.Feature))
                return false;
            if (tile.Feature is { State: { } state } && (state.OriginX > tile.X || state.OriginY > tile.Y))
                return false;
        }
        return true;
    }

    internal static bool FeatureValid(ReviewWorldFeature? feature)
    {
        if (feature is null) return true;
        if (!TextValid(feature.Id, MaximumFeatureIdLength) || feature.State is not { } state)
            return false;
        bool health = state.Health is { } value && float.IsFinite(value)
            && value is >= MinimumFeatureHealth and <= MaximumFeatureHealth;
        bool origin = state.OriginX is >= -MaximumTileCoordinate and <= MaximumTileCoordinate
            && state.OriginY is >= -MaximumTileCoordinate and <= MaximumTileCoordinate;
        bool noOrigin = state.OriginX is null && state.OriginY is null;
        bool noGrowth = state.GrowthStage is null && state.Stump is null;
        bool numericId = int.TryParse(feature.Id, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            && id >= 0 && feature.Id == id.ToString(CultureInfo.InvariantCulture);
        return feature.Kind switch
        {
            "tree" => state.GrowthStage is >= 0 and <= MaximumFeatureGrowthStage
                && state.Stump is not null && health && state.NumberOfWeeds is null && noOrigin,
            "fruitTree" => state.GrowthStage is >= 0 and <= 4
                && state.Stump is not null && health && state.NumberOfWeeds is null && noOrigin,
            "grass" => numericId && id <= byte.MaxValue && state.NumberOfWeeds is >= 0 and <= 4
                && noGrowth && state.Health is null && noOrigin,
            "bush" => numericId && id <= 4 && noGrowth && health && state.NumberOfWeeds is null && origin,
            "resourceClump" => numericId && noGrowth && health && state.NumberOfWeeds is null && origin,
            "giantCrop" => noGrowth && health && state.NumberOfWeeds is null && origin,
            "unavailable" => FeatureFamilies.Contains(feature.Id) && noGrowth && state.Health is null
                && state.NumberOfWeeds is null && noOrigin,
            "furniture" => QualifiedItemIdValid(feature.Id) && feature.Id!.StartsWith("(F)", StringComparison.Ordinal)
                && noGrowth && state.Health is null && state.NumberOfWeeds is null && origin,
            _ => false,
        };
    }

    private static bool SoilValid(ReviewWorldSoil? soil)
    {
        if (soil is null) return true;
        if (!ReviewTransportToken.IsRequestId(soil.InstanceId)
            || !ReviewTransportToken.IsRequestId(soil.Revision)
            || soil.FertilizerItemId is not null && !QualifiedItemIdValid(soil.FertilizerItemId))
            return false;
        ReviewWorldCropObservation cropObservation = soil.Crop;
        if (cropObservation is null) return false;
        if (cropObservation.State == "missing")
            return cropObservation.Reason is null && cropObservation.Data is null;
        if (cropObservation.State == "unsupported")
            return cropObservation.Reason == "worldCropFamilyUnsupported" && cropObservation.Data is null;
        if (cropObservation.State == "unavailable")
            return cropObservation.Reason == "worldCropPropertiesUnavailable" && cropObservation.Data is null;
        if (cropObservation.State != "available" || cropObservation.Reason is not null
            || cropObservation.Data is not { } crop) return false;
        return ReviewTransportToken.IsRequestId(crop.InstanceId)
            && ReviewTransportToken.IsRequestId(crop.Revision)
            && QualifiedItemIdValid(crop.SeedItemId)
            && QualifiedItemIdValid(crop.HarvestItemId)
            && crop.PhaseCount is >= 1 and <= MaximumPhaseCount
            && crop.CurrentPhase >= 0 && crop.CurrentPhase < crop.PhaseCount
            && crop.DayOfCurrentPhase >= 0
            && (!crop.Dead || !crop.ReadyForHarvest);
    }

    private static bool ObjectValid(ReviewWorldTile tile)
    {
        if (tile.ObjectState == "missing")
            return tile.ObjectReason is null && tile.ObjectQualifiedItemId is null && tile.Machine is null;
        if (tile.ObjectState == "unsupported")
            return tile.ObjectReason == "worldObjectFamilyUnsupported"
                && QualifiedItemIdValid(tile.ObjectQualifiedItemId) && tile.Machine is null;
        if (tile.ObjectState == "unavailable")
            return tile.ObjectReason == "worldObjectPropertiesUnavailable"
                && (tile.ObjectQualifiedItemId is null || QualifiedItemIdValid(tile.ObjectQualifiedItemId))
                && tile.Machine is null;
        if (tile.ObjectState != "machine" || tile.ObjectReason is not null
            || !QualifiedItemIdValid(tile.ObjectQualifiedItemId) || tile.Machine is null)
            return false;
        ReviewWorldMachine machine = tile.Machine;
        if (!ReviewTransportToken.IsRequestId(machine.InstanceId)
            || !ReviewTransportToken.IsRequestId(machine.Revision)
            || machine.QualifiedItemId != tile.ObjectQualifiedItemId
            || !ItemObservationValid(machine.Input) || !ItemObservationValid(machine.Output))
            return false;
        if (machine.MinutesUntilReady is < -1 or > MaximumMachineMinutes) return false;
        return machine.State switch
        {
            "idle" => !machine.ReadyForHarvest && machine.MinutesUntilReady is >= -1 and <= 0
                && machine.Output!.State == "absent",
            "processing" => !machine.ReadyForHarvest && machine.MinutesUntilReady > 0
                && machine.Output!.State != "absent",
            "ready" => machine.ReadyForHarvest && machine.MinutesUntilReady is >= -1 and <= 0
                && machine.Output!.State != "absent",
            _ => false,
        };
    }

    private static bool TextValid(string? value, int maximum) => value is { Length: > 0 }
        && value.Length <= maximum && !value.Any(char.IsControl);
}

internal sealed record ReviewWorldArea(int X, int Y, int Width, int Height);
internal sealed record ReviewWorldItemObservation(string State, string? Reason, SelectedItemValues? Item);
internal sealed record ReviewWorldCrop(string InstanceId, string Revision, string SeedItemId,
    string HarvestItemId, int CurrentPhase, int DayOfCurrentPhase, int PhaseCount, bool FullyGrown,
    bool Dead, bool ReadyForHarvest, bool RegrowsAfterHarvest);
internal sealed record ReviewWorldCropObservation(string State, string? Reason, ReviewWorldCrop? Data);
internal sealed record ReviewWorldSoil(string InstanceId, string Revision, bool Watered,
    bool NeedsWatering, string? FertilizerItemId, ReviewWorldCropObservation Crop);
internal sealed record ReviewWorldMachine(string InstanceId, string Revision, string QualifiedItemId,
    string State, bool ReadyForHarvest, int MinutesUntilReady, ReviewWorldItemObservation Input,
    ReviewWorldItemObservation Output);
internal sealed record ReviewWorldTile(int X, int Y, ReviewWorldSoil? Soil, string ObjectState,
    string? ObjectReason, string? ObjectQualifiedItemId, ReviewWorldMachine? Machine,
    ReviewWorldFeature? Feature = null);
internal sealed record ReviewWorldFeature(string Kind, string Id, ReviewWorldFeatureState State);
internal sealed record ReviewWorldFeatureState(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GrowthStage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Stump = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Health = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NumberOfWeeds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OriginX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OriginY = null);
internal sealed record ReviewWorldValues(string LocationName, string LocationInstanceId,
    string PlayerId, int CaptureTick, ReviewWorldArea Area, bool Complete,
    IReadOnlyList<ReviewWorldTile> Tiles);
internal sealed record ReviewWorldReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset CapturedAtUtc,
    ReviewWorldValues? Data);
internal sealed record ReviewWorldResponseEnvelope(int SchemaVersion, string RequestId,
    ReviewWorldReport Report);
