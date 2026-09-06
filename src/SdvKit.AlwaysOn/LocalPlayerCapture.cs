#if SDVKIT_GAME_AVAILABLE
using System.Globalization;
using SdvKit.Cli.LiveLab;
using StardewValley;

namespace SdvKit.AlwaysOn;

internal static class LocalPlayerCapture
{
    // Called only by the existing main-thread runtime capture. Never retain game objects.
    public static LocalPlayerSnapshot Capture(bool worldReady)
    {
        if (!worldReady)
        {
            return LocalPlayerSnapshotContract.WithoutData("worldNotReady");
        }

        try
        {
            Farmer player = Game1.player;
            int slot = player.CurrentToolIndex;
            if (slot < -1 || slot >= player.Items.Count)
            {
                return LocalPlayerSnapshotContract.WithoutData("unavailable", "selectionUnavailable");
            }

            Item? item = slot < 0 ? null : player.Items[slot];
            var values = new LocalPlayerValues(
                player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture),
                player.Money,
                player.health,
                player.maxHealth,
                player.Stamina,
                player.MaxStamina,
                slot < 0 ? null : slot,
                item is null ? null : new SelectedItemValues(
                    item.QualifiedItemId,
                    item.Stack,
                    item is StardewValley.Object obj ? obj.Quality : null));
            return LocalPlayerSnapshotContract.ValuesValid(values)
                ? new LocalPlayerSnapshot(LocalPlayerSnapshotContract.SchemaVersion, "available", null, values)
                : LocalPlayerSnapshotContract.WithoutData("error", "invalidValues");
        }
        catch (Exception)
        {
            // Modded item getters can fail. Publish an explicit error, never last-known values.
            return LocalPlayerSnapshotContract.WithoutData("error", "captureFailed");
        }
    }
}
#endif
