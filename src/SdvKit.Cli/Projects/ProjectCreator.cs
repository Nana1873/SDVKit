using System.Security;
using System.Text;
using System.Text.Json;

namespace SdvKit.Cli;

internal sealed record ProjectCreationRequest(
    string Kind,
    string Path,
    string Name,
    string Author,
    string UniqueId,
    string Description);

internal sealed record ProjectCreationReport(
    int SchemaVersion,
    string Root,
    string Kind,
    IReadOnlyList<string> Files,
    IReadOnlyList<ProjectProblem> Problems);

internal static class ProjectCreator
{
    public const string SmapiMod = "smapi-mod";
    public const string ContentPack = "content-pack";

    private const string InitialVersion = "1.0.0";
    private const string ModBuildConfigVersion = "4.4.0";
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static bool IsValidRequest(ProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return (string.Equals(request.Kind, SmapiMod, StringComparison.Ordinal)
                || string.Equals(request.Kind, ContentPack, StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(request.Path)
            && !string.IsNullOrWhiteSpace(request.Name)
            && !string.IsNullOrWhiteSpace(request.Author)
            && !string.IsNullOrWhiteSpace(request.Description)
            && IsValidUniqueId(request.UniqueId)
            && ProjectFileName(request.UniqueId) is not null;
    }

    public static ProjectCreationReport Create(ProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Path));
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException)
        {
            return Failure(request.Path, "invalidPath");
        }

        if (!IsValidRequest(request))
        {
            return Failure(root, "invalidProjectIdentity");
        }

        if (Directory.Exists(root))
        {
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                {
                    return Failure(root, "reparsePointNotAllowed");
                }

                if (Directory.EnumerateFileSystemEntries(root).Any())
                {
                    return Failure(root, "targetNotEmpty");
                }
            }
            catch (Exception exception) when (exception is IOException
                or SecurityException
                or UnauthorizedAccessException)
            {
                return Failure(root, "pathUnreadable");
            }
        }
        else if (File.Exists(root))
        {
            return Failure(root, "targetNotDirectory");
        }

        IReadOnlyDictionary<string, string> files = string.Equals(
            request.Kind,
            SmapiMod,
            StringComparison.Ordinal)
            ? SmapiModFiles(request)
            : ContentPackFiles(request);
        bool createdDirectory = !Directory.Exists(root);
        var writtenFiles = new List<string>();
        try
        {
            Directory.CreateDirectory(root);
            foreach ((string relativePath, string contents) in files)
            {
                string outputPath = Path.Combine(root, relativePath);
                using var stream = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                writtenFiles.Add(outputPath);
                using var writer = new StreamWriter(stream, Utf8WithoutBom);
                writer.Write(contents);
            }
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            CleanPartialCreation(root, writtenFiles, createdDirectory);
            return Failure(root, "createFailed");
        }

        ProjectInspectionReport inspection = ProjectInspector.Inspect(root);
        string expectedKind = string.Equals(request.Kind, SmapiMod, StringComparison.Ordinal)
            ? ProjectInspectionReport.SmapiMod
            : ProjectInspectionReport.ContentPack;
        if (inspection.Problems.Count > 0
            || !string.Equals(inspection.Kind, expectedKind, StringComparison.Ordinal))
        {
            return new ProjectCreationReport(
                1,
                root,
                inspection.Kind,
                SortPaths(files.Keys),
                inspection.Problems.Count > 0
                    ? inspection.Problems
                    : [new ProjectProblem("createValidationFailed", null)]);
        }

        return new ProjectCreationReport(1, root, inspection.Kind, SortPaths(files.Keys), []);
    }

    private static Dictionary<string, string> SmapiModFiles(ProjectCreationRequest request)
    {
        string projectName = ProjectFileName(request.UniqueId)!;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".gitignore"] = "/.sdvkit/\n",
            [$"{projectName}.csproj"] = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net6.0</TargetFramework>
                    <Version>{InitialVersion}</Version>
                    <EnableModDeploy>false</EnableModDeploy>
                    <EnableModZip>false</EnableModZip>
                  </PropertyGroup>

                  <ItemGroup>
                    <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="{ModBuildConfigVersion}" />
                  </ItemGroup>
                </Project>
                """ + "\n",
            ["ModEntry.cs"] = """
                using StardewModdingAPI;

                internal sealed class ModEntry : Mod
                {
                    public override void Entry(IModHelper helper)
                    {
                    }
                }
                """ + "\n",
            ["manifest.json"] = SerializeJson(new
            {
                request.Name,
                request.Author,
                Version = InitialVersion,
                request.Description,
                UniqueID = request.UniqueId,
                EntryDll = $"{projectName}.dll",
                MinimumApiVersion = "4.0.0",
                UpdateKeys = Array.Empty<string>(),
            }),
        };
    }

    private static Dictionary<string, string> ContentPackFiles(ProjectCreationRequest request)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".gitignore"] = "/.sdvkit/\n",
            ["content.json"] = SerializeJson(new
            {
                Format = "2.9.0",
                Changes = Array.Empty<object>(),
            }),
            ["manifest.json"] = SerializeJson(new
            {
                request.Name,
                request.Author,
                Version = InitialVersion,
                request.Description,
                UniqueID = request.UniqueId,
                UpdateKeys = Array.Empty<string>(),
                ContentPackFor = new
                {
                    UniqueID = "Pathoschild.ContentPatcher",
                },
            }),
        };
    }

    private static string SerializeJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, ManifestJsonOptions) + "\n";
    }

    private static string? ProjectFileName(string uniqueId)
    {
        string value = uniqueId.Split('.').LastOrDefault() ?? string.Empty;
        return value.Length > 0
            && value is not "." and not ".."
            && value.All(IsModIdCharacter)
            && value.Any(char.IsLetterOrDigit)
            ? value
            : null;
    }

    private static bool IsValidUniqueId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains('.', StringComparison.Ordinal)
            && value.Split('.').All(segment =>
                segment.Length > 0
                && segment.Any(char.IsLetterOrDigit)
                && segment.All(IsModIdCharacter));
    }

    private static bool IsModIdCharacter(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '-';
    }

    private static string[] SortPaths(IEnumerable<string> paths)
    {
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ProjectCreationReport Failure(string root, string code)
    {
        return new ProjectCreationReport(
            1,
            root,
            ProjectInspectionReport.Unknown,
            [],
            [new ProjectProblem(code, null)]);
    }

    private static void CleanPartialCreation(
        string root,
        IEnumerable<string> writtenFiles,
        bool createdDirectory)
    {
        foreach (string path in writtenFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException
                or SecurityException
                or UnauthorizedAccessException)
            {
                // Best effort only; never delete files which this operation didn't create.
            }
        }

        if (!createdDirectory)
        {
            return;
        }

        try
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            // Best effort only; preserve anything that appeared concurrently.
        }
    }
}
