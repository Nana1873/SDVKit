using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewContainerContract
{
    public const int SchemaVersion = 1;
    public const int MaximumPlayerSlots = ReviewInventoryContract.MaximumSlots;
    public const int MaximumContainerSlots = 72;
    public const int MaximumResponseBytes = 96 * 1024;
    public const int StandardChestCapacity = 36;

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-container-{requestId}.json");

    public static string? UnavailableReason(bool exactMenu, bool hasChild, bool chestSource,
        bool exactChest, bool sameSourceItem, bool regularVanillaId, bool regularFlags,
        bool placedAtCurrentTile, bool sameContainerInventory, bool samePlayerInventory,
        int containerCapacity, int containerCount, int playerCount) =>
        !exactMenu || hasChild || !chestSource ? "containerMenuUnsupported"
        : !exactChest || !sameSourceItem ? "containerBackingAmbiguous"
        : !regularVanillaId || !regularFlags ? "containerFamilyUnsupported"
        : !placedAtCurrentTile ? "containerLocationUnavailable"
        : !sameContainerInventory || !samePlayerInventory ? "containerBackingAmbiguous"
        : containerCapacity != StandardChestCapacity || containerCount < 0 || containerCount > containerCapacity
            || playerCount is <= 0 or > MaximumPlayerSlots ? "containerCapacityUnsupported"
        : null;

    public static string OpaqueIdentity(string launchId, string identityScope, string kind, long observationId) =>
        Hash(launchId, identityScope, kind, observationId.ToString(CultureInfo.InvariantCulture));

    public static string NativeItemRevision(string instanceIdentity, string runtimeType,
        int maximumStackSize, string name, uint? packedColor, string? orderData) => Hash(instanceIdentity,
            runtimeType, maximumStackSize.ToString(CultureInfo.InvariantCulture), name,
            packedColor?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, orderData ?? string.Empty);

    public static string SelectionIdentity(string launchId, string identityScope, string backingIdentity,
        string playerId, string locationName, int tileX, int tileY, string chestItemId) => Hash(
            launchId, identityScope, backingIdentity, playerId, locationName,
            tileX.ToString(CultureInfo.InvariantCulture), tileY.ToString(CultureInfo.InvariantCulture), chestItemId);

    public static string Revision(string selectionIdentity, ReviewContainerSide player,
        ReviewContainerSide container, ReviewObservedItem heldItem)
    {
        var values = new List<string> { selectionIdentity };
        AppendSide(player);
        AppendSide(container);
        AppendItem(heldItem.State, heldItem.Reason, heldItem.Item, heldItem.Identity);
        return Hash([.. values]);

        void AppendSide(ReviewContainerSide side)
        {
            values.Add(side.Side);
            values.Add(side.Capacity.ToString(CultureInfo.InvariantCulture));
            foreach (ReviewContainerSlot slot in side.Slots)
            {
                values.Add(slot.Slot.ToString(CultureInfo.InvariantCulture));
                AppendItem(slot.State, slot.Reason, slot.Item, slot.Identity);
            }
        }

        void AppendItem(string state, string? reason, SelectedItemValues? item,
            ReviewContainerItemIdentity? identity)
        {
            values.Add(state);
            values.Add(reason ?? string.Empty);
            values.Add(item?.QualifiedItemId ?? string.Empty);
            values.Add(item?.Stack.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            values.Add(item?.Quality?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            if (state != "empty")
            {
                values.Add(identity?.InstanceIdentity ?? string.Empty);
                values.Add(identity?.ItemRevision ?? string.Empty);
            }
        }
    }

    public static bool DataValid(ReviewContainerValues? data, string expectedLaunchId)
    {
        if (!ReviewTransportToken.IsRequestId(expectedLaunchId)
            || data is null || !ReviewTransportToken.IsRequestId(data.CaptureId)
            || !ReviewTransportToken.IsRequestId(data.IdentityScope)
            || !ReviewInventoryContract.IsRevision(data.SelectionIdentity)
            || !ReviewInventoryContract.IsRevision(data.ContainerRevision)
            || !ReviewInventoryContract.IsRevision(data.BackingIdentity)
            || !long.TryParse(data.PlayerId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long playerId)
            || playerId == 0 || data.PlayerId != playerId.ToString(CultureInfo.InvariantCulture)
            || !TextValid(data.LocationName, 256) || data.ChestItemId is not ("(BC)130" or "(BC)232")
            || !SideValid(data.Player, "player", MaximumPlayerSlots, exactCapacity: null)
            || !SideValid(data.Container, "container", MaximumContainerSlots, StandardChestCapacity)
            || !ObservedItemValid(data.HeldItem)
            || data.Limitations is null || data.Limitations.Count > 1
            || data.Complete != SidesComplete(data)
            || (data.Complete ? data.Limitations.Count != 0
                : !data.Limitations.SequenceEqual(["itemDataUnavailable"], StringComparer.Ordinal))) return false;
        string selection = SelectionIdentity(expectedLaunchId, data.IdentityScope, data.BackingIdentity, data.PlayerId,
            data.LocationName, data.TileX, data.TileY, data.ChestItemId);
        return data.SelectionIdentity == selection
            && data.ContainerRevision == Revision(selection, data.Player, data.Container, data.HeldItem);
    }

    public static ReviewObservedItem ReadItem(ItemReadState state) => state.ItemPresent
        ? ReadOccupied(state.Read, state.Identity)
        : new("empty", null, null, null);

    private static ReviewObservedItem ReadOccupied(Func<SelectedItemValues> read, ReviewContainerItemIdentity? identity)
    {
        ReviewItemSlot slot = ReviewItemSlotContract.ReadOccupied(0, read);
        return new(slot.State, slot.Reason, slot.Item, identity);
    }

    private static bool SideValid(ReviewContainerSide? side, string expectedSide, int maximum, int? exactCapacity) =>
        side is not null && side.Side == expectedSide && side.Capacity is > 0 && side.Capacity <= maximum
        && (exactCapacity is null || side.Capacity == exactCapacity) && side.Slots is not null
        && side.Slots.Count == side.Capacity
        && Enumerable.Range(0, side.Capacity).All(index => ContainerSlotValid(side.Slots[index], index));

    private static bool ContainerSlotValid(ReviewContainerSlot? slot, int expectedIndex) => slot is not null
        && ReviewItemSlotContract.SlotValid(new(slot.Slot, slot.State, slot.Reason, slot.Item), expectedIndex)
        && (slot.State == "empty" ? slot.Identity is null : IdentityValid(slot.Identity));

    private static bool ObservedItemValid(ReviewObservedItem? item) => item is not null
        && (item.State switch
        {
            "empty" => item.Reason is null && item.Item is null && item.Identity is null,
            "occupied" => item.Reason is null && ReviewItemSlotContract.ItemValid(item.Item) && IdentityValid(item.Identity),
            "unavailable" => item.Reason == "itemDataUnavailable" && item.Item is null && IdentityValid(item.Identity),
            _ => false,
        });

    private static bool IdentityValid(ReviewContainerItemIdentity? identity) => identity is not null
        && ReviewInventoryContract.IsRevision(identity.InstanceIdentity)
        && ReviewInventoryContract.IsRevision(identity.ItemRevision);

    private static bool SidesComplete(ReviewContainerValues data) =>
        data.Player.Slots.All(slot => slot.State != "unavailable")
        && data.Container.Slots.All(slot => slot.State != "unavailable")
        && data.HeldItem.State != "unavailable";

    private static bool TextValid(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum
        && !value.Any(char.IsControl);

    private static string Hash(params string[] values)
    {
        var text = new StringBuilder();
        foreach (string value in values)
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}

internal sealed record ItemReadState(bool ItemPresent, Func<SelectedItemValues> Read,
    ReviewContainerItemIdentity? Identity = null);
internal sealed record ReviewContainerItemIdentity(string InstanceIdentity, string ItemRevision);
internal sealed record ReviewContainerSlot(int Slot, string State, string? Reason, SelectedItemValues? Item,
    ReviewContainerItemIdentity? Identity);
internal sealed record ReviewContainerSide(string Side, int Capacity, IReadOnlyList<ReviewContainerSlot> Slots);
internal sealed record ReviewObservedItem(string State, string? Reason, SelectedItemValues? Item,
    ReviewContainerItemIdentity? Identity);
internal sealed record ReviewContainerValues(string CaptureId, string SelectionIdentity, string ContainerRevision,
    string IdentityScope, string BackingIdentity, string PlayerId, string LocationName, int TileX, int TileY, string ChestItemId,
    ReviewContainerSide Player, ReviewContainerSide Container, ReviewObservedItem HeldItem,
    bool Complete, IReadOnlyList<string> Limitations);
internal sealed record ReviewContainerReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset CapturedAtUtc, ReviewContainerValues? Data);
internal sealed record ReviewContainerResponseEnvelope(int SchemaVersion, string RequestId, ReviewContainerReport Report);
