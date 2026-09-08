using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewInventoryUsage = "Usage: sdvkit project review inventory [--topology single] --json";

    private static int RunProjectReviewInventory(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewInventoryUsage);
            output.WriteLine("Read every bounded backpack slot from the exact world-ready player in an owned single review. Includes empty, occupied, and explicitly unavailable slots plus a capture ID and content revision. Read-only; no menu or item mutation.");
            return Success;
        }
        if (!TryParseReviewMenu(arguments, out string topology, out string? role) || topology != "single" || role is not null)
        {
            error.WriteLine(ReviewInventoryUsage);
            return UsageError;
        }
        ReviewInventoryReport report = ProjectReviewInventoryService.Execute(
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory));
        return WriteReviewInventoryReport(report, output);
    }

    internal static int WriteReviewInventoryReport(ReviewInventoryReport report, TextWriter output)
    {
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }
}
