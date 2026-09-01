using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectModStager
{
    private static readonly HashSet<string> ReviewForbiddenDirectories = new(
        [".git", ".sdvkit", ".vs", ".idea", "bin", "obj", "Saves"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ReviewSaveMarkerFileNames = new(
        ["SaveGameInfo", "SaveGameInfo_old"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ReviewForbiddenReadyExtensions = new(
        [".cs", ".csproj", ".sln", ".exe", ".zip"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ReviewForbiddenGameAssemblyNames = new(
        [
            "0Harmony", "BmFont", "FAudio-CS", "GalaxyCSharp",
            "GalaxyCSharpGlue", "Lidgren.Network", "Mono.Cecil",
            "Mono.Cecil.Mdb", "Mono.Cecil.Pdb", "MonoGame.Framework",
            "MonoMod.Common", "Newtonsoft.Json", "SkiaSharp", "SMAPI.Toolkit",
            "SMAPI.Toolkit.CoreInterfaces", "Stardew Valley", "StardewModdingAPI",
            "StardewValley.GameData", "Steamworks.NET", "TextCopy", "TMXTile",
            "xTile",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static ProjectReviewPreparationResult PrepareReview(
        string targetPath,
        IReadOnlyList<string> companionPaths,
        IReadOnlyList<string> contentPackPaths,
        LiveLabPaths paths,
        Func<DoctorReport> discoverInstallations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(companionPaths);
        ArgumentNullException.ThrowIfNull(contentPackPaths);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        string? preparationRoot = null;
        try
        {
            paths.EnsureDirectories();
            string preparationParent = Path.Combine(paths.SingleRoot, "review-prepared");
            EnsurePlainDirectory(preparationParent);
            preparationRoot = Path.Combine(preparationParent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(preparationRoot);
            RefuseReparsePoint(preparationRoot);

            DoctorReport? doctor = null;
            DoctorReport FrozenDoctor() => doctor ??= discoverInstallations();

            Func<DoctorReport> frozenDoctor = FrozenDoctor;
            var artifacts = new List<ProjectReviewPreparedArtifact>();
            ProjectReviewProblem? problem;
            ProjectInspectionReport targetInspection = ProjectInspector.Inspect(targetPath);
            ProjectReviewPreparedArtifact? target = Directory.Exists(targetPath)
                && targetInspection.ProjectFiles.Count == 0
                    ? PrepareReadyDirectory(
                        ProjectReviewArtifactRole.Target,
                        ProjectInspectionReport.ContentPack,
                        targetPath,
                        preparationRoot,
                        artifacts.Count,
                        out problem)
                    : PrepareProject(
                        ProjectReviewArtifactRole.Target,
                        targetPath,
                        preparationRoot,
                        artifacts.Count,
                        frozenDoctor,
                        out problem);
            if (target is null)
            {
                return PreparationFailure(preparationRoot, paths, problem!);
            }

            artifacts.Add(target);
            foreach (string source in companionPaths)
            {
                ProjectInspectionReport inspection = ProjectInspector.Inspect(source);
                ProjectReviewPreparedArtifact? companion = inspection.ProjectFiles.Count > 0
                    ? PrepareProject(
                        ProjectReviewArtifactRole.Companion,
                        source,
                        preparationRoot,
                        artifacts.Count,
                        frozenDoctor,
                        out problem)
                    : PrepareReadyDirectory(
                        ProjectReviewArtifactRole.Companion,
                        ProjectInspectionReport.SmapiMod,
                        source,
                        preparationRoot,
                        artifacts.Count,
                        out problem);
                if (companion is null)
                {
                    return PreparationFailure(preparationRoot, paths, problem!);
                }

                artifacts.Add(companion);
            }

            foreach (string source in contentPackPaths)
            {
                ProjectReviewPreparedArtifact? contentPack = PrepareReadyDirectory(
                    ProjectReviewArtifactRole.ContentPack,
                    ProjectInspectionReport.ContentPack,
                    source,
                    preparationRoot,
                    artifacts.Count,
                    out problem);
                if (contentPack is null)
                {
                    return PreparationFailure(preparationRoot, paths, problem!);
                }

                artifacts.Add(contentPack);
            }

            ProjectReviewProblem? setProblem = ValidateReviewSet(artifacts);
            return setProblem is null
                ? new ProjectReviewPreparationResult(artifacts, preparationRoot, null)
                : PreparationFailure(preparationRoot, paths, setProblem);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            ProjectReviewProblem problem = ReviewProblem(
                "reviewPreparationFailed",
                null,
                exception.Message);
            return preparationRoot is null
                ? new ProjectReviewPreparationResult([], null, problem)
                : PreparationFailure(preparationRoot, paths, problem);
        }
    }

    public static bool RemoveReviewPreparation(
        string? preparationRoot,
        LiveLabPaths paths)
    {
        if (string.IsNullOrWhiteSpace(preparationRoot))
        {
            return true;
        }

        string parent = Path.Combine(paths.SingleRoot, "review-prepared");
        string candidate = Path.GetFullPath(preparationRoot);
        return IsBelow(parent, candidate) && DeleteKnownDirectory(candidate);
    }

    internal static ProjectReviewManifest? ReadReviewManifest(
        string manifestPath,
        bool allowVersionToken,
        out string? error)
    {
        try
        {
            using FileStream stream = new(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            JsonElement root = document.RootElement;
            string? name = StringProperty(root, "Name");
            string? uniqueId = StringProperty(root, "UniqueID");
            string? version = StringProperty(root, "Version");
            string? entryDll = StringProperty(root, "EntryDll");
            JsonElement? contentPackProperty = Property(root, "ContentPackFor");
            string? contentPackFor = contentPackProperty is JsonElement contentPack
                && contentPack.ValueKind == JsonValueKind.Object
                    ? StringProperty(contentPack, "UniqueID")
                    : null;
            var providerVersionValid = true;
            string? contentPackMinimumVersion = null;
            if (contentPackProperty is JsonElement provider
                && provider.ValueKind == JsonValueKind.Object)
            {
                contentPackMinimumVersion = OptionalVersion(
                    provider,
                    "MinimumVersion",
                    out providerVersionValid);
            }
            bool codeMod = entryDll is not null;
            bool contentPackManifest = contentPackProperty is not null;
            if (root.ValueKind != JsonValueKind.Object
                || string.IsNullOrWhiteSpace(name)
                || !IsModId(uniqueId)
                || !IsSemanticVersion(version, allowVersionToken)
                || codeMod == contentPackManifest
                || (codeMod && !IsEntryDll(entryDll))
                || (contentPackManifest && !IsModId(contentPackFor))
                || !providerVersionValid)
            {
                error = "The root manifest is invalid for project review.";
                return null;
            }

            var required = new List<ProjectReviewDependency>();
            JsonElement? dependenciesProperty = Property(root, "Dependencies");
            if (dependenciesProperty is JsonElement dependencies)
            {
                if (dependencies.ValueKind != JsonValueKind.Array)
                {
                    error = "The manifest Dependencies field must be an array.";
                    return null;
                }

                foreach (JsonElement dependency in dependencies.EnumerateArray())
                {
                    string? dependencyId = dependency.ValueKind == JsonValueKind.Object
                        ? StringProperty(dependency, "UniqueID")
                        : null;
                    JsonElement? requiredProperty = dependency.ValueKind == JsonValueKind.Object
                        ? Property(dependency, "IsRequired")
                        : null;
                    var dependencyVersionValid = false;
                    string? minimumVersion = null;
                    if (dependency.ValueKind == JsonValueKind.Object)
                    {
                        minimumVersion = OptionalVersion(
                            dependency,
                            "MinimumVersion",
                            out dependencyVersionValid);
                    }
                    if (!IsModId(dependencyId)
                        || !dependencyVersionValid
                        || (requiredProperty is JsonElement requiredValue
                            && requiredValue.ValueKind is not (
                                JsonValueKind.True or JsonValueKind.False)))
                    {
                        error = "The manifest contains an invalid runtime dependency declaration.";
                        return null;
                    }

                    bool isRequired = requiredProperty is not JsonElement value
                        || value.GetBoolean();
                    if (isRequired)
                    {
                        required.Add(new ProjectReviewDependency(
                            dependencyId!,
                            minimumVersion));
                    }
                }
            }

            error = null;
            return new ProjectReviewManifest(
                codeMod
                    ? ProjectInspectionReport.SmapiMod
                    : ProjectInspectionReport.ContentPack,
                name!,
                uniqueId!,
                version!,
                entryDll,
                contentPackFor,
                contentPackMinimumVersion,
                required
                    .DistinctBy(
                        dependency => (dependency.UniqueId, dependency.MinimumVersion),
                        ReviewDependencyComparer.Instance)
                    .OrderBy(
                        dependency => dependency.UniqueId,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        dependency => dependency.MinimumVersion,
                        StringComparer.Ordinal)
                    .ToArray());
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException
            or JsonException)
        {
            error = $"The root manifest could not be read: {exception.Message}";
            return null;
        }
    }

    private static ProjectReviewPreparedArtifact? PrepareProject(
        string role,
        string sourcePath,
        string preparationRoot,
        int index,
        Func<DoctorReport> discoverInstallations,
        out ProjectReviewProblem? problem)
    {
        ModBuildTargetResolution resolution = ProjectBuilder.ResolveTarget(sourcePath);
        if (resolution.Target is null
            || !string.Equals(
                resolution.Inspection.Kind,
                ProjectInspectionReport.SmapiMod,
                StringComparison.Ordinal)
            || resolution.Inspection.Manifests.Count != 1)
        {
            ProjectProblem sourceProblem = resolution.Problems.Count > 0
                ? resolution.Problems[0]
                : new ProjectProblem("reviewProjectAmbiguous", null);
            problem = ReviewProblem(
                sourceProblem.Code,
                sourceProblem.Path,
                "A target or project companion must contain exactly one SMAPI code-mod project and root manifest.");
            return null;
        }

        ProjectBuildReport build = ProjectBuilder.Build(
            resolution.Inspection.Root,
            discoverInstallations);
        if (build.Problems.Count > 0)
        {
            ProjectProblem sourceProblem = build.Problems[0];
            problem = ReviewProblem(
                sourceProblem.Code,
                sourceProblem.Path,
                "The isolated Release build for an explicit review project failed.");
            return null;
        }

        ProjectPackageReport package = ProjectPackager.Package(
            resolution.Inspection.Root,
            discoverInstallations);
        if (package.Problems.Count > 0)
        {
            ProjectProblem sourceProblem = package.Problems[0];
            problem = ReviewProblem(
                sourceProblem.Code,
                sourceProblem.Path,
                "The validated Release package for an explicit review project failed.");
            return null;
        }

        return PreparePackage(
            role,
            resolution.Target,
            package,
            build.Log,
            preparationRoot,
            index,
            out problem);
    }

    private static ProjectReviewPreparedArtifact? PreparePackage(
        string role,
        ModBuildTarget target,
        ProjectPackageReport package,
        string? buildLog,
        string preparationRoot,
        int index,
        out ProjectReviewProblem? problem)
    {
        if (string.IsNullOrWhiteSpace(package.Archive)
            || package.Entries.Count == 0)
        {
            problem = ReviewProblem(
                "validatedPackageRequired",
                package.Archive,
                "Project review requires the validated Release package for each code project.");
            return null;
        }

        string archivePath = Path.GetFullPath(FromSlashPath(package.Archive), package.Root);
        if (!IsBelow(package.Root, archivePath) || !File.Exists(archivePath))
        {
            problem = ReviewProblem(
                "packageArchiveUnavailable",
                package.Archive,
                "The validated project package is no longer available below its source .sdvkit directory.");
            return null;
        }

        string itemRoot = Path.Combine(
            preparationRoot,
            index.ToString("D4", CultureInfo.InvariantCulture));
        string frozenArchive = Path.Combine(itemRoot, "package.zip");
        string extractionRoot = Path.Combine(itemRoot, "content");
        Directory.CreateDirectory(extractionRoot);
        File.Copy(archivePath, frozenArchive, overwrite: false);
        using ZipArchive archive = ZipFile.OpenRead(frozenArchive);
        ZipArchiveEntry[] files = archive.Entries
            .Where(entry => !entry.FullName.EndsWith('/'))
            .ToArray();
        string[] entries = files
            .Select(entry => NormalizeArchivePath(entry.FullName))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] reported = package.Entries
            .Select(NormalizeArchivePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] topLevels = entries
            .Select(entry => entry.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (entries.Length == 0
            || entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length
            || !entries.SequenceEqual(reported, StringComparer.Ordinal)
            || topLevels.Length != 1
            || !IsSafeSegment(topLevels[0])
            || entries.Count(entry => string.Equals(
                Path.GetFileName(entry),
                "manifest.json",
                StringComparison.OrdinalIgnoreCase)) != 1
            || !entries.Contains(
                $"{topLevels[0]}/manifest.json",
                StringComparer.OrdinalIgnoreCase))
        {
            problem = ReviewProblem(
                "reviewPackageInvalid",
                package.Archive,
                "The validated code-project package changed or is not one standalone root mod.");
            return null;
        }

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = NormalizeArchivePath(entry.FullName.TrimEnd('/', '\\'));
            if (normalized.Length == 0)
            {
                continue;
            }

            string[] segments = normalized.Split('/');
            if (segments.Any(segment => !IsSafeSegment(segment))
                || !string.Equals(segments[0], topLevels[0], StringComparison.Ordinal))
            {
                problem = ReviewProblem(
                    "reviewPackageInvalid",
                    entry.FullName,
                    "The project package contains an unsafe path.");
                return null;
            }

            string destination = Path.GetFullPath(FromSlashPath(normalized), extractionRoot);
            if (!IsBelow(extractionRoot, destination))
            {
                problem = ReviewProblem(
                    "reviewPackageInvalid",
                    entry.FullName,
                    "The project package path escapes its preparation root.");
                return null;
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream input = entry.Open();
            using FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            input.CopyTo(output);
        }

        string preparedPath = Path.Combine(extractionRoot, topLevels[0]);
        ProjectReviewManifest? manifest = ReadReviewManifest(
            Path.Combine(preparedPath, "manifest.json"),
            allowVersionToken: false,
            out string? manifestError);
        if (manifest is null
            || !string.Equals(
                manifest.Kind,
                ProjectInspectionReport.SmapiMod,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.UniqueId,
                target.Manifest.UniqueId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                manifest.EntryDll,
                target.Manifest.EntryDll,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(preparedPath, manifest.EntryDll!)))
        {
            problem = ReviewProblem(
                "reviewPackageManifestInvalid",
                package.Archive,
                manifestError
                    ?? "The package manifest identity or entry DLL does not match the explicit code project.");
            return null;
        }

        problem = null;
        return new ProjectReviewPreparedArtifact(
            role,
            target.Inspection.Root,
            preparedPath,
            topLevels[0],
            manifest,
            ModBuildIdentity.ComputeFileSet(preparedPath),
            buildLog,
            package.Log);
    }

    private static ProjectReviewPreparedArtifact? PrepareReadyDirectory(
        string role,
        string expectedKind,
        string sourcePath,
        string preparationRoot,
        int index,
        out ProjectReviewProblem? problem)
    {
        ProjectInspectionReport inspection = ProjectInspector.Inspect(sourcePath);
        if (inspection.Problems.Count > 0
            || inspection.ProjectFiles.Count != 0
            || inspection.Manifests.Count != 1
            || !string.Equals(
                inspection.Manifests[0].Path,
                "manifest.json",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(inspection.Kind, expectedKind, StringComparison.Ordinal))
        {
            ProjectProblem sourceProblem = inspection.Problems.Count > 0
                ? inspection.Problems[0]
                : new ProjectProblem("reviewReadyDirectoryInvalid", null);
            problem = ReviewProblem(
                sourceProblem.Code,
                sourceProblem.Path,
                string.Equals(expectedKind, ProjectInspectionReport.ContentPack, StringComparison.Ordinal)
                    ? "A content-pack review source must be one ready root content-pack directory without a C# project."
                    : "A ready companion must be one root code-mod directory without a C# project.");
            return null;
        }

        string topLevel = Path.GetFileName(inspection.Root);
        if (!IsSafeSegment(topLevel))
        {
            problem = ReviewProblem(
                "reviewStagingNameInvalid",
                inspection.Root,
                "The explicit ready directory name is unsafe for isolated staging.");
            return null;
        }

        string preparedPath = Path.Combine(
            preparationRoot,
            index.ToString("D4", CultureInfo.InvariantCulture),
            "content",
            topLevel);
        CopyReadyTree(inspection.Root, preparedPath);
        ProjectReviewManifest? manifest = ReadReviewManifest(
            Path.Combine(preparedPath, "manifest.json"),
            allowVersionToken: false,
            out string? manifestError);
        if (manifest is null
            || !string.Equals(manifest.Kind, expectedKind, StringComparison.Ordinal)
            || !string.Equals(
                manifest.UniqueId,
                inspection.Manifests[0].UniqueId,
                StringComparison.OrdinalIgnoreCase)
            || (manifest.EntryDll is not null
                && !File.Exists(Path.Combine(preparedPath, manifest.EntryDll))))
        {
            problem = ReviewProblem(
                "reviewReadyManifestInvalid",
                inspection.Root,
                manifestError
                    ?? "The prepared ready directory no longer matches its inspected root manifest.");
            return null;
        }

        problem = null;
        return new ProjectReviewPreparedArtifact(
            role,
            inspection.Root,
            preparedPath,
            topLevel,
            manifest,
            ModBuildIdentity.ComputeFileSet(preparedPath),
            null,
            null);
    }

    internal static void CopyReadyTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "An explicit ready review directory cannot be a reparse point.");
        }

        Directory.CreateDirectory(destination);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            (string currentSource, string currentDestination) = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(currentSource))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"The explicit ready review directory contains a reparse point: {entry}");
                }

                string name = Path.GetFileName(entry);
                string target = Path.Combine(currentDestination, name);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (ReviewForbiddenDirectories.Contains(name))
                    {
                        throw new InvalidDataException(
                            $"The explicit ready review directory contains an unsafe development or save path: {entry}");
                    }

                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                    continue;
                }

                string extension = Path.GetExtension(name);
                if (ReviewSaveMarkerFileNames.Contains(name)
                    || IsReviewForbiddenGameAssembly(name)
                    || string.Equals(name, ".env", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                    || ReviewForbiddenReadyExtensions.Contains(extension))
                {
                    throw new InvalidDataException(
                        $"The explicit ready review directory contains a source, secret, save, executable, archive, or game file: {entry}");
                }

                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private static string? OptionalVersion(
        JsonElement element,
        string name,
        out bool valid)
    {
        JsonElement? property = Property(element, name);
        if (property is null or { ValueKind: JsonValueKind.Null })
        {
            valid = true;
            return null;
        }

        string? value = property is { ValueKind: JsonValueKind.String }
            ? property.Value.GetString()
            : null;
        valid = value is not null && IsSemanticVersion(value, allowToken: false);
        return value;
    }

    private static bool IsReviewForbiddenGameAssembly(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
            && ReviewForbiddenGameAssemblyNames.Contains(
                Path.GetFileNameWithoutExtension(fileName));
    }

    private static ProjectReviewPreparationResult PreparationFailure(
        string preparationRoot,
        LiveLabPaths paths,
        ProjectReviewProblem problem)
    {
        bool removed = RemoveReviewPreparation(preparationRoot, paths);
        return removed
            ? new ProjectReviewPreparationResult([], null, problem)
            : new ProjectReviewPreparationResult(
                [],
                preparationRoot,
                ReviewProblem(
                    "reviewPreparationCleanupIncomplete",
                    null,
                    $"{problem.Message} The exact temporary preparation directory could not be removed."));
    }

    private sealed class ReviewDependencyComparer
        : IEqualityComparer<(string UniqueId, string? MinimumVersion)>
    {
        public static ReviewDependencyComparer Instance { get; } = new();

        public bool Equals(
            (string UniqueId, string? MinimumVersion) left,
            (string UniqueId, string? MinimumVersion) right) =>
            string.Equals(left.UniqueId, right.UniqueId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.MinimumVersion,
                right.MinimumVersion,
                StringComparison.Ordinal);

        public int GetHashCode((string UniqueId, string? MinimumVersion) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.UniqueId),
                value.MinimumVersion is null
                    ? 0
                    : StringComparer.Ordinal.GetHashCode(value.MinimumVersion));
    }
}
