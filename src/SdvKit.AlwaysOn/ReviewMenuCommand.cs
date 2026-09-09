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
    private const int MaximumDialogueText = 8192;
    private const int MaximumDialogueChoices = 64;
    private const int MaximumCraftingRecipes = 128;
    private const int MaximumRecipeIngredients = 64;
    private const int MaximumRecipeOutputs = 16;
    public object? Root => Game1.activeClickableMenu;
    public float UiScale => Game1.options.uiScale;
    public float Zoom => Game1.options.zoomLevel;
    public ReviewMenuRectangle Viewport => new(Game1.uiViewport.X, Game1.uiViewport.Y,
        Game1.uiViewport.Width, Game1.uiViewport.Height);

    public MenuObservation Read(object instance)
    {
        var menu = (IClickableMenu)instance;
        Type type = menu.GetType();
        var components = new List<MenuComponentObservation>();
        var children = new List<MenuChildObservation>();
        MenuTextFieldObservation? textField = null;
        bool truncated = false;
        int scanned = 0;
        string adapter = "publicBase";
        int? tab = null, scroll = null;
        MenuDialogueObservation? dialogue = null;
        MenuCraftingObservation? crafting = null;
        var menuBounds = new ReviewMenuRectangle(menu.xPositionOnScreen, menu.yPositionOnScreen,
            menu.width, menu.height);
        var limitations = new List<string>();
        if (type == typeof(NamingMenu))
        {
            adapter = "namingMenu";
            var naming = (NamingMenu)menu;
            Add(naming.textBoxCC, "publicComponent");
            Add(naming.doneNamingButton, "publicComponent");
            Add(naming.randomButton, "publicComponent");
            if (naming.textBox is { } box && box.GetType() == typeof(TextBox) && !box.PasswordBox
                && Game1.keyboardDispatcher is { } dispatcher && dispatcher.GetType() == typeof(KeyboardDispatcher))
                textField = new(box, dispatcher, dispatcher.Subscriber, box.Selected,
                    ReferenceEquals(menu, Root) && menu.GetChildMenu() is null && box.Selected
                        && ReferenceEquals(box, dispatcher.Subscriber), new(box.X, box.Y, box.Width, box.Height));
        }
        else if (type == typeof(GameMenu))
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
        else if (type == typeof(DialogueBox))
        {
            adapter = "dialogueBox";
            var dialogueBox = (DialogueBox)menu;
            menuBounds = ReviewMenuCapture.DialogueBounds(dialogueBox.transitioning,
                dialogueBox.transitionX, dialogueBox.transitionY, dialogueBox.transitionWidth,
                dialogueBox.transitionHeight, dialogueBox.isQuestion, dialogueBox.x, dialogueBox.y,
                dialogueBox.width, dialogueBox.height, dialogueBox.heightForQuestions);
            string text = dialogueBox.getCurrentString() ?? "";
            if (text.Length > MaximumDialogueText) limitations.Add("dialogueTextTruncated");
            var choices = new List<MenuDialogueChoiceObservation>();
            Response[] responses = dialogueBox.responses ?? [];
            for (int index = 0; index < Math.Min(responses.Length, MaximumDialogueChoices); index++)
            {
                Response response = responses[index];
                ClickableComponent? component = dialogueBox.responseCC is { Count: > 0 } && index < dialogueBox.responseCC.Count
                    ? dialogueBox.responseCC[index] : null;
                if (component is null)
                {
                    limitations.Add("dialogueChoiceControlUnavailable");
                    continue;
                }
                if (!ReviewMenuCapture.TryStableIdentity(response.responseKey, 128, out string key))
                {
                    limitations.Add("dialogueChoiceKeyUnavailable");
                    continue;
                }
                if ((response.responseText?.Length ?? 0) > 1024) limitations.Add("dialogueChoiceTextTruncated");
                choices.Add(new(component, key, Bound(response.responseText, 1024),
                    component.myID, new(component.bounds.X, component.bounds.Y, component.bounds.Width, component.bounds.Height),
                    component.visible, ReferenceEquals(component, menu.currentlySnappedComponent)));
            }
            dialogue = new(Bound(text, MaximumDialogueText), dialogueBox.selectedResponse, choices);
            if (responses.Length > MaximumDialogueChoices)
            {
                limitations.Add("dialogueChoiceLimit");
            }
        }
        else if (type == typeof(CraftingPage))
        {
            adapter = "craftingPage";
            var craftingPage = (CraftingPage)menu;
            var recipes = new List<MenuCraftingRecipeObservation>();
            bool availabilityAvailable = craftingPage._materialContainers is null || craftingPage._materialContainers.Count == 0;
            if (!availabilityAvailable) limitations.Add("craftingAvailabilityUnavailable");
            int? currentPage = craftingPage.currentCraftingPage >= 0
                && craftingPage.currentCraftingPage < craftingPage.pagesOfCraftingRecipes.Count
                    ? craftingPage.currentCraftingPage
                    : null;
            if (currentPage is null) limitations.Add("craftingPageUnavailable");
            if (currentPage is int page)
            {
                foreach ((ClickableTextureComponent component, CraftingRecipe recipe) in craftingPage.pagesOfCraftingRecipes[page]
                    .Take(MaximumCraftingRecipes + 1))
                {
                    if (recipes.Count >= MaximumCraftingRecipes)
                    {
                        limitations.Add("craftingCollectionLimit");
                        break;
                    }
                    KeyValuePair<string, int>[] ingredients = recipe.recipeList.Take(MaximumRecipeIngredients).ToArray();
                    string[] outputs = recipe.itemToProduce.Take(MaximumRecipeOutputs).ToArray();
                    if (!ReviewMenuCapture.TryStableIdentity(recipe.name, 256, out string recipeId)
                        || outputs.Any(item => !ReviewMenuCapture.TryStableIdentity(item, 256, out _))
                        || ingredients.Any(pair => !ReviewMenuCapture.TryStableIdentity(pair.Key, 256, out _) || pair.Value <= 0))
                    {
                        limitations.Add("craftingRecipeIdentityUnavailable");
                        continue;
                    }
                    if ((recipe.DisplayName?.Length ?? 0) > 1024) limitations.Add("craftingDisplayNameTruncated");
                    recipes.Add(new(component, recipeId, Bound(recipe.DisplayName, 1024),
                        availabilityAvailable
                            ? recipe.doesFarmerHaveIngredientsInInventory()
                            : null,
                        availabilityAvailable
                            ? recipe.getCraftableCount((IList<Item>)null!)
                            : null,
                        ingredients.Select(pair => new ReviewCraftingIngredient(pair.Key, pair.Value)).ToArray(),
                        outputs));
                    if (recipe.recipeList.Count > MaximumRecipeIngredients || recipe.itemToProduce.Count > MaximumRecipeOutputs)
                    {
                        limitations.Add("craftingCollectionLimit");
                    }
                }
            }
            crafting = new(currentPage, recipes);
        }
        Add(menu.upperRightCloseButton, "close");
        AddList(menu.allClickableComponents, "publicComponent");
        Child(menu.GetChildMenu(), "child");
        return new(type.FullName ?? type.Name, adapter, adapter != "publicBase",
            menuBounds,
            tab, scroll, components, children, truncated, type.Assembly.GetName().Name ?? "UnknownAssembly", textField,
            dialogue, crafting, limitations);

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
        static string Bound(string? value, int maximum) => string.IsNullOrEmpty(value)
            ? "" : value.Length <= maximum ? value : value[..maximum];
    }

}

internal sealed class ReviewMenuCommand
{
    private readonly ReviewMenuCapture _capture = new();
    private readonly StardewReviewMenuSource _source = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal ReviewMenuReport CaptureCurrent() => _capture.Capture(_source,
        Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "", DateTimeOffset.UtcNow);

    internal void ObserveRoot(IClickableMenu? menu) => _capture.ObserveRoot(menu);

    internal bool CurrentTextField(long id)
    {
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        return _capture.Capture(_source, launch, DateTimeOffset.UtcNow).Menus
            .Any(m => m.Relationship == "root" && m.TextField is { Available: true } field && field.Id == id);
    }

    internal string? CurrentIdentityScope() => _capture.Capture(_source,
        Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "", DateTimeOffset.UtcNow).IdentityScope;

    internal long CurrentObservationId(object instance) => _capture.ObservationId(instance);

    internal string? CurrentRevision() => CaptureRevision(false);
    internal string? CurrentContinuity() => CaptureRevision(true);
    internal string CurrentViewport() => ReviewMenuCapture.ViewportRevision(_source);

    private string? CaptureRevision(bool continuityOnly)
    {
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        string? role = Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE");
        role = string.IsNullOrWhiteSpace(role) ? null : role;
        return Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") == "1"
            && Context.IsWorldReady && !Game1.exitToTitle && ReviewTransportToken.IsRequestId(launch)
            && (role is null || NetworkTwoContract.IsRole(role))
                ? _capture.Capture(_source, launch, DateTimeOffset.UtcNow,
                    role is null ? "single" : "network-2", role, continuityOnly).UiRevision
                : null;
    }

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
