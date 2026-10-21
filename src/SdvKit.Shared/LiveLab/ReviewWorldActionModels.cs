namespace SdvKit.Cli.LiveLab;

internal static class ReviewWorldActionContract
{
    public const int SchemaVersion = 1;
    public const int MaximumResponseBytes = 16 * 1024;
    public const string Water = "water";
    public const string Harvest = "harvest";
    public const string MachineInsert = "machineInsert";
    public const string MachineCollect = "machineCollect";

    public static bool IsAction(string? value) => value is Water or Harvest or MachineInsert or MachineCollect;

    public static bool IsGameError(string? value) => value is
        "worldActionBindingInvalid" or "worldActionTestSaveRequired" or "worldActionWorldNotReady"
        or "worldActionPlayerBusy" or "worldActionTargetNotAdjacentAndFaced" or "worldActionTargetNotAdjacent"
        or "worldActionInventoryChanged" or "worldActionTargetChanged" or "worldActionTargetUnsupported"
        or "worldActionWrongToolOrState" or "worldActionWrongItemOrState"
        or "worldActionInsufficientResource"
        or "worldActionHarvestMethodUnsupported" or "worldActionInventoryFull"
        or "worldActionItemNotAccepted" or "worldActionViewportUnavailable"
        or "worldActionTargetNotVisible" or "worldActionViewportOrCursorChanged"
        or "worldActionInputRejected" or "worldActionCursorCleanupFailed"
        or "worldActionCompletionUncertain" or "worldActionValidationFailed"
        or "worldActionCursorAimUnavailable";

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-world-action-{requestId}.json");

    /// <summary>
    /// The last transport argument of a world action or container transfer: the review selection the request was
    /// bound to, so the game refuses it on any other role or screen. <c>single</c> is the unbound single review on
    /// screen 0, a network-2 role is its name, and a local split-screen screen is <c>screen-</c> and its ID.
    /// </summary>
    public static string SelectionToken(string? role, int? screenId) =>
        screenId is int screen ? "screen-" + screen.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : role ?? "single";

    public static bool TryParseSelection(string? token, out string? role, out int? screenId)
    {
        role = null;
        screenId = null;
        if (token == "single") return true;
        if (token is NetworkTwoContract.HostRole or NetworkTwoContract.FarmhandRole)
        {
            role = token;
            return true;
        }
        if (token is null || !token.StartsWith("screen-", StringComparison.Ordinal)
            || !int.TryParse(token["screen-".Length..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int screen)
            || SelectionToken(null, screen) != token) return false;
        screenId = screen;
        return true;
    }

    public static string? Validate(ReviewWorldActionQuery? query)
    {
        if (query is null || !IsAction(query.Action)
            || Math.Abs((long)query.X) > ReviewWorldContract.MaximumTileCoordinate
            || Math.Abs((long)query.Y) > ReviewWorldContract.MaximumTileCoordinate
            || !ReviewTransportToken.IsRequestId(query.TargetInstanceId)
            || !ReviewTransportToken.IsRequestId(query.TargetRevision)
            || !ReviewInventoryContract.IsRevision(query.InventoryRevision))
            return "worldActionArgumentsInvalid";
        return null;
    }
}

internal static class ReviewWorldActionCursor
{
    public static bool TryMap(float localWorldX, float localWorldY, float zoom, float uiScale,
        int uiWidth, int uiHeight, out int x, out int y)
    {
        x = y = 0;
        if (!float.IsFinite(localWorldX) || !float.IsFinite(localWorldY)
            || !float.IsFinite(zoom) || zoom <= 0 || !float.IsFinite(uiScale) || uiScale <= 0
            || uiWidth <= 0 || uiHeight <= 0) return false;
        double mappedX = localWorldX * zoom / uiScale;
        double mappedY = localWorldY * zoom / uiScale;
        if (!double.IsFinite(mappedX) || !double.IsFinite(mappedY)
            || mappedX < 0 || mappedY < 0 || mappedX > int.MaxValue || mappedY > int.MaxValue)
            return false;
        x = (int)Math.Round(mappedX, MidpointRounding.AwayFromZero);
        y = (int)Math.Round(mappedY, MidpointRounding.AwayFromZero);
        return x >= 0 && y >= 0 && x < uiWidth && y < uiHeight;
    }
}

internal sealed record ReviewWorldActionFacts(
    bool BindingReady,
    bool FixtureReady,
    bool WorldReady,
    bool PlayerReady,
    bool FacedAdjacent,
    bool InventoryMatches,
    string TargetKind,
    bool TargetMatches,
    bool CropAlive,
    bool SoilWatered,
    bool SoilNeedsWater,
    bool WateringCanSelected,
    bool HasWater,
    bool EmptyHand,
    bool HarvestReady,
    bool GrabHarvest,
    bool InventoryCanAccept,
    string? MachineState,
    bool SelectedMachineItem,
    bool MachineAcceptsItem);

internal static class ReviewWorldActionDecision
{
    /// <summary>
    /// Dispatch state for an action SDVKit did not send after its cursor was already on the target.
    /// Physical or external input can still act there, and the game applies it later in the same
    /// tick, on the next tick, or at the end of a tool animation. So <c>notDispatched</c> requires
    /// settled input (no action or tool input held or queued, no tool in use) and an unchanged
    /// target and backpack.
    /// </summary>
    public static string UnsentDispatchState(
        ReviewWorldActionQuery query,
        bool inputSettled,
        string? targetInstanceId,
        string? targetRevision,
        string? inventoryRevision) =>
        inputSettled
            && targetInstanceId == query.TargetInstanceId
            && targetRevision == query.TargetRevision
            && inventoryRevision == query.InventoryRevision
                ? "notDispatched"
                : "mayHaveRun";

    public static string? Rejection(ReviewWorldActionQuery query, ReviewWorldActionFacts facts)
    {
        if (!facts.BindingReady) return "worldActionBindingInvalid";
        if (!facts.FixtureReady) return "worldActionTestSaveRequired";
        if (!facts.WorldReady) return "worldActionWorldNotReady";
        if (!facts.PlayerReady) return "worldActionPlayerBusy";
        if (!facts.FacedAdjacent) return "worldActionTargetNotAdjacentAndFaced";
        if (!facts.InventoryMatches) return "worldActionInventoryChanged";
        if (!facts.TargetMatches) return "worldActionTargetChanged";
        if (query.Action == ReviewWorldActionContract.Water)
        {
            if (facts.TargetKind != "soil" || !facts.CropAlive) return "worldActionTargetUnsupported";
            if (facts.SoilWatered || !facts.SoilNeedsWater || !facts.WateringCanSelected)
                return "worldActionWrongToolOrState";
            return facts.HasWater ? null : "worldActionInsufficientResource";
        }
        if (query.Action == ReviewWorldActionContract.Harvest)
        {
            if (facts.TargetKind != "crop" || !facts.CropAlive) return "worldActionTargetUnsupported";
            if (!facts.HarvestReady || !facts.EmptyHand) return "worldActionWrongItemOrState";
            if (!facts.GrabHarvest) return "worldActionHarvestMethodUnsupported";
            return facts.InventoryCanAccept ? null : "worldActionInventoryFull";
        }
        if (facts.TargetKind != "machine") return "worldActionTargetUnsupported";
        if (query.Action == ReviewWorldActionContract.MachineInsert)
        {
            if (facts.MachineState != "idle" || !facts.SelectedMachineItem)
                return "worldActionWrongItemOrState";
            return facts.MachineAcceptsItem ? null : "worldActionItemNotAccepted";
        }
        if (facts.MachineState != "ready" || !facts.EmptyHand)
            return "worldActionWrongItemOrState";
        return facts.InventoryCanAccept ? null : "worldActionInventoryFull";
    }
}

internal sealed class ReviewWorldActionDispatchGate(
    int expectedX, int expectedY, Func<(int X, int Y)?> currentCursor,
    Func<string?> validateState)
{
    public string? Validate() => currentCursor() != (expectedX, expectedY)
        ? "worldActionViewportOrCursorChanged"
        : validateState();

    public static bool TryAuthorize(Func<string?> validate, out string? problem)
    {
        problem = validate();
        return problem is null;
    }
}

internal sealed record ReviewWorldActionQuery(string Action, int X, int Y,
    string TargetInstanceId, string TargetRevision, string InventoryRevision);

internal sealed record ReviewWorldActionReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset ObservedAtUtc,
    string Action, int X, int Y, string DispatchState, string? Button,
    int? StartTick, int? EndTick);

internal sealed record ReviewWorldActionResponseEnvelope(int SchemaVersion, string RequestId,
    ReviewWorldActionReport Report);

/// <summary>
/// A faced-tile player action without a target identity: the action button (<c>check</c>, right click) or the
/// selected tool (<c>useTool</c>, left click) on a tile next to the player. SDVKit puts the virtual cursor on the
/// tile, so the game starts there instead of on a tile picked from the player's sub-tile position. The game's own
/// fallbacks (neighbouring tiles, the player's tile, the held item, facing-based weapons) still apply.
/// </summary>
internal static class ReviewWorldActContract
{
    public const string Check = "check";
    public const string UseTool = "useTool";

    public static bool IsAction(string? value) => value is Check or UseTool;

    public static string ButtonFor(string action) => action == UseTool ? "MouseLeft" : "MouseRight";

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-world-act-{requestId}.json");

    public static string? Validate(ReviewWorldActQuery? query)
    {
        if (query is null || !IsAction(query.Action)
            || Math.Abs((long)query.X) > ReviewWorldContract.MaximumTileCoordinate
            || Math.Abs((long)query.Y) > ReviewWorldContract.MaximumTileCoordinate
            || !ReviewInventoryContract.IsRevision(query.InventoryRevision))
            return "worldActionArgumentsInvalid";
        return null;
    }

    /// <summary>The target must be one of the four tiles next to the player's tile.</summary>
    public static bool IsAdjacent(int playerX, int playerY, int targetX, int targetY) =>
        Math.Abs((long)playerX - targetX) + Math.Abs((long)playerY - targetY) == 1;

    public static string? Rejection(ReviewWorldActQuery query, ReviewWorldActFacts facts)
    {
        if (!facts.BindingReady) return "worldActionBindingInvalid";
        if (!facts.FixtureReady) return "worldActionTestSaveRequired";
        if (!facts.WorldReady) return "worldActionWorldNotReady";
        if (!facts.PlayerReady) return "worldActionPlayerBusy";
        if (!facts.CursorAimed) return "worldActionCursorAimUnavailable";
        if (!facts.Adjacent) return "worldActionTargetNotAdjacent";
        if (!facts.InventoryMatches) return "worldActionInventoryChanged";
        if (query.Action == UseTool && !facts.ToolSelected) return "worldActionWrongToolOrState";
        return null;
    }

    /// <summary>
    /// Dispatch state for an action SDVKit did not send after its cursor was on the target. Without a target
    /// identity, "nothing happened" needs settled input, the same backpack, no menu that the action could have
    /// opened, and an unchanged player and neighbourhood (location, tile, facing, stamina, health and the 3x3
    /// tiles around the player) since before the cursor was placed.
    /// </summary>
    public static string UnsentDispatchState(ReviewWorldActQuery query, bool inputSettled,
        string? inventoryRevision, bool menuOpen, bool surroundingsUnchanged) =>
        inputSettled && !menuOpen && surroundingsUnchanged && inventoryRevision == query.InventoryRevision
            ? "notDispatched" : "mayHaveRun";
}

internal sealed record ReviewWorldActFacts(
    bool BindingReady,
    bool FixtureReady,
    bool WorldReady,
    bool PlayerReady,
    bool CursorAimed,
    bool Adjacent,
    bool InventoryMatches,
    bool ToolSelected);

internal sealed record ReviewWorldActQuery(string Action, int X, int Y, string InventoryRevision);
