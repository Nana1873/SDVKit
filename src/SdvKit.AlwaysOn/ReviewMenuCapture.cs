using System.Runtime.CompilerServices;
using SdvKit.Cli.LiveLab;

namespace SdvKit.AlwaysOn;

internal sealed record MenuComponentObservation(object Instance, string Kind, int ControllerId,
    ReviewMenuRectangle Bounds, bool VisibleFlag, bool ControllerFocused);
internal sealed record MenuChildObservation(object Instance, string Relationship);
internal sealed record MenuObservation(string Type, string Adapter, bool Supported,
    ReviewMenuRectangle Bounds, int? CurrentTab, int? ScrollIndex,
    IReadOnlyList<MenuComponentObservation> Components, IReadOnlyList<MenuChildObservation> Children,
    bool ScanTruncated = false, string Assembly = "UnknownAssembly");
internal interface IReviewMenuSource
{
    object? Root { get; }
    ReviewMenuRectangle Viewport { get; }
    MenuObservation Read(object menu);
}

internal sealed class ReviewMenuCapture
{
    private sealed record Identity(long Value);
    private ConditionalWeakTable<object, Identity> _identities = new();
    private object? _root;
    private long _nextId;
    private string _scope = Guid.NewGuid().ToString("N");

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
        string topology = "single", string? role = null)
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
        return new(1, "ready", null, launchId, topology, role, now, root is null ? null : _scope,
            viewport, root is not null, limitations.Count == 0, truncated,
            Array.AsReadOnly(limitations.ToArray()), Array.AsReadOnly(nodes.ToArray()));

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
                Array.AsReadOnly(components.OrderBy(c => c.Id).ToArray())));
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

    private static bool Intersects(ReviewMenuRectangle a, ReviewMenuRectangle b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0
        && (long)a.X < (long)b.X + b.Width && (long)b.X < (long)a.X + a.Width
        && (long)a.Y < (long)b.Y + b.Height && (long)b.Y < (long)a.Y + a.Height;
}
