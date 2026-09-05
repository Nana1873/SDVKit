namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string CpRefreshUsage = "Usage: sdvkit project review cp-refresh <source-pack> --pack <root-UniqueID> --provider Pathoschild.ContentPatcher --file <relative.json> [--file <relative.json> ...] --observe-data <asset> --key <key> --json";

    private static int RunProjectReviewCpRefresh(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(CpRefreshUsage);
            output.WriteLine("Refresh up to 16 existing patch JSON files in the same owned single CP 2.9.1 review. Reload, diagnose, then observe one selected Data record. Root non-patch fields, providers and other files must be unchanged; otherwise stop/reset/rebuild/restart. See docs/cp-refresh.md for limits and uncertain-delivery recovery.");
            return Success;
        }
        if (!TryParseCpRefresh(arguments, out string root, out string pack, out string provider,
                out string[] files, out string asset, out string key))
        {
            error.WriteLine(CpRefreshUsage);
            return UsageError;
        }
        var result = ProjectReviewCpRefresh.Execute(Environment.CurrentDirectory, root, pack, provider, files, asset, key);
        WriteJson(output, result);
        return result.State == "observed" ? Success : InspectionFailed;
    }

    internal static bool TryParseCpRefresh(IReadOnlyList<string> arguments, out string root,
        out string pack, out string provider, out string[] files, out string asset, out string key)
    {
        root = pack = provider = asset = key = "";
        files = [];
        if (arguments.Count < 4 || arguments[3].StartsWith("--", StringComparison.Ordinal)) return false;
        root = arguments[3];
        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 4; i < arguments.Count; i++)
        {
            string option = arguments[i];
            if (option != "--file" && !seen.Add(option)) return false;
            if (option == "--json") continue;
            if (++i >= arguments.Count) return false;
            switch (option)
            {
                case "--pack": pack = arguments[i]; break;
                case "--provider": provider = arguments[i]; break;
                case "--file": selected.Add(arguments[i]); break;
                case "--observe-data": asset = arguments[i]; break;
                case "--key": key = arguments[i]; break;
                default: return false;
            }
        }
        files = selected.ToArray();
        return seen.Contains("--json") && !string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(key)
            && key.Length <= LiveLab.ReviewDataContract.MaximumKeyLength && !key.Any(char.IsControl)
            && ProjectReviewCpRefresh.ValidFiles(files)
            && ProjectReviewCpDiagnosis.ValidArguments(pack, provider, asset, null);
    }
}
