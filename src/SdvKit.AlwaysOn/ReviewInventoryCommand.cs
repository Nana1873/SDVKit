#if SDVKIT_GAME_AVAILABLE
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewObject = StardewValley.Object;

namespace SdvKit.AlwaysOn;

internal static class ReviewInventoryCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void Handle(string[] args, string runtimePath, IMonitor monitor)
    {
        if (args.Length != 3 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]))
        {
            monitor.Log("SDVKit review-inventory rejected an invalid request.", LogLevel.Error);
            return;
        }
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        ReviewInventoryReport Failure(string code) => new(ReviewInventoryContract.SchemaVersion,
            "unavailable", code, launch, "single", null, DateTimeOffset.UtcNow, null);
        ReviewInventoryReport report;
        try
        {
            report = Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1" || args[2] != launch
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
                    ? Failure("inventoryReviewBindingInvalid")
                    : !Context.IsWorldReady || Game1.exitToTitle ? Failure("inventoryWorldNotReady")
                    : Capture(args[1], launch);
        }
        catch (Exception)
        {
            report = Failure("inventoryCaptureFailed");
        }
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new ReviewInventoryResponseEnvelope(ReviewInventoryContract.SchemaVersion, args[1], report), JsonOptions);
            if (bytes.Length > ReviewInventoryContract.MaximumResponseBytes)
                bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ReviewInventoryResponseEnvelope(ReviewInventoryContract.SchemaVersion, args[1],
                        Failure("inventoryResponseLimit")), JsonOptions);
            ReviewResponseFile.Write(ReviewInventoryContract.ResponsePath(runtimePath, args[1]), bytes);
        }
        catch (Exception)
        {
            monitor.Log("SDVKit review-inventory could not publish its bounded response.", LogLevel.Error);
        }
    }

    private static ReviewInventoryReport Capture(string captureId, string launch)
    {
        ReviewInventoryReport Failure(string code) => new(ReviewInventoryContract.SchemaVersion,
            "unavailable", code, launch, "single", null, DateTimeOffset.UtcNow, null);
        Farmer player = Game1.player;
        int capacity = player.Items.Count;
        int rawSelectedSlot = player.CurrentToolIndex;
        int? selectedSlot = rawSelectedSlot < 0 ? null : rawSelectedSlot;
        if (capacity is <= 0 or > ReviewInventoryContract.MaximumSlots
            || selectedSlot >= capacity) return Failure("inventoryCapacityUnsupported");

        var slots = new List<ReviewItemSlot>(capacity);
        for (int index = 0; index < capacity; index++)
        {
            Item? item = player.Items[index];
            if (item is null)
            {
                slots.Add(new(index, "empty", null, null));
                continue;
            }
            slots.Add(ReviewItemSlotContract.ReadOccupied(index, () => new SelectedItemValues(
                item.QualifiedItemId,
                item.Stack,
                item is StardewObject obj ? obj.Quality : null)));
        }
        bool complete = slots.All(slot => slot.State != "unavailable");
        string playerId = player.UniqueMultiplayerID.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new ReviewInventoryValues(captureId,
            ReviewInventoryContract.Revision(launch, playerId, capacity, selectedSlot, slots),
            playerId, capacity, selectedSlot, complete, complete ? [] : ["itemDataUnavailable"], slots.AsReadOnly());
        return ReviewInventoryContract.DataValid(values, launch)
            ? new(ReviewInventoryContract.SchemaVersion, "ready", null, launch, "single", null, DateTimeOffset.UtcNow, values)
            : Failure("inventoryValuesInvalid");
    }
}
#endif
