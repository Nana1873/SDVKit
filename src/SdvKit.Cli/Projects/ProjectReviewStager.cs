using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectModStager
{
    private const int ReviewOwnershipSchemaVersion = 1;
    private const string ReviewOwnershipFileName = "project-review-staging.json";

    public static ProjectReviewStagingResult StageReview(
        IReadOnlyList<ProjectReviewPreparedArtifact> artifacts,
        LiveLabPaths paths,
        Action<string, string>? copyTree = null,
        Func<string, bool>? deleteTree = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(paths);
        copyTree ??= CopyPlainTree;
        deleteTree ??= DeleteKnownDirectory;

        try
        {
            paths.EnsureDirectories();
            ProjectReviewProblem? setProblem = ValidateReviewSet(artifacts);
            if (setProblem is not null)
            {
                return new ProjectReviewStagingResult(null, setProblem);
            }

            string ownershipPath = ReviewOwnershipPath(paths);
            ProjectReviewStagingResult retained = ReadReview(
                paths,
                detectUnownedArtifacts: false);
            if (retained.Problem is not null)
            {
                return retained;
            }

            if (retained.Staging is not null)
            {
                return ReviewFailure(
                    "reviewStagingOwnershipPresent",
                    RelativePath(paths.ProjectRoot, ownershipPath),
                    "A previous exact SDVKit-owned project-review staging is still present and was left untouched.");
            }

            string smokeOwnershipPath = Path.Combine(paths.SingleRoot, OwnershipFileName);
            if (File.Exists(smokeOwnershipPath))
            {
                return ReviewFailure(
                    "smokeStagingOwnershipPresent",
                    RelativePath(paths.ProjectRoot, smokeOwnershipPath),
                    "A retained project-smoke staging blocks project review.");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(paths.ModsPath))
            {
                if (!PathEquals(entry, paths.AlwaysOnModPath))
                {
                    return ReviewFailure(
                        "foreignLabModCollision",
                        RelativePath(paths.ProjectRoot, entry),
                        "Project review requires an isolated mod group containing only SDVKit AlwaysOn before its exact staging set is installed.");
                }
            }

            var owned = artifacts.Select(artifact =>
            {
                string stagingPath = Path.Combine(paths.ModsPath, artifact.TopLevelDirectory);
                return new ProjectReviewOwnedArtifact(
                    artifact.Role,
                    artifact.SourceRoot,
                    artifact.TopLevelDirectory,
                    stagingPath,
                    artifact.Manifest,
                    artifact.BuildIdentity,
                    artifact.BuildLog,
                    artifact.PackageLog);
            }).ToArray();

            if (owned.Any(artifact => Directory.Exists(artifact.StagingPath)
                    || File.Exists(artifact.StagingPath)))
            {
                ProjectReviewOwnedArtifact collision = owned.First(artifact =>
                    Directory.Exists(artifact.StagingPath) || File.Exists(artifact.StagingPath));
                return ReviewFailure(
                    "reviewStagingCollision",
                    RelativePath(paths.ProjectRoot, collision.StagingPath),
                    "A project-review staging destination already exists without current review ownership.");
            }

            var created = new List<string>();
            try
            {
                foreach ((ProjectReviewPreparedArtifact source, ProjectReviewOwnedArtifact target)
                    in artifacts.Zip(owned))
                {
                    created.Add(target.StagingPath);
                    copyTree(source.PreparedPath, target.StagingPath);
                    string identity = ModBuildIdentity.ComputeFileSet(target.StagingPath);
                    if (!string.Equals(identity, source.BuildIdentity, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "A staged project-review file set differs from its prepared source.");
                    }
                }

                var staging = new ProjectReviewStaging(
                    ReviewOwnershipSchemaVersion,
                    ownershipPath,
                    owned);
                WriteReviewOwnership(ownershipPath, staging);
                return new ProjectReviewStagingResult(staging, null);
            }
            catch (Exception exception)
            {
                var rollbackComplete = true;
                foreach (string path in created)
                {
                    rollbackComplete = deleteTree(path) && rollbackComplete;
                }

                try
                {
                    File.Delete(ownershipPath);
                }
                catch (Exception cleanupException) when (IsControlledFailure(cleanupException))
                {
                    rollbackComplete = false;
                }

                if (!rollbackComplete || File.Exists(ownershipPath))
                {
                    return ReviewFailure(
                        "reviewStagingRollbackIncomplete",
                        null,
                        "Project-review staging failed and its exact partial destinations could not be fully removed.");
                }

                throw new InvalidOperationException(
                    "Project-review staging failed and was rolled back.",
                    exception);
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return ReviewFailure("reviewStagingFailed", null, exception.Message);
        }
    }

    public static ProjectReviewStagingResult ReadReview(
        LiveLabPaths paths,
        bool detectUnownedArtifacts = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string ownershipPath = ReviewOwnershipPath(paths);
        if (!File.Exists(ownershipPath))
        {
            if (detectUnownedArtifacts && Directory.Exists(paths.ModsPath))
            {
                try
                {
                    string? unownedArtifact = Directory
                        .EnumerateFileSystemEntries(paths.ModsPath)
                        .FirstOrDefault(entry => !PathEquals(entry, paths.AlwaysOnModPath));
                    if (unownedArtifact is not null)
                    {
                        return ReviewFailure(
                            "reviewStagingOwnershipMissing",
                            RelativePath(paths.ProjectRoot, unownedArtifact),
                            "The isolated mod group contains a non-AlwaysOn artifact without a project-review ownership marker; it was left untouched.");
                    }
                }
                catch (Exception exception) when (IsControlledFailure(exception))
                {
                    return ReviewFailure(
                        "reviewStagingOwnershipInvalid",
                        RelativePath(paths.ProjectRoot, paths.ModsPath),
                        $"The isolated project-review mod group could not be proven clean: {exception.Message}");
                }
            }

            return new ProjectReviewStagingResult(null, null);
        }

        try
        {
            using FileStream stream = new(
                ownershipPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            ProjectReviewStaging staging = JsonSerializer.Deserialize<ProjectReviewStaging>(
                    stream,
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "The project-review ownership marker is empty.");
            staging = staging with { OwnershipPath = ownershipPath };
            ProjectReviewProblem? problem = ValidateOwnedReview(staging, paths);
            return problem is null
                ? new ProjectReviewStagingResult(staging, null)
                : new ProjectReviewStagingResult(null, problem);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return ReviewFailure(
                "reviewStagingOwnershipInvalid",
                RelativePath(paths.ProjectRoot, ownershipPath),
                $"The retained project-review staging could not be proven as SDVKit-owned: {exception.Message}");
        }
    }

    public static ProjectReviewCleanupResult RemoveReview(LiveLabPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ProjectReviewStagingResult current = ReadReview(paths);
        if (current.Problem is not null)
        {
            return new ProjectReviewCleanupResult(false, current.Problem);
        }

        if (current.Staging is null)
        {
            return new ProjectReviewCleanupResult(true, null);
        }

        try
        {
            foreach (ProjectReviewOwnedArtifact artifact in current.Staging.Artifacts)
            {
                Directory.Delete(artifact.StagingPath, recursive: true);
            }

            File.Delete(current.Staging.OwnershipPath);
            return new ProjectReviewCleanupResult(true, null);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new ProjectReviewCleanupResult(
                false,
                new ProjectReviewProblem(
                    "reviewStagingCleanupFailed",
                    null,
                    exception.Message));
        }
    }

    internal static ProjectReviewProblem? ValidateReviewSet(
        IReadOnlyList<ProjectReviewPreparedArtifact> artifacts)
    {
        if (artifacts.Count == 0
            || artifacts.Count(artifact => string.Equals(
                artifact.Role,
                ProjectReviewArtifactRole.Target,
                StringComparison.Ordinal)) != 1)
        {
            return ReviewProblem(
                "reviewTargetRequired",
                null,
                "Project review requires exactly one target mod.");
        }

        foreach (ProjectReviewPreparedArtifact artifact in artifacts)
        {
            bool expectedKind = artifact.Role switch
            {
                ProjectReviewArtifactRole.Target or ProjectReviewArtifactRole.Companion =>
                    string.Equals(
                        artifact.Manifest.Kind,
                        ProjectInspectionReport.SmapiMod,
                        StringComparison.Ordinal),
                ProjectReviewArtifactRole.ContentPack => string.Equals(
                    artifact.Manifest.Kind,
                    ProjectInspectionReport.ContentPack,
                    StringComparison.Ordinal),
                _ => false,
            };
            if (!expectedKind)
            {
                return ReviewProblem(
                    "reviewArtifactKindMismatch",
                    artifact.SourceRoot,
                    "The explicit review source has the wrong manifest kind for its role.");
            }

            if (string.Equals(
                    artifact.Manifest.UniqueId,
                    AlwaysOnUniqueId,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    artifact.TopLevelDirectory,
                    "SDVKit.AlwaysOn",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ReviewProblem(
                    "reservedModIdentity",
                    artifact.SourceRoot,
                    "The SDVKit.AlwaysOn identity and staging path are reserved.");
            }

            if (!Directory.Exists(artifact.PreparedPath)
                || !IsSafeSegment(artifact.TopLevelDirectory)
                || !ModBuildIdentity.IsValid(artifact.BuildIdentity)
                || !string.Equals(
                    ModBuildIdentity.ComputeFileSet(artifact.PreparedPath),
                    artifact.BuildIdentity,
                    StringComparison.Ordinal))
            {
                return ReviewProblem(
                    "reviewPreparedArtifactInvalid",
                    artifact.SourceRoot,
                    "A prepared project-review artifact is missing, unsafe, or changed before staging.");
            }

            LiveLabPaths.RejectReparsePointsBelow(artifact.PreparedPath);
        }

        ProjectReviewPreparedArtifact? duplicateId = artifacts
            .GroupBy(artifact => artifact.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Skip(1))
            .FirstOrDefault();
        if (duplicateId is not null)
        {
            return ReviewProblem(
                "reviewModIdentityCollision",
                duplicateId.SourceRoot,
                $"The explicit review set contains duplicate UniqueID {duplicateId.Manifest.UniqueId}.");
        }

        ProjectReviewPreparedArtifact? duplicateDirectory = artifacts
            .GroupBy(artifact => artifact.TopLevelDirectory, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Skip(1))
            .FirstOrDefault();
        if (duplicateDirectory is not null)
        {
            return ReviewProblem(
                "reviewStagingNameCollision",
                duplicateDirectory.SourceRoot,
                $"The explicit review set contains duplicate staging directory {duplicateDirectory.TopLevelDirectory}.");
        }

        Dictionary<string, string> available = artifacts.ToDictionary(
            artifact => artifact.Manifest.UniqueId,
            artifact => artifact.Manifest.Version,
            StringComparer.OrdinalIgnoreCase);
        available.Add(AlwaysOnUniqueId, AlwaysOnVersion);
        foreach (ProjectReviewPreparedArtifact artifact in artifacts)
        {
            var required = artifact.Manifest.RequiredDependencies.ToList();
            if (artifact.Manifest.ContentPackFor is not null)
            {
                required.Add(new ProjectReviewDependency(
                    artifact.Manifest.ContentPackFor,
                    artifact.Manifest.ContentPackForMinimumVersion));
            }

            foreach (ProjectReviewDependency dependency in required)
            {
                if (!available.TryGetValue(dependency.UniqueId, out string? version))
                {
                    return ReviewProblem(
                        "reviewDependencyUnavailable",
                        artifact.SourceRoot,
                        $"{artifact.Manifest.UniqueId} requires explicit local dependency {dependency.UniqueId}; SDVKit does not search for or download it.");
                }

                if (!IsMinimumVersionSatisfied(version, dependency.MinimumVersion))
                {
                    return ReviewProblem(
                        "reviewDependencyVersionMismatch",
                        artifact.SourceRoot,
                        $"{artifact.Manifest.UniqueId} requires {dependency.UniqueId} >= {dependency.MinimumVersion}, but the explicit set provides {version}.");
                }
            }
        }

        return null;
    }

    private static ProjectReviewProblem? ValidateOwnedReview(
        ProjectReviewStaging staging,
        LiveLabPaths paths)
    {
        if (staging.SchemaVersion != ReviewOwnershipSchemaVersion
            || staging.Artifacts is null
            || staging.Artifacts.Count == 0
            || staging.Artifacts.Count(artifact => string.Equals(
                artifact.Role,
                ProjectReviewArtifactRole.Target,
                StringComparison.Ordinal)) != 1)
        {
            return ReviewProblem(
                "reviewStagingOwnershipInvalid",
                null,
                "The retained project-review ownership marker is structurally invalid.");
        }

        if (staging.Artifacts.Select(artifact => artifact.StagingPath)
                .Distinct(PathComparer()).Count() != staging.Artifacts.Count
            || staging.Artifacts.Select(artifact => artifact.Manifest.UniqueId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != staging.Artifacts.Count)
        {
            return ReviewProblem(
                "reviewStagingOwnershipInvalid",
                null,
                "The retained project-review ownership marker contains duplicate artifacts.");
        }

        foreach (ProjectReviewOwnedArtifact artifact in staging.Artifacts)
        {
            string stagingPath = Path.GetFullPath(artifact.StagingPath);
            if (!PathEquals(Path.GetDirectoryName(stagingPath)!, paths.ModsPath)
                || PathEquals(stagingPath, paths.AlwaysOnModPath)
                || !Directory.Exists(stagingPath)
                || (File.GetAttributes(stagingPath) & FileAttributes.ReparsePoint) != 0
                || !ModBuildIdentity.IsValid(artifact.BuildIdentity))
            {
                return ReviewProblem(
                    "reviewStagingOwnershipInvalid",
                    null,
                    "A retained review staging path is missing, unsafe, or outside the exact isolated mod group.");
            }

            LiveLabPaths.RejectReparsePointsBelow(stagingPath);
            ProjectReviewManifest? manifest = ReadReviewManifest(
                Path.Combine(stagingPath, "manifest.json"),
                allowVersionToken: false,
                out _);
            if (manifest is null
                || !string.Equals(
                    manifest.UniqueId,
                    artifact.Manifest.UniqueId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    manifest.Version,
                    artifact.Manifest.Version,
                    StringComparison.Ordinal)
                || !string.Equals(
                    ModBuildIdentity.ComputeFileSet(stagingPath),
                    artifact.BuildIdentity,
                    StringComparison.Ordinal))
            {
                return ReviewProblem(
                    "reviewStagingOwnershipDrifted",
                    null,
                    "A retained project-review staging differs from its ownership marker and was left untouched.");
            }
        }

        return null;
    }

    private static void WriteReviewOwnership(
        string path,
        ProjectReviewStaging staging)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException(
                "The review staging ownership path has no parent.");
        EnsurePlainDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{ReviewOwnershipFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, staging, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string ReviewOwnershipPath(LiveLabPaths paths) =>
        Path.Combine(paths.SingleRoot, ReviewOwnershipFileName);

    private static ProjectReviewStagingResult ReviewFailure(
        string code,
        string? path,
        string message) =>
        new(null, ReviewProblem(code, path, message));

    private static ProjectReviewProblem ReviewProblem(
        string code,
        string? path,
        string message) =>
        new(code, path, message);
}
