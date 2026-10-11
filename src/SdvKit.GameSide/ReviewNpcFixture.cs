using Microsoft.Xna.Framework;
using SdvKit.Cli.LiveLab;
using StardewValley;

namespace SdvKit.GameSide;

internal sealed partial class StardewReviewFixtureRuntime
{
    public ReviewFixtureResult PlaceNpc(ReviewFixtureAccess access, string name, int x, int y)
    {
        // Operation dispatch has just verified the owned disposable save and main-player role.
        Farmer player = Game1.player;
        GameLocation? location = player.currentLocation;
        if (location is null || Game1.activeClickableMenu is not null || Game1.eventUp || Game1.isFestival()
            || Game1.fadeToBlack)
        {
            return new ReviewFixtureResult(false, "NPC placement requires a loaded location with no menu, event, festival or fade; nothing moved.");
        }

        NPC? npc = Game1.getCharacterFromName(name, mustBeVillager: true);
        if (npc is null || npc.Name != name)
        {
            return new ReviewFixtureResult(false, $"No villager is named {name}; nothing moved.");
        }

        var tile = new Vector2(x, y);
        if (player.TilePoint == new Point(x, y) || !location.CanSpawnCharacterHere(tile))
        {
            return new ReviewFixtureResult(false, $"Tile {x},{y} in {location.NameOrUniqueName} is not free for a character; nothing moved.");
        }

        Game1.warpCharacter(npc, location, tile);
        // Stop the current path so the villager stays put until its schedule moves it again.
        npc.Halt();
        npc.controller = null;
        npc.temporaryController = null;
        npc.faceGeneralDirection(player.getStandingPosition(), 0, opposite: false, useTileCalculations: true);
        if (npc.currentLocation != location || npc.TilePoint != new Point(x, y))
        {
            return new ReviewFixtureResult(false, $"{name} was warped but is not at {x},{y} in {location.NameOrUniqueName}; read the NPCs again.");
        }

        return new ReviewFixtureResult(true,
            $"Placed {name} at {x},{y} in {location.NameOrUniqueName}. Test setup; its schedule can move it again.",
            Navigation: new ReviewFixtureNavigationReport(location.NameOrUniqueName, x, y, Changed: true));
    }
}
