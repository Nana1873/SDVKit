using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewMenuUsage =
        "Usage: sdvkit project review menu [--topology <single|network-2>] [--role <host|farmhand>] [--screen <id>] --json";

    private static int RunProjectReviewMenu(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewMenuUsage);
            output.WriteLine("Read fresh bounded menu geometry, active pages and public controls from the exact world-ready role or bound local screen. Vanilla inventory, shop, dialogue/question and crafting adapters; unknown mod menus expose partial public base fields. No clicks or inferred selection/hover/clickability. IDs last only for the observed root-menu lifetime within one launch and screen context.");
            return Success;
        }
        if (!TryParseReviewMenu(arguments, out string topology, out string? role, out int? screenId))
        {
            error.WriteLine(ReviewMenuUsage);
            return UsageError;
        }
        var reader = new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory, topology, role, screenId: screenId);
        ReviewMenuReport report = ProjectReviewMenuService.Execute(reader);
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }

    internal static bool TryParseReviewMenu(IReadOnlyList<string> arguments, out string topology, out string? role, out int? screenId)
    {
        topology = "single";
        role = null;
        screenId = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 3; i < arguments.Count; i++)
        {
            string option = arguments[i];
            if (!seen.Add(option)) return false;
            if (option == "--json") continue;
            if (++i >= arguments.Count) return false;
            if (option == "--topology") topology = arguments[i];
            else if (option == "--role") role = arguments[i];
            else if (option == "--screen" && int.TryParse(arguments[i],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                && parsed >= 0) screenId = parsed;
            else return false;
        }
        return seen.Contains("--json") && (topology == "single" ? role is null
            : topology == "network-2" && role is not null && screenId is null && NetworkTwoContract.IsRole(role));
    }

    internal static bool TryParseReviewMenu(IReadOnlyList<string> arguments, out string topology, out string? role)
    {
        bool valid = TryParseReviewMenu(arguments, out topology, out role, out int? screenId);
        return valid && screenId is null;
    }
}
