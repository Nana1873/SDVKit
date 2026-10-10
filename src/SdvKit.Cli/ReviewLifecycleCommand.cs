using System.Globalization;
using SdvKit.Cli.LiveLab;
using SdvKit.GameSide;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewSplitScreenUsage =
        "       sdvkit project review split-screen <join|leave|status> [--screen <id>] --json";
    private const string ReviewNetworkUsage =
        "       sdvkit project review network <leave|join|status> --role <host|farmhand> --json";

    /// <summary>
    /// Local split-screen and network-2 farmhand lifecycle. These change which players exist, so they
    /// stay CLI commands rather than MCP tools.
    /// </summary>
    private static int RunProjectReviewLifecycle(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewConsoleCommandRunner runProjectReviewConsole)
    {
        bool network = arguments[2] == "network";
        if (arguments.Skip(3).Any(IsHelp))
        {
            WriteProjectReviewLifecycleHelp(network, output);
            return Success;
        }

        if (!TryParseProjectReviewLifecycle(arguments, out string? line, out string? topology, out string? role))
        {
            error.WriteLine((network ? ReviewNetworkUsage : ReviewSplitScreenUsage).TrimStart());
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewConsole(line!, topology!, role, Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static void WriteProjectReviewLifecycleHelp(bool network, TextWriter output)
    {
        if (network)
        {
            output.WriteLine(ReviewNetworkUsage.TrimStart());
            output.WriteLine("Runs one farmhand lifecycle step in the passed network-2 review: 'leave' and 'join' require --role farmhand; 'status' logs the selected role's phase.");
        }
        else
        {
            output.WriteLine(ReviewSplitScreenUsage.TrimStart());
            output.WriteLine("Joins, removes or reports the local split-screen farmhand in the owned single --test-save review. 'join' and 'leave' run on the host screen (no --screen, or --screen 0); 'status' accepts any current screen ID.");
        }

        output.WriteLine("commandWritten=true confirms that the step ran on the game thread; confirm its effect in the owned SMAPI log or review status.");
    }

    internal static bool TryParseProjectReviewLifecycle(
        IReadOnlyList<string> arguments,
        out string? line,
        out string? topology,
        out string? role)
    {
        line = null;
        topology = null;
        role = null;
        if (arguments.Count < 4)
        {
            return false;
        }

        string family = arguments[2];
        string operation = arguments[3];
        bool json = false;
        int? screen = null;
        string? selectedRole = null;
        for (int index = 4; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--json" when !json:
                    json = true;
                    break;
                case "--screen" when family == "split-screen"
                    && screen is null
                    && index + 1 < arguments.Count
                    && int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedScreen):
                    screen = parsedScreen;
                    index++;
                    break;
                case "--role" when family == "network"
                    && selectedRole is null
                    && index + 1 < arguments.Count
                    && NetworkTwoContract.IsRole(arguments[index + 1]):
                    selectedRole = arguments[index + 1];
                    index++;
                    break;
                default:
                    return false;
            }
        }

        if (!json)
        {
            return false;
        }

        if (family == "split-screen" && operation is "join" or "leave" or "status")
        {
            // The game runs join and leave only on the host screen; reject other screens up front.
            if (operation != "status" && screen is not null and not 0)
            {
                return false;
            }

            line = screen is int id
                ? $"sdvkit split-screen {operation} screen={id.ToString(CultureInfo.InvariantCulture)}"
                : $"sdvkit split-screen {operation}";
            topology = LiveLabState.SingleTopology;
            return true;
        }

        if (family == "network" && operation is "leave" or "join" or "status" && selectedRole is not null)
        {
            // Only the farmhand leaves and rejoins; the host only reports status.
            if (operation != "status" && selectedRole != NetworkTwoContract.FarmhandRole)
            {
                return false;
            }

            line = $"sdvkit network {operation}";
            topology = NetworkTwoContract.Topology;
            role = selectedRole;
            return true;
        }

        return false;
    }

    /// <summary>
    /// SDVKit's own console grammar is internal from 2.0; lines through 'project review command'
    /// still run for existing scripts, with a pointer to the supported surface.
    /// </summary>
    private static void WarnIfSdvKitConsoleLine(string command, TextWriter error)
    {
        string[] tokens = ReviewCommandLine.Split(command);
        if (tokens.Length > 0 && ReviewCommandLine.IsSdvKitCommand(tokens[0]))
        {
            error.WriteLine(
                "warning: SDVKit console lines through 'project review command' are deprecated. Use 'project review call <tool>' for input, screenshots and fixtures, or 'project review split-screen'/'project review network' for lifecycle. 'project review command' remains for the reviewed mod's and SMAPI's console commands.");
        }
    }
}
