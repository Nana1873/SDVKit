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

    private static bool IsCanonicalAssetName(string assetName) =>
        assetName.Length <= ReviewMapContract.MaximumAssetLength
        && ReviewTransportText.IsWellFormedUtf16(assetName)
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

    private sealed record ResolvedMap(string AssetName, ReviewMapLoadedAsset Loaded);

    private sealed record PropertySelection(
        ResolvedMap Map,
        ReviewMapLayerSnapshot? Layer,
        ReviewMapTileSnapshot? Tile,
        IReadOnlyList<ReviewMapPropertyValue> Properties);
}
