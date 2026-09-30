using System.Globalization;
using System.Security;
using System.Text.Json.Nodes;

namespace SdvKit.Cli;

internal static partial class ProjectChecker
{
    // Match the existing CP refresh traversal's file count and per-file bounds.
    internal const int MaximumCpFiles = 64;
    internal const int MaximumCpIncludeBytes = ProjectReviewCpRefresh.MaximumFileBytes;

    private static void CheckContentPatcherReferences(string root, JsonNode? content,
        List<ProjectCheckedFile> files, List<ProjectCheckProblem> problems, List<ProjectCheckProblem> warnings)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "content.json" };
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "content.json" };
        bool limitReported = false;
        void Visit(string file, JsonNode? document)
        {
            if (document is not JsonObject obj || obj["Changes"] is not JsonArray changes) return;
            for (int index = 0; index < changes.Count; index++)
            {
                if (changes[index] is not JsonObject patch || patch["FromFile"] is not JsonValue value
                    || !value.TryGetValue(out string? from) || from is null) continue;
                string field = "/Changes/" + index.ToString(CultureInfo.InvariantCulture) + "/FromFile";
                if (patch["When"] is not null)
                {
                    warnings.Add(new("conditionalReferenceSkipped", file, field,
                        "FromFile resolution skipped: this patch has When conditions which require Content Patcher in game."));
                    continue;
                }
                if (from.Contains("{{", StringComparison.Ordinal) || from.Contains("}}", StringComparison.Ordinal))
                {
                    warnings.Add(new("dynamicReferenceSkipped", file, field,
                        "FromFile resolution skipped: tokenized paths require Content Patcher in game."));
                    continue;
                }
                bool include = patch["Action"] is JsonValue action && action.TryGetValue(out string? actionName)
                    && actionName == "Include";
                foreach (string reference in include ? from.Split(',') : [from])
                {
                    string? relative = ResolveCpFile(root, reference.Trim(), file, field, problems);
                    if (relative is null || !include) continue;
                    if (active.Contains(relative))
                    {
                        problems.Add(new("includeCycle", file, field, $"Include creates a circular reference to '{relative}'."));
                        continue;
                    }
                    if (visited.Contains(relative)) continue;
                    if (visited.Count >= MaximumCpFiles)
                    {
                        if (!limitReported)
                        {
                            warnings.Add(new("includeLimit", file, field,
                                $"Further Include validation skipped: the offline limit is {MaximumCpFiles} JSON files including content.json; validate the remaining graph in game."));
                            limitReported = true;
                        }
                        continue;
                    }
                    visited.Add(relative);
                    active.Add(relative);
                    JsonNode? included = CheckFile(root, relative, "content-patcher", files, problems, include: true,
                        maximumBytes: MaximumCpIncludeBytes, warnings: warnings);
                    Visit(relative, included);
                    active.Remove(relative);
                }
            }
        }
        Visit("content.json", content);
    }

    private static string? ResolveCpFile(string root, string reference, string file, string field,
        List<ProjectCheckProblem> problems)
    {
        try
        {
            // Normalize both CP separators before containment checks. Never inspect an escaped path.
            string normalized = reference.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized)
                || normalized.Contains(':') || normalized.Split('/').Any(part => part == ".."))
            {
                problems.Add(new("referencePathInvalid", file, field,
                    $"FromFile '{reference}' must be a relative file path within the selected mod root."));
                return null;
            }
            string path = Path.GetFullPath(Path.Combine(root, normalized));
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                problems.Add(new("referencePathInvalid", file, field,
                    $"FromFile '{reference}' escapes the selected mod root."));
                return null;
            }
            // Check each component before touching the next, including directory junctions and file links.
            string current = root;
            string[] parts = relative.Split('/');
            for (int index = 0; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    problems.Add(new("linkedPath", file, field, $"FromFile '{relative}' uses a symbolic link or junction."));
                    return null;
                }
                if (((attributes & FileAttributes.Directory) != 0) != (index < parts.Length - 1))
                {
                    problems.Add(new("referenceNotFile", file, field, $"FromFile '{relative}' must identify a file."));
                    return null;
                }
            }
            return relative;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException)
        {
            problems.Add(new(exception is FileNotFoundException or DirectoryNotFoundException ? "fileNotFound" : "referenceUnreadable",
                file, field, $"FromFile '{reference}' could not be found or inspected within the selected mod root."));
            return null;
        }
    }
}
