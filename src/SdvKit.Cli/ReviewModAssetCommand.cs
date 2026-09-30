using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private static int RunProjectReviewModAssets(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewModAssetCommandRunner runProjectReviewModAsset)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewModAssetUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewModAssets(
                arguments,
                out ReviewModAssetQuery? query))
        {
            WriteProjectReviewModAssetUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewModAsset(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewModAssets(
        IReadOnlyList<string> arguments,
        out ReviewModAssetQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "mod-assets", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewModAssetContract.AssetsOperation
                or ReviewModAssetContract.KeysOperation
                or ReviewModAssetContract.GetOperation))
        {
            return false;
        }

        bool listOperation = operation != ReviewModAssetContract.GetOperation;
        if (!TryParseReviewQueryOptions(
                arguments,
                listOperation,
                allowFrame: false,
                ReviewModAssetContract.DefaultPageLimit,
                ReviewModAssetContract.MaximumPageLimit,
                out ReviewQueryOptions options))
        {
            return false;
        }

        IReadOnlyList<string> operands = options.Operands;
        int offset = options.Offset;
        int limit = options.Limit;

        int expectedOperands = operation switch
        {
            ReviewModAssetContract.AssetsOperation => 0,
            ReviewModAssetContract.KeysOperation => 1,
            ReviewModAssetContract.GetOperation => 2,
            _ => throw new InvalidOperationException(),
        };
        if (operands.Count != expectedOperands
            || (operands.Count > 0
                && !ReviewModAssetContract.IsCanonicalAssetName(operands[0]))
            || (operands.Count > 1
                && !ReviewModAssetContract.IsBoundedText(
                    operands[1],
                    ReviewModAssetContract.MaximumKeyLength)))
        {
            return false;
        }

        query = new ReviewModAssetQuery(
            operation,
            operands.Count > 0 ? operands[0] : null,
            operands.Count > 1 ? operands[1] : null,
            offset,
            limit);
        return ProjectReviewModAssetService.Validate(query) is null;
    }
}
