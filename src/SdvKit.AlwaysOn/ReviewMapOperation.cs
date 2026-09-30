using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SdvKit.Cli.LiveLab;
#if SDVKIT_GAME_AVAILABLE
using StardewModdingAPI;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;
#endif

namespace SdvKit.AlwaysOn;

internal static partial class ReviewMapOperation
{
    public static ReviewMapReport Execute(ReviewMapQuery query, IReviewMapSource source)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(source);

        ReviewMapProblem? requestProblem = ReviewMapQueryValidation.Validate(query);
        if (requestProblem is not null)
        {
            return Failure(query.Operation, source, requestProblem);
        }

        IReadOnlyList<string> discovered;
        try
        {
            IReadOnlyList<string> inventory = source.DiscoverCanonicalAssetNames();
            if (inventory.Count > ReviewMapContract.MaximumDiscoveredAssets)
            {
                return Failure(
                    query.Operation,
                    source,
                    Problem(
                        "mapInventoryTooLarge",
                        $"The installed canonical Maps asset inventory exceeds the bounded maximum of {ReviewMapContract.MaximumDiscoveredAssets} assets."));
            }

            discovered = inventory
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                query.Operation,
                source,
                Problem(
                    "mapInventoryFailed",
                    $"The installed canonical Maps asset inventory could not be read ({exception.GetType().Name})."));
        }

        return query.Operation switch
        {
            ReviewMapContract.AssetsOperation => ListAssets(query, source, discovered),
            ReviewMapContract.GetOperation => GetMap(query, source, discovered),
            ReviewMapContract.LayersOperation => ListLayers(query, source, discovered),
            ReviewMapContract.LayerOperation => GetLayer(query, source, discovered),
            ReviewMapContract.TileSheetsOperation => ListTileSheets(query, source, discovered),
            ReviewMapContract.WarpsOperation => ListWarps(query, source, discovered),
            ReviewMapContract.TileOperation => GetTile(query, source, discovered),
            ReviewMapContract.PropertyOperation => GetProperty(query, source, discovered),
            _ => Failure(
                query.Operation,
                source,
                Problem("mapOperationUnknown", "The review-map operation is unknown.")),
        };
    }

    public static ReviewMapReport Failure(
        string operation,
        IReviewMapSource source,
        ReviewMapProblem problem) =>
        Report(
            "blocked",
            operation,
            source,
            problems: [problem]);

    private static ReviewMapReport ListAssets(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        IReadOnlyDictionary<string, int> collisionCounts = discovered
            .Where(IsCanonicalAssetName)
            .GroupBy(StableIdentityNormalizer.Normalize, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var reports = new List<ReviewMapAssetReport>();
        var mapAssets = 0;
        var nonMapAssets = 0;
        var supported = 0;
        var unknown = 0;
        var unclassified = 0;
        var unsupported = 0;

        for (var assetOrdinal = 0; assetOrdinal < discovered.Count; assetOrdinal++)
        {
            string assetName = discovered[assetOrdinal];
            if (!IsCanonicalAssetName(assetName))
            {
                unknown++;
                reports.Add(Gap(
                    $"invalid-map-asset-{assetOrdinal:D4}",
                    null,
                    "mapAssetNameInvalid"));
                continue;
            }

            string normalized = StableIdentityNormalizer.Normalize(assetName);
            if (normalized.Length == 0 || collisionCounts[normalized] != 1)
            {
                unclassified++;
                reports.Add(Gap(assetName, null, "mapAssetNormalizationCollision"));
                continue;
            }

            if (!TryLoadTypedMapOrKnownNonMap(
                    source,
                    assetName,
                    out ReviewMapLoadedAsset loaded))
            {
                unclassified++;
                reports.Add(Gap(assetName, null, "mapAssetLoadFailed"));
                continue;
            }

            if (loaded.Map is null)
            {
                nonMapAssets++;
                reports.Add(new ReviewMapAssetReport(
                    assetName,
                    loaded.DataType,
                    "nonMap",
                    null,
                    false,
                    null));
                continue;
            }

            mapAssets++;
            string? problemCode = ValidateMap(loaded.Map);
            if (problemCode is not null)
            {
                unsupported++;
                reports.Add(new ReviewMapAssetReport(
                    assetName,
                    loaded.DataType,
                    "map",
                    null,
                    false,
                    problemCode));
                continue;
            }

            supported++;
            reports.Add(new ReviewMapAssetReport(
                assetName,
                loaded.DataType,
                "map",
                loaded.Map.Summary,
                true,
                null));
        }

        int classified = mapAssets + nonMapAssets;
        var coverage = new ReviewMapCoverageReport(
            discovered.Count,
            classified,
            mapAssets,
            nonMapAssets,
            supported,
            unknown,
            unclassified,
            unsupported);
        IReadOnlyList<ReviewMapAssetReport> orderedReports = reports
            .OrderBy(report => report.AssetName, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<ReviewMapAssetReport> page = orderedReports
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
        return Report(
            coverage.Complete ? "ready" : "blocked",
            query.Operation,
            source,
            assets: page,
            page: Page(query, page.Count, orderedReports.Count),
            coverage: coverage,
            problems: coverage.Complete
                ? []
                : [Problem(
                    "mapCoverageIncomplete",
                    "The installed Maps candidate inventory contains unknown, unclassified, or unsupported map assets.")]);
    }

    private static ReviewMapReport GetMap(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveMap(query.Asset!, source, discovered, out ResolvedMap? resolved, out ReviewMapProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        return Report(
            "ready",
            query.Operation,
            source,
            resolved!.AssetName,
            resolved.Loaded.DataType,
            map: resolved.Loaded.Map!.Summary);
    }

    private static ReviewMapReport ListLayers(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveMap(query.Asset!, source, discovered, out ResolvedMap? resolved, out ReviewMapProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        IReadOnlyList<ReviewMapLayerReport> page = resolved!.Loaded.Map!.Layers
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(layer => layer.Report)
            .ToArray();
        return Report(
            "ready",
            query.Operation,
            source,
            resolved.AssetName,
            resolved.Loaded.DataType,
            layers: page,
            page: Page(query, page.Count, resolved.Loaded.Map.Layers.Count));
    }

    private static ReviewMapReport ListTileSheets(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveMap(query.Asset!, source, discovered, out ResolvedMap? resolved, out ReviewMapProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        IReadOnlyList<ReviewMapTileSheetReport> page = resolved!.Loaded.Map!.TileSheets
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
        return Report(
            "ready",
            query.Operation,
            source,
            resolved.AssetName,
            resolved.Loaded.DataType,
            tileSheets: page,
            page: Page(query, page.Count, resolved.Loaded.Map.TileSheets.Count));
    }

    private static ReviewMapReport GetLayer(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveMap(query.Asset!, source, discovered, out ResolvedMap? resolved, out ReviewMapProblem? problem)
            || !TryResolveLayer(query.Layer!, resolved?.Loaded.Map!, out ReviewMapLayerSnapshot? layer, out problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        return Report(
            "ready",
            query.Operation,
            source,
            resolved!.AssetName,
            resolved.Loaded.DataType,
            layer: layer!.Report);
    }

    private static ReviewMapReport ListWarps(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveMap(query.Asset!, source, discovered, out ResolvedMap? resolved, out ReviewMapProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        IReadOnlyList<ReviewMapWarpReport> page = resolved!.Loaded.Map!.Warps
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
        return Report(
            "ready",
            query.Operation,
            source,
            resolved.AssetName,
            resolved.Loaded.DataType,
            warps: page,
            page: Page(query, page.Count, resolved.Loaded.Map.Warps.Count));
    }

    private static ReviewMapReport GetTile(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveMap(query.Asset!, source, discovered, out ResolvedMap? resolved, out ReviewMapProblem? problem)
            || !TryResolveLayer(query.Layer!, resolved?.Loaded.Map!, out ReviewMapLayerSnapshot? layer, out problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        if (!Inside(layer!.Report, query.X!.Value, query.Y!.Value))
        {
            return Failure(
                query.Operation,
                source,
                Problem("mapTileOutOfBounds", "The selected tile is outside the exact layer dimensions."));
        }

        ReviewMapTileSnapshot tile;
        try
        {
            tile = source.ReadTile(
                resolved!.AssetName,
                layer.Report.Id,
                query.X.Value,
                query.Y.Value);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                query.Operation,
                source,
                Problem(
                    "mapTileReadFailed",
                    $"The exact map tile could not be read safely ({exception.GetType().Name})."));
        }

        string? tileProblem = ValidateTile(tile, layer.Report, resolved!.Loaded.Map!);
        if (tileProblem is not null)
        {
            return Failure(
                query.Operation,
                source,
                Problem(tileProblem, "The exact map tile has an unsupported or unsafe shape."));
        }

        return Report(
            "ready",
            query.Operation,
            source,
            resolved.AssetName,
            resolved.Loaded.DataType,
            tile: tile.Report);
    }

    private static ReviewMapReport GetProperty(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveProperties(query, source, discovered, out PropertySelection? selected, out ReviewMapProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        if (!TryResolveProperty(query.Property!, selected!.Properties, out ReviewMapPropertyValue? property, out problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        return Report(
            "ready",
            query.Operation,
            source,
            selected.Map.AssetName,
            selected.Map.Loaded.DataType,
            layer: selected.Layer?.Report,
            tile: selected.Tile?.Report,
            property: new ReviewMapPropertyReport(
                query.PropertyScope!,
                query.PropertySource!,
                query.FrameIndex,
                property!.Name,
                property.Type,
                property.Value));
    }

    private static ReviewMapAssetReport Gap(
        string assetName,
        string? dataType,
        string problemCode) =>
        new(assetName, dataType, "gap", null, false, problemCode);

    private static ReviewMapPage Page(ReviewMapQuery query, int returned, int total)
    {
        int consumed = Math.Min(total, checked(query.Offset + returned));
        return new ReviewMapPage(
            query.Offset,
            query.Limit,
            returned,
            total,
            consumed < total ? consumed : null);
    }

    private static ReviewMapReport Report(
        string state,
        string operation,
        IReviewMapSource source,
        string? assetName = null,
        string? dataType = null,
        ReviewMapSummary? map = null,
        ReviewMapLayerReport? layer = null,
        ReviewMapTileReport? tile = null,
        ReviewMapPropertyReport? property = null,
        IReadOnlyList<ReviewMapAssetReport>? assets = null,
        IReadOnlyList<ReviewMapLayerReport>? layers = null,
        IReadOnlyList<ReviewMapTileSheetReport>? tileSheets = null,
        IReadOnlyList<ReviewMapWarpReport>? warps = null,
        ReviewMapPage? page = null,
        ReviewMapCoverageReport? coverage = null,
        IReadOnlyList<ReviewMapProblem>? problems = null) =>
        new(
            ReviewMapContract.SchemaVersion,
            state,
            operation,
            source.GameVersion,
            source.GameFileVersion,
            assetName,
            dataType,
            map,
            layer,
            tile,
            property,
            assets,
            layers,
            tileSheets,
            warps,
            page,
            coverage,
            problems ?? []);

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
            or UnauthorizedAccessException;
}
