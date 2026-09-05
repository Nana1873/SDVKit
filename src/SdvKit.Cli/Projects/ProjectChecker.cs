using System.Globalization;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace SdvKit.Cli;

internal sealed record ProjectCheckProblem(string Code, string File, string Field, string Message);
internal sealed record ProjectCheckedFile(string File, string Schema);
internal sealed record ProjectCheckReport(
    int SchemaVersion,
    string Root,
    string Status,
    string SchemaSource,
    IReadOnlyList<ProjectCheckedFile> Files,
    IReadOnlyList<ProjectCheckProblem> Problems);

internal static class ProjectChecker
{
    internal const string SchemaCommit = "79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0";
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static ProjectCheckReport Check(string path)
    {
        string root = path;
        var files = new List<ProjectCheckedFile>();
        var problems = new List<ProjectCheckProblem>();
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(root))
            {
                problems.Add(new("pathNotFound", ".", "", "Select an existing mod directory containing manifest.json."));
            }
            else if (HasLinkedAncestor(root))
            {
                problems.Add(new("linkedPath", ".", "", "Select a directory without symbolic links or junctions in its path."));
            }
            else
            {
                CheckRoot(root, files, problems);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or SecurityException
            or IOException or UnauthorizedAccessException)
        {
            problems.Add(new("pathUnreadable", ".", "", "The selected project directory could not be read."));
        }

        return new(1, root, problems.Count == 0 ? "passed" : "failed", SchemaCommit, files, problems);
    }

    private static void CheckRoot(string root, List<ProjectCheckedFile> files, List<ProjectCheckProblem> problems)
    {
        JsonNode? manifest = CheckFile(root, "manifest.json", "manifest", files, problems);
        if (manifest is JsonObject obj && obj["ContentPackFor"] is JsonObject provider)
        {
            if (provider["UniqueID"] is JsonValue id && id.TryGetValue(out string? value))
            {
                if (string.Equals(value, "Pathoschild.ContentPatcher", StringComparison.OrdinalIgnoreCase))
                {
                    CheckFile(root, "content.json", "content-patcher", files, problems);
                }
                else
                {
                    problems.Add(new("unsupportedProvider", "manifest.json", "/ContentPackFor/UniqueID",
                        "Only Content Patcher content files are supported; the manifest and i18n are still checked."));
                }
            }
        }

        string translations = Path.Combine(root, "i18n");
        if (!Path.Exists(translations))
        {
            return;
        }

        if ((File.GetAttributes(translations) & FileAttributes.ReparsePoint) != 0)
        {
            problems.Add(new("linkedPath", "i18n", "", "The i18n directory must not be a symbolic link or junction."));
            return;
        }

        if (!Directory.Exists(translations))
        {
            problems.Add(new("pathUnreadable", "i18n", "", "Expected an i18n directory."));
            return;
        }

        string[] translationFiles = Directory.GetFileSystemEntries(translations)
            .Where(file => string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        if (!translationFiles.Any(file => string.Equals(Path.GetFileName(file), "default.json", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(new("fileNotFound", "i18n/default.json", "", "An i18n directory requires default.json."));
        }

        foreach (string file in translationFiles)
        {
            CheckFile(root, "i18n/" + Path.GetFileName(file), "i18n", files, problems);
        }
    }

    internal static IReadOnlyList<ProjectCheckProblem> CheckPatchFile(string root, string relative, bool include)
    {
        var problems = new List<ProjectCheckProblem>();
        CheckFile(root, relative, "content-patcher", [], problems, include);
        return problems;
    }

    private static JsonNode? CheckFile(string root, string relative, string schemaName,
        List<ProjectCheckedFile> files, List<ProjectCheckProblem> problems, bool include = false)
    {
        string path = Path.Combine(root, relative);
        JsonNode? instance;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                problems.Add(new("linkedPath", relative, "", "Authoring files must not be symbolic links."));
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
            int before = problems.Count;
            CheckDuplicateProperties(document.RootElement, relative, "", problems);
            if (problems.Count != before)
            {
                return null;
            }

            instance = JsonNode.Parse(document.RootElement.GetRawText(), documentOptions: JsonOptions);
        }
        catch (JsonException exception)
        {
            problems.Add(new("invalidJson", relative, exception.Path ?? "",
                $"Invalid JSON at line {exception.LineNumber + 1}, byte {exception.BytePositionInLine + 1}: {exception.Message}"));
            return null;
        }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException)
        {
            problems.Add(new(exception is FileNotFoundException or DirectoryNotFoundException ? "fileNotFound" : "fileUnreadable",
                relative, "", "The required authoring file could not be read."));
            return null;
        }

        if (include)
        {
            // Official Include files contain only Changes. Validate those patches
            // with the same bundled schema under its required root Format wrapper.
            if (instance is not JsonObject included || included.Count != 1 || included["Changes"] is not JsonArray)
            {
                problems.Add(new("includeShapeInvalid", relative, "", "An Include JSON file must contain only a Changes array."));
                return instance;
            }
            included["Format"] = "2.9.0";
        }

        string schemaId = $"https://smapi.io/schemas/{schemaName}.json";
        if (instance is JsonObject obj && obj.TryGetPropertyValue("$schema", out JsonNode? declaration)
            && (declaration is not JsonValue declaredValue || !declaredValue.TryGetValue(out string? declaredId)
                || !string.Equals(declaredId, schemaId, StringComparison.Ordinal)))
        {
            problems.Add(new("unsupportedSchema", relative, "/$schema", $"Expected {schemaId}; custom schemas are not fetched."));
            return instance;
        }

        if (schemaName == "content-patcher" && instance is JsonObject content
            && content["Format"] is JsonValue format && format.TryGetValue(out string? formatText)
            && !Regex.IsMatch(formatText, @"^2\.9\.[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            problems.Add(new("unsupportedFormat", relative, "/Format", "The bundled Content Patcher schema supports Format 2.9.x only."));
            return instance;
        }

        try
        {
            JsonSchema schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", schemaName + ".json")));
            var options = new EvaluationOptions
            {
                EvaluateAs = SpecVersion.Draft7,
                OutputFormat = OutputFormat.Hierarchical,
                Culture = CultureInfo.InvariantCulture,
            };
            options.SchemaRegistry.Fetch = _ => null;
            EvaluationResults result = schema.Evaluate(instance, options);
            files.Add(new(relative, schemaId));
            AddErrors(result, relative, problems);
        }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException
            or JsonException or JsonSchemaException or ArgumentException or RegexMatchTimeoutException)
        {
            problems.Add(new("schemaUnavailable", relative, "", "The bundled schema could not be evaluated; restore the complete SDVKit package."));
        }

        return instance;
    }

    private static void AddErrors(EvaluationResults result, string file, List<ProjectCheckProblem> problems)
    {
        if (result.IsValid)
        {
            return;
        }

        int before = problems.Count;
        if (result.HasErrors)
        {
            foreach ((string keyword, string message) in result.Errors!)
            {
                problems.Add(new("schemaViolation", file, result.InstanceLocation.ToString(), $"{keyword}: {message}"));
            }
        }

        if (result.HasDetails)
        {
            foreach (EvaluationResults child in result.Details)
            {
                AddErrors(child, file, problems);
            }
        }

        // Some assertions (not and false schemas) report failure without an error message.
        if (problems.Count == before)
        {
            problems.Add(new("schemaViolation", file, result.InstanceLocation.ToString(),
                $"Value does not satisfy schema rule {result.EvaluationPath}."));
        }
    }

    private static void CheckDuplicateProperties(JsonElement element, string file, string pointer, List<ProjectCheckProblem> problems)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string field = pointer + "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                if (!names.Add(property.Name))
                {
                    problems.Add(new("duplicateProperty", file, field, "Remove the duplicate property so its value is unambiguous."));
                }
                CheckDuplicateProperties(property.Value, file, field, problems);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                CheckDuplicateProperties(item, file, pointer + "/" + index.ToString(CultureInfo.InvariantCulture), problems);
                index++;
            }
        }
    }

    internal static bool HasLinkedAncestor(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        return false;
    }
}
