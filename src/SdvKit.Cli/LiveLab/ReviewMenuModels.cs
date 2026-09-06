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
internal sealed record ReviewMenuNode(long Id, long? ParentId, string Relationship,
    string Type, string Assembly, string Adapter, string Coverage, ReviewMenuRectangle Bounds,
    int? CurrentTab, int? ScrollIndex, IReadOnlyList<ReviewMenuComponent> Components);
internal sealed record ReviewMenuReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset CapturedAtUtc, string? IdentityScope,
    ReviewMenuRectangle? Viewport, bool MenuOpen, bool Complete, bool Truncated,
    IReadOnlyList<string> Limitations, IReadOnlyList<ReviewMenuNode> Menus);
internal sealed record ReviewMenuResponseEnvelope(int SchemaVersion, string RequestId, ReviewMenuReport Report);
