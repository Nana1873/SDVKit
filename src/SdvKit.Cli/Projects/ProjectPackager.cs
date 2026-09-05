using System.IO.Compression;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli;

internal sealed record ProjectPackageReport(
    int SchemaVersion,
    string Root,
    string Kind,
    string? Archive,
    IReadOnlyList<string> Entries,
    string? Log,
    IReadOnlyList<ProjectProblem> Problems);

internal static class ProjectPackager
{
    private const string ContentPatcherId = "Pathoschild.ContentPatcher";
    private const string PackageDirectory = ".sdvkit/packages";
    private const string PackageStagingDirectory = ".sdvkit/package-staging";

    private static readonly HashSet<string> ExcludedDirectories = new(
        [".git", ".sdvkit", ".vs", ".idea", "bin", "obj", "Saves"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedFileNames = new(
        [
            ".editorconfig",
            ".gitattributes",
            ".gitignore",
            "Stardew Valley.exe",
            "StardewModdingAPI.exe",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ForbiddenGameAssemblyNames = new(
        [
            "0Harmony",
            "BmFont",
            "FAudio-CS",
            "GalaxyCSharp",
            "GalaxyCSharpGlue",
            "Lidgren.Network",
            "Mono.Cecil",
            "Mono.Cecil.Mdb",
            "Mono.Cecil.Pdb",
            "MonoGame.Framework",
            "MonoMod.Common",
            "Newtonsoft.Json",
            "SkiaSharp",
            "SMAPI.Toolkit",
            "SMAPI.Toolkit.CoreInterfaces",
            "Stardew Valley",
            "StardewModdingAPI",
            "StardewValley.GameData",
            "Steamworks.NET",
            "TextCopy",
            "TMXTile",
            "xTile",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedExtensions = new(
        [
            ".binlog",
            ".cs",
            ".csproj",
            ".dll",
            ".exe",
            ".nupkg",
            ".pdb",
            ".props",
            ".sln",
            ".snupkg",
            ".suo",
            ".targets",
            ".user",
            ".xnb",
            ".zip",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SaveMarkerFileNames = new(
        ["SaveGameInfo", "SaveGameInfo_old"],
        StringComparer.OrdinalIgnoreCase);

    public static ProjectPackageReport Package(
        string path,
        Func<DoctorReport> discoverInstallations,
        DotNetBuildRunner? runner = null,
        string? projectFile = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        ProjectInspectionReport inspection = ProjectBuilder.InspectTarget(path, projectFile);
        if (inspection.Problems.Count > 0)
        {
            return Report(inspection, null, [], null, inspection.Problems);
        }

        return inspection.Kind switch
        {
            ProjectInspectionReport.SmapiMod or ProjectInspectionReport.Hybrid => PackageMod(
                path,
                discoverInstallations,
                runner ?? ProjectBuilder.RunDotNet,
                stateDirectory: null,
                projectFile),
            ProjectInspectionReport.ContentPack => PackageContentPack(inspection),
            _ => Report(
                inspection,
                null,
                [],
                null,
                [new ProjectProblem("projectNotPackageable", null)]),
        };
    }

    internal static ProjectPackageReport PackageForReview(
        string path,
        string stateDirectory,
        Func<DoctorReport> discoverInstallations,
        DotNetBuildRunner? runner = null,
        string? projectFile = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        ProjectInspectionReport inspection = ProjectBuilder.InspectTarget(path, projectFile);
        if (inspection.Problems.Count > 0)
        {
            return Report(inspection, null, [], null, inspection.Problems);
        }

        return inspection.Kind is ProjectInspectionReport.SmapiMod
            or ProjectInspectionReport.Hybrid
            ? PackageMod(
                path,
                discoverInstallations,
                runner ?? ProjectBuilder.RunDotNet,
                Path.GetFullPath(stateDirectory),
                projectFile)
            : Report(
                inspection,
                null,
                [],
                null,
                [new ProjectProblem("projectNotPackageable", null)]);
    }

    private static ProjectPackageReport PackageMod(
        string path,
        Func<DoctorReport> discoverInstallations,
        DotNetBuildRunner runner,
        string? stateDirectory,
        string? projectFile)
    {
        ModBuildTargetResolution resolution = ProjectBuilder.ResolveTarget(path, projectFile);
        if (resolution.Target is null)
        {
            return Report(
                resolution.Inspection,
                null,
                [],
                null,
                resolution.Problems);
        }

        ProjectProblem? gameProblem = ProjectBuilder.GetGamePath(
            discoverInstallations(),
            out string? gamePath);
        if (gameProblem is not null)
        {
            return Report(resolution.Inspection, null, [], null, [gameProblem]);
        }

        string root = resolution.Inspection.Root;
        if (stateDirectory is null)
        {
            ProjectProblem? stateProblem = ProjectBuilder.CheckStateDirectory(root);
            if (stateProblem is not null)
            {
                return Report(resolution.Inspection, null, [], null, [stateProblem]);
            }
        }

        PackFileScan sourceScan = ScanContentPackFiles(projectFile is null ? root : Path.GetDirectoryName(resolution.Target.ProjectFile)!);
        if (sourceScan.Problem is not null)
        {
            return Report(resolution.Inspection, null, [], null, [sourceScan.Problem]);
        }

        string stateRoot = stateDirectory ?? Path.Combine(root, ".sdvkit");
        string artifactsPath = Path.Combine(stateRoot, "build");
        ProjectProblem? outputProblem = ProjectBuilder.PrepareOutputIsolation(
            artifactsPath,
            "packageOutputUnavailable",
            stateDirectory);
        if (outputProblem is not null)
        {
            return Report(resolution.Inspection, null, [], null, [outputProblem]);
        }

        string stagingRoot = Path.Combine(
            stateRoot,
            "package-staging",
            Guid.NewGuid().ToString("N"));
        string logFile = Path.Combine(stateRoot, "logs", "package.log");
        string reportLogPath = stateDirectory is null
            ? ProjectBuilder.PackageLogPath
            : logFile;
        try
        {
            if (stateDirectory is not null
                && (!ProjectBuilder.ReviewStateTreeIsPlain(stateDirectory)
                    || !ProjectBuilder.ReviewStatePathIsPlain(
                        stateDirectory,
                        stagingRoot,
                        allowFinalFile: false)))
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    null,
                    [new ProjectProblem("packageOutputUnavailable", stagingRoot)]);
            }

            Directory.CreateDirectory(stagingRoot);
            if (stateDirectory is not null
                && !ProjectBuilder.ReviewStatePathIsPlain(
                    stateDirectory,
                    stagingRoot,
                    allowFinalFile: false))
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    null,
                    [new ProjectProblem("packageOutputUnavailable", stagingRoot)]);
            }
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return Report(
                resolution.Inspection,
                null,
                [],
                null,
                [new ProjectProblem("packageOutputUnavailable", null)]);
        }

        try
        {
            DotNetBuildCommand command = ProjectBuilder.CreateCommand(
                resolution.Target,
                artifactsPath,
                gamePath!,
                enableZip: true,
                stagingRoot);
            DotNetBuildResult build = ProjectBuilder.RunAndLog(
                command,
                logFile,
                runner,
                stateDirectory);
            IReadOnlyList<ProjectProblem> buildProblems = ProjectBuilder.ProcessProblems(
                build,
                reportLogPath,
                "packageBuildFailed",
                "packageLogUnavailable");
            if (buildProblems.Count > 0)
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    reportLogPath,
                    buildProblems);
            }

            if (stateDirectory is not null
                && (!ProjectBuilder.ReviewStateTreeIsPlain(stateDirectory)
                    || !ProjectBuilder.ReviewStatePathIsPlain(
                        stateDirectory,
                        stagingRoot,
                        allowFinalFile: false)))
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    reportLogPath,
                    [new ProjectProblem("packageOutputUnavailable", stagingRoot)]);
            }

            string[] archives = Directory.GetFiles(stagingRoot, "*.zip", SearchOption.TopDirectoryOnly);
            if (archives.Length != 1)
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    reportLogPath,
                    [new ProjectProblem("packageArchiveNotFound", reportLogPath)]);
            }

            if (stateDirectory is not null
                && (!ProjectBuilder.ReviewStateTreeIsPlain(stateDirectory)
                    || !ProjectBuilder.ReviewStatePathIsPlain(
                        stateDirectory,
                        archives[0],
                        allowFinalFile: true)))
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    reportLogPath,
                    [new ProjectProblem("packageOutputUnavailable", archives[0])]);
            }

            ArchiveValidation validation = ValidateArchive(
                archives[0],
                resolution.Target.Manifest);
            if (validation.Problem is not null)
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    reportLogPath,
                    [validation.Problem]);
            }

            string packagesPath = Path.Combine(stateRoot, "packages");
            if (stateDirectory is not null
                && !ProjectBuilder.ReviewStatePathIsPlain(
                    stateDirectory,
                    packagesPath,
                    allowFinalFile: false))
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    reportLogPath,
                    [new ProjectProblem("packageOutputUnavailable", packagesPath)]);
            }

            Directory.CreateDirectory(packagesPath);
            string destination = Path.Combine(packagesPath, Path.GetFileName(archives[0]));
            if (stateDirectory is not null
                && (!ProjectBuilder.ReviewStateTreeIsPlain(stateDirectory)
                    || !ProjectBuilder.ReviewStatePathIsPlain(
                        stateDirectory,
                        destination,
                        allowFinalFile: true)
                    || File.Exists(destination)
                    || Directory.Exists(destination)))
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    reportLogPath,
                    [new ProjectProblem("packageOutputUnavailable", destination)]);
            }

            File.Move(
                archives[0],
                destination,
                overwrite: stateDirectory is null);
            if (stateDirectory is not null
                && (!ProjectBuilder.ReviewStateTreeIsPlain(stateDirectory)
                    || !ProjectBuilder.ReviewStatePathIsPlain(
                        stateDirectory,
                        destination,
                        allowFinalFile: true)))
            {
                return Report(
                    resolution.Inspection,
                    null,
                    [],
                    reportLogPath,
                    [new ProjectProblem("packageOutputUnavailable", destination)]);
            }

            return Report(
                resolution.Inspection,
                stateDirectory is null ? RelativePath(root, destination) : destination,
                validation.Entries,
                reportLogPath,
                []);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return Report(
                resolution.Inspection,
                null,
                [],
                reportLogPath,
                [new ProjectProblem("packageFailed", reportLogPath)]);
        }
        finally
        {
            DeleteStagingDirectory(stagingRoot, stateDirectory);
        }
    }

    private static ProjectPackageReport PackageContentPack(ProjectInspectionReport inspection)
    {
        ProjectManifestSummary[] manifests = inspection.Manifests
            .Where(manifest => string.Equals(
                manifest.Kind,
                ProjectInspectionReport.ContentPack,
                StringComparison.Ordinal))
            .ToArray();
        if (manifests.Length != 1
            || !string.Equals(manifests[0].Path, "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return Report(
                inspection,
                null,
                [],
                null,
                [new ProjectProblem("contentPackRootRequired", null)]);
        }

        ProjectManifestSummary manifest = manifests[0];
        if (!string.Equals(manifest.ContentPackFor, ContentPatcherId, StringComparison.OrdinalIgnoreCase))
        {
            return Report(
                inspection,
                null,
                [],
                null,
                [new ProjectProblem("unsupportedContentPack", "manifest.json")]);
        }

        string contentPath = Path.Combine(inspection.Root, "content.json");
        if (!File.Exists(contentPath))
        {
            return Report(
                inspection,
                null,
                [],
                null,
                [new ProjectProblem("contentFileNotFound", "content.json")]);
        }

        ProjectProblem? stateProblem = ProjectBuilder.CheckStateDirectory(inspection.Root);
        if (stateProblem is not null)
        {
            return Report(inspection, null, [], null, [stateProblem]);
        }

        PackFileScan scan = ScanContentPackFiles(inspection.Root);
        if (scan.Problem is not null)
        {
            return Report(inspection, null, [], null, [scan.Problem]);
        }

        if (!scan.Files.Contains("manifest.json", StringComparer.OrdinalIgnoreCase)
            || !scan.Files.Contains("content.json", StringComparer.OrdinalIgnoreCase))
        {
            return Report(
                inspection,
                null,
                [],
                null,
                [new ProjectProblem("contentPackIncomplete", null)]);
        }

        string topLevelDirectory = Path.GetFileName(inspection.Root);
        if (!IsSafeArchiveSegment(topLevelDirectory)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.Version.Contains("%ProjectVersion%", StringComparison.Ordinal))
        {
            return Report(
                inspection,
                null,
                [],
                null,
                [new ProjectProblem("invalidPackageName", null)]);
        }

        string stagingRoot = Path.Combine(
            inspection.Root,
            FromSlashPath(PackageStagingDirectory),
            Guid.NewGuid().ToString("N"));
        string archiveName = $"{topLevelDirectory} {manifest.Version}.zip";
        string stagingArchive = Path.Combine(stagingRoot, archiveName);
        try
        {
            Directory.CreateDirectory(stagingRoot);
            using (FileStream stream = new(
                stagingArchive,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (string relativePath in scan.Files)
                {
                    string entryPath = $"{topLevelDirectory}/{relativePath}";
                    ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using Stream entryStream = entry.Open();
                    using FileStream source = File.OpenRead(Path.Combine(
                        inspection.Root,
                        FromSlashPath(relativePath)));
                    source.CopyTo(entryStream);
                }
            }

            ArchiveValidation validation = ValidateArchive(stagingArchive, requiredModManifest: null);
            if (validation.Problem is not null)
            {
                return Report(inspection, null, [], null, [validation.Problem]);
            }

            string packagesPath = Path.Combine(inspection.Root, FromSlashPath(PackageDirectory));
            Directory.CreateDirectory(packagesPath);
            string destination = Path.Combine(packagesPath, archiveName);
            File.Move(stagingArchive, destination, overwrite: true);
            return Report(
                inspection,
                RelativePath(inspection.Root, destination),
                validation.Entries,
                null,
                []);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return Report(
                inspection,
                null,
                [],
                null,
                [new ProjectProblem("packageFailed", null)]);
        }
        finally
        {
            DeleteStagingDirectory(stagingRoot, reviewStateDirectory: null);
        }
    }

    private static PackFileScan ScanContentPackFiles(string root)
    {
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return new PackFileScan([], new ProjectProblem("reparsePointNotAllowed", "."));
            }

            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string childDirectory in Directory.GetDirectories(directory)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(
                        Path.GetFileName(childDirectory),
                        "Saves",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return new PackFileScan(
                            [],
                            new ProjectProblem(
                                "saveDataNotAllowed",
                                RelativePath(root, childDirectory)));
                    }

                    if (ExcludedDirectories.Contains(Path.GetFileName(childDirectory)))
                    {
                        continue;
                    }

                    if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                    {
                        return new PackFileScan(
                            [],
                            new ProjectProblem(
                                "reparsePointNotAllowed",
                                RelativePath(root, childDirectory)));
                    }

                    pending.Push(childDirectory);
                }

                foreach (string file in Directory.GetFiles(directory))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        return new PackFileScan(
                            [],
                            new ProjectProblem("reparsePointNotAllowed", RelativePath(root, file)));
                    }

                    if (SaveMarkerFileNames.Contains(Path.GetFileName(file)))
                    {
                        return new PackFileScan(
                            [],
                            new ProjectProblem("saveDataNotAllowed", RelativePath(root, file)));
                    }

                    if (!IsExcludedFile(file))
                    {
                        files.Add(RelativePath(root, file));
                    }
                }
            }

            return new PackFileScan(
                files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                null);
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return new PackFileScan([], new ProjectProblem("packageSourceUnreadable", null));
        }
    }

    private static ArchiveValidation ValidateArchive(
        string archivePath,
        ProjectManifestSummary? requiredModManifest)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string? requiredEntryDll = requiredModManifest?.EntryDll;
            string[] entries = archive.Entries
                .Where(entry => !entry.FullName.EndsWith('/'))
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (entries.Length == 0
                || entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            {
                return InvalidArchive();
            }

            string? topLevelDirectory = null;
            foreach (string entry in entries)
            {
                string[] segments = entry.Split('/');
                if (segments.Length < 2
                    || segments.Any(segment => !IsSafeArchiveSegment(segment))
                    || segments.Skip(1).Take(segments.Length - 2).Any(ExcludedDirectories.Contains)
                    || IsExcludedArchiveFile(entry, requiredEntryDll))
                {
                    return InvalidArchive(entry);
                }

                topLevelDirectory ??= segments[0];
                if (!string.Equals(topLevelDirectory, segments[0], StringComparison.Ordinal))
                {
                    return InvalidArchive(entry);
                }
            }

            foreach (string directoryEntry in archive.Entries
                .Where(entry => entry.FullName.EndsWith('/'))
                .Select(entry => entry.FullName.Replace('\\', '/').TrimEnd('/')))
            {
                string[] segments = directoryEntry.Split('/');
                if (segments.Any(segment => !IsSafeArchiveSegment(segment))
                    || !string.Equals(topLevelDirectory, segments[0], StringComparison.Ordinal)
                    || segments.Skip(1).Any(ExcludedDirectories.Contains))
                {
                    return InvalidArchive(directoryEntry);
                }
            }

            if (requiredModManifest is null)
            {
                string manifestEntry = $"{topLevelDirectory}/manifest.json";
                return entries.Contains(manifestEntry, StringComparer.OrdinalIgnoreCase)
                    ? new ArchiveValidation(entries, null)
                    : InvalidArchive(manifestEntry);
            }

            string[] matchingManifests = archive.Entries
                .Where(entry => string.Equals(
                    Path.GetFileName(entry.FullName.Replace('\\', '/')),
                    "manifest.json",
                    StringComparison.OrdinalIgnoreCase))
                .Where(entry => ManifestMatches(entry, requiredModManifest))
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToArray();
            if (matchingManifests.Length != 1)
            {
                return InvalidArchive(requiredModManifest.UniqueId);
            }

            string manifestDirectory = matchingManifests[0]
                [..^"manifest.json".Length];
            string requiredDllEntry = manifestDirectory + requiredEntryDll;
            return entries.Contains(requiredDllEntry, StringComparer.OrdinalIgnoreCase)
                ? new ArchiveValidation(entries, null)
                : InvalidArchive(requiredDllEntry);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return InvalidArchive();
        }
    }

    private static bool IsExcludedFile(string path)
    {
        string fileName = Path.GetFileName(path);
        return ExcludedFileNames.Contains(fileName)
            || IsForbiddenGameAssembly(fileName)
            || string.Equals(fileName, ".env", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || ExcludedExtensions.Contains(Path.GetExtension(fileName));
    }

    private static bool IsExcludedArchiveFile(string path, string? requiredEntryDll)
    {
        string fileName = Path.GetFileName(path);
        if (ExcludedFileNames.Contains(fileName)
            || SaveMarkerFileNames.Contains(fileName)
            || IsForbiddenGameAssembly(fileName)
            || string.Equals(fileName, ".env", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string extension = Path.GetExtension(fileName);
        if (requiredEntryDll is not null
            && (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return ExcludedExtensions.Contains(extension);
    }

    internal static bool IsForbiddenGameAssembly(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
            && ForbiddenGameAssemblyNames.Contains(Path.GetFileNameWithoutExtension(fileName));
    }

    private static bool ManifestMatches(
        ZipArchiveEntry entry,
        ProjectManifestSummary expected)
    {
        try
        {
            using Stream stream = entry.Open();
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return string.Equals(
                    StringProperty(document.RootElement, "UniqueID"),
                    expected.UniqueId,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    StringProperty(document.RootElement, "EntryDll"),
                    expected.EntryDll,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or JsonException)
        {
            return false;
        }
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static bool IsSafeArchiveSegment(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value is not "." and not ".."
            && !Path.IsPathRooted(value)
            && value.IndexOfAny(['/', '\\', ':']) < 0;
    }

    private static ArchiveValidation InvalidArchive(string? path = null)
    {
        return new ArchiveValidation([], new ProjectProblem("unsafePackageArchive", path));
    }

    private static ProjectPackageReport Report(
        ProjectInspectionReport inspection,
        string? archive,
        IReadOnlyList<string> entries,
        string? log,
        IReadOnlyList<ProjectProblem> problems)
    {
        return new ProjectPackageReport(
            1,
            inspection.Root,
            inspection.Kind,
            archive,
            entries,
            log,
            problems);
    }

    private static void DeleteStagingDirectory(
        string path,
        string? reviewStateDirectory)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (reviewStateDirectory is not null
                    && (!ProjectBuilder.ReviewStateTreeIsPlain(reviewStateDirectory)
                        || !ProjectBuilder.ReviewStatePathIsPlain(
                            reviewStateDirectory,
                            path,
                            allowFinalFile: false)))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            // The ignored staging directory can be removed by a later package run.
        }
    }

    private static string RelativePath(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string FromSlashPath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private sealed record PackFileScan(
        IReadOnlyList<string> Files,
        ProjectProblem? Problem);

    private sealed record ArchiveValidation(
        IReadOnlyList<string> Entries,
        ProjectProblem? Problem);
}
