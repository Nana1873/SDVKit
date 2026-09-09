using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewContainerTransferUsage = "Usage: sdvkit project review container-transfer <deposit|withdraw> <source-slot> <quantity> <qualified-item-id> <selection-identity> <container-revision> <instance-identity> <item-revision> [--topology single] --json";

    private static int RunProjectReviewContainerTransfer(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        { output.WriteLine(ReviewContainerTransferUsage); return Success; }
        bool suffixValid = arguments.Count == 12 && arguments[11] == "--json"
            || arguments.Count == 14 && arguments[11] == "--topology"
                && arguments[12] == "single" && arguments[13] == "--json";
        if (!suffixValid || !int.TryParse(arguments[4], out int slot) || !int.TryParse(arguments[5], out int quantity))
        { error.WriteLine(ReviewContainerTransferUsage); return UsageError; }
        var query = new ReviewContainerTransferQuery(arguments[3], slot, quantity, arguments[6], arguments[7],
            arguments[8], arguments[9], arguments[10]);
        if (ReviewContainerTransferContract.Validate(query) is not null)
        { error.WriteLine(ReviewContainerTransferUsage); return UsageError; }
        ReviewContainerTransferReport report = ProjectReviewContainerTransferService.Execute(query,
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory));
        WriteJson(output, report);
        return report.State == "completed" ? Success : InspectionFailed;
    }
}
