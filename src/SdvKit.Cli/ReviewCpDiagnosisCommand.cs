using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string CpDiagnosisUsage = "Usage: sdvkit project review cp-diagnose --pack <staged-UniqueID> --provider Pathoschild.ContentPatcher [--asset <asset>] [--parse <token-string>] --json";

    private static int RunProjectReviewCpDiagnosis(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            output.WriteLine(CpDiagnosisUsage);
            output.WriteLine("Diagnose one selected CP 2.9.1 pack in an owned single review. Uses bounded patch summary/parse replies; requires an idle console. No asset is loaded: inspect the result separately after this observation. See docs/cp-diagnosis.md.");
            return Success;
        }
        if (!TryParseCpDiagnosis(arguments, out string pack, out string provider, out string? asset, out string? parse))
        {
            error.WriteLine(CpDiagnosisUsage);
            return UsageError;
        }
        var result = ProjectReviewCpDiagnosis.Execute(new ProjectReviewMcpRuntimeReader(Environment.CurrentDirectory), pack, provider, asset, parse);
        WriteJson(output, result);
        return result.State == "ready" ? Success : InspectionFailed;
    }

    internal static bool TryParseCpDiagnosis(IReadOnlyList<string> arguments, out string pack,
        out string provider, out string? asset, out string? parse)
    {
        pack = provider = "";
        asset = parse = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 3; i < arguments.Count; i++)
        {
            string option = arguments[i];
            if (!seen.Add(option)) return false;
            if (option == "--json") continue;
            if (++i >= arguments.Count) return false;
            switch (option)
            {
                case "--pack": pack = arguments[i]; break;
                case "--provider": provider = arguments[i]; break;
                case "--asset": asset = arguments[i]; break;
                case "--parse": parse = arguments[i]; break;
                default: return false;
            }
        }
        return seen.Contains("--json") && ProjectReviewCpDiagnosis.ValidArguments(pack, provider, asset, parse);
    }
}
