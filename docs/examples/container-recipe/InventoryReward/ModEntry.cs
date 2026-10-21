using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace InventoryReward;

public sealed class ModEntry : Mod
{
    private const string RewardItemId = "(O)390";
    private const int RewardQuantity = 10;
    private const string GrantedKey = "ExampleAuthor.InventoryReward/RewardGranted";
    private const string ChestKey = "ExampleAuthor.InventoryReward/Chest";

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.ConsoleCommands.Add("inventory_reward_chest", "Place one empty wooden chest next to the player.", (_, _) => PrepareChest());
        helper.ConsoleCommands.Add("inventory_reward_open", "Open the marked chest.", (_, _) => OpenChest());
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        // The marker is written only after the whole stack was accepted, so a save never grants twice.
        if (Context.IsMultiplayer || Game1.player.modData.ContainsKey(GrantedKey))
            return;

        Item reward = ItemRegistry.Create(RewardItemId, RewardQuantity);
        if (!Game1.player.addItemToInventoryBool(reward))
        {
            Monitor.Log("The reward does not fit in the backpack; nothing was marked as granted.", LogLevel.Warn);
            return;
        }

        Game1.player.modData[GrantedKey] = "true";
        Monitor.Log($"Granted {RewardQuantity} Stone once for this save.", LogLevel.Info);
    }

    private void PrepareChest()
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer || FindChest() is not null)
        {
            Monitor.Log("inventory_reward_chest needs a loaded single-player world without a marked chest.", LogLevel.Error);
            return;
        }

        GameLocation location = Game1.currentLocation;
        Vector2 origin = Game1.player.Tile;
        for (int radius = 2; radius <= 6; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    Vector2 tile = new((int)origin.X + dx, (int)origin.Y + dy);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius || !location.isTileOnMap(tile) || !location.isTilePlaceable(tile)
                        || location.Objects.ContainsKey(tile) || location.terrainFeatures.ContainsKey(tile))
                        continue;

                    var chest = new Chest(playerChest: true, tile, "130");
                    chest.modData[ChestKey] = "true";
                    location.Objects.Add(tile, chest);
                    Monitor.Log($"Placed an empty chest in {location.NameOrUniqueName} at {(int)tile.X},{(int)tile.Y}.", LogLevel.Info);
                    return;
                }
            }
        }

        Monitor.Log("No free tile near the player for the chest.", LogLevel.Error);
    }

    private void OpenChest()
    {
        Chest? chest = FindChest();
        if (!Context.IsWorldReady || chest is null || !ReferenceEquals(chest.Location, Game1.currentLocation))
        {
            Monitor.Log("inventory_reward_open needs the marked chest in the player's location.", LogLevel.Error);
            return;
        }

        if (Game1.activeClickableMenu is not null)
            Game1.exitActiveMenu();
        chest.ShowMenu();
        Monitor.Log($"Opened the chest with {chest.Items.Count(item => item is not null)} occupied slots.", LogLevel.Info);
    }

    private static Chest? FindChest()
    {
        Chest? found = null;
        Utility.ForEachLocation(location =>
        {
            foreach (var pair in location.Objects.Pairs)
            {
                if (pair.Value is Chest chest && chest.modData.ContainsKey(ChestKey))
                    found = chest;
            }

            return true;
        });
        return found;
    }
}
