using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewInventoryUsage = "Usage: sdvkit project review inventory [--topology <single|network-2>] [--role <host|farmhand>] [--screen <id>] --json";

    private static int RunProjectReviewInventory(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewInventoryUsage);
            output.WriteLine("Read every bounded backpack slot from the exact selected world-ready player. Network roles and bound local screens remain player-local. Includes empty, occupied, and explicitly unavailable slots plus a capture ID and content revision. Read-only; no peer fallback or item mutation.");
            return Success;
        }
        if (!TryParseReviewMenu(arguments, out string topology, out string? role, out int? screenId))
        {
            error.WriteLine(ReviewInventoryUsage);
            return UsageError;
        }
        ReviewInventoryReport report = ProjectReviewInventoryService.Execute(
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory, topology, role, screenId: screenId));
        return WriteReviewInventoryReport(report, output);
    }

    internal static int WriteReviewInventoryReport(ReviewInventoryReport report, TextWriter output)
    {
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }
}
