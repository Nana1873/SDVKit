using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewMenuUsage =
        "Usage: sdvkit project review menu [--topology <single|network-2>] [--role <host|farmhand>] --json";

    private static int RunProjectReviewMenu(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewMenuUsage);
            output.WriteLine("Read fresh bounded menu geometry, active pages and public controls from the exact world-ready role. Vanilla inventory/shop adapters; unknown mod menus expose partial public base fields. No clicks or inferred selection/hover/clickability. IDs last only for the observed root-menu lifetime within one launch.");
            return Success;
        }
        if (!TryParseReviewMenu(arguments, out string topology, out string? role))
        {
            error.WriteLine(ReviewMenuUsage);
            return UsageError;
        }
        var reader = new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory, topology, role);
        ReviewMenuReport report = ProjectReviewMenuService.Execute(reader);
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }

    internal static bool TryParseReviewMenu(IReadOnlyList<string> arguments, out string topology, out string? role)
    {
        topology = "single";
        role = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 3; i < arguments.Count; i++)
        {
            string option = arguments[i];
            if (!seen.Add(option)) return false;
            if (option == "--json") continue;
            if (++i >= arguments.Count) return false;
            if (option == "--topology") topology = arguments[i];
            else if (option == "--role") role = arguments[i];
            else return false;
        }
        return seen.Contains("--json") && (topology == "single" ? role is null
            : topology == "network-2" && role is not null && NetworkTwoContract.IsRole(role));
    }
}
