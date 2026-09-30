using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private static int RunProjectReviewTexture(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewTextureCommandRunner runProjectReviewTexture)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewTextureUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewTexture(arguments, out ReviewTextureQuery? query))
        {
            WriteProjectReviewTextureUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewTexture(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewTexture(
        IReadOnlyList<string> arguments,
        out ReviewTextureQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "texture", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewTextureContract.AssetsOperation
                or ReviewTextureContract.GetOperation
                or ReviewTextureContract.PreviewOperation))
        {
            return false;
        }

        bool listOperation = operation == ReviewTextureContract.AssetsOperation;
        if (!TryParseReviewQueryOptions(
                arguments,
                listOperation,
                allowFrame: false,
                ReviewTextureContract.DefaultPageLimit,
                ReviewTextureContract.MaximumPageLimit,
                out ReviewQueryOptions options))
        {
            return false;
        }

        IReadOnlyList<string> operands = options.Operands;
        int offset = options.Offset;
        int limit = options.Limit;

        int expectedOperands = operation == ReviewTextureContract.AssetsOperation
            ? 0
            : 1;
        if (operands.Count != expectedOperands
            || (operands.Count == 1
                && !ReviewTextureContract.IsCanonicalAssetName(operands[0])))
        {
            return false;
        }

        query = new ReviewTextureQuery(
            operation,
            operands.Count > 0 ? operands[0] : null,
            offset,
            limit);
        return true;
    }
}
