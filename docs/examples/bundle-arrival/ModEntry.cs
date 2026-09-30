using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace BundleArrival;

public sealed class Arrival
{
    public string Location { get; set; } = "Farm";
    public int TileX { get; set; } = 64;
    public int TileY { get; set; } = 15;
}

public sealed class ModEntry : Mod
{
    public override void Entry(IModHelper helper) => helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer)
            return;

        // The code effect is independent of whether a content pack is selected.
        Game1.player.Money += 7;
        IContentPack? pack = Helper.ContentPacks.GetOwned().SingleOrDefault(
            candidate => candidate.Manifest.UniqueID == "SDVKit.BundleArrival.Destination");
        if (pack is null)
        {
            Monitor.Log("Bundle Arrival destination pack is missing; no pack warp was applied.", LogLevel.Warn);
            return;
        }
        Arrival arrival = pack.ReadJsonFile<Arrival>("arrival.json")
            ?? throw new InvalidOperationException("Bundle Arrival destination JSON is missing.");
        if (arrival.Location != "Farm" || arrival.TileX != 64 || arrival.TileY != 15)
            throw new InvalidOperationException("This example accepts only its bounded Farm destination.");
        Game1.warpFarmer(arrival.Location, arrival.TileX, arrival.TileY, false);
    }
}
