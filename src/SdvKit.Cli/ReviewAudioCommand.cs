using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private static int RunProjectReviewAudio(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewAudioCommandRunner runProjectReviewAudio)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewAudioUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewAudio(arguments, out ReviewAudioQuery? query))
        {
            WriteProjectReviewAudioUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewAudio(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewAudio(
        IReadOnlyList<string> arguments,
        out ReviewAudioQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "audio", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewAudioContract.CuesOperation
                or ReviewAudioContract.CueOperation))
        {
            return false;
        }

        bool listOperation = operation != ReviewAudioContract.CueOperation;
        if (!TryParseReviewQueryOptions(
                arguments,
                listOperation,
                allowFrame: false,
                ReviewAudioContract.DefaultPageLimit,
                ReviewAudioContract.MaximumPageLimit,
                out ReviewQueryOptions options))
        {
            return false;
        }

        IReadOnlyList<string> operands = options.Operands;
        int offset = options.Offset;
        int limit = options.Limit;

        int expectedOperands = operation == ReviewAudioContract.CueOperation ? 1 : 0;
        if (operands.Count != expectedOperands
            || (operation == ReviewAudioContract.CueOperation
                && !ReviewAudioValidation.IsSafeCueId(operands[0])))
        {
            return false;
        }

        query = new ReviewAudioQuery(
            operation,
            operands.Count == 1 ? operands[0] : null,
            offset,
            limit);
        return ProjectReviewAudioService.Validate(query) is null;
    }
}
