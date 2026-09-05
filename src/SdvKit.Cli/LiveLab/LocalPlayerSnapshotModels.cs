using System.Globalization;
using System.Text.Json.Serialization;

namespace SdvKit.Cli.LiveLab;

internal sealed record LocalPlayerSnapshot(
    int SchemaVersion,
    string Availability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] LocalPlayerValues? Data);

internal sealed record LocalPlayerValues(
    string PlayerId,
    int Money,
    int Health,
    int MaxHealth,
    float Stamina,
    float MaxStamina,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SelectedSlot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SelectedItemValues? SelectedItem);

internal sealed record SelectedItemValues(
    string QualifiedItemId,
    int Stack,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Quality);

internal static class LocalPlayerSnapshotContract
{
    public const int SchemaVersion = 1;
    public const int MaximumItemIdLength = 256;

    public static LocalPlayerSnapshot WithoutData(string availability, string? reason = null) =>
        new(SchemaVersion, availability, reason, null);

    public static bool ValuesValid(LocalPlayerValues values) =>
        long.TryParse(values.PlayerId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id)
        && id != 0
        && values.PlayerId == id.ToString(CultureInfo.InvariantCulture)
        && float.IsFinite(values.Stamina)
        && float.IsFinite(values.MaxStamina)
        && values.SelectedSlot is null or >= 0
        && (values.SelectedItem is null
            || values.SelectedSlot is not null && ItemValid(values.SelectedItem));

    private static bool ItemValid(SelectedItemValues item) =>
        !string.IsNullOrWhiteSpace(item.QualifiedItemId)
        && item.QualifiedItemId.Length <= MaximumItemIdLength
        && item.QualifiedItemId[0] == '('
        && item.QualifiedItemId.IndexOf(')') is > 1 and < MaximumItemIdLength
        && item.QualifiedItemId[^1] != ')'
        && !item.QualifiedItemId.Any(char.IsControl);

    public static bool TryRead(LocalPlayerSnapshot? marker, bool worldReady, out LocalPlayerSnapshot report)
    {
        report = WithoutData("unavailable", "notPublished");
        if (marker is null)
        {
            return true;
        }

        if (marker.SchemaVersion != SchemaVersion)
        {
            report = WithoutData("unsupportedVersion", "unsupportedSchema");
            return true;
        }

        bool valid = marker.Availability switch
        {
            "available" => worldReady && marker.Reason is null
                && marker.Data is { } data && ValuesValid(data),
            "worldNotReady" => !worldReady && marker.Reason is null && marker.Data is null,
            "unavailable" => worldReady && marker.Reason == "selectionUnavailable" && marker.Data is null,
            "error" => worldReady && marker.Reason is "captureFailed" or "invalidValues" && marker.Data is null,
            _ => false,
        };
        if (valid)
        {
            report = marker;
        }

        return valid;
    }
}
