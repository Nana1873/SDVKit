using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static class ProjectReviewMapService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-map");

    private const int OperationFailed = 3;
    private const string MissingToken = "-";
    private const int MaximumVersionLength = 128;
    private const int MaximumDataTypeLength = 512;
    private const int MaximumImageSourceLength = 512;
    private const int MaximumProblemCount = 8;
    private const int MaximumProblemCodeLength = 128;
    private const int MaximumProblemMessageLength = 512;
    private const int MaximumJsonDepth = 32;
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = MaximumJsonDepth,
    };
    private static readonly JsonDocumentOptions ResponseDocumentOptions = new()
    {
        MaxDepth = MaximumJsonDepth,
    };
    private static readonly HashSet<string> EnvelopeProperties = PropertySet(
        "schemaVersion",
        "requestId",
        "report");
    private static readonly HashSet<string> ReportProperties = PropertySet(
        "schemaVersion",
        "state",
        "operation",
        "gameVersion",
        "gameFileVersion",
        "assetName",
        "dataType",
        "map",
        "layer",
        "tile",
        "property",
        "assets",
        "layers",
        "tileSheets",
        "warps",
        "page",
        "coverage",
        "problems");
    private static readonly HashSet<string> ProblemProperties = PropertySet(
        "code",
        "message");
    private static readonly HashSet<string> PropertyProperties = PropertySet(
        "scope",
        "source",
        "frameIndex",
        "name",
        "type",
        "value");
    private static readonly HashSet<string> LayerProperties = PropertySet(
        "ordinal",
        "id",
        "width",
        "height",
        "tileWidth",
        "tileHeight",
        "visible",
        "propertyCount");
    private static readonly HashSet<string> TileSheetProperties = PropertySet(
        "ordinal",
        "id",
        "imageSource",
        "sheetWidth",
        "sheetHeight",
        "tileWidth",
        "tileHeight",
        "marginWidth",
        "marginHeight",
        "spacingWidth",
        "spacingHeight",
        "tileCount",
        "propertyCount");
    private static readonly HashSet<string> WarpProperties = PropertySet(
        "ordinal",
        "sourceProperty",
        "sourceIndex",
        "kind",
        "fromX",
        "fromY",
        "targetName",
        "targetX",
        "targetY");
    private static readonly HashSet<string> SummaryProperties = PropertySet(
        "displayWidth",
        "displayHeight",
        "layerCount",
        "tileSheetCount",
        "warpCount",
        "propertyCount");
    private static readonly HashSet<string> AssetProperties = PropertySet(
        "assetName",
        "dataType",
        "kind",
        "map",
        "supported",
        "problemCode");
    private static readonly HashSet<string> TileFrameProperties = PropertySet(
        "ordinal",
        "tileSheetId",
        "tileIndex",
        "blendMode",
        "tileIndexPropertyCount");
    private static readonly HashSet<string> TileProperties = PropertySet(
        "layerId",
        "x",
        "y",
        "present",
        "kind",
        "tileSheetId",
        "tileIndex",
        "blendMode",
        "frameInterval",
        "frames",
        "directPropertyCount",
        "tileIndexPropertyCount",
        "problemCode");
    private static readonly HashSet<string> PageProperties = PropertySet(
        "offset",
        "limit",
        "returned",
        "total",
        "nextOffset");
    private static readonly HashSet<string> CoverageProperties = PropertySet(
        "discovered",
        "classified",
        "mapAssets",
        "nonMapAssets",
        "supported",
        "unknown",
        "unclassified",
        "unsupported",
        "complete");

    public static LiveLabCommandResult Execute(
        ReviewMapQuery query,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);

        ReviewMapProblem? queryProblem = Validate(query);
        if (queryProblem is not null)
        {
            return Failure(query.Operation, queryProblem);
        }

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(labRoot);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(query.Operation, Problem("labPathInvalid", exception.Message));
        }

        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewMapContract.ResponsePath(paths.RuntimePath, requestId);
        ProjectReviewResponseTransportResult<ReviewMapResponseEnvelope> transported =
            ProjectReviewResponseTransport.Execute(
                BuildCommand(requestId, query),
                responsePath,
                ReviewMapContract.MaximumResponseBytes,
                "map",
                "review-map",
                labRoot,
                DeserializeResponse,
                envelope => MatchesResponse(envelope, requestId, query),
                inputSender,
                delay,
                responseTimeout);
        if (transported.Response is null)
        {
            return Failure(
                query.Operation,
                transported.Problems
                    .Select(problem => Problem(problem.Code, problem.Message))
                    .ToArray());
        }

        ReviewMapReport report = transported.Response.Report;
        return new LiveLabCommandResult(
            report.Problems.Count == 0
                && string.Equals(report.State, "ready", StringComparison.Ordinal)
                    ? 0
                    : OperationFailed,
            report);
    }

    internal static string BuildCommand(string requestId, ReviewMapQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException("The review-map request ID is invalid.", nameof(requestId));
        }
        if (Validate(query) is ReviewMapProblem queryProblem)
        {
            throw new ArgumentException(queryProblem.Message, nameof(query));
        }

        string command = string.Join(
            " ",
            "sdvkit",
            "map",
            requestId,
            query.Operation,
            query.Offset.ToString(CultureInfo.InvariantCulture),
            query.Limit.ToString(CultureInfo.InvariantCulture),
            EncodeOptional(query.Asset),
            EncodeOptional(query.Layer),
            CoordinateToken(query.X),
            CoordinateToken(query.Y),
            EncodeOptional(query.PropertyScope),
            EncodeOptional(query.PropertySource),
            CoordinateToken(query.FrameIndex),
            EncodeOptional(query.Property));
        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        return command;
    }

    internal static ReviewMapProblem? Validate(ReviewMapQuery query) =>
        ReviewMapQueryValidation.Validate(query);

    internal static ReviewMapResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using (JsonDocument document = JsonDocument.Parse(bytes, ResponseDocumentOptions))
        {
            ValidateEnvelopeShape(document.RootElement);
        }

        return JsonSerializer.Deserialize<ReviewMapResponseEnvelope>(
            bytes,
            ResponseJsonOptions);
    }

    internal static bool MatchesResponse(
        ReviewMapResponseEnvelope? envelope,
        string requestId,
        ReviewMapQuery? query)
    {
        try
        {
            return MatchesResponseCore(envelope, requestId, query);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return false;
        }
    }

    private static bool MatchesResponseCore(
        ReviewMapResponseEnvelope? envelope,
        string requestId,
        ReviewMapQuery? query)
    {
        if (envelope is null
            || query is null
            || Validate(query) is not null
            || !ReviewTransportToken.IsRequestId(requestId)
            || envelope.SchemaVersion != ReviewMapContract.SchemaVersion
            || !string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal)
            || envelope.Report is null)
        {
            return false;
        }

        ReviewMapReport report = envelope.Report;
        if (report.SchemaVersion != ReviewMapContract.SchemaVersion
            || !string.Equals(report.Operation, query.Operation, StringComparison.Ordinal)
            || !IsSafeText(report.GameVersion, MaximumVersionLength)
            || !IsSafeText(report.GameFileVersion, MaximumVersionLength)
            || !ProblemsAreSafe(report.Problems))
        {
            return false;
        }

        bool ready = string.Equals(report.State, "ready", StringComparison.Ordinal);
        bool blocked = string.Equals(report.State, "blocked", StringComparison.Ordinal);
        if ((!ready && !blocked)
            || (ready && report.Problems.Count != 0)
            || (blocked && report.Problems.Count == 0))
        {
            return false;
        }

        if (blocked)
        {
            return query.Operation == ReviewMapContract.AssetsOperation
                    && HasAssetInventoryPayload(report)
                ? AssetInventoryMatches(report, query, expectedComplete: false)
                : EmptyPayloadMatches(report);
        }

        return query.Operation switch
        {
            ReviewMapContract.AssetsOperation =>
                AssetInventoryMatches(report, query, expectedComplete: true),
            ReviewMapContract.GetOperation => ExactMapMatches(report, query),
            ReviewMapContract.LayersOperation => LayerListMatches(report, query),
            ReviewMapContract.LayerOperation => ExactLayerMatches(report, query),
            ReviewMapContract.TileSheetsOperation =>
                TileSheetListMatches(report, query),
            ReviewMapContract.WarpsOperation => WarpListMatches(report, query),
            ReviewMapContract.TileOperation => ExactTileMatches(report, query),
            ReviewMapContract.PropertyOperation => ExactMapIdentityMatches(report, query)
                && PropertyMatches(report, query),
            _ => false,
        };
    }

    private static bool ExactMapMatches(ReviewMapReport report, ReviewMapQuery query) =>
        ExactMapIdentityMatches(report, query)
        && SummaryMatches(report.Map)
        && report.Layer is null
        && report.Tile is null
        && report.Property is null
        && report.Assets is null
        && report.Layers is null
        && report.TileSheets is null
        && report.Warps is null
        && report.Page is null
        && report.Coverage is null;

    private static bool AssetInventoryMatches(
        ReviewMapReport report,
        ReviewMapQuery query,
        bool expectedComplete)
    {
        if (report.AssetName is not null
            || report.DataType is not null
            || report.Map is not null
            || report.Layer is not null
            || report.Tile is not null
            || report.Property is not null
            || report.Layers is not null
            || report.TileSheets is not null
            || report.Warps is not null
            || report.Assets is not { } assets
            || report.Coverage is not { } coverage
            || !CoverageMatches(coverage, report.Page?.Total, expectedComplete)
            || !PageMatches(
                report.Page,
                query,
                assets.Count,
                ReviewMapContract.MaximumDiscoveredAssets))
        {
            return false;
        }

        string? previousName = null;
        var normalizedGroups = new Dictionary<string, List<ReviewMapAssetReport>>(
            StringComparer.Ordinal);
        var pageMapAssets = 0;
        var pageNonMapAssets = 0;
        var pageSupported = 0;
        var pageUnknown = 0;
        var pageUnclassified = 0;
        var pageUnsupported = 0;
        foreach (ReviewMapAssetReport? asset in assets)
        {
            if (asset is null
                || !AssetReportMatches(asset, expectedComplete)
                || (previousName is not null
                    && string.CompareOrdinal(previousName, asset.AssetName) >= 0))
            {
                return false;
            }

            if (IsCanonicalMapAssetName(asset.AssetName))
            {
                string normalizedName = StableIdentityNormalizer.Normalize(asset.AssetName);
                if (!normalizedGroups.TryGetValue(
                        normalizedName,
                        out List<ReviewMapAssetReport>? group))
                {
                    group = [];
                    normalizedGroups.Add(normalizedName, group);
                }

                group.Add(asset);
            }

            switch (asset.Kind)
            {
                case "map":
                    pageMapAssets++;
                    if (asset.Supported)
                    {
                        pageSupported++;
                    }
                    else
                    {
                        pageUnsupported++;
                    }

                    break;
                case "nonMap":
                    pageNonMapAssets++;
                    break;
                case "gap" when asset.ProblemCode == "mapAssetNameInvalid":
                    pageUnknown++;
                    break;
                case "gap" when asset.ProblemCode is
                    "mapAssetNormalizationCollision" or "mapAssetLoadFailed":
                    pageUnclassified++;
                    break;
                default:
                    return false;
            }

            previousName = asset.AssetName;
        }

        bool completePage = report.Page!.Offset == 0
            && report.Page.Returned == report.Page.Total;
        foreach (List<ReviewMapAssetReport> group in normalizedGroups.Values)
        {
            bool allCollisionGaps = group.All(asset =>
                asset.Kind == "gap"
                && asset.ProblemCode == "mapAssetNormalizationCollision");
            if ((group.Count > 1 && !allCollisionGaps)
                || (completePage && (group.Count > 1) != allCollisionGaps))
            {
                return false;
            }
        }

        if (pageMapAssets > coverage.MapAssets
            || pageNonMapAssets > coverage.NonMapAssets
            || pageSupported > coverage.Supported
            || pageUnknown > coverage.Unknown
            || pageUnclassified > coverage.Unclassified
            || pageUnsupported > coverage.Unsupported)
        {
            return false;
        }

        return !completePage
            || (pageMapAssets == coverage.MapAssets
                && pageNonMapAssets == coverage.NonMapAssets
                && pageSupported == coverage.Supported
                && pageUnknown == coverage.Unknown
                && pageUnclassified == coverage.Unclassified
                && pageUnsupported == coverage.Unsupported);
    }

    private static bool AssetReportMatches(
        ReviewMapAssetReport asset,
        bool expectedComplete)
    {
        if (!IsSafeText(asset.AssetName, ReviewMapContract.MaximumAssetLength))
        {
            return false;
        }

        return asset.Kind switch
        {
            "map" => IsCanonicalMapAssetName(asset.AssetName)
                && IsSafeText(asset.DataType, MaximumDataTypeLength)
                && (asset.Supported
                    ? SummaryMatches(asset.Map) && asset.ProblemCode is null
                    : !expectedComplete
                        && asset.Map is null
                        && IsProblemCode(asset.ProblemCode)),
            "nonMap" => IsCanonicalMapAssetName(asset.AssetName)
                && IsSafeText(asset.DataType, MaximumDataTypeLength)
                && asset.Map is null
                && !asset.Supported
                && asset.ProblemCode is null,
            "gap" => !expectedComplete
                && asset.DataType is null
                && asset.Map is null
                && !asset.Supported
                && asset.ProblemCode switch
                {
                    "mapAssetNameInvalid" => !IsCanonicalMapAssetName(asset.AssetName),
                    "mapAssetNormalizationCollision" or "mapAssetLoadFailed" =>
                        IsCanonicalMapAssetName(asset.AssetName),
                    _ => false,
                },
            _ => false,
        };
    }

    private static bool CoverageMatches(
        ReviewMapCoverageReport coverage,
        int? total,
        bool expectedComplete) =>
        total is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && coverage.Discovered == total
        && coverage.Discovered is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && coverage.Classified is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && coverage.MapAssets is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && coverage.NonMapAssets is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && coverage.Supported is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && coverage.Unknown is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && coverage.Unclassified is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && coverage.Unsupported is >= 0 and <= ReviewMapContract.MaximumDiscoveredAssets
        && (long)coverage.MapAssets + coverage.NonMapAssets == coverage.Classified
        && (long)coverage.Classified + coverage.Unknown + coverage.Unclassified
            == coverage.Discovered
        && (long)coverage.Supported + coverage.Unsupported == coverage.MapAssets
        && expectedComplete == (coverage.Unknown == 0
            && coverage.Unclassified == 0
            && coverage.Unsupported == 0);

    private static bool ExactMapIdentityMatches(ReviewMapReport report, ReviewMapQuery query) =>
        IsMapAssetRequest(query.Asset)
        && IsCanonicalMapAssetName(report.AssetName)
        && IsSafeText(report.DataType, MaximumDataTypeLength)
        && IdentityMatches(report.AssetName, query.Asset);

    private static bool LayerListMatches(
        ReviewMapReport report,
        ReviewMapQuery query)
    {
        if (!ExactMapIdentityMatches(report, query)
            || report.Map is not null
            || report.Layer is not null
            || report.Tile is not null
            || report.Property is not null
            || report.Assets is not null
            || report.TileSheets is not null
            || report.Warps is not null
            || report.Coverage is not null
            || report.Layers is not { } layers
            || !PageMatches(
                report.Page,
                query,
                layers.Count,
                ReviewMapContract.MaximumLayersPerMap))
        {
            return false;
        }

        var exactIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < layers.Count; index++)
        {
            ReviewMapLayerReport? layer = layers[index];
            if (!LayerReportMatches(layer, checked(query.Offset + index), expectedId: null)
                || !exactIds.Add(layer.Id)
                || !normalizedIds.Add(StableIdentityNormalizer.Normalize(layer.Id)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ExactLayerMatches(
        ReviewMapReport report,
        ReviewMapQuery query) =>
        ExactMapIdentityMatches(report, query)
        && report.Map is null
        && LayerReportMatches(report.Layer, expectedOrdinal: null, query.Layer)
        && report.Tile is null
        && report.Property is null
        && report.Assets is null
        && report.Layers is null
        && report.TileSheets is null
        && report.Warps is null
        && report.Page is null
        && report.Coverage is null;

    private static bool TileSheetListMatches(
        ReviewMapReport report,
        ReviewMapQuery query)
    {
        if (!ExactMapIdentityMatches(report, query)
            || report.Map is not null
            || report.Layer is not null
            || report.Tile is not null
            || report.Property is not null
            || report.Assets is not null
            || report.Layers is not null
            || report.Warps is not null
            || report.Coverage is not null
            || report.TileSheets is not { } tileSheets
            || !PageMatches(
                report.Page,
                query,
                tileSheets.Count,
                ReviewMapContract.MaximumTileSheetsPerMap))
        {
            return false;
        }

        var exactIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tileSheets.Count; index++)
        {
            ReviewMapTileSheetReport? tileSheet = tileSheets[index];
            if (!TileSheetMatches(tileSheet, checked(query.Offset + index))
                || !exactIds.Add(tileSheet.Id)
                || !normalizedIds.Add(StableIdentityNormalizer.Normalize(tileSheet.Id)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool WarpListMatches(
        ReviewMapReport report,
        ReviewMapQuery query)
    {
        if (!ExactMapIdentityMatches(report, query)
            || report.Map is not null
            || report.Layer is not null
            || report.Tile is not null
            || report.Property is not null
            || report.Assets is not null
            || report.Layers is not null
            || report.TileSheets is not null
            || report.Coverage is not null
            || report.Warps is not { } warps
            || !PageMatches(
                report.Page,
                query,
                warps.Count,
                ReviewMapContract.MaximumWarpsPerMap))
        {
            return false;
        }

        for (var index = 0; index < warps.Count; index++)
        {
            if (!WarpMatches(warps[index], checked(query.Offset + index)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ExactTileMatches(
        ReviewMapReport report,
        ReviewMapQuery query) =>
        ExactMapIdentityMatches(report, query)
        && report.Map is null
        && report.Layer is null
        && TileMatches(report.Tile, query)
        && report.Property is null
        && report.Assets is null
        && report.Layers is null
        && report.TileSheets is null
        && report.Warps is null
        && report.Page is null
        && report.Coverage is null;

    private static bool PropertyMatches(ReviewMapReport report, ReviewMapQuery query)
    {
        if (report.Property is not ReviewMapPropertyReport property
            || report.Map is not null
            || report.Assets is not null
            || report.Layers is not null
            || report.TileSheets is not null
            || report.Warps is not null
            || report.Page is not null
            || report.Coverage is not null
            || !string.Equals(property.Scope, query.PropertyScope, StringComparison.Ordinal)
            || !string.Equals(property.Source, query.PropertySource, StringComparison.Ordinal)
            || property.FrameIndex != query.FrameIndex
            || !string.Equals(property.Name, query.Property, StringComparison.Ordinal)
            || !PropertyValueMatches(property))
        {
            return false;
        }

        return query.PropertyScope switch
        {
            ReviewMapContract.MapScope => report.Layer is null && report.Tile is null,
            ReviewMapContract.LayerScope =>
                LayerReportMatches(report.Layer, expectedOrdinal: null, query.Layer)
                && report.Layer!.PropertyCount > 0
                && report.Tile is null,
            ReviewMapContract.TileScope =>
                LayerReportMatches(report.Layer, expectedOrdinal: null, query.Layer)
                && TilePropertyContextMatches(report.Layer!, report.Tile, query),
            _ => false,
        };
    }

    private static bool TilePropertyContextMatches(
        ReviewMapLayerReport layer,
        ReviewMapTileReport? tile,
        ReviewMapQuery query)
    {
        if (!TileMatches(tile, query)
            || tile is null
            || !tile.Present
            || !string.Equals(tile.LayerId, layer.Id, StringComparison.Ordinal)
            || tile.X >= layer.Width
            || tile.Y >= layer.Height)
        {
            return false;
        }

        if (query.PropertySource == ReviewMapContract.DirectSource)
        {
            return query.FrameIndex is null && tile.DirectPropertyCount > 0;
        }

        if (query.PropertySource != ReviewMapContract.TileIndexSource)
        {
            return false;
        }

        if (tile.Kind == "static")
        {
            return query.FrameIndex is null && tile.TileIndexPropertyCount > 0;
        }

        return tile.Kind == "animated"
            && query.FrameIndex is int frameIndex
            && tile.Frames is not null
            && frameIndex >= 0
            && frameIndex < tile.Frames.Count
            && tile.Frames[frameIndex].TileIndexPropertyCount > 0;
    }

    private static bool LayerReportMatches(
        ReviewMapLayerReport? layer,
        int? expectedOrdinal,
        string? expectedId)
    {
        if (layer is null
            || layer.Ordinal is < 0 or >= ReviewMapContract.MaximumLayersPerMap
            || (expectedOrdinal is not null && layer.Ordinal != expectedOrdinal)
            || !IsStableIdentity(layer.Id, ReviewMapContract.MaximumIdentityLength)
            || (expectedId is not null && !IdentityMatches(layer.Id, expectedId))
            || layer.Width is <= 0 or > ReviewMapContract.MaximumLayerDimension
            || layer.Height is <= 0 or > ReviewMapContract.MaximumLayerDimension
            || (long)layer.Width * layer.Height > ReviewMapContract.MaximumLayerTiles
            || layer.TileWidth is <= 0 or > 1024
            || layer.TileHeight is <= 0 or > 1024
            || (long)layer.Width * layer.TileWidth
                > ReviewMapContract.MaximumDisplayDimension
            || (long)layer.Height * layer.TileHeight
                > ReviewMapContract.MaximumDisplayDimension
            || layer.PropertyCount is < 0 or > ReviewMapContract.MaximumPropertiesPerScope)
        {
            return false;
        }

        return true;
    }

    private static bool TileSheetMatches(
        ReviewMapTileSheetReport? tileSheet,
        int expectedOrdinal)
    {
        if (tileSheet is null
            || tileSheet.Ordinal != expectedOrdinal
            || tileSheet.Ordinal is < 0 or >= ReviewMapContract.MaximumTileSheetsPerMap
            || !IsStableIdentity(tileSheet.Id, ReviewMapContract.MaximumIdentityLength)
            || !IsSafeImageSource(tileSheet.ImageSource)
            || tileSheet.SheetWidth is <= 0 or > ReviewMapContract.MaximumTileSheetDimension
            || tileSheet.SheetHeight is <= 0 or > ReviewMapContract.MaximumTileSheetDimension
            || (long)tileSheet.SheetWidth * tileSheet.SheetHeight
                > ReviewMapContract.MaximumTileSheetTiles
            || tileSheet.TileWidth is <= 0 or > 1024
            || tileSheet.TileHeight is <= 0 or > 1024
            || tileSheet.MarginWidth < 0
            || tileSheet.MarginHeight < 0
            || tileSheet.SpacingWidth < 0
            || tileSheet.SpacingHeight < 0
            || tileSheet.TileCount
                != (long)tileSheet.SheetWidth * tileSheet.SheetHeight
            || tileSheet.PropertyCount is < 0
                or > ReviewMapContract.MaximumPropertiesPerScope)
        {
            return false;
        }

        return true;
    }

    private static bool WarpMatches(
        ReviewMapWarpReport? warp,
        int expectedOrdinal) =>
        warp is not null
        && warp.Ordinal == expectedOrdinal
        && warp.Ordinal is >= 0 and < ReviewMapContract.MaximumWarpsPerMap
        && warp.SourceIndex is >= 0 and < ReviewMapContract.MaximumWarpsPerMap
        && (warp.SourceProperty, warp.Kind) is
            (("Warp", "playerAndNpc") or ("NPCWarp", "npc"))
        && IsSafeText(warp.TargetName, ReviewMapContract.MaximumIdentityLength);

    private static bool TileMatches(ReviewMapTileReport? tile, ReviewMapQuery query) =>
        tile is not null
        && IdentityMatches(tile.LayerId, query.Layer)
        && tile.X == query.X
        && tile.Y == query.Y
        && TileShapeMatches(tile);

    private static bool TileShapeMatches(ReviewMapTileReport tile)
    {
        if (!IsStableIdentity(tile.LayerId, ReviewMapContract.MaximumIdentityLength)
            || tile.X is < 0 or >= ReviewMapContract.MaximumLayerDimension
            || tile.Y is < 0 or >= ReviewMapContract.MaximumLayerDimension
            || tile.DirectPropertyCount is < 0
                or > ReviewMapContract.MaximumPropertiesPerScope
            || tile.TileIndexPropertyCount is < 0
                or > ReviewMapContract.MaximumPropertiesPerScope
            || tile.ProblemCode is not null)
        {
            return false;
        }

        if (!tile.Present)
        {
            return tile.Kind is null
                && tile.TileSheetId is null
                && tile.TileIndex is null
                && tile.BlendMode is null
                && tile.FrameInterval is null
                && tile.Frames is null
                && tile.DirectPropertyCount == 0
                && tile.TileIndexPropertyCount == 0;
        }

        if (tile.Kind == "static")
        {
            return IsTileReference(
                    tile.TileSheetId,
                    tile.TileIndex,
                    tile.BlendMode)
                && tile.FrameInterval is null
                && tile.Frames is null;
        }

        if (tile.Kind != "animated"
            || tile.TileSheetId is not null
            || tile.TileIndex is not null
            || tile.BlendMode is not null
            || tile.FrameInterval is not > 0
            || tile.Frames is not { Count: >= 1 } frames
            || frames.Count > ReviewMapContract.MaximumFramesPerTile
            || tile.TileIndexPropertyCount != 0
            || tile.FrameInterval > long.MaxValue / frames.Count)
        {
            return false;
        }

        for (var index = 0; index < frames.Count; index++)
        {
            ReviewMapTileFrameReport? frame = frames[index];
            if (frame is null
                || frame.Ordinal != index
                || !IsTileReference(
                    frame.TileSheetId,
                    frame.TileIndex,
                    frame.BlendMode)
                || frame.TileIndexPropertyCount is < 0
                    or > ReviewMapContract.MaximumPropertiesPerScope)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTileReference(
        string? tileSheetId,
        int? tileIndex,
        string? blendMode) =>
        IsStableIdentity(tileSheetId, ReviewMapContract.MaximumIdentityLength)
        && tileIndex is >= 0 and < ReviewMapContract.MaximumTileSheetTiles
        && blendMode is "Alpha" or "Additive";

    private static bool SummaryMatches(ReviewMapSummary? summary) =>
        summary is not null
        && summary.DisplayWidth is > 0 and <= ReviewMapContract.MaximumDisplayDimension
        && summary.DisplayHeight is > 0 and <= ReviewMapContract.MaximumDisplayDimension
        && summary.LayerCount is >= 1 and <= ReviewMapContract.MaximumLayersPerMap
        && summary.TileSheetCount is >= 0 and <= ReviewMapContract.MaximumTileSheetsPerMap
        && summary.WarpCount is >= 0 and <= ReviewMapContract.MaximumWarpsPerMap
        && summary.PropertyCount is >= 0 and <= ReviewMapContract.MaximumPropertiesPerScope;

    private static bool PropertyValueMatches(ReviewMapPropertyReport property)
    {
        if (!IsSafeText(property.Name, ReviewMapContract.MaximumPropertyNameLength))
        {
            return false;
        }

        int utf8Bytes;
        switch (property.Type)
        {
            case "string" when property.Value.ValueKind == JsonValueKind.String:
                string? text = property.Value.GetString();
                if (text is null
                    || text.Any(char.IsControl)
                    || !ReviewTransportText.IsWellFormedUtf16(text))
                {
                    return false;
                }

                utf8Bytes = Encoding.UTF8.GetByteCount(text);
                break;
            case "boolean" when property.Value.ValueKind is
                JsonValueKind.True or JsonValueKind.False:
                utf8Bytes = Encoding.UTF8.GetByteCount(property.Value.GetRawText());
                break;
            case "integer" when property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out _):
                utf8Bytes = Encoding.UTF8.GetByteCount(property.Value.GetRawText());
                break;
            case "float" when property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetSingle(out float value)
                && float.IsFinite(value):
                utf8Bytes = Encoding.UTF8.GetByteCount(property.Value.GetRawText());
                break;
            default:
                return false;
        }

        return utf8Bytes <= ReviewMapContract.MaximumPropertyValueBytes;
    }

    private static bool IdentityMatches(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string normalizedExpected = StableIdentityNormalizer.Normalize(expected);
        return normalizedExpected.Length > 0
            && string.Equals(
                StableIdentityNormalizer.Normalize(actual),
                normalizedExpected,
                StringComparison.Ordinal);
    }

    private static bool PageMatches(
        ReviewMapPage? page,
        ReviewMapQuery query,
        int returned,
        int maximumTotal)
    {
        if (page is null
            || returned < 0
            || returned > query.Limit
            || page.Offset != query.Offset
            || page.Limit != query.Limit
            || page.Returned != returned
            || page.Total < 0
            || page.Total > maximumTotal)
        {
            return false;
        }

        long remaining = Math.Max(0L, (long)page.Total - query.Offset);
        int expectedReturned = (int)Math.Min(query.Limit, remaining);
        if (returned != expectedReturned)
        {
            return false;
        }

        long consumed = Math.Min((long)page.Total, (long)query.Offset + returned);
        int? expectedNextOffset = consumed < page.Total ? (int)consumed : null;
        return page.NextOffset == expectedNextOffset;
    }

    private static bool HasAssetInventoryPayload(ReviewMapReport report) =>
        report.Assets is not null
        || report.Page is not null
        || report.Coverage is not null;

    private static bool EmptyPayloadMatches(ReviewMapReport report) =>
        report.AssetName is null
        && report.DataType is null
        && report.Map is null
        && report.Layer is null
        && report.Tile is null
        && report.Property is null
        && report.Assets is null
        && report.Layers is null
        && report.TileSheets is null
        && report.Warps is null
        && report.Page is null
        && report.Coverage is null;

    private static bool ProblemsAreSafe(IReadOnlyList<ReviewMapProblem>? problems)
    {
        if (problems is null || problems.Count > MaximumProblemCount)
        {
            return false;
        }

        foreach (ReviewMapProblem? problem in problems)
        {
            if (problem is null
                || !IsProblemCode(problem.Code)
                || !IsSafeText(problem.Message, MaximumProblemMessageLength))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsProblemCode(string? value) =>
        IsSafeText(value, MaximumProblemCodeLength)
        && value!.All(character => char.IsAsciiLetterOrDigit(character));

    private static bool IsSafeImageSource(string? value)
    {
        if (!IsSafeText(value, MaximumImageSourceLength))
        {
            return false;
        }

        return !Path.IsPathFullyQualified(value!);
    }

    private static bool IsCanonicalMapAssetName(string? value)
    {
        if (!IsSafeText(value, ReviewMapContract.MaximumAssetLength)
            || value!.Contains('\\')
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !IsMapAssetRequest(value))
        {
            return false;
        }

        return true;
    }

    private static bool IsMapAssetRequest(string? value)
    {
        if (!IsSafeText(value, ReviewMapContract.MaximumAssetLength))
        {
            return false;
        }

        string normalized = value!.Replace('\\', '/').Trim();
        return normalized.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith('/')
            && !normalized.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
            && normalized.Split('/').All(segment =>
            segment.Length > 0
            && segment is not "." and not ".."
            && StableIdentityNormalizer.Normalize(segment).Length > 0);
    }

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl)
        && ReviewTransportText.IsWellFormedUtf16(value);

    private static bool IsStableIdentity(string? value, int maximumLength) =>
        IsSafeText(value, maximumLength)
        && StableIdentityNormalizer.Normalize(value!).Length > 0;

    private static void ValidateEnvelopeShape(JsonElement root)
    {
        ResponseJson.RequireExactObject(root, EnvelopeProperties);
        JsonElement report = root.GetProperty("report");
        ResponseJson.RequireExactObject(report, ReportProperties);

        ValidateOptionalObject(report.GetProperty("map"), SummaryProperties);
        ValidateOptionalObject(report.GetProperty("layer"), LayerProperties);
        ValidateOptionalTile(report.GetProperty("tile"));
        ValidateOptionalObject(report.GetProperty("property"), PropertyProperties);
        ResponseJson.ValidateOptionalArray(
            report.GetProperty("assets"),
            int.MaxValue,
            asset =>
            {
                ResponseJson.RequireExactObject(asset, AssetProperties);
                ValidateOptionalObject(asset.GetProperty("map"), SummaryProperties);
            });
        ResponseJson.ValidateOptionalArray(
            report.GetProperty("layers"),
            int.MaxValue,
            layer => ResponseJson.RequireExactObject(layer, LayerProperties));
        ResponseJson.ValidateOptionalArray(
            report.GetProperty("tileSheets"),
            int.MaxValue,
            tileSheet => ResponseJson.RequireExactObject(tileSheet, TileSheetProperties));
        ResponseJson.ValidateOptionalArray(
            report.GetProperty("warps"),
            int.MaxValue,
            warp => ResponseJson.RequireExactObject(warp, WarpProperties));
        ValidateOptionalObject(report.GetProperty("page"), PageProperties);
        ValidateOptionalCoverage(report.GetProperty("coverage"));
        ResponseJson.ValidateRequiredArray(
            report.GetProperty("problems"),
            int.MaxValue,
            problem => ResponseJson.RequireExactObject(problem, ProblemProperties));
    }

    private static void ValidateOptionalTile(JsonElement tile)
    {
        if (tile.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ResponseJson.RequireExactObject(tile, TileProperties);
        ResponseJson.ValidateOptionalArray(
            tile.GetProperty("frames"),
            int.MaxValue,
            frame => ResponseJson.RequireExactObject(frame, TileFrameProperties));
    }

    private static void ValidateOptionalCoverage(JsonElement coverage)
    {
        if (coverage.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ResponseJson.RequireExactObject(coverage, CoverageProperties);
        int discovered = ResponseJson.RequiredInt32(coverage, "discovered");
        int classified = ResponseJson.RequiredInt32(coverage, "classified");
        int mapAssets = ResponseJson.RequiredInt32(coverage, "mapAssets");
        int nonMapAssets = ResponseJson.RequiredInt32(coverage, "nonMapAssets");
        int supported = ResponseJson.RequiredInt32(coverage, "supported");
        int unknown = ResponseJson.RequiredInt32(coverage, "unknown");
        int unclassified = ResponseJson.RequiredInt32(coverage, "unclassified");
        int unsupported = ResponseJson.RequiredInt32(coverage, "unsupported");
        JsonElement completeValue = coverage.GetProperty("complete");
        if (completeValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                "The review-map coverage completion flag is not a Boolean.");
        }

        bool complete = completeValue.GetBoolean();
        bool calculatedComplete = discovered == classified
            && (long)classified == (long)mapAssets + nonMapAssets
            && (long)mapAssets == (long)supported + unsupported
            && unknown == 0
            && unclassified == 0
            && unsupported == 0;
        if (complete != calculatedComplete)
        {
            throw new InvalidDataException(
                "The review-map coverage completion flag is inconsistent.");
        }
    }

    private static void ValidateOptionalObject(
        JsonElement value,
        HashSet<string> properties)
    {
        if (value.ValueKind != JsonValueKind.Null)
        {
            ResponseJson.RequireExactObject(value, properties);
        }
    }

    private static HashSet<string> PropertySet(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static string EncodeOptional(string? value) =>
        value is null ? MissingToken : ReviewTransportToken.Encode(value);

    private static string CoordinateToken(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? MissingToken;

    private static LiveLabCommandResult Failure(
        string operation,
        params ReviewMapProblem[] problems) =>
        new(
            OperationFailed,
            new ReviewMapReport(
                ReviewMapContract.SchemaVersion,
                "blocked",
                operation,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                problems));

    private static ReviewMapProblem Problem(string code, string message) => new(code, message);

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or JsonException
            or NotSupportedException
            or OverflowException
            or PathTooLongException
            or SecurityException
            or UnauthorizedAccessException;
}
