#if SDVKIT_GAME_AVAILABLE
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Xna.Framework;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewObject = StardewValley.Object;

namespace SdvKit.AlwaysOn;

internal static class ReviewWorldCommand
{
    private sealed class InstanceIdentity
    {
        internal string Value { get; } = Guid.NewGuid().ToString("N");
    }

    private static readonly ConditionalWeakTable<object, InstanceIdentity> Identities = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void Handle(string[] args, string runtimePath, IMonitor monitor)
    {
        if (args.Length != 7 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2])
            || !int.TryParse(args[3], out int x) || !int.TryParse(args[4], out int y)
            || !int.TryParse(args[5], out int width) || !int.TryParse(args[6], out int height))
        {
            monitor.Log("SDVKit review-world rejected an invalid request.", LogLevel.Error);
            return;
        }
        var area = new ReviewWorldArea(x, y, width, height);
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        ReviewWorldReport Failure(string code) => new(1, "unavailable", code, launch,
            "single", null, DateTimeOffset.UtcNow, null);
        ReviewWorldReport report;
        try
        {
            string? queryProblem = ReviewWorldContract.QueryProblem(area);
            report = Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1" || args[2] != launch
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
                    ? Failure("worldReviewBindingInvalid")
                    : queryProblem is not null ? Failure(queryProblem)
                    : !Context.IsWorldReady || Game1.exitToTitle || Game1.currentLocation is null
                        ? Failure("worldNotReady")
                        : Capture(launch, area);
        }
        catch (Exception)
        {
            report = Failure("worldCaptureFailed");
        }
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new ReviewWorldResponseEnvelope(1, args[1], report), JsonOptions);
            if (bytes.Length > ReviewWorldContract.MaximumResponseBytes)
                bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ReviewWorldResponseEnvelope(1, args[1], Failure("worldResponseLimit")), JsonOptions);
            ReviewResponseFile.Write(ReviewWorldContract.ResponsePath(runtimePath, args[1]), bytes);
        }
        catch (Exception)
        {
            monitor.Log("SDVKit review-world could not publish its bounded response.", LogLevel.Error);
        }
    }

    private static ReviewWorldReport Capture(string launch, ReviewWorldArea area)
    {
        ReviewWorldReport Failure(string code) => new(1, "unavailable", code, launch,
            "single", null, DateTimeOffset.UtcNow, null);
        GameLocation location = Game1.currentLocation;
        string locationName = location.NameOrUniqueName;
        if (string.IsNullOrWhiteSpace(locationName) || locationName.Length > ReviewWorldContract.MaximumLocationLength)
            return Failure("worldLocationUnavailable");
        if (location.Map?.GetLayer("Back") is not { } back || back.LayerWidth < 1 || back.LayerHeight < 1)
            return Failure("worldMapUnavailable");
        if (area.X < 0 || area.Y < 0 || (long)area.X + area.Width > back.LayerWidth
            || (long)area.Y + area.Height > back.LayerHeight)
            return Failure("worldAreaOutsideMap");
        var tiles = new List<ReviewWorldTile>(area.Width * area.Height);
        for (int tileY = area.Y; tileY < area.Y + area.Height; tileY++)
        {
            for (int tileX = area.X; tileX < area.X + area.Width; tileX++)
            {
                var tile = new Vector2(tileX, tileY);
                ReviewWorldSoil? soil = location.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature)
                    && feature is HoeDirt dirt ? ReadSoil(dirt) : null;
                tiles.Add(ReadObject(location, tile, tileX, tileY, soil));
            }
        }
        var values = new ReviewWorldValues(locationName, Identity(location),
            Game1.player.UniqueMultiplayerID.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Game1.ticks, area, true, tiles.AsReadOnly());
        return ReviewWorldContract.DataValid(values)
            ? new(1, "ready", null, launch, "single", null, DateTimeOffset.UtcNow, values)
            : Failure("worldValuesInvalid");
    }

    private static ReviewWorldTile ReadObject(GameLocation location, Vector2 tile, int x, int y,
        ReviewWorldSoil? soil)
    {
        if (!location.Objects.TryGetValue(tile, out StardewObject? obj) || obj is null)
            return new(x, y, soil, "missing", null, null, null);
        string? qualifiedId = SafeQualifiedItemId(obj);
        try
        {
            if (!ReviewWorldContract.QualifiedItemIdValid(qualifiedId))
                return new(x, y, soil, "unavailable", "worldObjectPropertiesUnavailable", null, null);
            if (obj.GetType() != typeof(StardewObject) || !obj.bigCraftable.Value
                || obj.GetMachineData() is null)
                return new(x, y, soil, "unsupported", "worldObjectFamilyUnsupported",
                    qualifiedId, null);
            ReviewWorldItemObservation output = ReadItem(obj.heldObject.Value);
            bool hasOutput = output.State != "absent";
            bool ready = obj.readyForHarvest.Value;
            int minutes = obj.MinutesUntilReady;
            string? state = !hasOutput && !ready && minutes is >= -1 and <= 0 ? "idle"
                : hasOutput && !ready && minutes > 0 ? "processing"
                : hasOutput && ready && minutes is >= -1 and <= 0 ? "ready"
                : null;
            if (state is null || minutes > ReviewWorldContract.MaximumMachineMinutes)
                return new(x, y, soil, "unavailable", "worldObjectPropertiesUnavailable", qualifiedId, null);
            ReviewWorldItemObservation input = ReadItem(obj.lastInputItem.Value);
            string instance = Identity(obj);
            string revision = Revision(instance, qualifiedId!, state,
                ready.ToString(), minutes.ToString(CultureInfo.InvariantCulture),
                ItemRevision(input), ItemRevision(output));
            var machine = new ReviewWorldMachine(instance, revision, qualifiedId!, state, ready, minutes, input, output);
            return new(x, y, soil, "machine", null, qualifiedId, machine);
        }
        catch (Exception)
        {
            return new(x, y, soil, "unavailable", "worldObjectPropertiesUnavailable",
                ReviewWorldContract.QualifiedItemIdValid(qualifiedId) ? qualifiedId : null, null);
        }
    }

    private static ReviewWorldSoil ReadSoil(HoeDirt dirt)
    {
        string instance = Identity(dirt);
        string? fertilizer = !dirt.HasFertilizer()
            ? null : QualifyObject(dirt.fertilizer.Value)
                ?? throw new InvalidOperationException("The fertilizer item identity is unavailable.");
        ReviewWorldCropObservation crop = ReadCrop(dirt.crop, dirt);
        string revision = Revision(instance, dirt.isWatered().ToString(), dirt.needsWatering().ToString(),
            fertilizer ?? "", crop.Data?.Revision ?? crop.State);
        return new(instance, revision, dirt.isWatered(), dirt.needsWatering(), fertilizer, crop);
    }

    private static ReviewWorldCropObservation ReadCrop(Crop? crop, HoeDirt dirt)
    {
        if (crop is null) return new("missing", null, null);
        if (crop.GetType() != typeof(Crop) || crop.forageCrop.Value)
            return new("unsupported", "worldCropFamilyUnsupported", null);
        string instance = Identity(crop);
        string? seed = QualifyObject(crop.netSeedIndex.Value);
        string? harvest = QualifyObject(crop.indexOfHarvest.Value);
        int phaseCount = crop.phaseDays.Count;
        bool ready = dirt.readyForHarvest();
        bool regrows = crop.RegrowsAfterHarvest();
        if (seed is null || harvest is null || phaseCount is < 1 or > ReviewWorldContract.MaximumPhaseCount
            || crop.currentPhase.Value < 0 || crop.currentPhase.Value >= phaseCount
            || crop.dayOfCurrentPhase.Value < 0)
            return new("unavailable", "worldCropPropertiesUnavailable", null);
        string revision = Revision(instance, seed, harvest,
            crop.currentPhase.Value.ToString(CultureInfo.InvariantCulture),
            crop.dayOfCurrentPhase.Value.ToString(CultureInfo.InvariantCulture),
            phaseCount.ToString(CultureInfo.InvariantCulture), crop.fullyGrown.Value.ToString(),
            crop.dead.Value.ToString(), ready.ToString(), regrows.ToString());
        return new("available", null, new(instance, revision, seed, harvest, crop.currentPhase.Value,
            crop.dayOfCurrentPhase.Value, phaseCount, crop.fullyGrown.Value, crop.dead.Value, ready, regrows));
    }

    private static ReviewWorldItemObservation ReadItem(Item? item)
    {
        if (item is null) return new("absent", null, null);
        try
        {
            var facts = new SelectedItemValues(item.QualifiedItemId, item.Stack,
                item is StardewObject obj ? obj.Quality : null);
            return ReviewItemSlotContract.ItemValid(facts)
                ? new("available", null, facts)
                : new("unavailable", "worldItemFactsUnavailable", null);
        }
        catch (Exception)
        {
            return new("unavailable", "worldItemFactsUnavailable", null);
        }
    }

    private static string? SafeQualifiedItemId(Item item)
    {
        try { return item.QualifiedItemId; }
        catch (Exception) { return null; }
    }

    private static string? QualifyObject(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        string qualified = itemId.StartsWith('(') ? itemId : $"(O){itemId}";
        return ReviewWorldContract.QualifiedItemIdValid(qualified) ? qualified : null;
    }

    private static string Identity(object value) => Identities.GetValue(value, _ => new InstanceIdentity()).Value;

    private static string ItemRevision(ReviewWorldItemObservation value) => value.Item is null
        ? value.State + ":" + value.Reason
        : $"{value.State}:{value.Item.QualifiedItemId}:{value.Item.Stack}:{value.Item.Quality}";

    private static string Revision(params string[] values)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', values)));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
#endif
