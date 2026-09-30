using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private static int RunProjectReviewMap(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewMapCommandRunner runProjectReviewMap)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewMapUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewMap(arguments, out ReviewMapQuery? query))
        {
            WriteProjectReviewMapUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewMap(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewMap(
        IReadOnlyList<string> arguments,
        out ReviewMapQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "map", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewMapContract.AssetsOperation
                or ReviewMapContract.GetOperation
                or ReviewMapContract.LayersOperation
                or ReviewMapContract.LayerOperation
                or ReviewMapContract.TileSheetsOperation
                or ReviewMapContract.WarpsOperation
                or ReviewMapContract.TileOperation
                or ReviewMapContract.PropertyOperation))
        {
            return false;
        }

        bool listOperation = operation is ReviewMapContract.AssetsOperation
            or ReviewMapContract.LayersOperation
            or ReviewMapContract.TileSheetsOperation
            or ReviewMapContract.WarpsOperation;
        if (!TryParseReviewQueryOptions(
                arguments,
                listOperation,
                allowFrame: operation == ReviewMapContract.PropertyOperation,
                ReviewMapContract.DefaultPageLimit,
                ReviewMapContract.MaximumPageLimit,
                out ReviewQueryOptions options))
        {
            return false;
        }

        IReadOnlyList<string> operands = options.Operands;
        int offset = options.Offset;
        int limit = options.Limit;
        int? frameIndex = options.FrameIndex;

        string? asset = null;
        string? layer = null;
        int? x = null;
        int? y = null;
        string? propertyScope = null;
        string? propertySource = null;
        string? property = null;
        switch (operation)
        {
            case ReviewMapContract.AssetsOperation when operands.Count == 0:
                break;
            case ReviewMapContract.GetOperation:
            case ReviewMapContract.LayersOperation:
            case ReviewMapContract.TileSheetsOperation:
            case ReviewMapContract.WarpsOperation:
                if (operands.Count != 1)
                {
                    return false;
                }
                asset = operands[0];
                break;
            case ReviewMapContract.LayerOperation:
                if (operands.Count != 2)
                {
                    return false;
                }
                asset = operands[0];
                layer = operands[1];
                break;
            case ReviewMapContract.TileOperation:
                if (operands.Count != 4
                    || !TryParseNonNegative(operands[2], out int tileX)
                    || !TryParseNonNegative(operands[3], out int tileY))
                {
                    return false;
                }
                asset = operands[0];
                layer = operands[1];
                x = tileX;
                y = tileY;
                break;
            case ReviewMapContract.PropertyOperation:
                if (!TryParseMapPropertyOperands(
                        operands,
                        frameIndex,
                        out asset,
                        out layer,
                        out x,
                        out y,
                        out propertyScope,
                        out propertySource,
                        out property))
                {
                    return false;
                }
                break;
            default:
                return false;
        }

        query = new ReviewMapQuery(
            operation,
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
        return ProjectReviewMapService.Validate(query) is null;
    }

    private static bool TryParseMapPropertyOperands(
        IReadOnlyList<string> operands,
        int? frameIndex,
        out string? asset,
        out string? layer,
        out int? x,
        out int? y,
        out string? scope,
        out string? source,
        out string? property)
    {
        asset = null;
        layer = null;
        x = null;
        y = null;
        scope = null;
        source = null;
        property = null;
        if (operands.Count == 3 && operands[1] == ReviewMapContract.MapScope)
        {
            asset = operands[0];
            scope = ReviewMapContract.MapScope;
            source = ReviewMapContract.DirectSource;
            property = operands[2];
            return frameIndex is null;
        }
        if (operands.Count == 4 && operands[1] == ReviewMapContract.LayerScope)
        {
            asset = operands[0];
            scope = ReviewMapContract.LayerScope;
            layer = operands[2];
            source = ReviewMapContract.DirectSource;
            property = operands[3];
            return frameIndex is null;
        }
        if (operands.Count == 7
            && operands[1] == ReviewMapContract.TileScope
            && operands[5] is ReviewMapContract.DirectSource or ReviewMapContract.TileIndexSource
            && TryParseNonNegative(operands[3], out int tileX)
            && TryParseNonNegative(operands[4], out int tileY))
        {
            asset = operands[0];
            scope = ReviewMapContract.TileScope;
            layer = operands[2];
            x = tileX;
            y = tileY;
            source = operands[5];
            property = operands[6];
            return source == ReviewMapContract.TileIndexSource || frameIndex is null;
        }

        return false;
    }
}
