namespace SdvKit.Cli.LiveLab;

/// <summary>
/// A bounded one-tile move: SDVKit holds the game's own movement key for the direction until the player's standing
/// pixel has passed the centre of the next tile in that direction (or a tick limit expires), then releases it, so
/// successive moves land one tile apart. <c>turnOnly</c> presses the key for one input tick, which turns the
/// player and moves it a few pixels inside its tile. The target must be on the map and collision-free for the
/// player's bounding box; otherwise the request is refused before any input. Pathfinding is out of scope.
/// </summary>
internal static class ReviewWorldMoveContract
{
    public const int SchemaVersion = 1;
    public const int MaximumResponseBytes = 16 * 1024;
    /// <summary>One tile at the game's walking speed takes about 30 input ticks; running about 12.</summary>
    public const int MaximumHoldTicks = 120;
    public const int TurnTicks = 1;
    public static readonly string[] Directions = ["up", "right", "down", "left"];

    public static bool IsDirection(string? value) => value is not null && Directions.Contains(value);

    /// <summary>Stardew's facing index for the direction: 0 up, 1 right, 2 down, 3 left.</summary>
    public static int FacingIndex(string direction) => Array.IndexOf(Directions, direction);

    public static (int Dx, int Dy) Delta(string direction) => direction switch
    {
        "up" => (0, -1),
        "right" => (1, 0),
        "down" => (0, 1),
        _ => (-1, 0),
    };

    public static bool IsGameError(string? value) => value is "worldMoveTargetBlocked"
        || ReviewWorldActionContract.IsGameError(value);

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-world-move-{requestId}.json");

    public static string? Validate(ReviewWorldMoveQuery? query) =>
        query is null || !IsDirection(query.Direction) ? "worldActionArgumentsInvalid" : null;

    public static string? Rejection(ReviewWorldMoveQuery query, ReviewWorldMoveFacts facts)
    {
        if (!facts.BindingReady) return "worldActionBindingInvalid";
        if (!facts.FixtureReady) return "worldActionTestSaveRequired";
        if (!facts.WorldReady) return "worldActionWorldNotReady";
        if (!facts.PlayerReady) return "worldActionPlayerBusy";
        if (!query.TurnOnly && !facts.TargetFree) return "worldMoveTargetBlocked";
        return null;
    }

    /// <summary>
    /// Whether the held key can be released: the player stands on the target tile with its standing pixel at or
    /// past the tile's centre line along the movement axis, so the next move starts from the middle of the tile.
    /// Also true when the player left the start and target tiles or the location (a warp), so the hold never
    /// continues into a second tile.
    /// </summary>
    public static bool ShouldRelease(string direction, int startX, int startY, int targetX, int targetY,
        string startLocation, string? location, int tileX, int tileY, float standingX, float standingY)
    {
        if (location != startLocation) return true;
        if (tileX == startX && tileY == startY) return false;
        if (tileX != targetX || tileY != targetY) return true;
        return direction switch
        {
            "up" => standingY <= targetY + 0.5f,
            "down" => standingY >= targetY + 0.5f,
            "left" => standingX <= targetX + 0.5f,
            _ => standingX >= targetX + 0.5f,
        };
    }

    /// <summary>Whether the move ended where it should: on the target tile of the same location, or for a turn
    /// on the start tile facing the direction.</summary>
    public static bool Reached(ReviewWorldMoveQuery query, int startX, int startY, int targetX, int targetY,
        string startLocation, string? location, int tileX, int tileY, string? facing)
    {
        if (location != startLocation || facing != query.Direction) return false;
        return query.TurnOnly ? tileX == startX && tileY == startY : tileX == targetX && tileY == targetY;
    }
}

internal sealed record ReviewWorldMoveFacts(
    bool BindingReady,
    bool FixtureReady,
    bool WorldReady,
    bool PlayerReady,
    bool TargetFree);

internal sealed record ReviewWorldMoveQuery(string Direction, bool TurnOnly);

internal sealed record ReviewWorldMoveReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset ObservedAtUtc,
    string Direction, bool TurnOnly, string DispatchState, string? Button, int? StartTick, int? EndTick,
    string? LocationId, int? TileX, int? TileY, PlayerPositionValues? Position,
    int? TargetX, int? TargetY, bool Reached);

internal sealed record ReviewWorldMoveResponseEnvelope(int SchemaVersion, string RequestId,
    ReviewWorldMoveReport Report);
