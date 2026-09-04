using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewMapContract
{
    public const int SchemaVersion = 1;
    public const int DefaultPageLimit = 50;
    public const int MaximumPageLimit = 100;
    public const int MaximumDiscoveredAssets = 2048;
    public const int MaximumAssetLength = 256;
    public const int MaximumIdentityLength = 256;
    public const int MaximumPropertyNameLength = 256;
    public const int MaximumPropertyValueBytes = 64 * 1024;
    public const int MaximumPropertyPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumPropertiesPerScope = 4096;
    public const int MaximumLayersPerMap = 256;
    public const int MaximumTileSheetsPerMap = 512;
    public const int MaximumWarpsPerMap = 4096;
    public const int MaximumFramesPerTile = 1024;
    public const int MaximumLayerDimension = 4096;
    public const int MaximumLayerTiles = 4 * 1024 * 1024;
    public const int MaximumTileSheetDimension = 4096;
    public const int MaximumTileSheetTiles = 4 * 1024 * 1024;
    public const int MaximumDisplayDimension = 1024 * 1024;
    public const int MaximumResponseBytes = 5 * 1024 * 1024;

    public const string AssetsOperation = "assets";
    public const string GetOperation = "get";
    public const string LayersOperation = "layers";
    public const string LayerOperation = "layer";
    public const string TileSheetsOperation = "tilesheets";
    public const string WarpsOperation = "warps";
    public const string TileOperation = "tile";
    public const string PropertyOperation = "property";

    public const string MapScope = "map";
    public const string LayerScope = "layer";
    public const string TileScope = "tile";
    public const string DirectSource = "direct";
    public const string TileIndexSource = "tile-index";

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-map runtime path is required.",
                nameof(runtimePath));
        }
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException("The review-map request ID is invalid.", nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-map-{requestId}.json");
    }
}

internal sealed record ReviewMapQuery(
    string Operation,
    string? Asset,
    string? Layer,
    int? X,
    int? Y,
    string? PropertyScope,
    string? PropertySource,
    int? FrameIndex,
    string? Property,
    int Offset,
    int Limit);

internal sealed record ReviewMapProblem(string Code, string Message);

internal static class ReviewMapQueryValidation
{
    public static ReviewMapProblem? Validate(ReviewMapQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        bool listOperation = query.Operation is ReviewMapContract.AssetsOperation
            or ReviewMapContract.LayersOperation
            or ReviewMapContract.TileSheetsOperation
            or ReviewMapContract.WarpsOperation;
        bool exactOperation = query.Operation is ReviewMapContract.GetOperation
            or ReviewMapContract.LayerOperation
            or ReviewMapContract.TileOperation
            or ReviewMapContract.PropertyOperation;
        if (!listOperation && !exactOperation)
        {
            return Problem("mapOperationUnknown", "The review-map operation is unknown.");
        }

        if (query.Offset < 0
            || query.Limit < 1
            || query.Limit > ReviewMapContract.MaximumPageLimit
            || (exactOperation && (query.Offset != 0 || query.Limit != 1)))
        {
            return Problem(
                "mapPaginationInvalid",
                $"List offsets must be non-negative with limits from 1 through {ReviewMapContract.MaximumPageLimit}; exact reads do not accept pagination.");
        }

        bool needsAsset = query.Operation != ReviewMapContract.AssetsOperation;
        if (needsAsset && !IsInput(query.Asset, ReviewMapContract.MaximumAssetLength))
        {
            return Problem("mapAssetInvalid", "A bounded non-empty Maps asset name is required.");
        }
        if (!needsAsset && query.Asset is not null)
        {
            return Problem("mapRequestInvalid", "The review-map request has unexpected operands.");
        }

        bool layerOperation = query.Operation == ReviewMapContract.LayerOperation;
        bool tileOperation = query.Operation == ReviewMapContract.TileOperation;
        bool propertyOperation = query.Operation == ReviewMapContract.PropertyOperation;
        if (layerOperation && !IsInput(query.Layer, ReviewMapContract.MaximumIdentityLength))
        {
            return Problem("mapLayerInvalid", "A bounded non-empty layer ID is required.");
        }
        if (tileOperation && (!IsInput(query.Layer, ReviewMapContract.MaximumIdentityLength)
                || query.X is null
                || query.Y is null))
        {
            return Problem("mapTileInvalid", "An exact layer ID and non-negative X/Y coordinates are required.");
        }

        if (propertyOperation)
        {
            bool mapScope = query.PropertyScope == ReviewMapContract.MapScope
                && query.Layer is null
                && query.X is null
                && query.Y is null
                && query.PropertySource == ReviewMapContract.DirectSource
                && query.FrameIndex is null;
            bool layerScope = query.PropertyScope == ReviewMapContract.LayerScope
                && IsInput(query.Layer, ReviewMapContract.MaximumIdentityLength)
                && query.X is null
                && query.Y is null
                && query.PropertySource == ReviewMapContract.DirectSource
                && query.FrameIndex is null;
            bool tileDirectScope = query.PropertyScope == ReviewMapContract.TileScope
                && IsInput(query.Layer, ReviewMapContract.MaximumIdentityLength)
                && query.X is not null
                && query.Y is not null
                && query.PropertySource == ReviewMapContract.DirectSource
                && query.FrameIndex is null;
            bool tileIndexScope = query.PropertyScope == ReviewMapContract.TileScope
                && IsInput(query.Layer, ReviewMapContract.MaximumIdentityLength)
                && query.X is not null
                && query.Y is not null
                && query.PropertySource == ReviewMapContract.TileIndexSource;
            if (!mapScope && !layerScope && !tileDirectScope && !tileIndexScope)
            {
                return Problem(
                    "mapPropertyScopeInvalid",
                    "Property scope must explicitly select map, layer, tile direct, or tile-index properties.");
            }
        }

        if (propertyOperation && !IsInput(query.Property, ReviewMapContract.MaximumPropertyNameLength))
        {
            return Problem("mapPropertyInvalid", "A bounded non-empty property name is required.");
        }

        if ((query.X is < 0 || query.Y is < 0 || query.FrameIndex is < 0)
            || (!layerOperation && !tileOperation && !propertyOperation && query.Layer is not null)
            || (!tileOperation && !propertyOperation && (query.X is not null || query.Y is not null))
            || (!propertyOperation && (query.PropertyScope is not null
                || query.PropertySource is not null
                || query.FrameIndex is not null
                || query.Property is not null)))
        {
            return Problem("mapRequestInvalid", "The review-map request has unexpected operands.");
        }

        return null;
    }

    private static bool IsInput(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl)
        && ReviewMapText.IsWellFormedUtf16(value);

    private static ReviewMapProblem Problem(string code, string message) => new(code, message);
}

internal static class ReviewMapText
{
    public static bool IsWellFormedUtf16(string? value) =>
        ReviewTransportText.IsWellFormedUtf16(value);
}

internal sealed record ReviewMapPropertyValue(
    string Name,
    string Type,
    JsonElement Value,
    int Utf8Bytes);

internal sealed record ReviewMapPropertyReport(
    string Scope,
    string Source,
    int? FrameIndex,
    string Name,
    string Type,
    JsonElement Value);

internal sealed record ReviewMapLayerReport(
    int Ordinal,
    string Id,
    int Width,
    int Height,
    int TileWidth,
    int TileHeight,
    bool Visible,
    int PropertyCount);

internal sealed record ReviewMapLayerSnapshot(
    ReviewMapLayerReport Report,
    IReadOnlyList<ReviewMapPropertyValue> Properties);

internal sealed record ReviewMapTileSheetReport(
    int Ordinal,
    string Id,
    string ImageSource,
    int SheetWidth,
    int SheetHeight,
    int TileWidth,
    int TileHeight,
    int MarginWidth,
    int MarginHeight,
    int SpacingWidth,
    int SpacingHeight,
    int TileCount,
    int PropertyCount);

internal sealed record ReviewMapWarpReport(
    int Ordinal,
    string SourceProperty,
    int SourceIndex,
    string Kind,
    int FromX,
    int FromY,
    string TargetName,
    int TargetX,
    int TargetY);

internal sealed record ReviewMapSummary(
    int DisplayWidth,
    int DisplayHeight,
    int LayerCount,
    int TileSheetCount,
    int WarpCount,
    int PropertyCount);

internal sealed record ReviewMapAssetSnapshot(
    ReviewMapSummary Summary,
    IReadOnlyList<ReviewMapLayerSnapshot> Layers,
    IReadOnlyList<ReviewMapTileSheetReport> TileSheets,
    IReadOnlyList<ReviewMapWarpReport> Warps,
    IReadOnlyList<ReviewMapPropertyValue> Properties,
    string? ProblemCode = null);

internal sealed record ReviewMapLoadedAsset(
    string DataType,
    ReviewMapAssetSnapshot? Map);

internal sealed record ReviewMapAssetReport(
    string AssetName,
    string? DataType,
    string Kind,
    ReviewMapSummary? Map,
    bool Supported,
    string? ProblemCode);

internal sealed record ReviewMapTileFrameReport(
    int Ordinal,
    string TileSheetId,
    int TileIndex,
    string BlendMode,
    int TileIndexPropertyCount);

internal sealed record ReviewMapTileFrameSnapshot(
    ReviewMapTileFrameReport Report,
    IReadOnlyList<ReviewMapPropertyValue> TileIndexProperties);

internal sealed record ReviewMapTileReport(
    string LayerId,
    int X,
    int Y,
    bool Present,
    string? Kind,
    string? TileSheetId,
    int? TileIndex,
    string? BlendMode,
    long? FrameInterval,
    IReadOnlyList<ReviewMapTileFrameReport>? Frames,
    int DirectPropertyCount,
    int TileIndexPropertyCount,
    string? ProblemCode = null);

internal sealed record ReviewMapTileSnapshot(
    ReviewMapTileReport Report,
    IReadOnlyList<ReviewMapPropertyValue> DirectProperties,
    IReadOnlyList<ReviewMapPropertyValue> TileIndexProperties,
    IReadOnlyList<ReviewMapTileFrameSnapshot> Frames);

internal sealed record ReviewMapPage(
    int Offset,
    int Limit,
    int Returned,
    int Total,
    int? NextOffset);

internal sealed record ReviewMapCoverageReport(
    int Discovered,
    int Classified,
    int MapAssets,
    int NonMapAssets,
    int Supported,
    int Unknown,
    int Unclassified,
    int Unsupported)
{
    public bool Complete =>
        Discovered == Classified
        && Classified == MapAssets + NonMapAssets
        && MapAssets == Supported + Unsupported
        && Unknown == 0
        && Unclassified == 0
        && Unsupported == 0;
}

internal sealed record ReviewMapReport(
    int SchemaVersion,
    string State,
    string Operation,
    string? GameVersion,
    string? GameFileVersion,
    string? AssetName,
    string? DataType,
    ReviewMapSummary? Map,
    ReviewMapLayerReport? Layer,
    ReviewMapTileReport? Tile,
    ReviewMapPropertyReport? Property,
    IReadOnlyList<ReviewMapAssetReport>? Assets,
    IReadOnlyList<ReviewMapLayerReport>? Layers,
    IReadOnlyList<ReviewMapTileSheetReport>? TileSheets,
    IReadOnlyList<ReviewMapWarpReport>? Warps,
    ReviewMapPage? Page,
    ReviewMapCoverageReport? Coverage,
    IReadOnlyList<ReviewMapProblem> Problems);

internal sealed record ReviewMapResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    ReviewMapReport Report);
