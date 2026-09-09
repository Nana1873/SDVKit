using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private readonly record struct ReviewQueryOptions(
        IReadOnlyList<string> Operands,
        int Offset,
        int Limit,
        int? FrameIndex,
        string Topology,
        string? Role,
        int? ScreenId);

    private static bool TryParseReviewQueryOptions(
        IReadOnlyList<string> arguments,
        bool allowPagination,
        bool allowFrame,
        int defaultLimit,
        int maximumLimit,
        out ReviewQueryOptions options)
        => TryParseReviewQueryOptions(arguments, allowPagination, allowFrame, allowSelection: false,
            defaultLimit, maximumLimit, out options);

    private static bool TryParseReviewQueryOptions(
        IReadOnlyList<string> arguments,
        bool allowPagination,
        bool allowFrame,
        bool allowSelection,
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
        string topology = LiveLabState.SingleTopology;
        string? role = null;
        int? screenId = null;
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
            if (argument is not ("--topology" or "--role" or "--screen" or "--offset" or "--limit" or "--frame")
                || ++index >= arguments.Count)
            {
                return false;
            }

            string value = arguments[index];
            switch (argument)
            {
                case "--topology":
                    if (value is not (LiveLabState.SingleTopology or NetworkTwoContract.Topology))
                    {
                        return false;
                    }
                    topology = value;
                    break;
                case "--role":
                    if (!allowSelection || !NetworkTwoContract.IsRole(value)) return false;
                    role = value;
                    break;
                case "--screen":
                    if (!allowSelection || !TryParseNonNegative(value, out int parsedScreen)) return false;
                    screenId = parsedScreen;
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
        if (!allowSelection && (topology != LiveLabState.SingleTopology || role is not null || screenId is not null)
            || allowSelection && (topology == LiveLabState.SingleTopology
                ? role is not null
                : topology != NetworkTwoContract.Topology || role is null || screenId is not null))
        {
            return false;
        }
        options = new ReviewQueryOptions(operands, offset, limit, frameIndex, topology, role, screenId);
        return true;
    }
}
