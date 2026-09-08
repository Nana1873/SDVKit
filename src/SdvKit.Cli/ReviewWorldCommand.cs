using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewWorldUsage =
        "Usage: sdvkit project review world <x> <y> <width> <height> [--topology single] --json";

    private static int RunProjectReviewWorld(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewWorldUsage);
            output.WriteLine("Read one complete row-major rectangle of live tilled soil, crops, and ordinary data-backed vanilla machines. Width/height are 1-32 and area is at most 256 tiles. Missing, unsupported, and unavailable object state stays explicit; no world mutation.");
            return Success;
        }
        if (!TryParseWorldQuery(arguments, out ReviewWorldArea? area))
        {
            error.WriteLine(ReviewWorldUsage);
            return UsageError;
        }
        ReviewWorldReport report = ProjectReviewWorldService.Execute(
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory), area!);
        return WriteReviewWorldReport(report, output);
    }

    internal static int WriteReviewWorldReport(ReviewWorldReport report, TextWriter output)
    {
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }

    private static bool TryParseWorldQuery(IReadOnlyList<string> arguments, out ReviewWorldArea? area)
    {
        area = null;
        if (arguments.Count is < 8 or > 10 || arguments[2] != "world") return false;
        var operands = new List<string>();
        var jsonCount = 0;
        var topologyCount = 0;
        for (var index = 3; index < arguments.Count; index++)
        {
            if (arguments[index] == "--json") { jsonCount++; continue; }
            if (arguments[index] == "--topology" && index + 1 < arguments.Count)
            {
                topologyCount++;
                if (arguments[++index] != "single") return false;
                continue;
            }
            operands.Add(arguments[index]);
        }
        if (jsonCount != 1 || topologyCount > 1 || operands.Count != 4
            || !int.TryParse(operands[0], out int x) || !int.TryParse(operands[1], out int y)
            || !int.TryParse(operands[2], out int width) || !int.TryParse(operands[3], out int height))
            return false;
        area = new(x, y, width, height);
        return ReviewWorldContract.QueryProblem(area) is null;
    }
}
