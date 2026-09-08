#if SDVKIT_GAME_AVAILABLE
using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewObject = StardewValley.Object;

namespace SdvKit.AlwaysOn;

internal static class ReviewContainerCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void Handle(string[] args, string runtimePath, IMonitor monitor, ReviewMenuCommand menuCommand)
    {
        if (args.Length != 3 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]))
        {
            monitor.Log("SDVKit review-container rejected an invalid request.", LogLevel.Error);
            return;
        }
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        ReviewContainerReport Failure(string code) => new(ReviewContainerContract.SchemaVersion,
            "unavailable", code, launch, "single", null, DateTimeOffset.UtcNow, null);
        ReviewContainerReport report;
        try
        {
            report = Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1" || args[2] != launch
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
                    ? Failure("containerReviewBindingInvalid")
                    : !Context.IsWorldReady || Game1.exitToTitle ? Failure("containerWorldNotReady")
                    : Capture(args[1], launch, menuCommand);
        }
        catch (Exception)
        {
            report = Failure("containerCaptureFailed");
        }
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new ReviewContainerResponseEnvelope(ReviewContainerContract.SchemaVersion, args[1], report), JsonOptions);
            if (bytes.Length > ReviewContainerContract.MaximumResponseBytes)
                bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ReviewContainerResponseEnvelope(ReviewContainerContract.SchemaVersion, args[1],
                        Failure("containerResponseLimit")), JsonOptions);
            ReviewResponseFile.Write(ReviewContainerContract.ResponsePath(runtimePath, args[1]), bytes);
        }
        catch (Exception)
        {
            monitor.Log("SDVKit review-container could not publish its bounded response.", LogLevel.Error);
        }
    }

    private static ReviewContainerReport Capture(string captureId, string launch, ReviewMenuCommand menuCommand)
    {
        ReviewContainerReport Failure(string code) => new(ReviewContainerContract.SchemaVersion,
            "unavailable", code, launch, "single", null, DateTimeOffset.UtcNow, null);
        ItemGrabMenu? menu = Game1.activeClickableMenu as ItemGrabMenu;
        Chest? chest = menu?.context as Chest;
        Farmer player = Game1.player;
        GameLocation location = Game1.currentLocation;
        IInventory? chestItems = chest?.GetItemsForPlayer(player.UniqueMultiplayerID);
        int capacity = chest?.GetActualCapacity() ?? 0;
        bool tileIntegral = chest is not null && chest.TileLocation.X == MathF.Truncate(chest.TileLocation.X)
            && chest.TileLocation.Y == MathF.Truncate(chest.TileLocation.Y);
        bool placed = tileIntegral && location.Objects.TryGetValue(chest!.TileLocation, out StardewObject? placedObject)
            && ReferenceEquals(placedObject, chest) && ReferenceEquals(chest.Location, location);
        bool regularId = chest?.QualifiedItemId is "(BC)130" or "(BC)232";
        bool regularFlags = chest is not null && chest.playerChest.Value && !chest.fridge.Value && !chest.giftbox.Value
            && chest.SpecialChestType == Chest.SpecialChestTypes.None && string.IsNullOrEmpty(chest.GlobalInventoryId);
        string? unavailable = ReviewContainerContract.UnavailableReason(
            menu?.GetType() == typeof(ItemGrabMenu), menu?.GetChildMenu() is not null,
            menu is not null && menu.source == ItemGrabMenu.source_chest,
            chest?.GetType() == typeof(Chest), menu is not null && ReferenceEquals(menu.sourceItem, chest),
            regularId, regularFlags, placed,
            menu?.ItemsToGrabMenu is not null && ReferenceEquals(menu.ItemsToGrabMenu.actualInventory, chestItems),
            menu?.inventory is not null && ReferenceEquals(menu.inventory.actualInventory, player.Items),
            capacity, chestItems?.Count ?? -1, player.Items.Count);
        if (unavailable is not null) return Failure(unavailable);
        string? identityScope = menuCommand.CurrentIdentityScope();
        if (!ReviewTransportToken.IsRequestId(identityScope)) return Failure("containerIdentityUnavailable");

        string backingIdentity = ReviewContainerContract.OpaqueIdentity(launch, identityScope!, "chest",
            menuCommand.CurrentObservationId(chest!));
        ReviewContainerSide playerSide = ReadSide("player", player.Items.Count, player.Items,
            launch, identityScope!, menuCommand);
        ReviewContainerSide containerSide = ReadSide("container", capacity, chestItems!,
            launch, identityScope!, menuCommand);
        ReviewObservedItem held = ReadItem(menu!.heldItem, launch, identityScope!, menuCommand);
        bool complete = playerSide.Slots.All(slot => slot.State != "unavailable")
            && containerSide.Slots.All(slot => slot.State != "unavailable") && held.State != "unavailable";
        string playerId = player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture);
        int tileX = (int)chest!.TileLocation.X;
        int tileY = (int)chest.TileLocation.Y;
        string selection = ReviewContainerContract.SelectionIdentity(launch, identityScope!, backingIdentity, playerId,
            location.NameOrUniqueName, tileX, tileY, chest.QualifiedItemId);
        var values = new ReviewContainerValues(captureId, selection,
            ReviewContainerContract.Revision(selection, playerSide, containerSide, held), identityScope!, backingIdentity, playerId,
            location.NameOrUniqueName, tileX, tileY, chest.QualifiedItemId, playerSide, containerSide, held,
            complete, complete ? [] : ["itemDataUnavailable"]);
        return ReviewContainerContract.DataValid(values, launch)
            ? new(ReviewContainerContract.SchemaVersion, "ready", null, launch, "single", null,
                DateTimeOffset.UtcNow, values)
            : Failure("containerValuesInvalid");
    }

    private static ReviewContainerSide ReadSide(string side, int capacity, IList<Item> items,
        string launch, string identityScope, ReviewMenuCommand menuCommand)
    {
        var slots = new List<ReviewContainerSlot>(capacity);
        for (int index = 0; index < capacity; index++)
        {
            Item? item = index < items.Count ? items[index] : null;
            if (item is null)
            {
                slots.Add(new(index, "empty", null, null, null));
                continue;
            }
            ReviewContainerItemIdentity identity = ReadIdentity(item, launch, identityScope, menuCommand,
                out bool identityAvailable);
            ReviewItemSlot observed = identityAvailable
                ? ReviewItemSlotContract.ReadOccupied(index, () => ReadItemValues(item))
                : new(index, "unavailable", "itemDataUnavailable", null);
            slots.Add(new(index, observed.State, observed.Reason, observed.Item, identity));
        }
        return new(side, capacity, slots.AsReadOnly());
    }

    private static ReviewObservedItem ReadItem(Item? item, string launch, string identityScope,
        ReviewMenuCommand menuCommand) => item is null
        ? new("empty", null, null, null)
        : ReadHeld(item, launch, identityScope, menuCommand);

    private static ReviewContainerItemIdentity ReadIdentity(Item item, string launch, string identityScope,
        ReviewMenuCommand menuCommand, out bool available)
    {
        string instance = ReviewContainerContract.OpaqueIdentity(launch, identityScope, "item",
            menuCommand.CurrentObservationId(item));
        try
        {
            if (item.GetType().Assembly != typeof(Item).Assembly)
                throw new InvalidOperationException("Mod item stacking semantics are outside the supported family.");
            available = true;
            return new(instance, ReviewContainerContract.NativeItemRevision(instance,
                item.GetType().FullName ?? item.GetType().Name, item.maximumStackSize(), item.Name,
                item is ColoredObject colored ? colored.color.Value.PackedValue : null,
                item is StardewObject obj ? obj.orderData.Value : null));
        }
        catch (Exception)
        {
            available = false;
            return new(instance, ReviewContainerContract.NativeItemRevision(instance,
                "unavailable", 0, "unavailable", null, null));
        }
    }

    private static ReviewObservedItem ReadHeld(Item item, string launch, string identityScope,
        ReviewMenuCommand menuCommand)
    {
        ReviewContainerItemIdentity identity = ReadIdentity(item, launch, identityScope, menuCommand,
            out bool identityAvailable);
        return identityAvailable
            ? ReviewContainerContract.ReadItem(new(true, () => ReadItemValues(item), identity))
            : new("unavailable", "itemDataUnavailable", null, identity);
    }

    private static SelectedItemValues ReadItemValues(Item item) =>
        new(item.QualifiedItemId, item.Stack, item is StardewObject obj ? obj.Quality : null);
}
#endif
