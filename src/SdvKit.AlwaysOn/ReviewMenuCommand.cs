#if SDVKIT_GAME_AVAILABLE
using System.Text.Json;
using Microsoft.Xna.Framework;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace SdvKit.AlwaysOn;

internal sealed class StardewReviewMenuSource : IReviewMenuSource
{
    public object? Root => Game1.activeClickableMenu;
    public ReviewMenuRectangle Viewport => new(Game1.uiViewport.X, Game1.uiViewport.Y,
        Game1.uiViewport.Width, Game1.uiViewport.Height);

    public MenuObservation Read(object instance)
    {
        var menu = (IClickableMenu)instance;
        Type type = menu.GetType();
        var components = new List<MenuComponentObservation>();
        var children = new List<MenuChildObservation>();
        bool truncated = false;
        int scanned = 0;
        string adapter = "publicBase";
        int? tab = null, scroll = null;
        if (type == typeof(GameMenu))
        {
            adapter = "gameMenu";
            var game = (GameMenu)menu;
            tab = game.currentTab;
            AddList(game.tabs, "tab");
            if (game.currentTab >= 0 && game.currentTab < game.pages.Count)
            {
                Child(game.pages[game.currentTab], "activePage");
            }
        }
        else if (type == typeof(InventoryPage))
        {
            adapter = "inventoryPage";
            var page = (InventoryPage)menu;
            Child(page.inventory, "inventory");
            AddList(page.equipmentIcons, "equipment");
            Add(page.portrait, "portrait");
            Add(page.trashCan, "trashCan");
            Add(page.organizeButton, "organize");
            Add(page.junimoNoteIcon, "junimoNote");
        }
        else if (type == typeof(InventoryMenu))
        {
            adapter = "inventoryMenu";
            AddList(((InventoryMenu)menu).inventory, "inventorySlot");
        }
        else if (type == typeof(ShopMenu))
        {
            adapter = "shopMenu";
            var shop = (ShopMenu)menu;
            scroll = shop.currentItemIndex;
            Child(shop.inventory, "inventory");
            AddList(shop.forSaleButtons, "saleRow");
            Add(shop.upArrow, "scrollUp");
            Add(shop.downArrow, "scrollDown");
            Add(shop.scrollBar, "scrollBar");
        }
        Add(menu.upperRightCloseButton, "close");
        AddList(menu.allClickableComponents, "publicComponent");
        Child(menu.GetChildMenu(), "child");
        return new(type.FullName ?? type.Name, adapter, adapter != "publicBase",
            new(menu.xPositionOnScreen, menu.yPositionOnScreen, menu.width, menu.height),
            tab, scroll, components, children, truncated, type.Assembly.GetName().Name ?? "UnknownAssembly");

        void Child(IClickableMenu? child, string relationship)
        {
            if (child is not null)
            {
                children.Add(new(child, relationship));
            }
        }
        void Add(ClickableComponent? component, string kind)
        {
            if (++scanned > ReviewMenuContract.MaximumScannedComponents)
            {
                truncated = true;
                return;
            }
            if (component is not null)
            {
                Rectangle bounds = component.bounds;
                components.Add(new(component, kind, component.myID,
                    new(bounds.X, bounds.Y, bounds.Width, bounds.Height), component.visible,
                    ReferenceEquals(component, menu.currentlySnappedComponent)));
            }
        }
        void AddList(List<ClickableComponent>? list, string kind)
        {
            if (list is null)
            {
                return;
            }
            int count = Math.Min(list.Count, Math.Max(0, ReviewMenuContract.MaximumScannedComponents - scanned));
            if (count < list.Count)
            {
                truncated = true;
            }
            for (int index = 0; index < count; index++)
            {
                Add(list[index], kind);
            }
        }
    }
}

internal sealed class ReviewMenuCommand
{
    private readonly ReviewMenuCapture _capture = new();
    private readonly StardewReviewMenuSource _source = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal void Reset() => _capture.Reset();

    internal void Handle(string[] args, string runtimePath, IMonitor monitor)
    {
        if (args.Length != 3 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]))
        {
            monitor.Log("SDVKit review-menu rejected an invalid request.", LogLevel.Error);
            return;
        }
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        string? role = Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE");
        role = string.IsNullOrWhiteSpace(role) ? null : role;
        string topology = role is null ? "single" : "network-2";
        ReviewMenuReport Failure(string code) => new(1, "unavailable", code, launch,
            topology, role, DateTimeOffset.UtcNow, null, null, false, false, false, [], []);
        ReviewMenuReport report;
        try
        {
            report = Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1"
                || args[2] != launch || (role is not null && !NetworkTwoContract.IsRole(role))
                    ? Failure("menuReviewBindingInvalid")
                    : !Context.IsWorldReady ? Failure("menuWorldNotReady")
                    : _capture.Capture(_source, launch, DateTimeOffset.UtcNow, topology, role);
        }
        catch (Exception)
        {
            report = Failure("menuCaptureFailed");
        }
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewMenuResponseEnvelope(1, args[1], report), JsonOptions);
            if (bytes.Length > ReviewMenuContract.MaximumResponseBytes)
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewMenuResponseEnvelope(1, args[1],
                    Failure("menuResponseLimit") with { Truncated = true }), JsonOptions);
            }
            ReviewResponseFile.Write(ReviewMenuContract.ResponsePath(runtimePath, args[1]), bytes);
        }
        catch (Exception)
        {
            monitor.Log("SDVKit review-menu could not publish its bounded response.", LogLevel.Error);
        }
    }
}
#endif
