#if SDVKIT_GAME_AVAILABLE
using System.Reflection;
using StardewValley;

namespace SdvKit.AlwaysOn;

internal static class ReviewFarmhandPreparation
{
    internal static Farmer CreateSingleUnclaimedFarmhand()
    {
        List<Microsoft.Xna.Framework.Vector2> startingCabinLocations =
            typeof(GameLocation).GetField("_startingCabinLocations", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Game1.getFarm())
                as List<Microsoft.Xna.Framework.Vector2>
            ?? throw new InvalidOperationException(
                "Stardew's starting-cabin location state is unavailable.");
        if (startingCabinLocations.Count != 0)
        {
            throw new InvalidOperationException(
                "Stardew retained unexpected starting-cabin locations after loading the exact baseline.");
        }

        xTile.Layers.Layer pathsLayer = Game1.getFarm().Map.GetLayer("Paths")
            ?? throw new InvalidOperationException(
                "The exact disposable farm map has no Paths layer.");
        var candidates = new List<Microsoft.Xna.Framework.Vector2>();
        for (var x = 0; x < pathsLayer.LayerWidth; x++)
        {
            for (var y = 0; y < pathsLayer.LayerHeight; y++)
            {
                xTile.Tiles.Tile? tile = pathsLayer.Tiles[x, y];
                if (tile?.TileIndex == 29
                    && tile.Properties.TryGetValue("Order", out xTile.ObjectModel.PropertyValue? order)
                    && string.Equals(order?.ToString(), "1", StringComparison.Ordinal))
                {
                    candidates.Add(new Microsoft.Xna.Framework.Vector2(x, y));
                }
            }
        }

        if (candidates.Count != 1)
        {
            throw new InvalidOperationException(
                $"The exact disposable farm map exposed {candidates.Count} first-cabin locations instead of one.");
        }

        startingCabinLocations.Add(candidates[0]);
        Game1.getFarm().BuildStartingCabins();

        List<Farmer> farmhands = Game1.getAllFarmhands().ToList();
        if (farmhands.Count != 1 || !farmhands[0].isUnclaimedFarmhand)
        {
            throw new InvalidOperationException(
                "The loaded disposable fixture did not create exactly one unclaimed farmhand.");
        }

        return farmhands[0];
    }

}
#endif
