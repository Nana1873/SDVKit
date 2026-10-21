#if SDVKIT_GAME_AVAILABLE
using System.Globalization;
using SdvKit.Cli.LiveLab;
using StardewValley;
using StardewValley.Tools;

namespace SdvKit.GameSide;

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
                    item is StardewValley.Object obj ? obj.Quality : null),
                item is FishingRod rod ? new FishingRodValues(
                    rod.isTimingCast, rod.isCasting, rod.castedButBobberStillInAir,
                    rod.isFishing, rod.isNibbling, rod.hit, rod.pullingOutOfWater,
                    rod.fishCaught, rod.fromFishPond,
                    rod.bobber.Value.X / 64f, rod.bobber.Value.Y / 64f,
                    rod.timeUntilFishingBite,
                    rod.fishCaught ? rod.whichFish?.QualifiedItemId : null,
                    rod.fishCaught ? Math.Max(0, rod.numberOfFishCaught) : 0) : null,
                CapturePosition(player),
                CaptureNpcs(player),
                CaptureInputMode());
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

    private static PlayerInputModeValues? CaptureInputMode()
    {
        // Left out rather than failing the whole player snapshot; the game's enum has no other modes.
        try
        {
            string? mode = Game1.options.gamepadMode switch
            {
                Options.GamepadModes.Auto => "auto",
                Options.GamepadModes.ForceOn => "forceOn",
                Options.GamepadModes.ForceOff => "forceOff",
                _ => null,
            };
            return mode is null ? null : new PlayerInputModeValues(
                Game1.options.gamepadControls,
                mode,
                Game1.wasMouseVisibleThisFrame && Game1.mouseCursorTransparency > 0f,
                Game1.input.GetGamePadState().IsConnected);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static LocationNpcValues[]? CaptureNpcs(Farmer player)
    {
        if (player.currentLocation is not { } location)
        {
            return [];
        }

        // Event and festival actors live in the event, not in location.characters, so they are not listed.
        var npcs = new List<LocationNpcValues>();
        foreach (NPC npc in location.characters)
        {
            // A modded NPC whose getters throw or whose values are unusable is left out rather than failing the
            // whole player snapshot. Invisible villagers (for example on hospital days) cannot be talked to.
            try
            {
                if (npc is null || !npc.IsVillager || npc.IsInvisible || string.IsNullOrWhiteSpace(npc.Name))
                {
                    continue;
                }

                var values = new LocationNpcValues(npc.Name,
                    string.IsNullOrWhiteSpace(npc.displayName) ? npc.Name : npc.displayName,
                    npc.TilePoint.X, npc.TilePoint.Y, LocalPlayerSnapshotContract.FacingName(npc.FacingDirection) ?? "");
                if (LocalPlayerSnapshotContract.NpcValid(values))
                {
                    npcs.Add(values);
                }
            }
            catch (Exception)
            {
            }
        }

        return npcs.OrderBy(npc => npc.Name, StringComparer.Ordinal)
            .Take(LocalPlayerSnapshotContract.MaximumNpcs)
            .ToArray();
    }

    internal static PlayerPositionValues? CapturePosition(Farmer player)
    {
        string? facing = LocalPlayerSnapshotContract.FacingName(player.FacingDirection);
        if (facing is null)
        {
            return null;
        }

        Microsoft.Xna.Framework.Point standing = player.StandingPixel;
        Microsoft.Xna.Framework.Vector2 grab = player.GetGrabTile();
        // A stale lastClick on the player's own tile would shift the overload without a position; use a neutral one.
        Microsoft.Xna.Framework.Vector2 tool = player.GetToolLocation(Microsoft.Xna.Framework.Vector2.Zero, ignoreClick: true);
        return new PlayerPositionValues(facing, standing.X / 64f, standing.Y / 64f, (int)grab.X, (int)grab.Y,
            (int)Math.Floor(tool.X / 64f), (int)Math.Floor(tool.Y / 64f));
    }
}
#endif
