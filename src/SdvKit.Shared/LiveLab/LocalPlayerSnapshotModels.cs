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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SelectedItemValues? SelectedItem,
    FishingRodValues? Fishing = null,
    PlayerPositionValues? Position = null,
    IReadOnlyList<LocationNpcValues>? Npcs = null);

// A villager in the player's current location (NPC.IsVillager; no pets, horses or monsters). Name is the internal
// name used by tools; DisplayName is the localized one.
internal sealed record LocationNpcValues(
    string Name,
    string DisplayName,
    int TileX,
    int TileY,
    string Facing);

// Where the player stands and what it faces. StandingX/Y are the standing pixel divided by 64, so their floor
// is the tile. The grab tile is Farmer.GetGrabTile(), which the action button checks first (then the tiles below
// and above it, then the player's own tile); it can be the player's own tile. The tool tile is where a tool used
// now lands (Farmer.GetToolLocation without the mouse). The mouse cursor can redirect both near the player.
internal sealed record PlayerPositionValues(
    string Facing,
    float StandingX,
    float StandingY,
    int GrabTileX,
    int GrabTileY,
    int ToolTileX,
    int ToolTileY);

// Instantaneous observations, not a catch history or an automatic fishing controller.
internal sealed record FishingRodValues(
    bool TimingCast,
    bool Casting,
    bool BobberInAir,
    bool Fishing,
    bool Nibbling,
    bool Hit,
    bool PullingOut,
    bool CatchReady,
    bool FromFishPond,
    float BobberTileX,
    float BobberTileY,
    float BiteMilliseconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CatchItemId,
    int CatchQuantity);

internal sealed record SelectedItemValues(
    string QualifiedItemId,
    int Stack,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Quality);

internal static class LocalPlayerSnapshotContract
{
    public const int SchemaVersion = 1;
    public const int MaximumItemIdLength = 256;
    public const int MaximumNpcs = 64;
    public const int MaximumNpcNameLength = 128;

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
            || values.SelectedSlot is not null && ItemValid(values.SelectedItem))
        && (values.Fishing is null || values.SelectedItem is not null && FishingValid(values.Fishing))
        && (values.Position is null || PositionValid(values.Position))
        && (values.Npcs is null || values.Npcs.Count <= MaximumNpcs && values.Npcs.All(NpcValid));

    public static bool NpcValid(LocationNpcValues npc) =>
        NameValid(npc.Name)
        && NameValid(npc.DisplayName)
        && FacingDirections.Contains(npc.Facing)
        && Math.Abs(npc.TileX) <= 100000
        && Math.Abs(npc.TileY) <= 100000;

    private static bool NameValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaximumNpcNameLength
        && !name.Any(char.IsControl);

    public static readonly string[] FacingDirections = ["up", "right", "down", "left"];

    // Stardew's FacingDirection: 0 up, 1 right, 2 down, 3 left.
    public static string? FacingName(int direction) =>
        direction is >= 0 and <= 3 ? FacingDirections[direction] : null;

    private static bool PositionValid(PlayerPositionValues position) =>
        FacingDirections.Contains(position.Facing)
        && float.IsFinite(position.StandingX)
        && float.IsFinite(position.StandingY)
        && Math.Abs(position.StandingX) <= 100000
        && Math.Abs(position.StandingY) <= 100000
        && Math.Abs(position.GrabTileX) <= 100000
        && Math.Abs(position.GrabTileY) <= 100000
        && Math.Abs(position.ToolTileX) <= 100000
        && Math.Abs(position.ToolTileY) <= 100000;

    private static bool FishingValid(FishingRodValues rod) =>
        float.IsFinite(rod.BobberTileX)
        && float.IsFinite(rod.BobberTileY)
        && float.IsFinite(rod.BiteMilliseconds)
        && rod.CatchQuantity >= 0
        && (rod.CatchItemId is null || ItemValid(new SelectedItemValues(rod.CatchItemId, 1, null)));

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
