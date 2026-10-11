using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Shops;

namespace ShopPurchaseProof;

public sealed class ModConfig
{
    public string ShopId { get; set; } = "SeedShop";
}

public sealed class ModEntry : Mod
{
    private const string OfferId = "ExampleAuthor.ShopPurchaseProof.Stone";
    private ModConfig Config = new();

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.ConsoleCommands.Add(
            "shop_purchase_proof_open",
            "Open the configured vanilla shop for the isolated purchase proof.",
            OnOpenShop);
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo("Data/Shops"))
            return;

        e.Edit(asset =>
        {
            IDictionary<string, ShopData> shops = asset.AsDictionary<string, ShopData>().Data;
            if (!shops.TryGetValue("SeedShop", out ShopData? shop))
            {
                Monitor.Log("Shop Purchase Proof could not find Data/Shops entry 'SeedShop'.", LogLevel.Error);
                return;
            }

            shop.Items.Insert(0, new ShopItemData
            {
                Id = OfferId,
                ItemId = "(O)390",
                Price = 10,
                AvailableStock = 3,
                IgnoreShopPriceModifiers = true,
            });
        });
    }

    private void OnOpenShop(string command, string[] args)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer)
        {
            Monitor.Log("Shop Purchase Proof requires a ready single-player world.", LogLevel.Error);
            return;
        }

        if (Game1.activeClickableMenu is not null)
        {
            Monitor.Log("Shop Purchase Proof requires no active menu before opening the shop.", LogLevel.Error);
            return;
        }

        Dictionary<string, ShopData> shops = Game1.content.Load<Dictionary<string, ShopData>>("Data/Shops");
        if (!shops.ContainsKey(Config.ShopId))
        {
            Monitor.Log($"Shop Purchase Proof could not open configured shop '{Config.ShopId}': Data/Shops has no matching entry.", LogLevel.Error);
            return;
        }

        if (!Utility.TryOpenShopMenu(Config.ShopId, ownerName: null, playOpenSound: true))
        {
            Monitor.Log($"Shop Purchase Proof could not open configured shop '{Config.ShopId}'.", LogLevel.Error);
            return;
        }

        Monitor.Log($"Shop Purchase Proof opened configured shop '{Config.ShopId}'.", LogLevel.Info);
    }
}
