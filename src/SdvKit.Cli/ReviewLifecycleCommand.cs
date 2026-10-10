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
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine((arguments[2] == "network" ? ReviewNetworkUsage : ReviewSplitScreenUsage).TrimStart());
            return Success;
        }

        if (!TryParseProjectReviewLifecycle(arguments, out string? line, out string? topology, out string? role))
        {
            error.WriteLine((arguments[2] == "network" ? ReviewNetworkUsage : ReviewSplitScreenUsage).TrimStart());
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewConsole(line!, topology!, role, Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
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
        if (arguments.Count < 5 || arguments[^1] != "--json")
        {
            return false;
        }

        string family = arguments[2];
        string operation = arguments[3];
        string[] options = arguments.Skip(4).Take(arguments.Count - 5).ToArray();
        if (family == "split-screen" && operation is "join" or "leave" or "status")
        {
            if (options.Length == 0)
            {
                line = $"sdvkit split-screen {operation}";
            }
            else if (options.Length == 2
                && options[0] == "--screen"
                && int.TryParse(options[1], NumberStyles.None, CultureInfo.InvariantCulture, out int screen))
            {
                line = $"sdvkit split-screen {operation} screen={screen.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                return false;
            }

            topology = LiveLabState.SingleTopology;
            return true;
        }

        if (family == "network"
            && operation is "leave" or "join" or "status"
            && options.Length == 2
            && options[0] == "--role"
            && NetworkTwoContract.IsRole(options[1]))
        {
            line = $"sdvkit network {operation}";
            topology = NetworkTwoContract.Topology;
            role = options[1];
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
