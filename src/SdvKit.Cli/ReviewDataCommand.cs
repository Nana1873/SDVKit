using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private static int RunProjectReviewData(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewDataCommandRunner runProjectReviewData)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewDataUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewData(
                arguments,
                out ReviewDataQuery? query,
                out string? topology,
                out string? role,
                out int? screenId))
        {
            WriteProjectReviewDataUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewData(
            query!,
            Environment.CurrentDirectory,
            topology!,
            role,
            screenId);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewData(
        IReadOnlyList<string> arguments,
        out ReviewDataQuery? query,
        out string? topology,
        out string? role,
        out int? screenId)
    {
        query = null;
        topology = null;
        role = null;
        screenId = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "data", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewDataContract.AssetsOperation
                or ReviewDataContract.KeysOperation
                or ReviewDataContract.GetOperation))
        {
            return false;
        }

        bool listOperation = operation != ReviewDataContract.GetOperation;
        if (!TryParseReviewQueryOptions(
                arguments,
                listOperation,
                allowFrame: false,
                allowSelection: true,
                ReviewDataContract.DefaultPageLimit,
                ReviewDataContract.MaximumPageLimit,
                out ReviewQueryOptions options))
        {
            return false;
        }

        IReadOnlyList<string> operands = options.Operands;
        int offset = options.Offset;
        int limit = options.Limit;

        int expectedOperands = operation switch
        {
            ReviewDataContract.AssetsOperation => 0,
            ReviewDataContract.KeysOperation => 1,
            ReviewDataContract.GetOperation => 2,
            _ => throw new InvalidOperationException(),
        };
        if (operands.Count != expectedOperands)
        {
            return false;
        }

        query = new ReviewDataQuery(
            operation,
            operands.Count > 0 ? operands[0] : null,
            operands.Count > 1 ? operands[1] : null,
            offset,
            limit);
        topology = options.Topology;
        role = options.Role;
        screenId = options.ScreenId;
        return true;
    }
}
