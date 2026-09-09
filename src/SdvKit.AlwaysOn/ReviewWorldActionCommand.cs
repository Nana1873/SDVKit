#if SDVKIT_GAME_AVAILABLE
using System.Text.Json;
using Microsoft.Xna.Framework;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using StardewObject = StardewValley.Object;

namespace SdvKit.AlwaysOn;

internal static class ReviewWorldActionCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void Handle(string[] args, string runtimePath, IMonitor monitor,
        IReviewInputRuntime input, Func<TestSaveAutomation?> testSave)
    {
        if (args.Length == 3 && args[1] == "cancel" && ReviewTransportToken.IsRequestId(args[2]))
        {
            ReviewVirtualCursor.CancelRequest(args[2]);
            return;
        }
        if (args.Length != 10 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]) || !int.TryParse(args[4], out int x)
            || !int.TryParse(args[5], out int y))
        {
            monitor.Log("SDVKit world action rejected invalid transport arguments.", LogLevel.Error);
            return;
        }
        var query = new ReviewWorldActionQuery(args[3], x, y, args[6], args[7], args[8]);
        if (ReviewWorldActionContract.Validate(query) is not null || args[9] != "single")
        {
            monitor.Log("SDVKit world action rejected invalid bounded arguments.", LogLevel.Error);
            return;
        }
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        ReviewWorldActionReport Report(string state, string? code, string dispatch, string? button = null,
            int? start = null, int? end = null) => new(ReviewWorldActionContract.SchemaVersion, state, code,
                launch, "single", null, DateTimeOffset.UtcNow, query.Action, query.X, query.Y,
                dispatch, button, start, end);
        void Write(ReviewWorldActionReport report)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ReviewWorldActionResponseEnvelope(1, args[1], report), JsonOptions);
                if (bytes.Length <= ReviewWorldActionContract.MaximumResponseBytes)
                    ReviewResponseFile.Write(ReviewWorldActionContract.ResponsePath(runtimePath, args[1]), bytes);
            }
            catch (Exception exception)
            {
                monitor.Log($"SDVKit world action response could not be published: {exception.Message}", LogLevel.Error);
            }
        }

        bool dispatched = false;
        bool cursorOwned = false;
        try
        {
            if (args[2] != launch)
            {
                Write(Report("unavailable", "worldActionBindingInvalid", "notDispatched"));
                return;
            }
            string? rejection = Validate(query, launch, testSave, out string button);
            if (rejection is not null)
            {
                Write(Report("unavailable", rejection, "notDispatched"));
                return;
            }
            (int cursorX, int cursorY)? CursorForTarget()
            {
                float uiScale = Game1.options.uiScale;
                float zoom = Game1.options.zoomLevel;
                Vector2 screen = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(query.X * Game1.tileSize + Game1.tileSize / 2,
                        query.Y * Game1.tileSize + Game1.tileSize / 2));
                return ReviewWorldActionCursor.TryMap(screen.X, screen.Y, zoom, uiScale,
                    input.UiWidth, input.UiHeight, out int mappedX, out int mappedY)
                        ? (mappedX, mappedY) : null;
            }
            if (CursorForTarget() is not { } cursor)
            {
                Write(Report("unavailable", "worldActionViewportUnavailable", "notDispatched"));
                return;
            }
            int cursorX = cursor.cursorX;
            int cursorY = cursor.cursorY;
            if (!input.TrySetCursor(cursorX, cursorY, out _))
            {
                Write(Report("unavailable", "worldActionTargetNotVisible", "notDispatched"));
                return;
            }
            cursorOwned = true;
            var request = new ReviewInputRequest(ReviewInputKind.Press, button, 0, 0, RequestId: args[1]);
            var dispatchGate = new ReviewWorldActionDispatchGate(cursorX, cursorY,
                () => CursorForTarget() == ReviewVirtualCursor.Position
                    ? (cursorX, cursorY) : null,
                () => Validate(query, launch, testSave, out _));
            dispatched = true;
            if (!input.TryWorldPress(request, dispatchGate.Validate, result =>
                {
                    bool cleared = input.TryClearCursor(out _);
                    cursorOwned = false;
                    if (!cleared)
                    {
                        Write(Report("unavailable", "worldActionCursorCleanupFailed",
                            result.StartTick is null ? "notDispatched" : "mayHaveRun",
                            result.CanonicalButton, result.StartTick, result.EndTick));
                        return;
                    }
                    Write(result.Succeeded
                        ? Report("completed", null, "completed", result.CanonicalButton,
                            result.StartTick, result.EndTick)
                        : Report("unavailable", "worldActionCompletionUncertain",
                            result.StartTick is null ? "notDispatched" : "mayHaveRun", result.CanonicalButton,
                            result.StartTick, result.EndTick));
                }, out _))
            {
                dispatched = false;
                bool cleared = input.TryClearCursor(out _);
                cursorOwned = false;
                Write(Report("unavailable", cleared ? "worldActionInputRejected"
                    : "worldActionCursorCleanupFailed", "notDispatched"));
            }
        }
        catch (Exception)
        {
            if (cursorOwned)
            {
                try { input.TryClearCursor(out _); }
                catch (Exception) { }
            }
            Write(Report("unavailable", dispatched ? "worldActionCompletionUncertain" : "worldActionValidationFailed",
                dispatched ? "mayHaveRun" : "notDispatched"));
        }
    }

    private static string? Validate(ReviewWorldActionQuery query, string launch,
        Func<TestSaveAutomation?> testSave, out string button)
    {
        button = query.Action == ReviewWorldActionContract.Water ? "MouseLeft" : "MouseRight";
        bool bindingReady = Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") == "1"
            && ReviewTransportToken.IsRequestId(launch)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
            && Context.ScreenId == 0;
        bool fixtureReady = bindingReady && testSave() is { } fixture
            && fixture.TryVerifyReviewFixture(out _, out _);
        bool worldReady = fixtureReady && Context.IsWorldReady && !Game1.exitToTitle
            && Game1.currentLocation is not null;
        Farmer? player = worldReady ? Game1.player : null;
        bool playerReady = player is not null && Game1.activeClickableMenu is null
            && Game1.currentMinigame is null && !Game1.uiMode && !Game1.eventUp && !Game1.fadeToBlack
            && !player.UsingTool && player.CanMove && player.CursorSlotItem is null;
        bool facedAdjacent = playerReady && player!.GetGrabTile() == new Vector2(query.X, query.Y);
        bool inventoryMatches = false;
        ReviewWorldTile? tile = null;
        if (facedAdjacent)
        {
            ReviewInventoryReport inventory = ReviewInventoryCommand.Capture(Guid.NewGuid().ToString("N"), launch);
            inventoryMatches = inventory.State == "ready"
                && inventory.Data?.InventoryRevision == query.InventoryRevision;
            if (inventoryMatches)
            {
                ReviewWorldReport world = ReviewWorldCommand.Capture(launch, new(query.X, query.Y, 1, 1));
                tile = world.State == "ready" ? world.Data?.Tiles.SingleOrDefault() : null;
            }
        }
        string targetKind = "unsupported";
        bool targetMatches = false;
        bool cropAlive = false;
        bool soilWatered = false;
        bool soilNeedsWater = false;
        bool harvestReady = false;
        bool grabHarvest = false;
        bool inventoryCanAccept = false;
        string? machineState = null;
        bool machineAcceptsItem = false;
        if (tile?.Soil is { } soil && query.Action == ReviewWorldActionContract.Water)
        {
            targetKind = "soil";
            targetMatches = soil.InstanceId == query.TargetInstanceId && soil.Revision == query.TargetRevision;
            cropAlive = soil.Crop.State == "missing" || soil.Crop.Data is { Dead: false };
            soilWatered = soil.Watered;
            soilNeedsWater = soil.NeedsWatering;
        }
        else if (tile?.Soil?.Crop.Data is { } crop && query.Action == ReviewWorldActionContract.Harvest)
        {
            targetKind = "crop";
            targetMatches = crop.InstanceId == query.TargetInstanceId && crop.Revision == query.TargetRevision;
            cropAlive = !crop.Dead;
            harvestReady = crop.ReadyForHarvest;
            grabHarvest = Game1.currentLocation!.terrainFeatures.TryGetValue(new Vector2(query.X, query.Y),
                    out TerrainFeature? feature) && feature is HoeDirt dirt
                && dirt.crop?.GetHarvestMethod() == HarvestMethod.Grab;
            if (grabHarvest)
                inventoryCanAccept = player!.couldInventoryAcceptThisItem(ItemRegistry.Create(crop.HarvestItemId));
        }
        else if (tile?.Machine is { } machine
            && Game1.currentLocation!.Objects.TryGetValue(new Vector2(query.X, query.Y), out StardewObject? obj)
            && obj is not null)
        {
            targetKind = "machine";
            targetMatches = machine.InstanceId == query.TargetInstanceId && machine.Revision == query.TargetRevision;
            machineState = machine.State;
            if (player!.CurrentItem is StardewObject selected)
                machineAcceptsItem = obj.performObjectDropInAction(selected, true, player);
            if (obj.heldObject.Value is Item output)
                inventoryCanAccept = player.couldInventoryAcceptThisItem(output);
        }
        WateringCan? wateringCan = player?.CurrentTool as WateringCan;
        var facts = new ReviewWorldActionFacts(bindingReady, fixtureReady, worldReady, playerReady,
            facedAdjacent, inventoryMatches,
            targetKind, targetMatches, cropAlive, soilWatered, soilNeedsWater,
            wateringCan is not null, wateringCan?.WaterLeft > 0, player?.CurrentItem is null,
            harvestReady, grabHarvest, inventoryCanAccept, machineState,
            player?.CurrentItem is StardewObject, machineAcceptsItem);
        return ReviewWorldActionDecision.Rejection(query, facts);
    }
}
#endif
