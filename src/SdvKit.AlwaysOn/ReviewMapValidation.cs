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

    private static bool IsInput(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl)
        && ReviewTransportText.IsWellFormedUtf16(value);

    private static bool IsStableIdentity(string? value, int maximumLength) =>
        IsInput(value, maximumLength)
        && StableIdentityNormalizer.Normalize(value!).Length > 0;

    private static bool Inside(ReviewMapLayerReport layer, int x, int y) =>
        x >= 0 && y >= 0 && x < layer.Width && y < layer.Height;
}
