using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewWorldActUsage =
        "Usage: sdvkit project review act <check|useTool> <x> <y> <inventory-revision> [--topology single] --json";

    private static int RunProjectReviewWorldAct(IReadOnlyList<string> arguments,
        TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewWorldActUsage);
            output.WriteLine("Press the action button (check) or use the selected tool (useTool) on a tile next to the player, with the virtual cursor on that tile. Requires a fresh inventoryRevision. Completion confirms input delivery only; read the game state again to prove the effect.");
            return Success;
        }
        if (!TryParseWorldAct(arguments, out ReviewWorldActQuery? query))
        {
            error.WriteLine(ReviewWorldActUsage);
            return UsageError;
        }
        ReviewWorldActionReport report = ProjectReviewWorldActService.Execute(
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory), query!);
        WriteJson(output, report);
        return report.State == "completed" ? Success : InspectionFailed;
    }

    private static bool TryParseWorldAct(IReadOnlyList<string> arguments, out ReviewWorldActQuery? query)
    {
        query = null;
        if (arguments.Count is < 8 or > 10 || arguments[2] != "act") return false;
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
        if (json != 1 || topology > 1 || values.Count != 4
            || !int.TryParse(values[1], out int x) || !int.TryParse(values[2], out int y)) return false;
        query = new(values[0], x, y, values[3]);
        return ReviewWorldActContract.Validate(query) is null;
    }
}
