using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private readonly record struct ReviewQueryOptions(
        IReadOnlyList<string> Operands,
        int Offset,
        int Limit,
        int? FrameIndex);

    private static bool TryParseReviewQueryOptions(
        IReadOnlyList<string> arguments,
        bool allowPagination,
        bool allowFrame,
        int defaultLimit,
        int maximumLimit,
        out ReviewQueryOptions options)
    {
        options = default;
        var operands = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        int limit = allowPagination ? defaultLimit : 1;
        int? frameIndex = null;
        var operandsAtEndMarker = -1;
        for (var index = 4; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (operandsAtEndMarker < 0 && argument == "--")
            {
                operandsAtEndMarker = operands.Count;
                continue;
            }
            if (operandsAtEndMarker >= 0 || !argument.StartsWith('-'))
            {
                operands.Add(argument);
                continue;
            }
            if (!seen.Add(argument))
            {
                return false;
            }
            if (argument == "--json")
            {
                continue;
            }
            if (argument is not ("--topology" or "--offset" or "--limit" or "--frame")
                || ++index >= arguments.Count)
            {
                return false;
            }

            string value = arguments[index];
            switch (argument)
            {
                case "--topology":
                    if (value != LiveLabState.SingleTopology)
                    {
                        return false;
                    }
                    break;
                case "--offset":
                    if (!allowPagination || !TryParseNonNegative(value, out offset))
                    {
                        return false;
                    }
                    break;
                case "--limit":
                    if (!allowPagination || !TryParseNonNegative(value, out limit)
                        || limit < 1 || limit > maximumLimit)
                    {
                        return false;
                    }
                    break;
                case "--frame":
                    if (!allowFrame || !TryParseNonNegative(value, out int frame))
                    {
                        return false;
                    }
                    frameIndex = frame;
                    break;
            }
        }

        if (!seen.Contains("--json")
            || operands.Any(string.IsNullOrWhiteSpace)
            || operandsAtEndMarker == operands.Count)
        {
            return false;
        }
        options = new ReviewQueryOptions(operands, offset, limit, frameIndex);
        return true;
    }
}
