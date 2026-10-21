using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewWorldActUsage =
        "Usage: sdvkit project review act <check|useTool> <x> <y> <inventory-revision> [--topology single] --json";
    private const string ReviewWorldMoveUsage =
        "       sdvkit project review act move <up|right|down|left> [--turn-only] [--topology single] --json";

    private static int RunProjectReviewWorldAct(IReadOnlyList<string> arguments,
        TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewWorldActUsage);
            output.WriteLine(ReviewWorldMoveUsage);
            output.WriteLine("Press the action button (check) or use the selected tool (useTool) on a tile next to the player, with the virtual cursor on that tile. Requires a fresh inventoryRevision. Completion confirms input delivery only; read the game state again to prove the effect.");
            output.WriteLine("move holds the game's movement key until the player stands on the next tile in that direction, then releases it, and reports the tile, position and whether the target was reached; --turn-only just faces the direction. A blocked or off-map target is refused with worldMoveTargetBlocked. No pathfinding.");
            return Success;
        }
        if (arguments.Count >= 4 && arguments[3] == "move")
        {
            if (!TryParseWorldMove(arguments, out ReviewWorldMoveQuery? move))
            {
                error.WriteLine(ReviewWorldMoveUsage.TrimStart());
                return UsageError;
            }
            ReviewWorldMoveReport moved = ProjectReviewWorldMoveService.Execute(
                new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory), move!);
            WriteJson(output, moved);
            return moved.State == "completed" ? Success : InspectionFailed;
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

    private static bool TryParseWorldMove(IReadOnlyList<string> arguments, out ReviewWorldMoveQuery? query)
    {
        query = null;
        if (arguments.Count is < 6 or > 9 || arguments[2] != "act" || arguments[3] != "move") return false;
        var values = new List<string>();
        int json = 0, topology = 0, turnOnly = 0;
        for (int index = 4; index < arguments.Count; index++)
        {
            if (arguments[index] == "--json") { json++; continue; }
            if (arguments[index] == "--turn-only") { turnOnly++; continue; }
            if (arguments[index] == "--topology" && index + 1 < arguments.Count)
            {
                topology++;
                if (arguments[++index] != "single") return false;
                continue;
            }
            values.Add(arguments[index]);
        }
        if (json != 1 || topology > 1 || turnOnly > 1 || values.Count != 1) return false;
        query = new(values[0], turnOnly == 1);
        return ReviewWorldMoveContract.Validate(query) is null;
    }
}
