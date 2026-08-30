using System.Security;
using System.Text.Json;

namespace SdvKit.Cli;

internal sealed record ProjectManifestSummary(
    string Path,
    string Kind,
    string? Name,
    string UniqueId,
    string? Version,
    string? EntryDll,
    string? ContentPackFor);

internal sealed record ProjectProblem(string Code, string? Path);

internal sealed record ProjectInspectionReport(
    int SchemaVersion,
    string Root,
    string Kind,
    IReadOnlyList<string> ProjectFiles,
    IReadOnlyList<ProjectManifestSummary> Manifests,
    IReadOnlyList<ProjectProblem> Problems)
{
    public const string SmapiMod = "smapiMod";
    public const string ContentPack = "contentPack";
    public const string Hybrid = "hybrid";
    public const string Unknown = "unknown";
}

internal static class ProjectInspector
{
    private static readonly HashSet<string> ExcludedDirectories = new(
        ["bin", "obj", ".git", ".sdvkit"],
        StringComparer.OrdinalIgnoreCase);

    public static ProjectInspectionReport Inspect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException)
        {
            return Failure(path, "invalidPath");
        }

        if (!Directory.Exists(root))
        {
            return Failure(root, "pathNotFound");
        }

        ProjectTreeScan scan = Scan(root);
        if (scan.ErrorPath is not null)
        {
            return Failure(root, "pathUnreadable", scan.ProjectFiles, errorPath: scan.ErrorPath);
        }

        if (scan.ManifestFiles.Count == 0)
        {
            return Failure(root, "manifestNotFound", scan.ProjectFiles);
        }

        var manifests = new List<ProjectManifestSummary>();
        foreach (string manifestFile in scan.ManifestFiles)
        {
            ManifestReadResult result = ReadManifest(root, manifestFile);
            if (result.Error is not null)
            {
                return Failure(
                    root,
                    result.Error,
                    scan.ProjectFiles,
                    manifests,
                    RelativePath(root, manifestFile));
            }

            manifests.Add(result.Manifest!);
        }

        bool hasSmapiMod = manifests.Any(manifest =>
            string.Equals(manifest.Kind, ProjectInspectionReport.SmapiMod, StringComparison.Ordinal));
        bool hasContentPack = manifests.Any(manifest =>
            string.Equals(manifest.Kind, ProjectInspectionReport.ContentPack, StringComparison.Ordinal));
        string kind = (hasSmapiMod, hasContentPack) switch
        {
            (true, true) => ProjectInspectionReport.Hybrid,
            (true, false) => ProjectInspectionReport.SmapiMod,
            (false, true) => ProjectInspectionReport.ContentPack,
            _ => ProjectInspectionReport.Unknown,
        };

        return new ProjectInspectionReport(
            1,
            root,
            kind,
            scan.ProjectFiles,
            manifests,
            []);
    }

    private static ProjectTreeScan Scan(string root)
    {
        var projectFiles = new List<string>();
        var manifestFiles = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException
                or SecurityException
                or UnauthorizedAccessException)
            {
                return new ProjectTreeScan(
                    SortRelative(root, projectFiles),
                    SortPaths(manifestFiles),
                    RelativePath(root, directory));
            }

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    manifestFiles.Add(file);
                }
                else if (string.Equals(Path.GetExtension(file), ".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    projectFiles.Add(file);
                }
            }

            foreach (string child in directories.OrderByDescending(
                directoryPath => directoryPath,
                StringComparer.OrdinalIgnoreCase))
            {
                if (ExcludedDirectories.Contains(Path.GetFileName(child)))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or SecurityException
                    or UnauthorizedAccessException)
                {
                    return new ProjectTreeScan(
                        SortRelative(root, projectFiles),
                        SortPaths(manifestFiles),
                        RelativePath(root, child));
                }

                pending.Push(child);
            }
        }

        return new ProjectTreeScan(
            SortRelative(root, projectFiles),
            SortPaths(manifestFiles),
            null);
    }

    private static ManifestReadResult ReadManifest(string root, string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            JsonElement manifest = document.RootElement;
            if (manifest.ValueKind != JsonValueKind.Object)
            {
                return new ManifestReadResult(null, "invalidManifest");
            }

            string? uniqueId = StringProperty(manifest, "UniqueID");
            string? name = StringProperty(manifest, "Name");
            string? author = StringProperty(manifest, "Author");
            string? version = StringProperty(manifest, "Version");
            string? description = StringProperty(manifest, "Description");
            JsonElement? entryDllProperty = Property(manifest, "EntryDll");
            string? entryDll = entryDllProperty is { ValueKind: JsonValueKind.String }
                ? entryDllProperty.Value.GetString()
                : null;
            JsonElement? contentPackProperty = Property(manifest, "ContentPackFor");
            string? contentPackFor = null;
            if (contentPackProperty is JsonElement contentPack
                && contentPack.ValueKind == JsonValueKind.Object)
            {
                contentPackFor = StringProperty(contentPack, "UniqueID");
            }

            bool declaresSmapiMod = entryDllProperty is not null;
            bool declaresContentPack = contentPackProperty is not null;
            if (name is null
                || author is null
                || description is null
                || !IsSemanticVersion(version)
                || !IsModId(uniqueId)
                || declaresSmapiMod == declaresContentPack
                || (declaresSmapiMod && !IsEntryDll(entryDll))
                || (declaresContentPack && !IsModId(contentPackFor)))
            {
                return new ManifestReadResult(null, "invalidManifest");
            }

            string kind = declaresSmapiMod
                ? ProjectInspectionReport.SmapiMod
                : ProjectInspectionReport.ContentPack;
            return new ManifestReadResult(
                new ProjectManifestSummary(
                    RelativePath(root, path),
                    kind,
                    name,
                    uniqueId!,
                    version,
                    entryDll,
                    contentPackFor),
                null);
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException
            or JsonException)
        {
            return new ManifestReadResult(null, "invalidManifest");
        }
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        JsonElement? value = Property(element, name);
        return value is { ValueKind: JsonValueKind.String }
            ? value.Value.GetString()
            : null;
    }

    private static bool IsEntryDll(string? value)
    {
        return value is not null
            && value.Length > ".dll".Length
            && value.EndsWith(".dll", StringComparison.Ordinal)
            && value.All(IsModIdCharacter);
    }

    private static bool IsModId(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.All(IsModIdCharacter);
    }

    private static bool IsModIdCharacter(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '.'
            or '-';
    }

    private static bool IsSemanticVersion(string? value)
    {
        if (string.Equals(value, "%ProjectVersion%", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string[] versionAndTag = value.Split('-', count: 2);
        string[] numbers = versionAndTag[0].Split('.');
        if (numbers.Length is < 2 or > 3 || numbers.Any(number =>
            number.Length == 0
            || (number.Length > 1 && number[0] == '0')
            || !number.All(character => character is >= '0' and <= '9')))
        {
            return false;
        }

        return versionAndTag.Length == 1 || IsVersionTag(versionAndTag[1]);
    }

    private static bool IsVersionTag(string value)
    {
        var needsAlphaNumeric = true;
        foreach (char character in value)
        {
            bool isAlphaNumeric = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9';
            if (isAlphaNumeric)
            {
                needsAlphaNumeric = false;
            }
            else if ((character is '.' or '-') && !needsAlphaNumeric)
            {
                needsAlphaNumeric = true;
            }
            else
            {
                return false;
            }
        }

        return !needsAlphaNumeric;
    }

    private static ProjectInspectionReport Failure(
        string root,
        string error,
        IReadOnlyList<string>? projectFiles = null,
        IReadOnlyList<ProjectManifestSummary>? manifests = null,
        string? errorPath = null)
    {
        return new ProjectInspectionReport(
            1,
            root,
            ProjectInspectionReport.Unknown,
            projectFiles ?? [],
            manifests ?? [],
            [new ProjectProblem(error, errorPath)]);
    }

    private static string[] SortRelative(string root, IEnumerable<string> paths)
    {
        return paths
            .Select(path => RelativePath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] SortPaths(IEnumerable<string> paths)
    {
        return paths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string RelativePath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "."
            ? "."
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private sealed record ProjectTreeScan(
        IReadOnlyList<string> ProjectFiles,
        IReadOnlyList<string> ManifestFiles,
        string? ErrorPath);

    private sealed record ManifestReadResult(
        ProjectManifestSummary? Manifest,
        string? Error);
}
