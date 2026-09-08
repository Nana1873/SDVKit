using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewItemSlotContract
{
    public static ReviewItemSlot ReadOccupied(int slot, Func<SelectedItemValues> read)
    {
        try
        {
            SelectedItemValues item = read();
            return ItemValid(item)
                ? new(slot, "occupied", null, item)
                : new(slot, "unavailable", "itemDataUnavailable", null);
        }
        catch (Exception)
        {
            return new(slot, "unavailable", "itemDataUnavailable", null);
        }
    }

    public static bool ItemValid(SelectedItemValues? item) => item is not null
        && item.QualifiedItemId is { Length: > 3 and <= LocalPlayerSnapshotContract.MaximumItemIdLength } id
        && id[0] == '(' && id.IndexOf(')') is > 1 && id[^1] != ')'
        && !id.Any(char.IsControl) && item.Stack > 0 && item.Quality is null or >= 0;

    public static bool SlotValid(ReviewItemSlot? slot, int expectedIndex) => slot is not null
        && slot.Slot == expectedIndex
        && (slot.State switch
        {
            "empty" => slot.Reason is null && slot.Item is null,
            "occupied" => slot.Reason is null && ItemValid(slot.Item),
            "unavailable" => slot.Reason == "itemDataUnavailable" && slot.Item is null,
            _ => false,
        });
}

internal static class ReviewInventoryContract
{
    public const int SchemaVersion = 1;
    public const int MaximumSlots = 144;
    public const int MaximumResponseBytes = 64 * 1024;

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-inventory-{requestId}.json");

    public static bool IsRevision(string? value) => value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.Skip(7).All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static string Revision(string launchId, string playerId, int capacity, int? selectedSlot,
        IReadOnlyList<ReviewItemSlot> slots)
    {
        var text = new StringBuilder();
        Append(launchId);
        Append(playerId);
        Append(capacity.ToString(CultureInfo.InvariantCulture));
        Append(selectedSlot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        foreach (ReviewItemSlot slot in slots)
        {
            Append(slot.Slot.ToString(CultureInfo.InvariantCulture));
            Append(slot.State);
            Append(slot.Reason ?? string.Empty);
            Append(slot.Item?.QualifiedItemId ?? string.Empty);
            Append(slot.Item?.Stack.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Append(slot.Item?.Quality?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();

        void Append(string value) => text.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append(';');
    }

    public static bool DataValid(ReviewInventoryValues? data, string expectedLaunchId)
    {
        if (data is null || !ReviewTransportToken.IsRequestId(data.CaptureId)
            || !long.TryParse(data.PlayerId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long playerId)
            || playerId == 0 || data.PlayerId != playerId.ToString(CultureInfo.InvariantCulture)
            || data.Capacity is <= 0 or > MaximumSlots
            || data.SelectedSlot is < 0 || data.SelectedSlot >= data.Capacity
            || data.Slots is null || data.Slots.Count != data.Capacity
            || data.Slots.Any(slot => slot is null)
            || data.Complete != data.Slots.All(slot => slot.State != "unavailable")
            || data.Limitations is null || data.Limitations.Count > 1
            || (data.Complete ? data.Limitations.Count != 0
                : !data.Limitations.SequenceEqual(["itemDataUnavailable"], StringComparer.Ordinal))) return false;
        for (int index = 0; index < data.Slots.Count; index++)
            if (!ReviewItemSlotContract.SlotValid(data.Slots[index], index)) return false;
        return ReviewTransportToken.IsRequestId(expectedLaunchId)
            && data.InventoryRevision == Revision(expectedLaunchId, data.PlayerId, data.Capacity,
                data.SelectedSlot, data.Slots);
    }
}

internal sealed record ReviewItemSlot(int Slot, string State, string? Reason, SelectedItemValues? Item);
internal sealed record ReviewInventoryValues(string CaptureId, string InventoryRevision, string PlayerId, int Capacity,
    int? SelectedSlot, bool Complete, IReadOnlyList<string> Limitations, IReadOnlyList<ReviewItemSlot> Slots);
internal sealed record ReviewInventoryReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset CapturedAtUtc, ReviewInventoryValues? Data);
internal sealed record ReviewInventoryResponseEnvelope(int SchemaVersion, string RequestId, ReviewInventoryReport Report);
