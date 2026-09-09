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
        or "worldActionPlayerBusy" or "worldActionTargetNotAdjacentAndFaced"
        or "worldActionInventoryChanged" or "worldActionTargetChanged" or "worldActionTargetUnsupported"
        or "worldActionWrongToolOrState" or "worldActionWrongItemOrState"
        or "worldActionInsufficientResource"
        or "worldActionHarvestMethodUnsupported" or "worldActionInventoryFull"
        or "worldActionItemNotAccepted" or "worldActionViewportUnavailable"
        or "worldActionTargetNotVisible" or "worldActionViewportOrCursorChanged"
        or "worldActionInputRejected" or "worldActionCursorCleanupFailed"
        or "worldActionCompletionUncertain" or "worldActionValidationFailed";

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-world-action-{requestId}.json");

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
