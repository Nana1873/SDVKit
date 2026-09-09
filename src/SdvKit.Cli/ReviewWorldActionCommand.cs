using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewWorldActionUsage =
        "Usage: sdvkit project review interact <water|harvest|machineInsert|machineCollect> <x> <y> <target-instance-id> <target-revision> <inventory-revision> [--topology single] --json";

    private static int RunProjectReviewWorldAction(IReadOnlyList<string> arguments,
        TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewWorldActionUsage);
            output.WriteLine("Dispatch one native adjacent world interaction after revalidating the exact inspected target and selected inventory. Completion confirms input delivery only; inspect world and inventory again to prove the effect.");
            return Success;
        }
        if (!TryParseWorldAction(arguments, out ReviewWorldActionQuery? query))
        {
            error.WriteLine(ReviewWorldActionUsage);
            return UsageError;
        }
        ReviewWorldActionReport report = ProjectReviewWorldActionService.Execute(
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory), query!);
        WriteJson(output, report);
        return report.State == "completed" ? Success : InspectionFailed;
    }

    private static bool TryParseWorldAction(IReadOnlyList<string> arguments,
        out ReviewWorldActionQuery? query)
    {
        query = null;
        if (arguments.Count is < 10 or > 12 || arguments[2] != "interact") return false;
        var values = new List<string>();
        int json = 0, topology = 0;
        for (int index = 3; index < arguments.Count; index++)
        {
            if (arguments[index] == "--json") { json++; continue; }
            if (arguments[index] == "--topology" && index + 1 < arguments.Count)
            {
                topology++;
                if (arguments[++index] != "single") return false;
                continue;
            }
            values.Add(arguments[index]);
        }
        if (json != 1 || topology > 1 || values.Count != 6
            || !int.TryParse(values[1], out int x) || !int.TryParse(values[2], out int y)) return false;
        query = new(values[0], x, y, values[3], values[4], values[5]);
        return ReviewWorldActionContract.Validate(query) is null;
    }
}
