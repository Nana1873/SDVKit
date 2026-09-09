using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewWorldUsage =
        "Usage: sdvkit project review world <x> <y> <width> <height> [--topology <single|network-2>] [--role <host|farmhand>] [--screen <id>] --json";

    private static int RunProjectReviewWorld(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewWorldUsage);
            output.WriteLine("Read one complete row-major rectangle of live tilled soil, crops, and ordinary data-backed vanilla machines. Width/height are 1-32 and area is at most 256 tiles. Missing, unsupported, and unavailable object state stays explicit; no world mutation.");
            return Success;
        }
        if (!TryParseWorldQuery(arguments, out ReviewWorldArea? area, out string topology,
                out string? role, out int? screenId))
        {
            error.WriteLine(ReviewWorldUsage);
            return UsageError;
        }
        ReviewWorldReport report = ProjectReviewWorldService.Execute(
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory, topology, role, screenId: screenId), area!);
        return WriteReviewWorldReport(report, output);
    }

    internal static int WriteReviewWorldReport(ReviewWorldReport report, TextWriter output)
    {
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }

    private static bool TryParseWorldQuery(IReadOnlyList<string> arguments, out ReviewWorldArea? area,
        out string topology, out string? role, out int? screenId)
    {
        area = null;
        topology = "single";
        role = null;
        screenId = null;
        if (arguments.Count is < 8 or > 14 || arguments[2] != "world") return false;
        var operands = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 3; index < arguments.Count; index++)
        {
            string option = arguments[index];
            if (option == "--json")
            {
                if (!seen.Add(option)) return false;
                continue;
            }
            if (option is "--topology" or "--role" or "--screen")
            {
                if (!seen.Add(option) || ++index >= arguments.Count) return false;
                string value = arguments[index];
                if (option == "--topology") topology = value;
                else if (option == "--role") role = value;
                else if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out int parsed) || parsed < 0) return false;
                else screenId = parsed;
                continue;
            }
            operands.Add(option);
        }
        if (!seen.Contains("--json") || operands.Count != 4
            || !int.TryParse(operands[0], out int x) || !int.TryParse(operands[1], out int y)
            || !int.TryParse(operands[2], out int width) || !int.TryParse(operands[3], out int height))
            return false;
        if (topology == "single" ? role is not null : topology != "network-2"
            || role is null || !NetworkTwoContract.IsRole(role) || screenId is not null) return false;
        area = new(x, y, width, height);
        return ReviewWorldContract.QueryProblem(area) is null;
    }
}
