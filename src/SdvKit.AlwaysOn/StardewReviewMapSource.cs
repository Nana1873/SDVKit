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
                    || !ReviewTransportText.IsWellFormedUtf16(text))))
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
        && ReviewTransportText.IsWellFormedUtf16(value);

    private static void EnsureOwnedTileSheet(Map map, TileSheet tileSheet)
    {
        if (!map.TileSheets.Any(candidate => ReferenceEquals(candidate, tileSheet)))
        {
            throw new InvalidDataException("The selected tile references a foreign tile sheet.");
        }
    }

}
#endif
