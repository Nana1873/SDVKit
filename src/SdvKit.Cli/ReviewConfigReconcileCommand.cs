namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string ConfigReconcileUsage = "Usage: sdvkit project review config-reconcile --mod <staged-UniqueID> [--topology single] --json";

    private static int RunProjectReviewConfigReconcile(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(ConfigReconcileUsage);
            output.WriteLine("Explicitly accept and export one selected staged mod's valid root config.json in the same owned running single review. All other files and launch ownership must still match. Retains old/new config hashes without changing source files, rebuilding or restarting. See docs/live-review.md for limits and recovery.");
            return Success;
        }
        if (!TryParseConfigReconcile(arguments, out string uniqueId))
        {
            error.WriteLine(ConfigReconcileUsage);
            return UsageError;
        }

        ConfigReconcileResult result = ProjectReviewConfigReconcile.Execute(Environment.CurrentDirectory, uniqueId);
        WriteJson(output, result);
        return result.State is "reconciled" or "unchanged" ? Success : InspectionFailed;
    }

    internal static bool TryParseConfigReconcile(IReadOnlyList<string> arguments, out string uniqueId)
    {
        uniqueId = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 3; i < arguments.Count; i++)
        {
            string option = arguments[i];
            if (!seen.Add(option)) return false;
            if (option == "--json") continue;
            if (++i >= arguments.Count) return false;
            switch (option)
            {
                case "--mod": uniqueId = arguments[i]; break;
                case "--topology" when arguments[i] == "single": break;
                default: return false;
            }
        }
        return seen.Contains("--json") && ProjectReviewLogDiagnostics.ValidQuery(uniqueId, 1);
    }
}
