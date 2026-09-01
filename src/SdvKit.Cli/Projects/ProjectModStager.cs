using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectModStager
{
    private const int OwnershipSchemaVersion = 1;
    private const string OwnershipFileName = "project-smoke-staging.json";
    private const string AlwaysOnUniqueId = "SDVKit.AlwaysOn";
    private const string AlwaysOnVersion = "0.5.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ProjectModManifestReadResult ReadSourceManifest(ModBuildTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string manifestPath = Path.Combine(
            target.Inspection.Root,
            FromSlashPath(target.Manifest.Path));
        return ReadManifest(manifestPath, target.Manifest.Path, allowVersionToken: true);
    }

    public static string[] FindUnavailableRequiredDependencies(
        ProjectModManifestInfo manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.RequiredDependencies
            .Where(dependency => !string.Equals(
                    dependency.UniqueId,
                    AlwaysOnUniqueId,
                    StringComparison.OrdinalIgnoreCase)
                || !IsMinimumVersionSatisfied(
                    AlwaysOnVersion,
                    dependency.MinimumVersion))
            .Select(dependency => dependency.MinimumVersion is null
                ? dependency.UniqueId
                : $"{dependency.UniqueId} >= {dependency.MinimumVersion}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ProjectModStagingResult Stage(
        ProjectPackageReport package,
        ModBuildTarget target,
        string topology,
        LiveLabPaths singlePaths,
        Action<string, string>? copyTree = null,
        Action<string>? afterPrepare = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        ArgumentNullException.ThrowIfNull(singlePaths);
        copyTree ??= CopyPlainTree;

        if (topology is not (LiveLabState.SingleTopology or NetworkTwoContract.Topology))
        {
            throw new ArgumentOutOfRangeException(nameof(topology));
        }

        if (package.Problems.Count > 0
            || string.IsNullOrWhiteSpace(package.Archive)
            || package.Entries.Count == 0)
        {
            return Failure(
                "validatedPackageRequired",
                package.Archive,
                "Project smoke requires one successful validated ProjectPackager archive.");
        }

        string archivePath = Path.GetFullPath(
            FromSlashPath(package.Archive),
            package.Root);
        if (!IsBelow(package.Root, archivePath) || !File.Exists(archivePath))
        {
            return Failure(
                "packageArchiveUnavailable",
                package.Archive,
                "The validated project package is no longer available below the source project .sdvkit directory.");
        }

        string? preparedRoot = null;
        ProjectModStagingResult FinishWithPreparedCleanup(ProjectModStagingResult result)
        {
            if (preparedRoot is null)
            {
                return result;
            }

            if (DeleteKnownDirectory(preparedRoot))
            {
                preparedRoot = null;
                return result;
            }

            bool rollbackIncomplete = result.Problem?.Code
                is "projectStagingRollbackIncomplete";
            return Failure(
                rollbackIncomplete
                    ? "projectStagingRollbackIncomplete"
                    : "preparedStagingCleanupIncomplete",
                package.Archive,
                rollbackIncomplete
                    ? "The exact target rollback and temporary prepared-package cleanup were incomplete. No game process was started."
                    : "The temporary prepared package remained in the isolated mod group. No game process was started.");
        }

        try
        {
            singlePaths.EnsureDirectories();
            LiveLabPaths[] rolePaths = ResolveRolePaths(singlePaths, topology);
            foreach (LiveLabPaths paths in rolePaths)
            {
                paths.EnsureDirectories();
            }

            string preparedParent = rolePaths[0].ModsPath;
            preparedRoot = Path.Combine(
                preparedParent,
                $".sdvkit-project-smoke-prepared-{Guid.NewGuid():N}");
            string preparedArchive = Path.Combine(preparedRoot, "package.zip");
            Directory.CreateDirectory(preparedRoot);
            RefuseReparsePoint(preparedRoot);
            File.Copy(archivePath, preparedArchive, overwrite: false);

            PreparedPackage prepared = ExtractAndValidate(
                preparedArchive,
                preparedRoot,
                package,
                target);
            afterPrepare?.Invoke(prepared.ModPath);
            if (string.Equals(
                    prepared.Manifest.UniqueId,
                    AlwaysOnUniqueId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return FinishWithPreparedCleanup(Failure(
                    "reservedModIdentity",
                    target.Manifest.Path,
                    $"Project smoke cannot stage the reserved {AlwaysOnUniqueId} mod identity."));
            }

            string[] unavailableDependencies =
                FindUnavailableRequiredDependencies(prepared.Manifest);
            if (unavailableDependencies.Length > 0)
            {
                return FinishWithPreparedCleanup(Failure(
                    "runtimeDependencyUnavailable",
                    target.Manifest.Path,
                    $"Required runtime dependencies are not provided by the isolated lab: {string.Join(", ", unavailableDependencies)}. SDVKit does not acquire dependencies automatically."));
            }

            if (rolePaths.Any(paths => PathEquals(
                    Path.Combine(paths.ModsPath, prepared.TopLevelDirectory),
                    paths.AlwaysOnModPath)))
            {
                return FinishWithPreparedCleanup(Failure(
                    "reservedModStagingPath",
                    package.Archive,
                    "The package top-level directory collides with the reserved SDVKit.AlwaysOn lab mod path."));
            }

            string ownershipPath = OwnershipPath(singlePaths, topology);
            ProjectSmokeProblem? previousProblem = RemovePreviousOwnedStage(
                ownershipPath,
                topology,
                rolePaths);
            if (previousProblem is not null)
            {
                return FinishWithPreparedCleanup(
                    new ProjectModStagingResult(null, previousProblem));
            }

            string[] destinations = rolePaths
                .Select(paths => Path.Combine(paths.ModsPath, prepared.TopLevelDirectory))
                .ToArray();
            for (var index = 0; index < rolePaths.Length; index++)
            {
                ProjectSmokeProblem? collision = InspectModGroup(
                    rolePaths[index],
                    destinations[index],
                    prepared.Manifest.UniqueId,
                    preparedRoot);
                if (collision is not null)
                {
                    return FinishWithPreparedCleanup(
                        new ProjectModStagingResult(null, collision));
                }
            }

            var created = new List<string>();
            try
            {
                foreach (string destination in destinations)
                {
                    created.Add(destination);
                    copyTree(prepared.ModPath, destination);
                    string copiedIdentity = ModBuildIdentity.ComputeFileSet(destination);
                    if (!string.Equals(
                            copiedIdentity,
                            prepared.BuildIdentity,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "A staged project-mod file set differs from the prepared validated package.");
                    }
                }

                var ownership = new StagingOwnership(
                    OwnershipSchemaVersion,
                    topology,
                    prepared.Manifest.UniqueId,
                    prepared.Manifest.Version,
                    prepared.PackageHash,
                    prepared.BuildIdentity,
                    destinations);
                WriteOwnership(ownershipPath, ownership);
            }
            catch (Exception exception)
            {
                var rollbackComplete = true;
                foreach (string path in created)
                {
                    rollbackComplete &= DeleteKnownDirectory(path);
                }

                try
                {
                    File.Delete(ownershipPath);
                }
                catch (Exception cleanupException) when (IsControlledFailure(cleanupException))
                {
                    rollbackComplete = false;
                }

                rollbackComplete &= !File.Exists(ownershipPath);
                if (!rollbackComplete)
                {
                    throw new StagingRollbackException(
                        "Project staging failed and its exact partial destination could not be fully removed.",
                        exception);
                }

                throw;
            }

            if (!DeleteKnownDirectory(preparedRoot))
            {
                bool destinationsRemoved = RemoveStagingArtifacts(
                    destinations,
                    ownershipPath);
                return Failure(
                    destinationsRemoved
                        ? "preparedStagingCleanupIncomplete"
                        : "projectStagingRollbackIncomplete",
                    package.Archive,
                    destinationsRemoved
                        ? "The temporary prepared package remained in the isolated mod group, so the target staging was rolled back and no game process was started."
                        : "The temporary prepared package remained and the exact target staging could not be fully rolled back. No game process was started.");
            }

            preparedRoot = null;

            var artifact = new ProjectModArtifact(
                prepared.Manifest,
                archivePath,
                package.Archive!,
                prepared.Entries,
                prepared.TopLevelDirectory,
                prepared.PackageHash,
                prepared.BuildIdentity);
            return new ProjectModStagingResult(
                new ProjectModStaging(
                    artifact,
                    topology,
                    ownershipPath,
                    destinations),
                null);
        }
        catch (StagingRollbackException exception)
        {
            return FinishWithPreparedCleanup(Failure(
                "projectStagingRollbackIncomplete",
                package.Archive,
                exception.Message));
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return FinishWithPreparedCleanup(
                Failure("projectStagingFailed", package.Archive, exception.Message));
        }
        finally
        {
            if (preparedRoot is not null)
            {
                DeleteKnownDirectory(preparedRoot);
            }
        }
    }

    public static ProjectModCleanupResult Remove(ProjectModStaging staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        try
        {
            StagingOwnership ownership = ReadOwnership(staging.OwnershipPath);
            if (!OwnershipMatches(ownership, staging))
            {
                return CleanupFailure(
                    "stagingOwnershipMismatch",
                    "The retained project-smoke ownership marker no longer matches the staged artifact. No mod staging was removed.");
            }

            ProjectSmokeProblem? validation = ValidateOwnedDirectories(ownership, AllowedParents(staging));
            if (validation is not null)
            {
                return new ProjectModCleanupResult(false, validation);
            }

            foreach (string path in ownership.StagingPaths)
            {
                Directory.Delete(path, recursive: true);
            }

            File.Delete(staging.OwnershipPath);
            return new ProjectModCleanupResult(true, null);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return CleanupFailure("projectStagingCleanupFailed", exception.Message);
        }
    }

    private static PreparedPackage ExtractAndValidate(
        string archivePath,
        string preparedRoot,
        ProjectPackageReport package,
        ModBuildTarget target)
    {
        string extractionRoot = Path.Combine(preparedRoot, "content");
        Directory.CreateDirectory(extractionRoot);
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry[] fileEntries = archive.Entries
            .Where(entry => !entry.FullName.EndsWith('/'))
            .ToArray();
        string[] normalizedEntries = fileEntries
            .Select(entry => NormalizeArchivePath(entry.FullName))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] reportedEntries = package.Entries
            .Select(NormalizeArchivePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedEntries.Length == 0
            || normalizedEntries.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != normalizedEntries.Length
            || !normalizedEntries.SequenceEqual(reportedEntries, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The package contents no longer match the validated ProjectPackager entry set.");
        }

        string[] topLevels = normalizedEntries
            .Select(path => path.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (topLevels.Length != 1 || !IsSafeSegment(topLevels[0]))
        {
            throw new InvalidDataException(
                "The project package must contain exactly one safe top-level mod directory.");
        }

        string topLevel = topLevels[0];
        string expectedManifestEntry = $"{topLevel}/manifest.json";
        string[] manifests = normalizedEntries
            .Where(path => string.Equals(
                Path.GetFileName(path),
                "manifest.json",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifests.Length != 1
            || !string.Equals(manifests[0], expectedManifestEntry, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Project smoke V1 requires exactly one root code-mod manifest in the validated package.");
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
                || !string.Equals(segments[0], topLevel, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The project package contains an unsafe path: {entry.FullName}");
            }

            string destination = Path.GetFullPath(FromSlashPath(normalized), extractionRoot);
            if (!IsBelow(extractionRoot, destination))
            {
                throw new InvalidDataException(
                    $"The project package path escapes its staging root: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream source = entry.Open();
            using FileStream targetStream = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            source.CopyTo(targetStream);
        }

        string modPath = Path.Combine(extractionRoot, topLevel);
        LiveLabPaths.RejectReparsePointsBelow(modPath);
        ProjectModManifestReadResult manifestResult = ReadManifest(
            Path.Combine(modPath, "manifest.json"),
            target.Manifest.Path,
            allowVersionToken: false);
        if (manifestResult.Manifest is null)
        {
            throw new InvalidDataException(
                manifestResult.Problem?.Message ?? "The packaged mod manifest is invalid.");
        }

        ProjectModManifestInfo manifest = manifestResult.Manifest;
        if (!string.Equals(manifest.UniqueId, target.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.EntryDll, target.Manifest.EntryDll, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(modPath, manifest.EntryDll)))
        {
            throw new InvalidDataException(
                "The packaged root manifest identity or entry DLL does not match the inspected SMAPI project.");
        }

        return new PreparedPackage(
            manifest,
            modPath,
            topLevel,
            normalizedEntries,
            ModBuildIdentity.ComputeFile(archivePath),
            ModBuildIdentity.ComputeFileSet(modPath));
    }

    private static ProjectModManifestReadResult ReadManifest(
        string path,
        string reportPath,
        bool allowVersionToken)
    {
        try
        {
            using FileStream stream = new(
                path,
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
            if (root.ValueKind != JsonValueKind.Object
                || string.IsNullOrWhiteSpace(name)
                || !IsModId(uniqueId)
                || !IsSemanticVersion(version, allowVersionToken)
                || !IsEntryDll(entryDll)
                || Property(root, "ContentPackFor") is not null)
            {
                return ManifestFailure(reportPath, "The SMAPI code-mod manifest is invalid for project smoke.");
            }

            JsonElement? dependenciesProperty = Property(root, "Dependencies");
            var required = new List<ProjectModDependencyInfo>();
            if (dependenciesProperty is JsonElement dependencies)
            {
                if (dependencies.ValueKind != JsonValueKind.Array)
                {
                    return ManifestFailure(reportPath, "The manifest Dependencies field must be an array.");
                }

                foreach (JsonElement dependency in dependencies.EnumerateArray())
                {
                    string? dependencyId = dependency.ValueKind == JsonValueKind.Object
                        ? StringProperty(dependency, "UniqueID")
                        : null;
                    JsonElement? requiredProperty = dependency.ValueKind == JsonValueKind.Object
                        ? Property(dependency, "IsRequired")
                        : null;
                    JsonElement? minimumVersionProperty =
                        dependency.ValueKind == JsonValueKind.Object
                            ? Property(dependency, "MinimumVersion")
                            : null;
                    string? minimumVersion = minimumVersionProperty is null
                        or { ValueKind: JsonValueKind.Null }
                        ? null
                        : minimumVersionProperty is { ValueKind: JsonValueKind.String }
                            ? minimumVersionProperty.Value.GetString()
                            : string.Empty;
                    if (!IsModId(dependencyId)
                        || (requiredProperty is JsonElement value
                            && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        || (minimumVersion is not null
                            && !IsSemanticVersion(minimumVersion, allowToken: false)))
                    {
                        return ManifestFailure(
                            reportPath,
                            "The manifest contains an invalid runtime dependency declaration.");
                    }

                    bool isRequired = requiredProperty is not JsonElement requiredValue
                        || requiredValue.GetBoolean();
                    if (isRequired)
                    {
                        required.Add(new ProjectModDependencyInfo(
                            dependencyId!,
                            minimumVersion));
                    }
                }
            }

            return new ProjectModManifestReadResult(
                new ProjectModManifestInfo(
                    name!,
                    uniqueId!,
                    version!,
                    entryDll!,
                    required
                        .DistinctBy(
                            dependency => (dependency.UniqueId, dependency.MinimumVersion),
                            DependencyComparer.Instance)
                        .OrderBy(
                            dependency => dependency.UniqueId,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(
                            dependency => dependency.MinimumVersion,
                            StringComparer.Ordinal)
                        .ToArray()),
                null);
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException
            or JsonException)
        {
            return ManifestFailure(reportPath, $"The project manifest could not be read: {exception.Message}");
        }
    }

    private static ProjectSmokeProblem? RemovePreviousOwnedStage(
        string ownershipPath,
        string topology,
        IReadOnlyList<LiveLabPaths> rolePaths)
    {
        if (!File.Exists(ownershipPath))
        {
            return null;
        }

        try
        {
            StagingOwnership ownership = ReadOwnership(ownershipPath);
            if (ownership.SchemaVersion != OwnershipSchemaVersion
                || !string.Equals(ownership.Topology, topology, StringComparison.Ordinal)
                || ownership.StagingPaths.Count != rolePaths.Count
                || !ModBuildIdentity.IsValid(ownership.BuildIdentity)
                || !ModBuildIdentity.IsValid(ownership.PackageHash)
                || string.IsNullOrWhiteSpace(ownership.UniqueId)
                || string.IsNullOrWhiteSpace(ownership.Version))
            {
                return Problem(
                    "stagingOwnershipInvalid",
                    RelativePath(rolePaths[0].ProjectRoot, ownershipPath),
                    "The retained project-smoke ownership marker is invalid. No staged mod was replaced.");
            }

            ProjectSmokeProblem? validation = ValidateOwnedDirectories(
                ownership,
                rolePaths.Select(paths => paths.ModsPath).ToArray());
            if (validation is not null)
            {
                return validation;
            }

            return Problem(
                "stagingOwnershipPresent",
                RelativePath(rolePaths[0].ProjectRoot, ownershipPath),
                "A previous exact SDVKit-owned project-smoke staging is still present. It was left untouched because a later invocation cannot prove that every earlier child process stopped.");
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Problem(
                "stagingOwnershipInvalid",
                RelativePath(rolePaths[0].ProjectRoot, ownershipPath),
                $"The retained project-smoke staging could not be proven as SDVKit-owned: {exception.Message}");
        }
    }

    private static ProjectSmokeProblem? ValidateOwnedDirectories(
        StagingOwnership ownership,
        IReadOnlyList<string> allowedParents)
    {
        if (ownership.StagingPaths.Count != allowedParents.Count)
        {
            return Problem(
                "stagingOwnershipInvalid",
                null,
                "The ownership marker does not contain the expected topology staging paths.");
        }

        for (var index = 0; index < ownership.StagingPaths.Count; index++)
        {
            string path = Path.GetFullPath(ownership.StagingPaths[index]);
            if (!PathEquals(Path.GetDirectoryName(path)!, allowedParents[index])
                || PathEquals(path, Path.Combine(allowedParents[index], "SDVKit.AlwaysOn"))
                || !Directory.Exists(path)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return Problem(
                    "stagingOwnershipInvalid",
                    null,
                    "A retained staging path is missing, unsafe, or outside its exact isolated mod group.");
            }

            LiveLabPaths.RejectReparsePointsBelow(path);
            ProjectModManifestReadResult manifest = ReadManifest(
                Path.Combine(path, "manifest.json"),
                null!,
                allowVersionToken: false);
            if (manifest.Manifest is null
                || !string.Equals(
                    manifest.Manifest.UniqueId,
                    ownership.UniqueId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(manifest.Manifest.Version, ownership.Version, StringComparison.Ordinal)
                || !ModBuildIdentity.MatchesFileSet(
                    path,
                    ownership.BuildIdentity,
                    allowNewRootConfigJson: true))
            {
                return Problem(
                    "stagingOwnershipDrifted",
                    null,
                    "The retained project-smoke staging differs from its ownership marker. It was left untouched.");
            }
        }

        return null;
    }

    private static ProjectSmokeProblem? InspectModGroup(
        LiveLabPaths paths,
        string destination,
        string uniqueId,
        string preparedRoot)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(paths.ModsPath))
        {
            if (PathEquals(entry, preparedRoot))
            {
                continue;
            }

            if (PathEquals(entry, paths.AlwaysOnModPath))
            {
                continue;
            }

            if (PathEquals(entry, destination))
            {
                return Problem(
                    "modStagingCollision",
                    RelativePath(paths.ProjectRoot, entry),
                    "The target project-mod directory already exists without current SDVKit project-smoke ownership.");
            }

            string manifestPath = Path.Combine(entry, "manifest.json");
            if (Directory.Exists(entry) && File.Exists(manifestPath))
            {
                ProjectModManifestReadResult manifest = ReadManifest(
                    manifestPath,
                    RelativePath(paths.ProjectRoot, manifestPath),
                    allowVersionToken: false);
                if (manifest.Manifest is not null
                    && string.Equals(
                        manifest.Manifest.UniqueId,
                        uniqueId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Problem(
                        "modIdentityCollision",
                        RelativePath(paths.ProjectRoot, entry),
                        $"The isolated mod group already contains {uniqueId} in another unowned directory.");
                }
            }

            return Problem(
                "foreignLabModCollision",
                RelativePath(paths.ProjectRoot, entry),
                "Project smoke requires an isolated mod group containing only SDVKit AlwaysOn and its own target staging.");
        }

        return null;
    }

    private static LiveLabPaths[] ResolveRolePaths(
        LiveLabPaths singlePaths,
        string topology)
    {
        return string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            ? [singlePaths]
            :
            [
                LiveLabPaths.ResolveNetworkRole(singlePaths, NetworkTwoContract.HostRole),
                LiveLabPaths.ResolveNetworkRole(singlePaths, NetworkTwoContract.FarmhandRole),
            ];
    }

    private static string[] AllowedParents(ProjectModStaging staging)
    {
        string labRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(staging.OwnershipPath)!)!,
            string.Empty);
        string projectRoot = Path.GetFullPath(Path.Combine(labRoot, "..", ".."));
        LiveLabPaths single = LiveLabPaths.Resolve(projectRoot);
        return ResolveRolePaths(single, staging.Topology)
            .Select(paths => paths.ModsPath)
            .ToArray();
    }

    private static string OwnershipPath(LiveLabPaths singlePaths, string topology)
    {
        string topologyRoot = string.Equals(
                topology,
                LiveLabState.SingleTopology,
                StringComparison.Ordinal)
            ? singlePaths.SingleRoot
            : Path.Combine(singlePaths.ProjectRoot, ".sdvkit", "lab", NetworkTwoContract.Topology);
        Directory.CreateDirectory(topologyRoot);
        RefuseReparsePoint(topologyRoot);
        return Path.Combine(topologyRoot, OwnershipFileName);
    }

    private static void CopyPlainTree(string source, string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"The target staging path already exists: {destination}");
        }

        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"The prepared mod contains a reparse point: {directory}");
            }

            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"The prepared mod contains a reparse point: {file}");
            }

            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void WriteOwnership(string path, StagingOwnership ownership)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The staging ownership path has no parent.");
        EnsurePlainDirectory(directory);
        string temporary = Path.Combine(directory, $".{OwnershipFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, ownership, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static StagingOwnership ReadOwnership(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        StagingOwnership? ownership =
            JsonSerializer.Deserialize<StagingOwnership>(stream, JsonOptions);
        if (ownership is null || ownership.StagingPaths is null)
        {
            throw new InvalidDataException(
                "The project-smoke ownership marker is empty or structurally incomplete.");
        }

        return ownership;
    }

    private static bool OwnershipMatches(
        StagingOwnership ownership,
        ProjectModStaging staging)
    {
        return ownership.SchemaVersion == OwnershipSchemaVersion
            && string.Equals(ownership.Topology, staging.Topology, StringComparison.Ordinal)
            && string.Equals(
                ownership.UniqueId,
                staging.Artifact.Manifest.UniqueId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                ownership.Version,
                staging.Artifact.Manifest.Version,
                StringComparison.Ordinal)
            && string.Equals(
                ownership.PackageHash,
                staging.Artifact.PackageHash,
                StringComparison.Ordinal)
            && string.Equals(
                ownership.BuildIdentity,
                staging.Artifact.BuildIdentity,
                StringComparison.Ordinal)
            && ownership.StagingPaths.SequenceEqual(
                staging.StagingPaths,
                PathComparer());
    }

    private static void EnsurePlainDirectory(string path)
    {
        Directory.CreateDirectory(path);
        RefuseReparsePoint(path);
    }

    private static void RefuseReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"A managed project-smoke path is a reparse point: {path}");
        }
    }

    private static bool RemoveStagingArtifacts(
        IEnumerable<string> paths,
        string ownershipPath)
    {
        var removed = true;
        foreach (string path in paths)
        {
            removed = DeleteKnownDirectory(path) && removed;
        }
        if (removed)
        {
            try
            {
                File.Delete(ownershipPath);
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                removed = false;
            }
        }

        return removed && !File.Exists(ownershipPath);
    }

    private static bool DeleteKnownDirectory(string path)
    {
        if (PathIsAbsent(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            // This is a current-operation temporary or rollback path below .sdvkit.
        }

        return PathIsAbsent(path);
    }

    private static bool PathIsAbsent(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return false;
        }
    }

    private static bool IsMinimumVersionSatisfied(
        string providedVersion,
        string? minimumVersion)
    {
        if (minimumVersion is null)
        {
            return true;
        }

        if (!TryReadComparableVersion(
                providedVersion,
                out string[] providedNumbers,
                out string[]? providedPrerelease)
            || !TryReadComparableVersion(
                minimumVersion,
                out string[] minimumNumbers,
                out string[]? minimumPrerelease))
        {
            return false;
        }

        for (var index = 0; index < 3; index++)
        {
            int numericComparison = CompareNumericText(
                providedNumbers[index],
                minimumNumbers[index]);
            if (numericComparison != 0)
            {
                return numericComparison > 0;
            }
        }

        if (providedPrerelease is null)
        {
            return true;
        }

        return minimumPrerelease is not null
            && ComparePrerelease(providedPrerelease, minimumPrerelease) >= 0;
    }

    private static bool TryReadComparableVersion(
        string version,
        out string[] coreNumbers,
        out string[]? prerelease)
    {
        coreNumbers = [];
        prerelease = null;
        string normalized;
        try
        {
            normalized = ProjectModLaunchState.NormalizeVersion(version);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        string precedence = normalized.Split('+', count: 2)[0];
        string[] parts = precedence.Split('-', count: 2);
        coreNumbers = parts[0].Split('.');
        if (coreNumbers.Length != 3)
        {
            return false;
        }

        if (parts.Length == 1)
        {
            return true;
        }

        prerelease = parts[1].Split('.');
        return !prerelease.Any(identifier =>
            IsNumericIdentifier(identifier)
            && identifier.Length > 1
            && identifier[0] == '0');
    }

    private static int ComparePrerelease(string[] left, string[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            if (string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                continue;
            }

            bool leftNumeric = IsNumericIdentifier(left[index]);
            bool rightNumeric = IsNumericIdentifier(right[index]);
            if (leftNumeric && rightNumeric)
            {
                return CompareNumericText(left[index], right[index]);
            }

            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            return string.Compare(left[index], right[index], StringComparison.Ordinal);
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool IsNumericIdentifier(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static int CompareNumericText(string left, string right)
    {
        int lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0
            ? lengthComparison
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    private static string NormalizeArchivePath(string path) =>
        path.Replace('\\', '/');

    private static bool IsSafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && !Path.IsPathRooted(value)
        && value.IndexOfAny(['/', '\\', ':']) < 0;

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

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

    private static bool IsEntryDll(string? value) =>
        value is not null
        && value.Length > ".dll".Length
        && value.EndsWith(".dll", StringComparison.Ordinal)
        && value.All(IsModIdCharacter);

    private static bool IsModId(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(IsModIdCharacter);

    private static bool IsModIdCharacter(char character) =>
        character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '.'
            or '-';

    private static bool IsSemanticVersion(string? value, bool allowToken)
    {
        if (allowToken && string.Equals(value, "%ProjectVersion%", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _ = ProjectModLaunchState.NormalizeVersion(value ?? string.Empty);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static ProjectModManifestReadResult ManifestFailure(
        string? path,
        string message) =>
        new(null, Problem("invalidProjectModManifest", path, message));

    private static ProjectModStagingResult Failure(
        string code,
        string? path,
        string message) =>
        new(null, Problem(code, path, message));

    private static ProjectModCleanupResult CleanupFailure(string code, string message) =>
        new(false, Problem(code, null, message));

    private static ProjectSmokeProblem Problem(string code, string? path, string message) =>
        new(code, path, message);

    private static bool IsBelow(string root, string path)
    {
        string absoluteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string absolutePath = Path.GetFullPath(path);
        return !PathEquals(absoluteRoot, absolutePath)
            && absolutePath.StartsWith(
                absoluteRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FromSlashPath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar);

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or SecurityException
            or UnauthorizedAccessException
            or JsonException;

    private sealed record PreparedPackage(
        ProjectModManifestInfo Manifest,
        string ModPath,
        string TopLevelDirectory,
        IReadOnlyList<string> Entries,
        string PackageHash,
        string BuildIdentity);

    private sealed record StagingOwnership(
        int SchemaVersion,
        string Topology,
        string UniqueId,
        string Version,
        string PackageHash,
        string BuildIdentity,
        IReadOnlyList<string> StagingPaths);

    private sealed class StagingRollbackException(string message, Exception innerException)
        : InvalidOperationException(message, innerException);

    private sealed class DependencyComparer
        : IEqualityComparer<(string UniqueId, string? MinimumVersion)>
    {
        public static DependencyComparer Instance { get; } = new();

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
