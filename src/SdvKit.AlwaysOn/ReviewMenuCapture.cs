using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.AlwaysOn;

internal sealed record MenuComponentObservation(object Instance, string Kind, int ControllerId,
    ReviewMenuRectangle Bounds, bool VisibleFlag, bool ControllerFocused);
internal sealed record MenuChildObservation(object Instance, string Relationship);
internal sealed record MenuTextFieldObservation(object Instance, object Dispatcher, object? Subscriber, bool Selected, bool Available, ReviewMenuRectangle Bounds);
internal sealed record MenuDialogueChoiceObservation(object Instance, string Key, string Text, int ControllerId,
    ReviewMenuRectangle Bounds, bool VisibleFlag, bool ControllerFocused);
internal sealed record MenuDialogueObservation(string Text, int? CurrentChoice,
    IReadOnlyList<MenuDialogueChoiceObservation> Choices);
internal sealed record MenuCraftingRecipeObservation(object Component, string RecipeId, string DisplayName,
    bool? Available, int? CraftableCount, IReadOnlyList<ReviewCraftingIngredient> Ingredients,
    IReadOnlyList<string> Outputs);
internal sealed record MenuCraftingObservation(int? CurrentPage, IReadOnlyList<MenuCraftingRecipeObservation> Recipes);
internal sealed record MenuObservation(string Type, string Adapter, bool Supported,
    ReviewMenuRectangle Bounds, int? CurrentTab, int? ScrollIndex,
    IReadOnlyList<MenuComponentObservation> Components, IReadOnlyList<MenuChildObservation> Children,
    bool ScanTruncated = false, string Assembly = "UnknownAssembly", MenuTextFieldObservation? TextField = null,
    MenuDialogueObservation? Dialogue = null, MenuCraftingObservation? Crafting = null,
    IReadOnlyList<string>? Limitations = null);
internal interface IReviewMenuSource
{
    object? Root { get; }
    ReviewMenuRectangle Viewport { get; }
    float UiScale => 1f;
    float Zoom => 1f;
    MenuObservation Read(object menu);
}

internal sealed class ReviewMenuCapture
{
    private sealed record Identity(long Value);
    private ConditionalWeakTable<object, Identity> _identities = new();
    private object? _root;
    private long _nextId;
    private string _scope = Guid.NewGuid().ToString("N");

    internal static string ViewportRevision(IReviewMenuSource source) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { source.Viewport.Width, source.Viewport.Height, source.UiScale, source.Zoom }))).ToLowerInvariant();

    internal static bool TryStableIdentity(string? value, int maximum, out string identity)
    {
        identity = value ?? "";
        return value is not null && value.Length > 0 && value.Length <= maximum;
    }

    internal static ReviewMenuRectangle DialogueBounds(bool transitioning,
        int transitionX, int transitionY, int transitionWidth, int transitionHeight,
        bool isQuestion, int x, int y, int width, int height, int heightForQuestions) =>
        transitioning
            ? new(transitionX, transitionY, transitionWidth, transitionHeight)
            : isQuestion
                ? new(x, y - (heightForQuestions - height), width, heightForQuestions)
                : new(x, y, width, height);

    internal void Reset()
    {
        _root = null;
        _identities = new();
        _nextId = 0;
        _scope = Guid.NewGuid().ToString("N");
    }

    internal void ObserveRoot(object? root)
    {
        if (!ReferenceEquals(root, _root))
        {
            Reset();
            _root = root;
        }
    }

    internal ReviewMenuReport Capture(IReviewMenuSource source, string launchId, DateTimeOffset now,
        string topology = "single", string? role = null, bool continuityOnly = false)
    {
        object? root = source.Root;
        ObserveRoot(root);
        // The game's UI viewport origin follows the world camera; menu bounds are screen-local.
        ReviewMenuRectangle measuredViewport = source.Viewport;
        var viewport = new ReviewMenuRectangle(0, 0, measuredViewport.Width, measuredViewport.Height);
        var nodes = new List<ReviewMenuNode>();
        var limitations = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        int componentCount = 0;
        bool truncated = false;
        if (root is not null)
        {
            Visit(root, null, "root", 1);
        }
        var report = new ReviewMenuReport(1, "ready", null, launchId, topology, role, now, root is null ? null : _scope,
            viewport, root is not null, limitations.Count == 0, truncated,
            Array.AsReadOnly(limitations.ToArray()), Array.AsReadOnly(nodes.ToArray()));
        // Bind the same bounded screen-local observation, including a no-menu lifetime.
        byte[] revision = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Scope = _scope,
            launchId,
            topology,
            role,
            source.UiScale,
            source.Zoom,
            report.Viewport,
            report.MenuOpen,
            Complete = continuityOnly || report.Complete,
            Truncated = !continuityOnly && report.Truncated,
            Limitations = continuityOnly ? Array.Empty<string>() : report.Limitations,
            Menus = continuityOnly
                ? (object)report.Menus.Select(m => new { m.Id, m.ParentId, m.Relationship, m.Type }).ToArray()
                : report.Menus,
        });
        return report with { UiRevision = Convert.ToHexString(SHA256.HashData(revision)).ToLowerInvariant() };

        void Visit(object instance, long? parentId, string relationship, int depth)
        {
            if (depth > ReviewMenuContract.MaximumDepth || nodes.Count >= ReviewMenuContract.MaximumNodes)
            {
                Limit("menuTreeLimit");
                return;
            }
            if (!visited.Add(instance))
            {
                Limit("repeatedMenuReference");
                return;
            }
            MenuObservation observed = source.Read(instance);
            long menuId = Id(instance);
            if (!observed.Supported)
            {
                limitations.Add("publicBaseOnly");
            }
            if (observed.ScanTruncated)
            {
                Limit("componentScanLimit");
            }
            foreach (string limitation in observed.Limitations ?? [])
            {
                Limit(limitation);
            }
            string type = observed.Type;
            string assembly = observed.Assembly;
            if (type.Length > ReviewMenuContract.MaximumTypeLength
                || type.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '.' or '+' or '`')))
            {
                type = "UnknownMenu";
                limitations.Add("typeIdentifierWithheld");
            }
            if (assembly.Length is 0 or > ReviewMenuContract.MaximumTypeLength
                || assembly.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '.' or '+' or '`' or '-' or ' ')))
            {
                assembly = "UnknownAssembly";
                limitations.Add("typeIdentifierWithheld");
            }
            var components = new List<ReviewMenuComponent>();
            var unique = new HashSet<object>(ReferenceEqualityComparer.Instance);
            int scanned = 0;
            foreach (MenuComponentObservation component in observed.Components)
            {
                if (++scanned > ReviewMenuContract.MaximumScannedComponents)
                {
                    Limit("componentScanLimit");
                    break;
                }
                if (!unique.Add(component.Instance))
                {
                    continue;
                }
                if (componentCount >= ReviewMenuContract.MaximumComponents)
                {
                    Limit("componentLimit");
                    break;
                }
                componentCount++;
                components.Add(new(Id(component.Instance), component.Kind, component.ControllerId,
                    component.Bounds, component.VisibleFlag, Intersects(component.Bounds, viewport),
                    component.ControllerFocused));
            }
            nodes.Add(new(menuId, parentId, relationship, type, assembly, observed.Adapter,
                observed.Supported ? "declaredFields" : "partial", observed.Bounds,
                observed.CurrentTab, observed.ScrollIndex,
                Array.AsReadOnly(components.OrderBy(c => c.Id).ToArray()),
                observed.TextField is { } field ? new(Id(field.Instance), Id(field.Dispatcher),
                    field.Subscriber is null ? null : Id(field.Subscriber), field.Selected, field.Available, field.Bounds) : null,
                observed.Dialogue is { } dialogue ? new(dialogue.Text, dialogue.CurrentChoice,
                    dialogue.Choices.Select(choice => new ReviewDialogueChoice(Id(choice.Instance), choice.Key,
                        choice.Text, choice.ControllerId, choice.Bounds, choice.VisibleFlag, choice.ControllerFocused)).ToArray()) : null,
                observed.Crafting is { } crafting ? new(crafting.CurrentPage,
                    crafting.Recipes.Select(recipe => new ReviewCraftingRecipe(recipe.RecipeId, recipe.DisplayName,
                        Id(recipe.Component), recipe.Available, recipe.CraftableCount, recipe.Ingredients, recipe.Outputs)).ToArray()) : null));
            foreach (MenuChildObservation child in observed.Children.Take(ReviewMenuContract.MaximumNodes + 1))
            {
                Visit(child.Instance, menuId, child.Relationship, depth + 1);
            }
        }
        void Limit(string code)
        {
            truncated = true;
            limitations.Add(code);
        }
    }

    private long Id(object instance) => _identities.GetValue(instance, _ => new(++_nextId)).Value;

    internal long ObservationId(object instance) => Id(instance);

    private static bool Intersects(ReviewMenuRectangle a, ReviewMenuRectangle b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0
        && (long)a.X < (long)b.X + b.Width && (long)b.X < (long)a.X + a.Width
        && (long)a.Y < (long)b.Y + b.Height && (long)b.Y < (long)a.Y + a.Height;
}
