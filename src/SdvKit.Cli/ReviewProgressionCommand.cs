using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ReviewProgressionUsage = "Usage: sdvkit project review progression --npc <id> --mail <id> --quest <id> [--topology single] --json";

    private static int RunProjectReviewProgression(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ReviewProgressionUsage);
            output.WriteLine("Read fresh selected NPC friendship points, exact mail membership/count and selected quest-log counts in the owned disposable single-player review. IDs are case-sensitive ASCII letters, digits, dot, underscore or hyphen (1-128 characters). No mutation or inferred story completion.");
            return Success;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        bool json = false;
        for (int index = 3; index < arguments.Count; index++)
        {
            string option = arguments[index];
            if (option == "--json" && !json) { json = true; continue; }
            if (option is not ("--npc" or "--mail" or "--quest" or "--topology")
                || index + 1 >= arguments.Count || !options.TryAdd(option, arguments[++index]))
            { error.WriteLine(ReviewProgressionUsage); return UsageError; }
        }
        if (!json || !options.TryGetValue("--npc", out string? npc) || !options.TryGetValue("--mail", out string? mail)
            || !options.TryGetValue("--quest", out string? quest)
            || options.TryGetValue("--topology", out string? topology) && topology != "single"
            || ReviewProgressionContract.QueryProblem(new(npc!, mail!, quest!)) is not null)
        { error.WriteLine(ReviewProgressionUsage); return UsageError; }
        ReviewProgressionReport report = ProjectReviewProgressionService.Execute(
            new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory), new(npc, mail, quest));
        WriteJson(output, report);
        return report.State == "ready" ? Success : InspectionFailed;
    }
}
