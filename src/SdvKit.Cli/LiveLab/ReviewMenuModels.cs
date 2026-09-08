namespace SdvKit.Cli.LiveLab;

internal static class ReviewMenuContract
{
    public const int MaximumDepth = 4;
    public const int MaximumNodes = 16;
    public const int MaximumComponents = 128;
    public const int MaximumScannedComponents = 512;
    public const int MaximumTypeLength = 128;
    public const int MaximumResponseBytes = 128 * 1024;

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-menu-{requestId}.json");
}

internal sealed record ReviewMenuRectangle(int X, int Y, int Width, int Height);
internal sealed record ReviewMenuComponent(long Id, string Kind, int ControllerId,
    ReviewMenuRectangle Bounds, bool VisibleFlag, bool IntersectsViewport, bool ControllerFocused);
internal sealed record ReviewMenuTextField(long Id, long DispatcherId, long? SubscriberId,
    bool Selected, bool Available, ReviewMenuRectangle Bounds);
internal sealed record ReviewDialogueChoice(long Id, string Key, string Text, int ControllerId,
    ReviewMenuRectangle Bounds, bool VisibleFlag, bool ControllerFocused);
internal sealed record ReviewDialogueObservation(string Text, int? CurrentChoice,
    IReadOnlyList<ReviewDialogueChoice> Choices);
internal sealed record ReviewCraftingIngredient(string ItemId, int Quantity);
internal sealed record ReviewCraftingRecipe(string RecipeId, string DisplayName, long ComponentId,
    bool Available, int CraftableCount, IReadOnlyList<ReviewCraftingIngredient> Ingredients,
    IReadOnlyList<string> Outputs);
internal sealed record ReviewCraftingObservation(int CurrentPage, IReadOnlyList<ReviewCraftingRecipe> Recipes);
internal sealed record ReviewMenuNode(long Id, long? ParentId, string Relationship,
    string Type, string Assembly, string Adapter, string Coverage, ReviewMenuRectangle Bounds,
    int? CurrentTab, int? ScrollIndex, IReadOnlyList<ReviewMenuComponent> Components,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReviewMenuTextField? TextField = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReviewDialogueObservation? Dialogue = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReviewCraftingObservation? Crafting = null);
internal sealed record ReviewMenuReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset CapturedAtUtc, string? IdentityScope,
    ReviewMenuRectangle? Viewport, bool MenuOpen, bool Complete, bool Truncated,
    IReadOnlyList<string> Limitations, IReadOnlyList<ReviewMenuNode> Menus, string? UiRevision = null);
internal sealed record ReviewMenuResponseEnvelope(int SchemaVersion, string RequestId, ReviewMenuReport Report);
