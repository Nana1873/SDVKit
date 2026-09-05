using System.Xml;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private static readonly string[] SaveProblemCodes = ["sizeLimit", "copyMismatch", "copyChanged", "schemaUnavailable", "versionUnavailable", "unsupportedVersion", "fixtureMismatch", "ambiguousRecord", "xmlLimit", "recordLimit", "ambiguousField", "fieldLimit", "invalidNumber", "invalidInteger", "unsafePath", "linkedPath"];
    private static readonly string[] FarmSaveFields = ["buildings: tileX, tileY, buildingType", "objects: X, Y, itemId, stack (first 100 tiles)"];
    private const string SaveUsage = "Usage: sdvkit save sections --json\n       sdvkit save inspect <--source exact-save-file|--fixture baseline|work> --json";

    private static int RunSave(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 1 || (arguments.Count == 2 && IsHelp(arguments[1])))
        {
            output.WriteLine(SaveUsage);
            return Success;
        }
        if (arguments.Count == 3 && arguments[1] == "sections" && arguments[2] == "--json")
        {
            WriteJson(output, new
            {
                schema = "stardew-1.6-known-fields",
                player = SaveInspector.PlayerFields,
                world = SaveInspector.WorldFields.Concat(["currentSeason"]),
                farm = FarmSaveFields,
                maximumBytes = SaveInspector.MaximumBytes,
                maximumRecords = SaveInspector.MaximumRecords,
            });
            return Success;
        }
        if (arguments.Count != 5 || arguments[1] != "inspect" || arguments[4] != "--json"
            || arguments[2] is not ("--source" or "--fixture")
            || (arguments[2] == "--fixture" && arguments[3] is not ("baseline" or "work")))
        {
            error.WriteLine(SaveUsage);
            return UsageError;
        }
        try
        {
            string root = Directory.GetCurrentDirectory();
            SaveInspector.RequirePlainAncestors(arguments[3]);
            TestSaveIdentity? fixture = null;
            string source = arguments[2] == "--source" ? arguments[3]
                : new TestSaveFixtureStore(LiveLabPaths.Resolve(root)).SelectInspectionSource(arguments[3], out fixture);
            SaveInspection inspection = SaveInspector.Inspect(root, source, fixture: fixture);
            WriteJson(output, new { status = "inspected", inspection });
            return Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or XmlException or System.Security.SecurityException)
        {
            string problem = exception is XmlException ? "malformedXml: Use valid XML without DTDs or external entities."
                : exception is InvalidDataException ? exception.Message
                : "saveUnavailable: Verify the exact local source, fixture ownership and access; close writers before retrying.";
            // Filesystem and XML exception text may contain personal paths or payload data.
            if (exception is InvalidDataException && !SaveProblemCodes.Any(code => problem.StartsWith(code + ":", StringComparison.Ordinal)))
                problem = "ownershipOrPathInvalid: Verify the registered fixture and plain single-link paths.";
            WriteJson(output, new { status = "failed", problems = new[] { problem } });
            return InspectionFailed;
        }
    }
}
