#if SDVKIT_GAME_AVAILABLE
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.GameSide;

/// <summary>
/// Moves the player exactly one tile, or only turns it, with the game's own movement key for the direction. The
/// key is held through SDVKit's process-local input until the player's standing pixel passes the centre of the
/// next tile, then released; the game halts the player in the tick that sees the release. A blocked or off-map
/// target is refused before any input. The report carries the tile, location and position the player ended on.
/// </summary>
internal static class ReviewWorldMoveCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Observation(string? LocationId, int? TileX, int? TileY, PlayerPositionValues? Position);

    internal static void Handle(string[] args, string runtimePath, IMonitor monitor,
        IReviewInputRuntime input, Func<TestSaveAutomation?> testSave, Func<NetworkTwoAutomation?> networkTwo)
    {
        if (args.Length == 3 && args[1] == "cancel" && ReviewTransportToken.IsRequestId(args[2]))
        {
            ReviewVirtualCursor.CancelRequest(args[2]);
            return;
        }
        if (args.Length != 6 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]) || args[4] is not ("true" or "false"))
        {
            monitor.Log("SDVKit world move rejected invalid transport arguments.", LogLevel.Error);
            return;
        }
        var query = new ReviewWorldMoveQuery(args[3], args[4] == "true");
        if (ReviewWorldMoveContract.Validate(query) is not null
            || !ReviewActionSelection.TryParse(args[5], out ReviewActionSelection selection))
        {
            monitor.Log("SDVKit world move rejected invalid bounded arguments.", LogLevel.Error);
            return;
        }
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        (int dx, int dy) = ReviewWorldMoveContract.Delta(query.Direction);
        int? targetX = null, targetY = null;
        ReviewWorldMoveReport Report(string state, string? code, string dispatch, string? button = null,
            int? start = null, int? end = null, bool reached = false, Observation? seen = null)
        {
            seen ??= Observe();
            return new(ReviewWorldMoveContract.SchemaVersion, state, code, launch, selection.Topology, selection.Role,
                DateTimeOffset.UtcNow, query.Direction, query.TurnOnly, dispatch, button, start, end,
                seen.LocationId, seen.TileX, seen.TileY, seen.Position, targetX, targetY, reached);
        }
        void Write(ReviewWorldMoveReport report)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ReviewWorldMoveResponseEnvelope(1, args[1], report), JsonOptions);
                if (bytes.Length <= ReviewWorldMoveContract.MaximumResponseBytes)
                    ReviewResponseMailbox.Publish(ReviewWorldMoveContract.ResponsePath(runtimePath, args[1]), bytes);
            }
            catch (Exception exception)
            {
                monitor.Log($"SDVKit world move response could not be published: {exception.Message}", LogLevel.Error);
            }
        }

        bool dispatched = false;
        try
        {
            if (args[2] != launch)
            {
                Write(Report("unavailable", "worldActionBindingInvalid", "notDispatched"));
                return;
            }
            string? rejection = Validate(query, launch, selection, testSave, networkTwo, dx, dy);
            if (rejection is not null)
            {
                Write(Report("unavailable", rejection, "notDispatched"));
                return;
            }
            Farmer player = Game1.player;
            Point start = player.TilePoint;
            string startLocation = Game1.currentLocation.NameOrUniqueName;
            targetX = start.X + dx;
            targetY = start.Y + dy;
            Keys key = Game1.options.getFirstKeyboardKeyFromInputButtonList(MoveButtons(query.Direction));
            SButton button = key.ToSButton();
            if (key == Keys.None || button == SButton.None)
            {
                Write(Report("unavailable", "worldActionInputRejected", "notDispatched"));
                return;
            }
            var request = new ReviewInputRequest(ReviewInputKind.Chord, null, 0, 0, RequestId: args[1],
                Buttons: [button.ToString()],
                DurationTicks: query.TurnOnly ? ReviewWorldMoveContract.TurnTicks : ReviewWorldMoveContract.MaximumHoldTicks);
            // Keep the press-edge refusal so the report names it instead of a generic uncertainty.
            string? gateProblem = null;
            string? Gate()
            {
                gateProblem = Validate(query, launch, selection, testSave, networkTwo, dx, dy);
                if (gateProblem is null && (!ReferenceEquals(Game1.player, player) || player.TilePoint != start
                    || Game1.currentLocation?.NameOrUniqueName != startLocation))
                    gateProblem = "worldActionTargetChanged";
                return gateProblem;
            }
            bool ReleaseWhen()
            {
                Farmer? current = Game1.player;
                if (query.TurnOnly || current is null || !ReferenceEquals(current, player)) return true;
                Point standing = current.StandingPixel;
                return ReviewWorldMoveContract.ShouldRelease(query.Direction, start.X, start.Y, targetX.Value,
                    targetY.Value, startLocation, Game1.currentLocation?.NameOrUniqueName, current.TilePoint.X,
                    current.TilePoint.Y, standing.X / 64f, standing.Y / 64f);
            }
            dispatched = true;
            if (!input.TryWorldHold(request, Gate, ReleaseWhen, result =>
                {
                    if (result.Succeeded)
                    {
                        Observation seen = Observe();
                        bool reached = seen.TileX is not null && seen.TileY is not null
                            && ReviewWorldMoveContract.Reached(query, start.X, start.Y, targetX.Value, targetY.Value,
                                startLocation, seen.LocationId, seen.TileX.Value, seen.TileY.Value, seen.Position?.Facing);
                        Write(Report("completed", null, "completed", result.CanonicalButton,
                            result.StartTick, result.EndTick, reached, seen));
                        return;
                    }
                    Write(Report("unavailable",
                        result.StartTick is null && gateProblem is not null ? gateProblem : "worldActionCompletionUncertain",
                        result.StartTick is null ? "notDispatched" : "mayHaveRun", result.CanonicalButton,
                        result.StartTick, result.EndTick));
                }, out _))
            {
                dispatched = false;
                Write(Report("unavailable", "worldActionInputRejected", "notDispatched"));
            }
        }
        catch (Exception)
        {
            Write(Report("unavailable", dispatched ? "worldActionCompletionUncertain" : "worldActionValidationFailed",
                dispatched ? "mayHaveRun" : "notDispatched"));
        }
    }

    private static InputButton[] MoveButtons(string direction) => direction switch
    {
        "up" => Game1.options.moveUpButton,
        "right" => Game1.options.moveRightButton,
        "down" => Game1.options.moveDownButton,
        _ => Game1.options.moveLeftButton,
    };

    /// <summary>The player's current location, tile and position; nulls when they cannot be read.</summary>
    private static Observation Observe()
    {
        try
        {
            Farmer? player = Game1.player;
            if (player is null || !Context.IsWorldReady || Game1.exitToTitle) return new(null, null, null, null);
            Point tile = player.TilePoint;
            return new(Game1.currentLocation?.NameOrUniqueName, tile.X, tile.Y, LocalPlayerCapture.CapturePosition(player));
        }
        catch (Exception)
        {
            return new(null, null, null, null);
        }
    }

    private static string? Validate(ReviewWorldMoveQuery query, string launch, ReviewActionSelection selection,
        Func<TestSaveAutomation?> testSave, Func<NetworkTwoAutomation?> networkTwo, int dx, int dy)
    {
        bool bindingReady = selection.BindingReady(launch);
        bool fixtureReady = bindingReady && selection.FixtureReady(testSave, networkTwo);
        bool worldReady = fixtureReady && Context.IsWorldReady && !Game1.exitToTitle
            && Game1.currentLocation is not null;
        Farmer? player = worldReady ? Game1.player : null;
        bool playerReady = player is not null && player.IsLocalPlayer && Game1.activeClickableMenu is null
            && Game1.currentMinigame is null && !Game1.uiMode && !Game1.eventUp && !Game1.fadeToBlack
            && !Game1.freezeControls && !Game1.globalFade && !Game1.isWarping && Game1.locationRequest is null
            && !player.UsingTool && player.CanMove && !player.isMoving() && player.movementDirections.Count == 0
            && !player.isRidingHorse();
        bool targetFree = false;
        if (playerReady && !query.TurnOnly && Game1.currentLocation is { } location)
        {
            Point tile = player!.TilePoint;
            var target = new Vector2(tile.X + dx, tile.Y + dy);
            // The move ends with the standing pixel (the bounding box centre) at or just past the target tile's
            // centre line, so check the whole path from the current box to that final box: their union is the
            // swept area of an axis-aligned move, and one movement step of overshoot is allowed for.
            Rectangle box = player.GetBoundingBox();
            Point standing = player.StandingPixel;
            int overshoot = (int)Math.Ceiling(player.getMovementSpeed());
            Rectangle final = box;
            final.Offset(
                dx == 0 ? 0 : (tile.X + dx) * Game1.tileSize + Game1.tileSize / 2 - standing.X + dx * overshoot,
                dy == 0 ? 0 : (tile.Y + dy) * Game1.tileSize + Game1.tileSize / 2 - standing.Y + dy * overshoot);
            Rectangle swept = Rectangle.Union(box, final);
            // The same check the game makes before each movement step, without its side effects (pushing
            // animals or villagers, touching terrain features). Warps are not collisions: a warp on the target
            // tile changes the location during the move, which the report then names.
            targetFree = location.isTileOnMap(target)
                && !location.isCollidingPosition(swept, Game1.viewport, isFarmer: true, damagesFarmer: 0,
                    glider: false, character: player, pathfinding: false, projectile: false,
                    ignoreCharacterRequirement: false, skipCollisionEffects: true);
        }
        var facts = new ReviewWorldMoveFacts(bindingReady, fixtureReady, worldReady, playerReady, targetFree);
        return ReviewWorldMoveContract.Rejection(query, facts);
    }
}
#endif
