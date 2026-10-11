namespace SdvKit.Cli.LiveLab;

internal static class ReviewContainerTransferContract
{
    public const int SchemaVersion = 1;
    public const int MaximumQuantity = 99;
    public const int MaximumResponseBytes = 256 * 1024;
    public const string Deposit = "deposit";
    public const string Withdraw = "withdraw";

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-container-transfer-{requestId}.json");

    public static bool IsDirection(string? value) => value is Deposit or Withdraw;
    public static bool IsSide(string? value) => value is "player" or "container";
    public static string SourceSide(string direction) => direction == Deposit ? "player" : "container";
    public static string DestinationSide(string direction) => direction == Deposit ? "container" : "player";

    public static string? Validate(ReviewContainerTransferQuery? query) => query is null
        || !IsDirection(query.Direction) || query.SourceSlot is < 0 or >= ReviewContainerContract.MaximumPlayerSlots
        || query.Quantity is < 1 or > MaximumQuantity
        || query.QualifiedItemId.Length is < 4 or > 256 || query.QualifiedItemId.Any(char.IsWhiteSpace)
        || !ReviewInventoryContract.IsRevision(query.SelectionIdentity)
        || !ReviewInventoryContract.IsRevision(query.ContainerRevision)
        || !ReviewInventoryContract.IsRevision(query.InstanceIdentity)
        || !ReviewInventoryContract.IsRevision(query.ItemRevision)
            ? "containerTransferArgumentsInvalid" : null;

    public static string? ObservationProblem(ReviewContainerTransferQuery query, ReviewContainerValues before)
    {
        string sourceSide = SourceSide(query.Direction);
        ReviewContainerSide side = sourceSide == "player" ? before.Player : before.Container;
        if (before.SelectionIdentity != query.SelectionIdentity || before.ContainerRevision != query.ContainerRevision)
            return "containerTransferSelectionStale";
        if (before.HeldItem.State != "empty") return "containerTransferHeldItemOccupied";
        if (query.SourceSlot < 0 || query.SourceSlot >= side.Capacity) return "containerTransferSourceSlotInvalid";
        ReviewContainerSlot slot = side.Slots[query.SourceSlot];
        if (slot.State != "occupied" || slot.Item is not { } item || slot.Identity is not { } identity
            || item.QualifiedItemId != query.QualifiedItemId || identity.InstanceIdentity != query.InstanceIdentity
            || identity.ItemRevision != query.ItemRevision) return "containerTransferSourceStale";
        return query.Quantity > item.Stack ? "containerTransferInsufficientSource" : null;
    }

    public static int ObservedQuantity(ReviewContainerValues before, ReviewContainerValues after,
        ReviewContainerTransferQuery query)
    {
        int Count(ReviewContainerSide side) => side.Slots
            .Where(slot => slot.Item?.QualifiedItemId == query.QualifiedItemId)
            .Sum(slot => slot.Item!.Stack);
        int sourceBefore = Count(query.Direction == Deposit ? before.Player : before.Container);
        int sourceAfter = Count(query.Direction == Deposit ? after.Player : after.Container);
        int destinationBefore = Count(query.Direction == Deposit ? before.Container : before.Player);
        int destinationAfter = Count(query.Direction == Deposit ? after.Container : after.Player);
        return Math.Max(0, Math.Min(sourceBefore - sourceAfter, destinationAfter - destinationBefore));
    }

    public static bool Conserved(ReviewContainerValues before, ReviewContainerValues after,
        ReviewContainerTransferQuery query)
    {
        int Total(ReviewContainerValues value) => value.Player.Slots.Concat(value.Container.Slots)
            .Where(slot => slot.Item?.QualifiedItemId == query.QualifiedItemId).Sum(slot => slot.Item!.Stack)
            + (value.HeldItem.Item?.QualifiedItemId == query.QualifiedItemId ? value.HeldItem.Item.Stack : 0);
        return Total(before) == Total(after) && after.HeldItem.State == "empty";
    }

    public static bool ProgressValid(ReviewContainerValues before, ReviewContainerValues current,
        ReviewContainerTransferQuery query, int maximumObserved) => maximumObserved >= 0
        && current.HeldItem.State == "empty" && Conserved(before, current, query)
        && ObservedQuantity(before, current, query) is int observed && observed >= 0 && observed <= maximumObserved;
}

internal sealed record ReviewContainerTransferQuery(string Direction, int SourceSlot, int Quantity,
    string QualifiedItemId, string SelectionIdentity, string ContainerRevision,
    string InstanceIdentity, string ItemRevision);

internal sealed record ReviewContainerTransferValues(string Direction, string SourceSide, int SourceSlot,
    int RequestedQuantity, int? ObservedQuantity, string QualifiedItemId, bool Dispatched,
    bool CancellationRequested, string Outcome, ReviewContainerValues Before,
    ReviewContainerValues? After, IReadOnlyList<string> Limitations);

internal sealed record ReviewContainerTransferReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset CapturedAtUtc,
    ReviewContainerTransferValues? Data);

internal sealed record ReviewContainerTransferResponseEnvelope(int SchemaVersion, string RequestId,
    ReviewContainerTransferReport Report);
