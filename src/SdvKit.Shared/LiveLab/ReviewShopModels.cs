namespace SdvKit.Cli.LiveLab;

internal static class ReviewShopContract
{
    public const int MaximumOffers = 256;
    public const int MaximumInventorySlots = 144;
    public const int MaximumResponseBytes = 128 * 1024;

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-shop-{requestId}.json");

    public static string? ShopUnavailableReason(bool exactRoot, string? shopId, bool hasShopData,
        int currency, int dataCurrency, bool hasChild, bool readOnly, bool purchaseCallback, bool purchaseCheck) =>
        !exactRoot ? "shopMenuUnsupported"
        : shopId != "SeedShop" || !hasShopData ? "shopFamilyUnsupported"
        : currency != 0 || dataCurrency != 0 ? "shopCurrencyUnsupported"
        : hasChild || readOnly || purchaseCallback || purchaseCheck ? "shopBehaviorUnsupported"
        : null;

    public static string? OfferUnavailableReason(bool ordinaryObject, bool recipe, bool bigCraftable,
        bool lostItem, string? qualifiedItemId, int stack, bool buyback, bool trade,
        bool purchaseActions, bool syncItem, int price, int stock) =>
        !ordinaryObject || recipe || bigCraftable || lostItem || qualifiedItemId is "(O)434" or "(O)858"
            || stack != 1 ? "shopItemUnsupported"
        : buyback || trade || purchaseActions || syncItem ? "shopOfferBehaviorUnsupported"
        : price < 0 || stock < 0 ? "shopOfferValuesInvalid"
        : null;

    public static bool ItemValid(SelectedItemValues? item) => ReviewItemSlotContract.ItemValid(item);

    public static bool DataValid(ReviewShopValues? data)
    {
        if (data is null || data.ShopId != "SeedShop" || data.Currency != "Gold" || data.ScrollIndex < 0
            || !ReviewTransportToken.IsRequestId(data.IdentityScope)
            || !long.TryParse(data.PlayerId, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out long playerId) || playerId == 0
            || data.PlayerId != playerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || data.Offers is null || data.Offers.Count > MaximumOffers
            || data.ScrollIndex > data.Offers.Count
            || data.Inventory is null || data.Inventory.Count > MaximumInventorySlots
            || data.HeldItem is not null && !ItemValid(data.HeldItem)) return false;
        for (int index = 0; index < data.Offers.Count; index++)
        {
            ReviewShopOffer offer = data.Offers[index];
            if (offer is null || offer.ForSaleIndex != index) return false;
            if (offer.Availability == "available")
            {
                if (offer.Reason is not null || !ItemValid(offer.Item) || offer.Item!.Stack != 1
                    || !offer.Item.QualifiedItemId.StartsWith("(O)", StringComparison.Ordinal)
                    || offer.Item.QualifiedItemId is "(O)434" or "(O)858" || offer.Item.Quality is null
                    || offer.Price is not >= 0 || offer.UnlimitedStock is null
                    || (offer.UnlimitedStock.Value ? offer.Stock is not null : offer.Stock is not >= 0 or int.MaxValue)) return false;
            }
            else if (offer.Availability != "unavailable" || offer.Reason is not
                ("shopItemUnsupported" or "shopOfferBehaviorUnsupported" or "shopOfferValuesInvalid" or "shopStockMissing")
                || offer.Item is not null || offer.Price is not null || offer.Stock is not null || offer.UnlimitedStock is not null) return false;
        }
        for (int index = 0; index < data.Inventory.Count; index++)
        {
            ReviewShopInventorySlot slot = data.Inventory[index];
            if (slot is null || slot.Slot != index || slot.Item is not null && !ItemValid(slot.Item)) return false;
        }
        return true;
    }
}

internal sealed record ReviewShopOffer(int ForSaleIndex, string Availability, string? Reason,
    SelectedItemValues? Item, int? Price, int? Stock, bool? UnlimitedStock);
internal sealed record ReviewShopInventorySlot(int Slot, SelectedItemValues? Item);
internal sealed record ReviewShopValues(string ShopId, string Currency, string IdentityScope, string PlayerId, int ScrollIndex,
    IReadOnlyList<ReviewShopOffer> Offers, int Money, IReadOnlyList<ReviewShopInventorySlot> Inventory,
    SelectedItemValues? HeldItem);
internal sealed record ReviewShopReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset CapturedAtUtc, ReviewShopValues? Data);
internal sealed record ReviewShopResponseEnvelope(int SchemaVersion, string RequestId, ReviewShopReport Report);
