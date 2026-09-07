using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewShopUsage = "Usage: sdvkit project review shop [--topology single] --json";

    private static int RunProjectReviewShop(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewShopUsage);
            output.WriteLine("Read fresh SeedShop Gold offers, player money, inventory and held item in an exact world-ready single review. Unsupported shop behavior or offers are explicitly unavailable. Point-in-time evidence; no purchase, clickability or transaction guarantee.");
            return Success;
        }
        if (!TryParseReviewMenu(arguments, out string topology, out string? role) || topology != "single" || role is not null)
        {
            error.WriteLine(ReviewShopUsage);
            return UsageError;
        }
        ReviewShopReport report = ProjectReviewShopService.Execute(new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory));
        return WriteReviewShopReport(report, output);
    }

    internal static int WriteReviewShopReport(ReviewShopReport report, TextWriter output)
    {
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }
}
