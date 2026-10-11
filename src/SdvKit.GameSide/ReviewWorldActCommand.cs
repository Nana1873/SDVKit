#if SDVKIT_GAME_AVAILABLE
using System.Text.Json;
using Microsoft.Xna.Framework;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.GameSide;

/// <summary>
/// The action button (check) or the selected tool (useTool) on a tile next to the player. The virtual cursor is
/// placed on the target first, because the game starts both buttons at the tile under the cursor when the mouse
/// cursor is visible and within one tile of the player; without it, the target would depend on where inside its
/// tile the player stands. When the game would ignore the cursor (gamepad mode, faded or hidden mouse, a held
/// gamepad button), the request is refused instead of acting on the faced tile.
/// </summary>
internal static class ReviewWorldActCommand
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
        if (args.Length != 8 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]) || !int.TryParse(args[4], out int x)
            || !int.TryParse(args[5], out int y))
        {
            monitor.Log("SDVKit world act rejected invalid transport arguments.", LogLevel.Error);
            return;
        }
        var query = new ReviewWorldActQuery(args[3], x, y, args[6]);
        if (ReviewWorldActContract.Validate(query) is not null || args[7] != "single")
        {
            monitor.Log("SDVKit world act rejected invalid bounded arguments.", LogLevel.Error);
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
                    ReviewResponseMailbox.Publish(ReviewWorldActContract.ResponsePath(runtimePath, args[1]), bytes);
            }
            catch (Exception exception)
            {
                monitor.Log($"SDVKit world act response could not be published: {exception.Message}", LogLevel.Error);
            }
        }

        bool dispatched = false;
        bool cursorOwned = false;
        // Once the cursor sits on the target, "not sent by SDVKit" no longer proves "did not run".
        bool cursorWasPlaced = false;
        string? surroundingsBefore = null;
        string Unsent() => cursorWasPlaced
            ? ObservedUnsentDispatchState(query, launch, surroundingsBefore) : "notDispatched";
        try
        {
            if (args[2] != launch)
            {
                Write(Report("unavailable", "worldActionBindingInvalid", "notDispatched"));
                return;
            }
            string? rejection = Validate(query, launch, testSave);
            if (rejection is not null)
            {
                Write(Report("unavailable", rejection, "notDispatched"));
                return;
            }
            string button = ReviewWorldActContract.ButtonFor(query.Action);
            (int cursorX, int cursorY)? CursorForTarget()
            {
                Vector2 screen = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(query.X * Game1.tileSize + Game1.tileSize / 2,
                        query.Y * Game1.tileSize + Game1.tileSize / 2));
                return ReviewWorldActionCursor.TryMap(screen.X, screen.Y, Game1.options.zoomLevel,
                    Game1.options.uiScale, input.UiWidth, input.UiHeight, out int mappedX, out int mappedY)
                        ? (mappedX, mappedY) : null;
            }
            if (CursorForTarget() is not { } cursor)
            {
                Write(Report("unavailable", "worldActionViewportUnavailable", "notDispatched"));
                return;
            }
            int cursorX = cursor.cursorX;
            int cursorY = cursor.cursorY;
            surroundingsBefore = Surroundings(launch);
            if (!input.TrySetCursor(cursorX, cursorY, out _))
            {
                Write(Report("unavailable", "worldActionTargetNotVisible", "notDispatched"));
                return;
            }
            cursorOwned = true;
            cursorWasPlaced = true;
            var request = new ReviewInputRequest(ReviewInputKind.Press, button, cursorX, cursorY, RequestId: args[1]);
            var dispatchGate = new ReviewWorldActionDispatchGate(cursorX, cursorY,
                () => CursorForTarget() == ReviewVirtualCursor.Position ? (cursorX, cursorY) : null,
                () => Validate(query, launch, testSave));
            dispatched = true;
            if (!input.TryWorldPress(request, dispatchGate.Validate, result =>
                {
                    bool cleared = input.TryClearCursor(out _);
                    cursorOwned = false;
                    if (!cleared)
                    {
                        Write(Report("unavailable", "worldActionCursorCleanupFailed",
                            result.StartTick is null ? Unsent() : "mayHaveRun",
                            result.CanonicalButton, result.StartTick, result.EndTick));
                        return;
                    }
                    Write(result.Succeeded
                        ? Report("completed", null, "completed", result.CanonicalButton,
                            result.StartTick, result.EndTick)
                        : Report("unavailable", "worldActionCompletionUncertain",
                            result.StartTick is null ? Unsent() : "mayHaveRun", result.CanonicalButton,
                            result.StartTick, result.EndTick));
                }, out _))
            {
                dispatched = false;
                bool cleared = input.TryClearCursor(out _);
                cursorOwned = false;
                Write(Report("unavailable", cleared ? "worldActionInputRejected"
                    : "worldActionCursorCleanupFailed", Unsent()));
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
                dispatched ? "mayHaveRun" : Unsent()));
        }
    }

    private static string ObservedUnsentDispatchState(ReviewWorldActQuery query, string launch, string? before)
    {
        try
        {
            bool inputSettled = !ReviewWorldActionCommand.ActionInputOrToolInProgress();
            ReviewInventoryReport inventory = ReviewInventoryCommand.Capture(Guid.NewGuid().ToString("N"), launch);
            string? after = Surroundings(launch);
            return ReviewWorldActContract.UnsentDispatchState(query, inputSettled,
                inventory.State == "ready" ? inventory.Data?.InventoryRevision : null,
                Game1.activeClickableMenu is not null, before is not null && before == after);
        }
        catch (Exception)
        {
            return "mayHaveRun";
        }
    }

    /// <summary>
    /// What an action without a target identity could change unnoticed by the backpack: the player's place,
    /// facing, stamina and health, and the 3x3 tiles around the player. Null when it cannot be read.
    /// </summary>
    private static string? Surroundings(string launch)
    {
        Farmer player = Game1.player;
        GameLocation location = Game1.currentLocation;
        if (player is null || location?.Map?.GetLayer("Back") is not { } back) return null;
        Point tile = player.TilePoint;
        int left = Math.Max(0, tile.X - 1), top = Math.Max(0, tile.Y - 1);
        int right = Math.Min(back.LayerWidth - 1, tile.X + 1), bottom = Math.Min(back.LayerHeight - 1, tile.Y + 1);
        if (right < left || bottom < top) return null;
        ReviewWorldReport world = ReviewWorldCommand.Capture(launch,
            new ReviewWorldArea(left, top, right - left + 1, bottom - top + 1));
        if (world.State != "ready" || world.Data is null) return null;
        return JsonSerializer.Serialize(new
        {
            Location = location.NameOrUniqueName,
            tile.X,
            tile.Y,
            player.FacingDirection,
            player.Stamina,
            player.health,
            world.Data.Tiles
        }, JsonOptions);
    }

    private static string? Validate(ReviewWorldActQuery query, string launch, Func<TestSaveAutomation?> testSave)
    {
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
            && !Game1.freezeControls && !Game1.globalFade
            && !player.UsingTool && player.CanMove && player.CursorSlotItem is null;
        // Stardew falls back to the faced tile when the mouse cursor is hidden or a gamepad drives input.
        bool cursorAimed = playerReady && !Game1.options.gamepadControls && Game1.wasMouseVisibleThisFrame
            && Game1.mouseCursorTransparency > 0f && !Game1.isAnyGamePadButtonBeingHeld();
        bool adjacent = cursorAimed
            && ReviewWorldActContract.IsAdjacent(player!.TilePoint.X, player.TilePoint.Y, query.X, query.Y);
        bool inventoryMatches = false;
        if (adjacent)
        {
            ReviewInventoryReport inventory = ReviewInventoryCommand.Capture(Guid.NewGuid().ToString("N"), launch);
            inventoryMatches = inventory.State == "ready"
                && inventory.Data?.InventoryRevision == query.InventoryRevision;
        }
        var facts = new ReviewWorldActFacts(bindingReady, fixtureReady, worldReady, playerReady, cursorAimed, adjacent,
            inventoryMatches, player?.CurrentTool is not null);
        return ReviewWorldActContract.Rejection(query, facts);
    }
}
#endif
