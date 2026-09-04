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

internal interface IReviewMapSource
{
    string GameVersion { get; }

    string GameFileVersion { get; }

    IReadOnlyList<string> DiscoverCanonicalAssetNames();

    ReviewMapAssetIdentity CanonicalizeAssetName(string assetName);

    bool AssetExistsForMapRequest(string assetName);

    ReviewMapLoadedAsset LoadAsset(string assetName);

    ReviewMapLoadedAsset LoadMapAsset(string assetName);

    ReviewMapTileSnapshot ReadTile(string assetName, string layerId, int x, int y);
}

internal sealed record ReviewMapAssetIdentity(
    string Name,
    string BaseName,
    string? LocaleCode);

internal static class ReviewMapOperation
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

    private static bool TryResolveProperties(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered,
        out PropertySelection? selected,
        out ReviewMapProblem? problem)
    {
        selected = null;
        if (!TryResolveMap(query.Asset!, source, discovered, out ResolvedMap? map, out problem))
        {
            return false;
        }

        if (query.PropertyScope == ReviewMapContract.MapScope)
        {
            selected = new PropertySelection(
                map!,
                null,
                null,
                map!.Loaded.Map!.Properties);
            return true;
        }

        if (!TryResolveLayer(query.Layer!, map!.Loaded.Map!, out ReviewMapLayerSnapshot? layer, out problem))
        {
            return false;
        }

        if (query.PropertyScope == ReviewMapContract.LayerScope)
        {
            selected = new PropertySelection(
                map,
                layer,
                null,
                layer!.Properties);
            return true;
        }

        if (!Inside(layer!.Report, query.X!.Value, query.Y!.Value))
        {
            problem = Problem("mapTileOutOfBounds", "The selected tile is outside the exact layer dimensions.");
            return false;
        }

        ReviewMapTileSnapshot tile;
        try
        {
            tile = source.ReadTile(
                map.AssetName,
                layer.Report.Id,
                query.X.Value,
                query.Y.Value);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            problem = Problem(
                "mapTileReadFailed",
                $"The exact map tile could not be read safely ({exception.GetType().Name}).");
            return false;
        }

        string? tileProblem = ValidateTile(tile, layer.Report, map.Loaded.Map!);
        if (tileProblem is not null)
        {
            problem = Problem(tileProblem, "The exact map tile has an unsupported or unsafe shape.");
            return false;
        }

        IReadOnlyList<ReviewMapPropertyValue> properties;
        if (query.PropertySource == ReviewMapContract.DirectSource)
        {
            properties = tile.DirectProperties;
        }
        else if (!tile.Report.Present)
        {
            problem = Problem("mapTileEmpty", "The selected in-bounds tile is empty and has no tile-index properties.");
            return false;
        }
        else if (string.Equals(tile.Report.Kind, "static", StringComparison.Ordinal)
            && query.FrameIndex is null)
        {
            properties = tile.TileIndexProperties;
        }
        else if (string.Equals(tile.Report.Kind, "static", StringComparison.Ordinal))
        {
            problem = Problem(
                "mapPropertyFrameInvalid",
                "Static tile-index properties do not accept a frame index.");
            return false;
        }
        else if (query.FrameIndex is int frameIndex
            && frameIndex >= 0
            && frameIndex < tile.Frames.Count)
        {
            properties = tile.Frames[frameIndex].TileIndexProperties;
        }
        else
        {
            problem = Problem(
                "mapPropertyFrameInvalid",
                "Animated tile-index properties require an explicit in-range frame index.");
            return false;
        }

        selected = new PropertySelection(map, layer, tile, properties);
        return true;
    }

    private static bool TryResolveMap(
        string input,
        IReviewMapSource source,
        IReadOnlyList<string> discovered,
        out ResolvedMap? resolved,
        out ReviewMapProblem? problem)
    {
        if (!IsMapAssetRequest(input))
        {
            resolved = null;
            problem = Problem("mapAssetUnknown", "The requested name is not a canonical Maps asset.");
            return false;
        }

        ReviewMapAssetIdentity canonicalIdentity;
        try
        {
            canonicalIdentity = source.CanonicalizeAssetName(input);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            resolved = null;
            problem = Problem(
                "mapAssetUnknown",
                $"The requested Maps asset name could not be canonicalized ({exception.GetType().Name}).");
            return false;
        }

        if (canonicalIdentity is null
            || canonicalIdentity.LocaleCode is not null
            || !string.Equals(
                canonicalIdentity.Name,
                canonicalIdentity.BaseName,
                StringComparison.Ordinal)
            || !IsCanonicalAssetName(canonicalIdentity.Name))
        {
            resolved = null;
            problem = Problem("mapAssetUnknown", "The requested name is not a canonical Maps asset.");
            return false;
        }

        string canonicalInput = canonicalIdentity.Name;
        string normalizedInput = StableIdentityNormalizer.Normalize(canonicalInput);
        string[] normalizedMatches = discovered
            .Where(IsCanonicalAssetName)
            .Where(candidate => string.Equals(
                StableIdentityNormalizer.Normalize(candidate),
                normalizedInput,
                StringComparison.Ordinal))
            .Take(3)
            .ToArray();
        if (normalizedMatches.Length > 1)
        {
            resolved = null;
            problem = Problem(
                "mapAssetAmbiguous",
                "The map asset token collides after case/separator normalization; the query cannot proceed safely.");
            return false;
        }

        string[] exactMatches = discovered
            .Where(IsCanonicalAssetName)
            .Where(candidate => string.Equals(
                candidate,
                canonicalInput,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        string? exactDiscoveredAssetName = exactMatches.Length == 1
            ? exactMatches[0]
            : null;
        var exactPipelineAssetExists = false;
        if (exactDiscoveredAssetName is null)
        {
            try
            {
                exactPipelineAssetExists = source.AssetExistsForMapRequest(canonicalInput);
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                resolved = null;
                problem = Problem(
                    "mapAssetAvailabilityFailed",
                    $"The canonical map asset's pipeline availability could not be checked ({exception.GetType().Name}).");
                return false;
            }
        }

        if (exactPipelineAssetExists && normalizedMatches.Length == 1)
        {
            resolved = null;
            problem = Problem(
                "mapAssetAmbiguous",
                "The exact active-pipeline map asset collides with a different physical map identity after normalization.");
            return false;
        }

        string? assetName = exactDiscoveredAssetName
            ?? (exactPipelineAssetExists
                ? canonicalInput
                : normalizedMatches.Length == 1
                    ? normalizedMatches[0]
                    : null);
        if (assetName is null)
        {
            resolved = null;
            problem = Problem(
                "mapAssetUnavailableInGameVersion",
                "The requested canonical map asset is unavailable through the running game's active content pipeline.");
            return false;
        }

        if (!TryLoadTypedMapOrKnownNonMap(
                source,
                assetName,
                out ReviewMapLoadedAsset loaded))
        {
            resolved = null;
            problem = Problem(
                "mapAssetLoadFailed",
                "The canonical map asset could not be loaded through the typed active content pipeline.");
            return false;
        }

        if (loaded.Map is null)
        {
            resolved = null;
            problem = Problem(
                "mapAssetNotMap",
                "The canonical Maps candidate exists, but its active content value is not an xTile map.");
            return false;
        }

        string? validationProblem = ValidateMap(loaded.Map);
        if (validationProblem is not null)
        {
            resolved = null;
            problem = Problem(
                validationProblem,
                "The canonical map asset has an unsupported or unsafe structure.");
            return false;
        }

        resolved = new ResolvedMap(assetName, loaded);
        problem = null;
        return true;
    }

    private static bool TryLoadTypedMapOrKnownNonMap(
        IReviewMapSource source,
        string assetName,
        out ReviewMapLoadedAsset loaded)
    {
        try
        {
            loaded = source.LoadMapAsset(assetName);
            return loaded is not null && loaded.Map is not null;
        }
        catch (Exception exception) when (IsControlledFailure(exception)
            || exception is InvalidCastException)
        {
            // Physical Content/Maps candidates can legitimately contain non-map XNBs.
            // A typed map load is still attempted first so DataType-sensitive SMAPI
            // providers and editors participate in every supported map classification.
        }

        try
        {
            ReviewMapLoadedAsset generic = source.LoadAsset(assetName);
            if (generic is null || generic.Map is not null)
            {
                loaded = null!;
                return false;
            }

            loaded = generic;
            return true;
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            loaded = null!;
            return false;
        }
    }

    private static bool TryResolveLayer(
        string input,
        ReviewMapAssetSnapshot map,
        out ReviewMapLayerSnapshot? layer,
        out ReviewMapProblem? problem)
    {
        ReviewMapLayerSnapshot[] exact = map.Layers
            .Where(candidate => string.Equals(candidate.Report.Id, input, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (exact.Length == 1)
        {
            layer = exact[0];
            problem = null;
            return true;
        }

        string normalizedInput = StableIdentityNormalizer.Normalize(input);
        ReviewMapLayerSnapshot[] normalized = map.Layers
            .Where(candidate => string.Equals(
                StableIdentityNormalizer.Normalize(candidate.Report.Id),
                normalizedInput,
                StringComparison.Ordinal))
            .Take(3)
            .ToArray();
        if (normalized.Length == 1)
        {
            layer = normalized[0];
            problem = null;
            return true;
        }

        layer = null;
        problem = normalized.Length > 1
            ? Problem(
                "mapLayerAmbiguous",
                "The layer token collides after case/separator normalization; use an exact canonical layer ID.")
            : Problem("mapLayerUnknown", "The canonical map has no layer with that stable ID.");
        return false;
    }

    private static bool TryResolveProperty(
        string input,
        IReadOnlyList<ReviewMapPropertyValue> properties,
        out ReviewMapPropertyValue? selected,
        out ReviewMapProblem? problem)
    {
        ReviewMapPropertyValue[] exact = properties
            .Where(property => string.Equals(property.Name, input, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (exact.Length == 1)
        {
            selected = exact[0];
            problem = null;
            return true;
        }

        selected = null;
        problem = exact.Length > 1
            ? Problem("mapPropertyAmbiguous", "The selected scope contains duplicate exact property names.")
            : Problem("mapPropertyUnknown", "The selected map scope has no exact case-sensitive property with that name.");
        return false;
    }

    private static string? ValidateMap(ReviewMapAssetSnapshot map)
    {
        if (map.ProblemCode is not null)
        {
            return map.ProblemCode;
        }
        if (map.Summary.DisplayWidth <= 0
            || map.Summary.DisplayHeight <= 0
            || map.Summary.DisplayWidth > ReviewMapContract.MaximumDisplayDimension
            || map.Summary.DisplayHeight > ReviewMapContract.MaximumDisplayDimension)
        {
            return "mapDimensionsInvalid";
        }
        if (map.Layers.Count is < 1 or > ReviewMapContract.MaximumLayersPerMap
            || map.TileSheets.Count > ReviewMapContract.MaximumTileSheetsPerMap
            || map.Warps.Count > ReviewMapContract.MaximumWarpsPerMap
            || map.Properties.Count > ReviewMapContract.MaximumPropertiesPerScope
            || map.Summary.LayerCount != map.Layers.Count
            || map.Summary.TileSheetCount != map.TileSheets.Count
            || map.Summary.WarpCount != map.Warps.Count
            || map.Summary.PropertyCount != map.Properties.Count)
        {
            return "mapStructureTooLarge";
        }
        if (map.Layers.Any(layer => !IsStableIdentity(
                layer.Report.Id,
                ReviewMapContract.MaximumIdentityLength))
            || map.TileSheets.Any(sheet => !IsStableIdentity(
                sheet.Id,
                ReviewMapContract.MaximumIdentityLength)))
        {
            return "mapIdentityInvalid";
        }
        if (map.Layers.GroupBy(layer => layer.Report.Id, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || map.Layers.GroupBy(layer => StableIdentityNormalizer.Normalize(layer.Report.Id), StringComparer.Ordinal).Any(group => group.Count() != 1)
            || map.TileSheets.GroupBy(sheet => sheet.Id, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || map.TileSheets.GroupBy(sheet => StableIdentityNormalizer.Normalize(sheet.Id), StringComparer.Ordinal).Any(group => group.Count() != 1))
        {
            return "mapIdentityCollision";
        }
        if (!ValidateProperties(map.Properties))
        {
            return "mapPropertyShapeInvalid";
        }
        long propertyPayloadBytes = 0;
        if (!TryAddPropertyPayload(map.Properties, ref propertyPayloadBytes))
        {
            return "mapPropertyPayloadTooLarge";
        }

        for (var ordinal = 0; ordinal < map.Layers.Count; ordinal++)
        {
            ReviewMapLayerSnapshot layer = map.Layers[ordinal];
            ReviewMapLayerReport report = layer.Report;
            long cells;
            long displayWidth;
            long displayHeight;
            try
            {
                cells = checked((long)report.Width * report.Height);
                displayWidth = checked((long)report.Width * report.TileWidth);
                displayHeight = checked((long)report.Height * report.TileHeight);
            }
            catch (OverflowException)
            {
                return "mapLayerShapeInvalid";
            }
            if (report.Ordinal != ordinal
                || !IsStableIdentity(report.Id, ReviewMapContract.MaximumIdentityLength)
                || report.Width <= 0
                || report.Height <= 0
                || report.Width > ReviewMapContract.MaximumLayerDimension
                || report.Height > ReviewMapContract.MaximumLayerDimension
                || cells > ReviewMapContract.MaximumLayerTiles
                || report.TileWidth <= 0
                || report.TileHeight <= 0
                || report.TileWidth > 1024
                || report.TileHeight > 1024
                || displayWidth > ReviewMapContract.MaximumDisplayDimension
                || displayHeight > ReviewMapContract.MaximumDisplayDimension
                || report.PropertyCount != layer.Properties.Count
                || !ValidateProperties(layer.Properties))
            {
                return "mapLayerShapeInvalid";
            }
            if (!TryAddPropertyPayload(layer.Properties, ref propertyPayloadBytes))
            {
                return "mapPropertyPayloadTooLarge";
            }
        }

        for (var ordinal = 0; ordinal < map.TileSheets.Count; ordinal++)
        {
            ReviewMapTileSheetReport sheet = map.TileSheets[ordinal];
            long tileCount;
            try
            {
                tileCount = checked((long)sheet.SheetWidth * sheet.SheetHeight);
            }
            catch (OverflowException)
            {
                return "mapTileSheetShapeInvalid";
            }
            if (sheet.Ordinal != ordinal
                || !IsStableIdentity(sheet.Id, ReviewMapContract.MaximumIdentityLength)
                || !IsSafeImageSource(sheet.ImageSource)
                || sheet.SheetWidth <= 0
                || sheet.SheetHeight <= 0
                || sheet.SheetWidth > ReviewMapContract.MaximumTileSheetDimension
                || sheet.SheetHeight > ReviewMapContract.MaximumTileSheetDimension
                || sheet.TileWidth <= 0
                || sheet.TileHeight <= 0
                || sheet.TileWidth > 1024
                || sheet.TileHeight > 1024
                || sheet.MarginWidth < 0
                || sheet.MarginHeight < 0
                || sheet.SpacingWidth < 0
                || sheet.SpacingHeight < 0
                || tileCount > int.MaxValue
                || tileCount > ReviewMapContract.MaximumTileSheetTiles
                || sheet.TileCount != tileCount
                || sheet.PropertyCount < 0
                || sheet.PropertyCount > ReviewMapContract.MaximumPropertiesPerScope)
            {
                return "mapTileSheetShapeInvalid";
            }
        }

        for (var ordinal = 0; ordinal < map.Warps.Count; ordinal++)
        {
            ReviewMapWarpReport warp = map.Warps[ordinal];
            if (warp.Ordinal != ordinal
                || warp.SourceIndex < 0
                || (warp.SourceProperty, warp.Kind) is not (("Warp", "playerAndNpc") or ("NPCWarp", "npc"))
                || !IsInput(warp.TargetName, ReviewMapContract.MaximumIdentityLength))
            {
                return "mapWarpInvalid";
            }
        }

        return null;
    }

    private static string? ValidateTile(
        ReviewMapTileSnapshot snapshot,
        ReviewMapLayerReport layer,
        ReviewMapAssetSnapshot map)
    {
        ReviewMapTileReport tile = snapshot.Report;
        if (tile.ProblemCode is not null)
        {
            return tile.ProblemCode;
        }
        if (!string.Equals(tile.LayerId, layer.Id, StringComparison.Ordinal)
            || !Inside(layer, tile.X, tile.Y)
            || !ValidateProperties(snapshot.DirectProperties)
            || !ValidateProperties(snapshot.TileIndexProperties)
            || tile.DirectPropertyCount != snapshot.DirectProperties.Count
            || tile.TileIndexPropertyCount != snapshot.TileIndexProperties.Count)
        {
            return "mapTileShapeInvalid";
        }
        long propertyPayloadBytes = 0;
        if (!TryAddPropertyPayload(snapshot.DirectProperties, ref propertyPayloadBytes)
            || !TryAddPropertyPayload(snapshot.TileIndexProperties, ref propertyPayloadBytes))
        {
            return "mapTilePropertyPayloadTooLarge";
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
                && tile.TileIndexPropertyCount == 0
                && snapshot.Frames.Count == 0
                    ? null
                    : "mapTileShapeInvalid";
        }
        if (tile.Kind == "static")
        {
            return IsValidTileReference(tile.TileSheetId, tile.TileIndex, tile.BlendMode, map)
                && tile.TileIndex >= 0
                && tile.FrameInterval is null
                && tile.Frames is null
                && snapshot.Frames.Count == 0
                    ? null
                    : "mapTileShapeInvalid";
        }
        if (tile.Kind != "animated"
            || tile.TileSheetId is not null
            || tile.TileIndex is not null
            || tile.BlendMode is not null
            || tile.FrameInterval is null
            || tile.FrameInterval <= 0
            || tile.Frames is null
            || tile.Frames.Count is < 1 or > ReviewMapContract.MaximumFramesPerTile
            || tile.TileIndexPropertyCount != 0
            || snapshot.TileIndexProperties.Count != 0
            || snapshot.Frames.Count != tile.Frames.Count)
        {
            return "mapTileShapeInvalid";
        }

        try
        {
            _ = checked(tile.FrameInterval.Value * tile.Frames.Count);
        }
        catch (OverflowException)
        {
            return "mapTileShapeInvalid";
        }

        for (var index = 0; index < tile.Frames.Count; index++)
        {
            ReviewMapTileFrameReport frame = tile.Frames[index];
            ReviewMapTileFrameSnapshot frameSnapshot = snapshot.Frames[index];
            if (frame.Ordinal != index
                || frameSnapshot.Report != frame
                || !IsValidTileReference(frame.TileSheetId, frame.TileIndex, frame.BlendMode, map)
                || frame.TileIndexPropertyCount != frameSnapshot.TileIndexProperties.Count
                || !ValidateProperties(frameSnapshot.TileIndexProperties))
            {
                return "mapTileShapeInvalid";
            }
            if (!TryAddPropertyPayload(frameSnapshot.TileIndexProperties, ref propertyPayloadBytes))
            {
                return "mapTilePropertyPayloadTooLarge";
            }
        }

        return null;
    }

    private static bool ValidateProperties(IReadOnlyList<ReviewMapPropertyValue> properties) =>
        properties.Count <= ReviewMapContract.MaximumPropertiesPerScope
        && properties.All(property =>
            IsInput(property.Name, ReviewMapContract.MaximumPropertyNameLength)
            && property.Utf8Bytes is >= 0 and <= ReviewMapContract.MaximumPropertyValueBytes
            && PropertyValueMatchesType(property))
        && !properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1);

    private static bool PropertyValueMatchesType(ReviewMapPropertyValue property)
    {
        bool typeMatches = property.Type switch
        {
            "string" => property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is string text
                && !text.Any(char.IsControl),
            "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out _),
            "float" => property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetSingle(out float value)
                && float.IsFinite(value),
            _ => false,
        };
        if (!typeMatches)
        {
            return false;
        }

        int actualBytes = property.Value.ValueKind == JsonValueKind.String
            ? Encoding.UTF8.GetByteCount(property.Value.GetString()!)
            : Encoding.UTF8.GetByteCount(property.Value.GetRawText());
        return property.Utf8Bytes == actualBytes;
    }

    private static bool TryAddPropertyPayload(
        IReadOnlyList<ReviewMapPropertyValue> properties,
        ref long payloadBytes)
    {
        foreach (ReviewMapPropertyValue property in properties)
        {
            payloadBytes += Encoding.UTF8.GetByteCount(property.Name) + (long)property.Utf8Bytes;
            if (payloadBytes > ReviewMapContract.MaximumPropertyPayloadBytes)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidTileReference(
        string? tileSheetId,
        int? tileIndex,
        string? blendMode,
        ReviewMapAssetSnapshot map)
    {
        if (!IsInput(tileSheetId, ReviewMapContract.MaximumIdentityLength)
            || tileIndex is null
            || tileIndex < 0
            || blendMode is not ("Alpha" or "Additive"))
        {
            return false;
        }

        ReviewMapTileSheetReport[] sheets = map.TileSheets
            .Where(sheet => string.Equals(sheet.Id, tileSheetId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return sheets.Length == 1 && tileIndex < sheets[0].TileCount;
    }

    internal static IReadOnlyList<ReviewMapWarpReport> CaptureWarps(
        IReadOnlyList<ReviewMapPropertyValue> properties,
        out string? problemCode)
    {
        var warps = new List<ReviewMapWarpReport>();
        foreach ((string propertyName, string kind) in new[]
        {
            ("NPCWarp", "npc"),
            ("Warp", "playerAndNpc"),
        })
        {
            ReviewMapPropertyValue[] matches = properties
                .Where(property => string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length > 1)
            {
                problemCode = "mapWarpPropertyAmbiguous";
                return [];
            }
            if (matches.Length == 0)
            {
                continue;
            }
            if (matches[0].Type != "string")
            {
                problemCode = "mapWarpInvalid";
                return [];
            }

            string[] tokens = (matches[0].Value.GetString() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length % 5 != 0)
            {
                problemCode = "mapWarpInvalid";
                return [];
            }
            for (var index = 0; index < tokens.Length; index += 5)
            {
                if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromX)
                    || !int.TryParse(tokens[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromY)
                    || !int.TryParse(tokens[index + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetX)
                    || !int.TryParse(tokens[index + 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetY)
                    || !IsInput(tokens[index + 2], ReviewMapContract.MaximumIdentityLength)
                    || warps.Count >= ReviewMapContract.MaximumWarpsPerMap)
                {
                    problemCode = "mapWarpInvalid";
                    return [];
                }

                warps.Add(new ReviewMapWarpReport(
                    warps.Count,
                    propertyName,
                    index / 5,
                    kind,
                    fromX,
                    fromY,
                    tokens[index + 2],
                    targetX,
                    targetY));
            }
        }

        problemCode = null;
        return warps;
    }

    private static bool IsSafeImageSource(string value)
    {
        if (!IsInput(value, 512))
        {
            return false;
        }

        try
        {
            return !Path.IsPathFullyQualified(value);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return false;
        }
    }

    private static bool IsCanonicalAssetName(string assetName) =>
        assetName.Length <= ReviewMapContract.MaximumAssetLength
        && ReviewMapText.IsWellFormedUtf16(assetName)
        && IsMapAssetRequest(assetName);

    private static bool IsMapAssetRequest(string input)
    {
        string normalized = input.Replace('\\', '/').Trim();
        return normalized.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith('/')
            && !normalized.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
            && normalized.Split('/').All(segment =>
                segment.Length > 0
                && segment is not "." and not ".."
                && !segment.Any(char.IsControl)
                && StableIdentityNormalizer.Normalize(segment).Length > 0);
    }

    private static bool IsInput(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl)
        && ReviewMapText.IsWellFormedUtf16(value);

    private static bool IsStableIdentity(string? value, int maximumLength) =>
        IsInput(value, maximumLength)
        && StableIdentityNormalizer.Normalize(value!).Length > 0;

    private static bool Inside(ReviewMapLayerReport layer, int x, int y) =>
        x >= 0 && y >= 0 && x < layer.Width && y < layer.Height;

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

    private sealed record ResolvedMap(string AssetName, ReviewMapLoadedAsset Loaded);

    private sealed record PropertySelection(
        ResolvedMap Map,
        ReviewMapLayerSnapshot? Layer,
        ReviewMapTileSnapshot? Tile,
        IReadOnlyList<ReviewMapPropertyValue> Properties);
}

internal static class ReviewMapFileInventory
{
    private static readonly Regex LocaleSuffix = new(
        @"\.[a-z]{2}(?:-[a-z]{2})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal const int MaximumVisitedEntries = ReviewMapContract.MaximumDiscoveredAssets * 4;

    public static IReadOnlyList<string> Discover(
        string contentRoot,
        string mapRoot,
        int maximumVisitedEntries = MaximumVisitedEntries,
        Func<string, bool>? isLocalizedAsset = null)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumVisitedEntries, 1);
#else
        if (maximumVisitedEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumVisitedEntries));
        }
#endif

        RefuseReparsePoint(mapRoot);
        var names = new List<string>();
        var pending = new Stack<string>();
        var visitedEntries = 0;
        pending.Push(mapRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                visitedEntries++;
                if (visitedEntries > maximumVisitedEntries)
                {
                    throw new InvalidDataException(
                        "The installed Maps asset tree exceeds its bounded entry maximum.");
                }

                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The installed Maps asset tree contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (!string.Equals(Path.GetExtension(entry), ".xnb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(contentRoot, entry).Replace('\\', '/');
                string assetName = relative[..^Path.GetExtension(relative).Length];
                bool localized = isLocalizedAsset?.Invoke(assetName)
                    ?? LocaleSuffix.IsMatch(assetName);
                if (!localized)
                {
                    names.Add(assetName);
                    if (names.Count > ReviewMapContract.MaximumDiscoveredAssets)
                    {
                        return names;
                    }
                }
            }
        }

        return names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void RefuseReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The installed Maps asset root is not a regular directory.");
        }
    }
}

#if SDVKIT_GAME_AVAILABLE
internal sealed class StardewReviewMapSource : IReviewMapSource
{
    private readonly IModHelper _helper;
    private readonly string _contentRoot;
    private readonly string _mapRoot;

    public StardewReviewMapSource(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _helper = helper;
        string gameRoot = Path.GetDirectoryName(typeof(Game1).Assembly.Location)
            ?? throw new InvalidOperationException("The game assembly has no directory.");
        _contentRoot = Path.Combine(gameRoot, "Content");
        _mapRoot = Path.Combine(_contentRoot, "Maps");
    }

    public string GameVersion => Game1.version.ToString();

    public string GameFileVersion =>
        FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion
        ?? string.Empty;

    public IReadOnlyList<string> DiscoverCanonicalAssetNames() =>
        ReviewMapFileInventory.Discover(
            _contentRoot,
            _mapRoot,
            isLocalizedAsset: assetName =>
                _helper.GameContent.ParseAssetName(assetName).LocaleCode is not null);

    public ReviewMapAssetIdentity CanonicalizeAssetName(string assetName)
    {
        IAssetName parsed = _helper.GameContent.ParseAssetName(assetName);
        return new ReviewMapAssetIdentity(
            parsed.Name,
            parsed.BaseName,
            parsed.LocaleCode);
    }

    public bool AssetExistsForMapRequest(string assetName)
    {
        try
        {
            return _helper.GameContent.DoesAssetExist<Map>(
                _helper.GameContent.ParseAssetName(assetName));
        }
        catch (Microsoft.Xna.Framework.Content.ContentLoadException exception)
        {
            throw new InvalidDataException(
                "The requested map asset's pipeline availability could not be checked.",
                exception);
        }
    }

    public ReviewMapLoadedAsset LoadAsset(string assetName)
    {
        object value;
        try
        {
            value = _helper.GameContent.Load<object>(assetName);
        }
        catch (Microsoft.Xna.Framework.Content.ContentLoadException exception)
        {
            throw new InvalidDataException(
                "The requested map asset is unavailable through the active content pipeline.",
                exception);
        }
        if (value is null)
        {
            throw new InvalidDataException(
                "The requested map asset pipeline returned a null value.");
        }

        string dataType = value.GetType().FullName ?? value.GetType().Name;
        if (value is not Map map)
        {
            return new ReviewMapLoadedAsset(dataType, null);
        }

        return CaptureMap(map, dataType);
    }

    public ReviewMapLoadedAsset LoadMapAsset(string assetName)
    {
        Map map = LoadTypedMap(assetName);
        string dataType = map.GetType().FullName ?? map.GetType().Name;
        return CaptureMap(map, dataType);
    }

    private static ReviewMapLoadedAsset CaptureMap(Map map, string dataType)
    {
        if (map.Layers.Count is < 1 or > ReviewMapContract.MaximumLayersPerMap
            || map.TileSheets.Count > ReviewMapContract.MaximumTileSheetsPerMap)
        {
            throw new InvalidDataException("The map exceeds its bounded structural limits.");
        }
        foreach (Layer layer in map.Layers)
        {
            if (layer.Tiles.Array.GetLength(0) != layer.LayerWidth
                || layer.Tiles.Array.GetLength(1) != layer.LayerHeight)
            {
                throw new InvalidDataException("A map layer tile array does not match its dimensions.");
            }
        }

        var propertyPayloadBytes = 0L;
        IReadOnlyList<ReviewMapPropertyValue> properties = CaptureProperties(
            map.Properties,
            ref propertyPayloadBytes);
        string? warpProblem = null;
        IReadOnlyList<ReviewMapWarpReport> warps = ReviewMapOperation.CaptureWarps(
            properties,
            out warpProblem);
        ReviewMapLayerSnapshot[] layers = map.Layers
            .Select((layer, ordinal) => new ReviewMapLayerSnapshot(
                new ReviewMapLayerReport(
                    ordinal,
                    layer.Id,
                    layer.LayerWidth,
                    layer.LayerHeight,
                    layer.TileWidth,
                    layer.TileHeight,
                    layer.Visible,
                    layer.Properties.Count),
                CaptureProperties(layer.Properties, ref propertyPayloadBytes)))
            .ToArray();
        ReviewMapTileSheetReport[] tileSheets = map.TileSheets
            .Select((sheet, ordinal) => new ReviewMapTileSheetReport(
                ordinal,
                sheet.Id,
                sheet.ImageSource,
                sheet.SheetWidth,
                sheet.SheetHeight,
                sheet.TileWidth,
                sheet.TileHeight,
                sheet.MarginWidth,
                sheet.MarginHeight,
                sheet.SpacingWidth,
                sheet.SpacingHeight,
                checked(sheet.SheetWidth * sheet.SheetHeight),
                sheet.Properties.Count))
            .ToArray();
        int displayWidth = layers.Length == 0
            ? 0
            : layers.Max(layer => checked(layer.Report.Width * layer.Report.TileWidth));
        int displayHeight = layers.Length == 0
            ? 0
            : layers.Max(layer => checked(layer.Report.Height * layer.Report.TileHeight));
        var summary = new ReviewMapSummary(
            displayWidth,
            displayHeight,
            layers.Length,
            tileSheets.Length,
            warps.Count,
            properties.Count);
        return new ReviewMapLoadedAsset(
            dataType,
            new ReviewMapAssetSnapshot(
                summary,
                layers,
                tileSheets,
                warps,
                properties,
                warpProblem));
    }

    public ReviewMapTileSnapshot ReadTile(string assetName, string layerId, int x, int y)
    {
        Map map = LoadTypedMap(assetName);

        Layer layer = map.Layers.Single(candidate => string.Equals(
            candidate.Id,
            layerId,
            StringComparison.Ordinal));
        if (!layer.IsValidTileLocation(x, y))
        {
            throw new InvalidDataException("The selected tile is outside the exact layer.");
        }

        Tile? tile = layer.Tiles[x, y];
        if (tile is null)
        {
            return new ReviewMapTileSnapshot(
                new ReviewMapTileReport(
                    layer.Id,
                    x,
                    y,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    0,
                    0),
                [],
                [],
                []);
        }
        if (!ReferenceEquals(tile.Layer, layer))
        {
            throw new InvalidDataException("The selected tile belongs to another layer.");
        }

        var propertyPayloadBytes = 0L;
        IReadOnlyList<ReviewMapPropertyValue> directProperties = CaptureProperties(
            tile.Properties,
            ref propertyPayloadBytes);
        if (tile is StaticTile staticTile)
        {
            EnsureOwnedTileSheet(map, staticTile.TileSheet);
            IReadOnlyList<ReviewMapPropertyValue> indexProperties =
                CaptureProperties(staticTile.TileIndexProperties, ref propertyPayloadBytes);
            return new ReviewMapTileSnapshot(
                new ReviewMapTileReport(
                    layer.Id,
                    x,
                    y,
                    true,
                    "static",
                    staticTile.TileSheet.Id,
                    staticTile.TileIndex,
                    staticTile.BlendMode.ToString(),
                    null,
                    null,
                    directProperties.Count,
                    indexProperties.Count),
                directProperties,
                indexProperties,
                []);
        }

        if (tile is AnimatedTile animatedTile)
        {
            if (animatedTile.TileFrames.Length is < 1 or > ReviewMapContract.MaximumFramesPerTile)
            {
                throw new InvalidDataException("The animated tile has an unsupported frame count.");
            }
            foreach (StaticTile frame in animatedTile.TileFrames)
            {
                if (!ReferenceEquals(frame.Layer, layer))
                {
                    throw new InvalidDataException("An animated frame belongs to another layer.");
                }
                EnsureOwnedTileSheet(map, frame.TileSheet);
            }
            ReviewMapTileFrameSnapshot[] frames = animatedTile.TileFrames
                .Select((frame, ordinal) =>
                {
                    IReadOnlyList<ReviewMapPropertyValue> indexProperties =
                        CaptureProperties(frame.TileIndexProperties, ref propertyPayloadBytes);
                    return new ReviewMapTileFrameSnapshot(
                        new ReviewMapTileFrameReport(
                            ordinal,
                            frame.TileSheet.Id,
                            frame.TileIndex,
                            frame.BlendMode.ToString(),
                            indexProperties.Count),
                        indexProperties);
                })
                .ToArray();
            return new ReviewMapTileSnapshot(
                new ReviewMapTileReport(
                    layer.Id,
                    x,
                    y,
                    true,
                    "animated",
                    null,
                    null,
                    null,
                    animatedTile.FrameInterval,
                    frames.Select(frame => frame.Report).ToArray(),
                    directProperties.Count,
                    0),
                directProperties,
                [],
                frames);
        }

        return new ReviewMapTileSnapshot(
            new ReviewMapTileReport(
                layer.Id,
                x,
                y,
                true,
                null,
                null,
                null,
                null,
                null,
                null,
                directProperties.Count,
                0,
                "mapTileTypeUnsupported"),
            directProperties,
            [],
            []);
    }

    private Map LoadTypedMap(string assetName)
    {
        try
        {
            return _helper.GameContent.Load<Map>(assetName)
                ?? throw new InvalidDataException(
                    "The requested map asset pipeline returned a null value.");
        }
        catch (Exception exception) when (exception is
            Microsoft.Xna.Framework.Content.ContentLoadException
            or InvalidCastException)
        {
            throw new InvalidDataException(
                "The requested map asset is unavailable through the active content pipeline.",
                exception);
        }
    }

    private static ReviewMapPropertyValue[] CaptureProperties(
        IPropertyCollection properties,
        ref long payloadBytes)
    {
        if (properties.Count > ReviewMapContract.MaximumPropertiesPerScope
            || properties.Any(property => !IsSafeInput(
                property.Key,
                ReviewMapContract.MaximumPropertyNameLength)))
        {
            throw new InvalidDataException(
                "A map property collection exceeds its bounded maximum or contains an unsafe identity.");
        }

        var captured = new List<ReviewMapPropertyValue>(properties.Count);
        foreach (KeyValuePair<string, PropertyValue> property in properties
            .OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            ReviewMapPropertyValue value = CaptureProperty(property.Key, property.Value);
            payloadBytes += Encoding.UTF8.GetByteCount(value.Name) + (long)value.Utf8Bytes;
            if (payloadBytes > ReviewMapContract.MaximumPropertyPayloadBytes)
            {
                throw new InvalidDataException(
                    "The map property payload exceeds its bounded maximum.");
            }

            captured.Add(value);
        }

        return captured.ToArray();
    }

    private static ReviewMapPropertyValue CaptureProperty(
        string name,
        PropertyValue value)
    {
        object typedValue;
        string type;
        if (value.Type == typeof(string))
        {
            typedValue = (string)value;
            type = "string";
        }
        else if (value.Type == typeof(bool))
        {
            typedValue = (bool)value;
            type = "boolean";
        }
        else if (value.Type == typeof(int))
        {
            typedValue = (int)value;
            type = "integer";
        }
        else if (value.Type == typeof(float))
        {
            float number = value;
            if (!float.IsFinite(number))
            {
                throw new InvalidDataException("A map property contains a non-finite number.");
            }

            typedValue = number;
            type = "float";
        }
        else
        {
            throw new InvalidDataException("A map property has an unsupported value type.");
        }

        if (!IsSafeInput(name, ReviewMapContract.MaximumPropertyNameLength)
            || (typedValue is string text
                && (text.Any(char.IsControl)
                    || !ReviewMapText.IsWellFormedUtf16(text))))
        {
            throw new InvalidDataException("A map property has an unsafe identity or value.");
        }

        int? stringUtf8Bytes = typedValue is string stringValue
            ? Encoding.UTF8.GetByteCount(stringValue)
            : null;
        if (stringUtf8Bytes > ReviewMapContract.MaximumPropertyValueBytes)
        {
            throw new InvalidDataException("A map property value exceeds its bounded maximum.");
        }

        JsonElement element = JsonSerializer.SerializeToElement(
            typedValue,
            typedValue.GetType());
        int utf8Bytes = stringUtf8Bytes
            ?? Encoding.UTF8.GetByteCount(element.GetRawText());
        if (utf8Bytes > ReviewMapContract.MaximumPropertyValueBytes)
        {
            throw new InvalidDataException("A map property value exceeds its bounded maximum.");
        }
        return new ReviewMapPropertyValue(name, type, element, utf8Bytes);
    }

    private static bool IsSafeInput(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl)
        && ReviewMapText.IsWellFormedUtf16(value);

    private static void EnsureOwnedTileSheet(Map map, TileSheet tileSheet)
    {
        if (!map.TileSheets.Any(candidate => ReferenceEquals(candidate, tileSheet)))
        {
            throw new InvalidDataException("The selected tile references a foreign tile sheet.");
        }
    }

}

internal static class ReviewMapCommand
{
    private const string MissingToken = "-";
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Handle(
        string[] arguments,
        IReviewMapSource source,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-map runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        string? requestId = arguments.Length > 1 ? arguments[1] : null;
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log("SDVKit review-map rejected an invalid request ID.", LogLevel.Error);
            return;
        }

        ReviewMapReport report;
        bool singleReview = string.Equals(
                Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW"),
                "1",
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
        if (!singleReview)
        {
            string operation = arguments.Length > 2 ? arguments[2] : "unknown";
            report = ReviewMapOperation.Failure(
                operation,
                source,
                new ReviewMapProblem(
                    "mapReviewTopologyUnsupported",
                    "Review-map queries require an active owned single project review."));
        }
        else if (!TryParse(arguments, out ReviewMapQuery? query, out ReviewMapProblem? problem))
        {
            string operation = arguments.Length > 2 ? arguments[2] : "unknown";
            report = ReviewMapOperation.Failure(operation, source, problem!);
        }
        else
        {
            try
            {
                report = ReviewMapOperation.Execute(query!, source);
            }
            catch (Exception exception)
            {
                report = ReviewMapOperation.Failure(
                    query!.Operation,
                    source,
                    new ReviewMapProblem(
                        "mapQueryFailed",
                        $"The review-map query failed closed ({exception.GetType().Name})."));
            }
        }

        var envelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId!,
            report);
        try
        {
            WriteResponse(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-map completed '{report.Operation}' with state '{report.State}'.",
                report.Problems.Count == 0 ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit review-map could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReviewMapQuery? query,
        out ReviewMapProblem? problem)
    {
        query = null;
        problem = null;
        if (arguments.Count != 13
            || !string.Equals(arguments[0], "map", StringComparison.Ordinal)
            || !ReviewTransportToken.IsRequestId(arguments[1])
            || !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int offset)
            || !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int limit))
        {
            problem = new ReviewMapProblem(
                "mapTransportInvalid",
                "The bounded review-map transport request is invalid.");
            return false;
        }

        if (!TryDecodeOptional(arguments[5], ReviewMapContract.MaximumAssetLength, out string? asset)
            || !TryDecodeOptional(arguments[6], ReviewMapContract.MaximumIdentityLength, out string? layer)
            || !TryParseOptionalCoordinate(arguments[7], out int? x)
            || !TryParseOptionalCoordinate(arguments[8], out int? y)
            || !TryDecodeOptional(arguments[9], 32, out string? propertyScope)
            || !TryDecodeOptional(arguments[10], 32, out string? propertySource)
            || !TryParseOptionalCoordinate(arguments[11], out int? frameIndex)
            || !TryDecodeOptional(arguments[12], ReviewMapContract.MaximumPropertyNameLength, out string? property))
        {
            problem = new ReviewMapProblem(
                "mapTransportInvalid",
                "The encoded review-map operands are invalid.");
            return false;
        }

        query = new ReviewMapQuery(
            arguments[2],
            asset,
            layer,
            x,
            y,
            propertyScope,
            propertySource,
            frameIndex,
            property,
            offset,
            limit);
        return true;
    }

    private static bool TryDecodeOptional(string token, int maximumLength, out string? value)
    {
        if (string.Equals(token, MissingToken, StringComparison.Ordinal))
        {
            value = null;
            return true;
        }

        if (ReviewTransportToken.TryDecode(token, maximumLength, out string decoded))
        {
            value = decoded;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParseOptionalCoordinate(string token, out int? value)
    {
        if (string.Equals(token, MissingToken, StringComparison.Ordinal))
        {
            value = null;
            return true;
        }

        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static void WriteResponse(string runtimePath, ReviewMapResponseEnvelope envelope)
    {
        string absoluteRuntimePath = Path.GetFullPath(runtimePath);
        FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
        if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
            || (runtimeAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The review runtime response root is not a regular directory.");
        }

        string responsePath = ReviewMapContract.ResponsePath(
            absoluteRuntimePath,
            envelope.RequestId);
        string temporaryPath = responsePath + ".tmp";
        if (File.Exists(responsePath) || File.Exists(temporaryPath))
        {
            throw new InvalidDataException(
                "The review-map response target already exists.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ResponseJsonOptions);
        if (bytes.Length > ReviewMapContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-map response exceeds its maximum size.");
        }

        var ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                ownsTemporary = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, responsePath);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
#endif
