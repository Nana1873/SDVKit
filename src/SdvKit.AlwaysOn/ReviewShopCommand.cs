#if SDVKIT_GAME_AVAILABLE
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewObject = StardewValley.Object;

namespace SdvKit.AlwaysOn;

internal static class ReviewShopCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void Handle(string[] args, string runtimePath, IMonitor monitor, ReviewMenuCommand menuCommand)
    {
        if (args.Length != 3 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]))
        {
            monitor.Log("SDVKit review-shop rejected an invalid request.", LogLevel.Error);
            return;
        }
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        ReviewShopReport Failure(string code) => new(1, "unavailable", code, launch,
            "single", null, DateTimeOffset.UtcNow, null);
        ReviewShopReport report;
        try
        {
            report = Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1" || args[2] != launch
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
                    ? Failure("shopReviewBindingInvalid")
                    : !Context.IsWorldReady || Game1.exitToTitle ? Failure("shopWorldNotReady")
                    : Capture(launch, menuCommand);
        }
        catch (Exception)
        {
            report = Failure("shopCaptureFailed");
        }
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewShopResponseEnvelope(1, args[1], report), JsonOptions);
            if (bytes.Length > ReviewShopContract.MaximumResponseBytes)
                bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewShopResponseEnvelope(1, args[1], Failure("shopResponseLimit")), JsonOptions);
            ReviewResponseFile.Write(ReviewShopContract.ResponsePath(runtimePath, args[1]), bytes);
        }
        catch (Exception)
        {
            monitor.Log("SDVKit review-shop could not publish its bounded response.", LogLevel.Error);
        }
    }

    private static ReviewShopReport Capture(string launch, ReviewMenuCommand menuCommand)
    {
        ReviewShopReport Failure(string code) => new(1, "unavailable", code, launch,
            "single", null, DateTimeOffset.UtcNow, null);
        var shop = Game1.activeClickableMenu as ShopMenu;
        string? unsupported = ReviewShopContract.ShopUnavailableReason(shop?.GetType() == typeof(ShopMenu),
            shop?.ShopId, shop?.ShopData is not null, shop?.currency ?? -1, shop?.ShopData?.Currency ?? -1,
            shop?.GetChildMenu() is not null, shop?.readOnly ?? false, shop?.onPurchase is not null, shop?.canPurchaseCheck is not null);
        if (unsupported is not null) return Failure(unsupported);
        if (shop!.forSale.Count > ReviewShopContract.MaximumOffers
            || Game1.player.Items.Count > ReviewShopContract.MaximumInventorySlots) return Failure("shopCaptureLimit");
        var offers = new List<ReviewShopOffer>();
        for (int index = 0; index < shop.forSale.Count; index++)
        {
            ISalable sale = shop.forSale[index];
            var item = sale as StardewObject;
            if (!shop.itemPriceAndStock.TryGetValue(sale, out var stock))
            {
                offers.Add(new(index, "unavailable", "shopStockMissing", null, null, null, null));
                continue;
            }
            string? reason = ReviewShopContract.OfferUnavailableReason(sale.GetType() == typeof(StardewObject),
                item?.IsRecipe ?? false, item?.bigCraftable.Value ?? false, item?.isLostItem ?? false,
                item?.QualifiedItemId, sale.Stack, shop.buyBackItems.Contains(sale), stock.TradeItem is not null
                    || stock.TradeItemCount is not null, stock.ActionsOnPurchase is { Count: > 0 },
                stock.ItemToSyncStack is not null, stock.Price, stock.Stock);
            offers.Add(reason is not null ? new(index, "unavailable", reason, null, null, null, null)
                : new(index, "available", null, ReadItem(item!), stock.Price,
                    stock.Stock == int.MaxValue ? null : stock.Stock, stock.Stock == int.MaxValue));
        }
        var inventory = new List<ReviewShopInventorySlot>();
        for (int index = 0; index < Game1.player.Items.Count; index++)
            inventory.Add(new(index, Game1.player.Items[index] is { } item ? ReadItem(item) : null));
        if (shop.heldItem is not null && shop.heldItem is not Item) return Failure("shopInventoryUnsupported");
        var values = new ReviewShopValues(shop.ShopId, "Gold", menuCommand.CurrentIdentityScope() ?? "",
            Game1.player.UniqueMultiplayerID.ToString(System.Globalization.CultureInfo.InvariantCulture), shop.currentItemIndex, offers.AsReadOnly(),
            Game1.player.Money, inventory.AsReadOnly(), shop.heldItem is Item held ? ReadItem(held) : null);
        return ReviewShopContract.DataValid(values)
            ? new(1, "ready", null, launch, "single", null, DateTimeOffset.UtcNow, values)
            : Failure("shopValuesInvalid");
    }

    private static SelectedItemValues ReadItem(Item item) =>
        new(item.QualifiedItemId, item.Stack, item is StardewObject obj ? obj.Quality : null);
}
#endif
