using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewContainerUsage = "Usage: sdvkit project review container [--topology <single|network-2>] [--role <host|farmhand>] [--screen <id>] --json";

    private static int RunProjectReviewContainer(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewContainerUsage);
            output.WriteLine("Read both sides and the held item for the exact open supported vanilla chest in the selected role or local screen. Returns a menu/chest selection identity and a visible-content revision. Read-only; no peer fallback and no open, sort, move, or mutation.");
            return Success;
        }
        if (!TryParseReviewMenu(arguments, out string topology, out string? role, out int? screenId))
        {
            error.WriteLine(ReviewContainerUsage);
            return UsageError;
        }
        ReviewContainerReport report = ProjectReviewContainerService.Execute(
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory, topology, role, screenId: screenId));
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }
}
