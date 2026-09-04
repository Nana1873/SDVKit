using System.Globalization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewDiagnosticsUsage =
        "Usage: sdvkit project review diagnostics --mod <staged-UniqueID> [--limit <1-100>] [--topology <single|network-2>] [--role <host|farmhand>] --json";

    private static int RunProjectReviewDiagnostics(IReadOnlyList<string> arguments,
        TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewDiagnosticsUsage);
            output.WriteLine("Read the latest bounded warning/exception entries from the exact active role's isolated SMAPI log. Default limit: 20. Attribution is not proof of cause; omitted context and scan limits are explicit. No path input or log upload.");
            return Success;
        }
        if (!TryParseReviewDiagnostics(arguments, out string modId, out string topology,
                out string? role, out int limit))
        {
            error.WriteLine(ReviewDiagnosticsUsage);
            return UsageError;
        }
        var reader = new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory, topology, role);
        ReviewLogDiagnosticsResult result = ProjectReviewLogDiagnostics.Execute(reader, modId, limit);
        WriteJson(output, result);
        return result.State == "ready" ? Success : InspectionFailed;
    }

    internal static bool TryParseReviewDiagnostics(IReadOnlyList<string> arguments,
        out string modId, out string topology, out string? role, out int limit)
    {
        modId = "";
        topology = LiveLabState.SingleTopology;
        role = null;
        limit = ProjectReviewLogDiagnostics.DefaultLimit;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 3; i < arguments.Count; i++)
        {
            string option = arguments[i];
            if (!seen.Add(option))
            {
                return false;
            }
            if (option == "--json")
            {
                continue;
            }
            if (++i >= arguments.Count)
            {
                return false;
            }
            string value = arguments[i];
            switch (option)
            {
                case "--mod": modId = value; break;
                case "--topology": topology = value; break;
                case "--role": role = value; break;
                case "--limit":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit))
                    {
                        return false;
                    }
                    break;
                default: return false;
            }
        }
        return seen.Contains("--json") && ProjectReviewLogDiagnostics.ValidQuery(modId, limit)
            && (topology == LiveLabState.SingleTopology ? role is null
                : topology == NetworkTwoContract.Topology && role is not null && NetworkTwoContract.IsRole(role));
    }
}
