using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.GameSide;

internal static partial class ReviewMapOperation
{
    private static readonly JsonSerializerOptions ActionResponseJsonOptions = new(JsonSerializerDefaults.Web);
    private static ReviewMapReport ListActions(
        ReviewMapQuery query,
        IReviewMapSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveMap(query.Asset!, source, discovered, out ResolvedMap? resolved, out ReviewMapProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        IReadOnlyList<ReviewMapActionReport> actions;
        try
        {
            actions = source.ReadActions(resolved!.AssetName);
            if (!ReviewMapActionValidation.IsValidList(actions))
            {
                return Failure(query.Operation, source,
                    Problem("mapActionShapeInvalid", "The map actions have an unsupported or unsafe shape."));
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(query.Operation, source,
                Problem("mapActionsReadFailed", $"The map actions could not be read safely ({exception.GetType().Name})."));
        }

        ReviewMapActionReport[] page = actions.Skip(query.Offset).Take(query.Limit).ToArray();
        ReviewMapPage pagination = Page(query, page.Length, actions.Count);
        ReviewMapReport report = Report("ready", query.Operation, source,
            resolved!.AssetName, resolved.Loaded.DataType,
            page: pagination,
            actions: page, truncated: pagination.NextOffset is not null);
        // Escaped JSON can be much larger than the raw UTF-8 property budget.
        var envelope = new ReviewMapResponseEnvelope(ReviewMapContract.SchemaVersion,
            "00000000000000000000000000000000", report);
        if (JsonSerializer.SerializeToUtf8Bytes(envelope, ActionResponseJsonOptions).Length
            > ReviewMapContract.MaximumResponseBytes)
        {
            return Failure(query.Operation, source,
                Problem("mapResponseTooLarge", "The map actions page exceeds the bounded response size; request a smaller limit."));
        }
        return report;
    }

    internal static IReadOnlyList<ReviewMapActionReport> CaptureActions(
        ReviewMapAssetSnapshot map,
        Func<string, int, int, ReviewMapTileSnapshot> readTile)
    {
        string? problem = ValidateMap(map);
        if (problem is not null)
        {
            throw new InvalidDataException("The map action scan requires a supported map shape.");
        }
        long work = map.Layers.Sum(layer => (long)layer.Report.Width * layer.Report.Height);
        if (work > ReviewMapContract.MaximumActionScanTiles)
        {
            throw new InvalidDataException("The map exceeds the aggregate action scan tile limit.");
        }

        var actions = new List<ReviewMapActionReport>();
        long payloadBytes = 0;
        foreach (ReviewMapLayerSnapshot layer in map.Layers.OrderBy(layer => layer.Report.Id, StringComparer.Ordinal))
        {
            for (int y = 0; y < layer.Report.Height; y++)
            {
                for (int x = 0; x < layer.Report.Width; x++)
                {
                    ReviewMapTileSnapshot tile = readTile(layer.Report.Id, x, y);
                    if (ValidateTile(tile, layer.Report, map) is not null
                        || tile.Report.X != x || tile.Report.Y != y)
                    {
                        throw new InvalidDataException("A map action tile has an unsupported or unsafe shape.");
                    }
                    work += tile.Frames.Count;
                    if (work > ReviewMapContract.MaximumActionScanTiles)
                    {
                        throw new InvalidDataException("The map exceeds the aggregate action scan frame limit.");
                    }
                    AddActions(tile.DirectProperties, ReviewMapContract.DirectSource, null);
                    AddActions(tile.TileIndexProperties, ReviewMapContract.TileIndexSource, null);
                    foreach (ReviewMapTileFrameSnapshot frame in tile.Frames)
                    {
                        AddActions(frame.TileIndexProperties, ReviewMapContract.TileIndexSource, frame.Report.Ordinal);
                    }

                    void AddActions(IReadOnlyList<ReviewMapPropertyValue> properties, string source, int? frameIndex)
                    {
                        if (!TryAddPropertyPayload(properties, ref payloadBytes))
                        {
                            throw new InvalidDataException("The map action scan property payload exceeds its bounded maximum.");
                        }
                        foreach (ReviewMapPropertyValue property in properties)
                        {
                            if (property.Name is not ("Action" or "TouchAction")) continue;
                            if (property.Type != "string" || property.Value.ValueKind != JsonValueKind.String)
                            {
                                throw new InvalidDataException("A map action property is not a string.");
                            }
                            string value = property.Value.GetString()!;
                            if (!ReviewTransportText.IsWellFormedUtf16(value)
                                || actions.Count >= ReviewMapContract.MaximumActionsPerMap)
                            {
                                throw new InvalidDataException("A map action value or count exceeds its bounded contract.");
                            }
                            actions.Add(new ReviewMapActionReport(layer.Report.Id, x, y, source,
                                frameIndex, property.Name, value, ReviewMapActionValidation.FirstToken(value)));
                        }
                    }
                }
            }
        }
        actions.Sort(ReviewMapActionValidation.Compare);
        return actions;
    }
}
