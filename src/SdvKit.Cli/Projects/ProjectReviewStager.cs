using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectModStager
{
    private const int ReviewOwnershipSchemaVersion = 2;
    private const string ReviewOwnershipFileName = "project-review-staging.json";

    public static ProjectReviewStagingResult StageReview(
        IReadOnlyList<ProjectReviewPreparedArtifact> artifacts,
        LiveLabPaths paths,
        Action<string, string>? copyTree = null,
        Func<string, bool>? deleteTree = null) =>
        StageReview(
            artifacts,
            LiveLabState.SingleTopology,
            paths,
            copyTree,
            deleteTree);

    public static ProjectReviewStagingResult StageReview(
        IReadOnlyList<ProjectReviewPreparedArtifact> artifacts,
        string topology,
        LiveLabPaths singlePaths,
        Action<string, string>? copyTree = null,
        Func<string, bool>? deleteTree = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        ArgumentNullException.ThrowIfNull(singlePaths);
        copyTree ??= CopyPlainTree;
        deleteTree ??= DeleteKnownDirectory;

        try
        {
            ProjectReviewProblem? setProblem = ValidateReviewSet(artifacts);
            if (setProblem is not null)
            {
                return new ProjectReviewStagingResult(null, setProblem);
            }

            ProjectReviewPreparedArtifact reviewTarget = artifacts.Single(artifact =>
                string.Equals(
                    artifact.Role,
                    ProjectReviewArtifactRole.Target,
                    StringComparison.Ordinal));
            if (string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                && string.Equals(
                    reviewTarget.Manifest.Kind,
                    ProjectInspectionReport.ContentPack,
                    StringComparison.Ordinal))
            {
                return ReviewFailure(
                    "reviewTargetTopologyUnsupported",
                    reviewTarget.SourceRoot,
                    "A content-pack review target supports only topology single.");
            }

            ReviewRolePaths[] rolePaths = ResolveReviewRolePaths(singlePaths, topology);
            foreach (ReviewRolePaths rolePath in rolePaths)
            {
                rolePath.Paths.EnsureDirectories();
            }

            string ownershipPath = ReviewOwnershipPath(singlePaths, topology);
            ProjectReviewStagingResult retained = ReadReview(
                singlePaths,
                topology,
                detectUnownedArtifacts: false);
            if (retained.Problem is not null)
            {
                return retained;
            }

            if (retained.Staging is not null)
            {
                return ReviewFailure(
                    "reviewStagingOwnershipPresent",
                    RelativePath(singlePaths.ProjectRoot, ownershipPath),
                    "A previous exact SDVKit-owned project-review staging is still present and was left untouched.");
            }

            string smokeOwnershipPath = SmokeOwnershipPath(singlePaths, topology);
            if (File.Exists(smokeOwnershipPath))
            {
                return ReviewFailure(
                    "smokeStagingOwnershipPresent",
                    RelativePath(singlePaths.ProjectRoot, smokeOwnershipPath),
                    "A retained project-smoke staging blocks project review.");
            }

            foreach (ReviewRolePaths rolePath in rolePaths)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                    rolePath.Paths.ModsPath))
                {
                    if (!PathEquals(entry, rolePath.Paths.AlwaysOnModPath))
                    {
                        return ReviewFailure(
                            "foreignLabModCollision",
                            RelativePath(singlePaths.ProjectRoot, entry),
                            $"Project review requires the isolated {rolePath.Role} mod group to contain only SDVKit AlwaysOn before its exact staging set is installed.");
                    }
                }
            }

            var owned = artifacts.Select(artifact =>
            {
                ProjectReviewRoleStagingPath[] stagingPaths = rolePaths
                    .Select(rolePath => new ProjectReviewRoleStagingPath(
                        rolePath.Role,
                        Path.Combine(
                            rolePath.Paths.ModsPath,
                            artifact.TopLevelDirectory)))
                    .ToArray();
                return new ProjectReviewOwnedArtifact(
                    artifact.Role,
                    artifact.SourceRoot,
                    artifact.TopLevelDirectory,
                    stagingPaths,
                    artifact.Manifest,
                    artifact.BuildIdentity,
                    artifact.BuildLog,
                    artifact.PackageLog)
                { ProjectFile = artifact.ProjectFile };
            }).ToArray();

            ProjectReviewRoleStagingPath? collision = owned
                .SelectMany(artifact => artifact.RoleStagingPaths)
                .FirstOrDefault(path => Directory.Exists(path.StagingPath)
                    || File.Exists(path.StagingPath));
            if (collision is not null)
            {
                return ReviewFailure(
                    "reviewStagingCollision",
                    RelativePath(singlePaths.ProjectRoot, collision.StagingPath),
                    "A project-review staging destination already exists without current review ownership.");
            }

            var created = new List<string>();
            try
            {
                foreach ((ProjectReviewPreparedArtifact source, ProjectReviewOwnedArtifact target)
                    in artifacts.Zip(owned))
                {
                    foreach (ProjectReviewRoleStagingPath roleStaging
                        in target.RoleStagingPaths)
                    {
                        created.Add(roleStaging.StagingPath);
                        copyTree(source.PreparedPath, roleStaging.StagingPath);
                        string identity = ModBuildIdentity.ComputeFileSet(
                            roleStaging.StagingPath);
                        if (!string.Equals(
                                identity,
                                source.BuildIdentity,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"The staged {roleStaging.Role} project-review file set differs from its prepared source.");
                        }
                    }
                }

                var staging = new ProjectReviewStaging(
                    ReviewOwnershipSchemaVersion,
                    topology,
                    ownershipPath,
                    owned);
                WriteReviewOwnership(ownershipPath, staging);
                return new ProjectReviewStagingResult(staging, null);
            }
            catch (Exception exception)
            {
                var rollbackComplete = true;
                foreach (string path in created.AsEnumerable().Reverse())
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
        bool detectUnownedArtifacts = true) =>
        ReadReview(
            paths,
            LiveLabState.SingleTopology,
            detectUnownedArtifacts);

    public static ProjectReviewStagingResult ReadReview(
        LiveLabPaths singlePaths,
        string topology,
        bool detectUnownedArtifacts = true)
    {
        ArgumentNullException.ThrowIfNull(singlePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        ReviewRolePaths[] rolePaths;
        try
        {
            rolePaths = ResolveReviewRolePaths(singlePaths, topology);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return ReviewFailure("reviewTopologyInvalid", null, exception.Message);
        }

        string ownershipPath = ReviewOwnershipPath(singlePaths, topology);
        if (!File.Exists(ownershipPath))
        {
            if (detectUnownedArtifacts && File.Exists(SmokeOwnershipPath(singlePaths, topology)))
            {
                return ReviewFailure(
                    "smokeStagingOwnershipPresent",
                    RelativePath(
                        singlePaths.ProjectRoot,
                        SmokeOwnershipPath(singlePaths, topology)),
                    "A retained project-smoke staging blocks project review.");
            }

            if (detectUnownedArtifacts)
            {
                try
                {
                    foreach (ReviewRolePaths rolePath in rolePaths)
                    {
                        if (!Directory.Exists(rolePath.Paths.ModsPath))
                        {
                            continue;
                        }

                        string? unownedArtifact = Directory
                            .EnumerateFileSystemEntries(rolePath.Paths.ModsPath)
                            .FirstOrDefault(entry => !PathEquals(
                                entry,
                                rolePath.Paths.AlwaysOnModPath));
                        if (unownedArtifact is not null)
                        {
                            return ReviewFailure(
                                "reviewStagingOwnershipMissing",
                                RelativePath(singlePaths.ProjectRoot, unownedArtifact),
                                $"The isolated {rolePath.Role} mod group contains a non-AlwaysOn artifact without a project-review ownership marker; it was left untouched.");
                        }
                    }
                }
                catch (Exception exception) when (IsControlledFailure(exception))
                {
                    return ReviewFailure(
                        "reviewStagingOwnershipInvalid",
                        RelativePath(singlePaths.ProjectRoot, ReviewTopologyRoot(singlePaths, topology)),
                        $"The isolated project-review mod groups could not be proven clean: {exception.Message}");
                }
            }

            return new ProjectReviewStagingResult(null, null);
        }

        return ReadReviewOwnership(
            singlePaths,
            topology,
            allowMissingStagingPaths: false,
            requireContentIdentity: true);
    }

    internal static ProjectReviewStagingResult ReadReviewForCleanup(
        LiveLabPaths singlePaths,
        string topology)
    {
        ArgumentNullException.ThrowIfNull(singlePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        string ownershipPath;
        try
        {
            ResolveReviewRolePaths(singlePaths, topology);
            ownershipPath = ReviewOwnershipPath(singlePaths, topology);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return ReviewFailure("reviewTopologyInvalid", null, exception.Message);
        }

        return File.Exists(ownershipPath)
            ? ReadReviewOwnership(
                singlePaths,
                topology,
                allowMissingStagingPaths: true,
                requireContentIdentity: false)
            : ReadReview(singlePaths, topology);
    }

    public static ProjectReviewCleanupResult RemoveReview(LiveLabPaths paths) =>
        RemoveReview(paths, LiveLabState.SingleTopology);

    public static ProjectReviewCleanupResult RemoveReview(
        LiveLabPaths singlePaths,
        string topology)
    {
        ArgumentNullException.ThrowIfNull(singlePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        string ownershipPath;
        try
        {
            ResolveReviewRolePaths(singlePaths, topology);
            ownershipPath = ReviewOwnershipPath(singlePaths, topology);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new ProjectReviewCleanupResult(
                false,
                ReviewProblem("reviewTopologyInvalid", null, exception.Message));
        }

        if (!File.Exists(ownershipPath))
        {
            ProjectReviewStagingResult absent = ReadReview(singlePaths, topology);
            return absent.Problem is null
                ? new ProjectReviewCleanupResult(true, null)
                : new ProjectReviewCleanupResult(false, absent.Problem);
        }

        ProjectReviewStagingResult current = ReadReviewOwnership(
            singlePaths,
            topology,
            allowMissingStagingPaths: true,
            requireContentIdentity: false);
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
            var cleanupErrors = new List<string>();
            string[] ownedPaths = current.Staging.Artifacts
                .SelectMany(artifact => artifact.RoleStagingPaths)
                .Select(roleStaging => roleStaging.StagingPath)
                .ToArray();
            foreach (ProjectReviewOwnedArtifact artifact in current.Staging.Artifacts)
            {
                foreach (ProjectReviewRoleStagingPath roleStaging
                    in artifact.RoleStagingPaths)
                {
                    if (!Directory.Exists(roleStaging.StagingPath))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(roleStaging.StagingPath, recursive: true);
                    }
                    catch (Exception exception) when (IsControlledFailure(exception))
                    {
                        cleanupErrors.Add(
                            $"{roleStaging.Role}:{roleStaging.StagingPath}: {exception.Message}");
                    }
                }
            }

            foreach (string remainingPath in ownedPaths.Where(path => !PathIsAbsent(path)))
            {
                cleanupErrors.Add($"retained:{remainingPath}");
            }

            if (cleanupErrors.Count > 0)
            {
                return new ProjectReviewCleanupResult(
                    false,
                    ReviewProblem(
                        "reviewStagingCleanupFailed",
                        null,
                        $"One or more exact owned review staging paths could not be removed; ownership was retained for retry: {string.Join(" | ", cleanupErrors)}"));
            }

            File.Delete(ownershipPath);
            if (File.Exists(ownershipPath))
            {
                return new ProjectReviewCleanupResult(
                    false,
                    ReviewProblem(
                        "reviewStagingCleanupFailed",
                        RelativePath(singlePaths.ProjectRoot, ownershipPath),
                        "The exact review staging paths were removed, but their ownership marker remains."));
            }

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
                ProjectReviewArtifactRole.Target =>
                    artifact.Manifest.Kind is ProjectInspectionReport.SmapiMod
                        or ProjectInspectionReport.ContentPack,
                ProjectReviewArtifactRole.Companion =>
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
            if (artifact.Manifest.ContentPackFor is not null)
            {
                ProjectReviewPreparedArtifact? provider = artifacts.SingleOrDefault(candidate =>
                    string.Equals(
                        candidate.Manifest.UniqueId,
                        artifact.Manifest.ContentPackFor,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        candidate.Manifest.Kind,
                        ProjectInspectionReport.SmapiMod,
                        StringComparison.Ordinal));
                bool targetContentPack = string.Equals(
                    artifact.Role,
                    ProjectReviewArtifactRole.Target,
                    StringComparison.Ordinal);
                if (provider is null
                    || (targetContentPack
                        && !string.Equals(
                            provider.Role,
                            ProjectReviewArtifactRole.Companion,
                            StringComparison.Ordinal)))
                {
                    return ReviewProblem(
                        "reviewDependencyUnavailable",
                        artifact.SourceRoot,
                        $"{artifact.Manifest.UniqueId} requires explicit local provider {artifact.Manifest.ContentPackFor}"
                            + (targetContentPack
                                ? " as --companion; SDVKit does not search for or download it."
                                : "; SDVKit does not search for or download it."));
                }

                if (!IsMinimumVersionSatisfied(
                        provider.Manifest.Version,
                        artifact.Manifest.ContentPackForMinimumVersion))
                {
                    return ReviewProblem(
                        "reviewDependencyVersionMismatch",
                        artifact.SourceRoot,
                        $"{artifact.Manifest.UniqueId} requires {artifact.Manifest.ContentPackFor} >= {artifact.Manifest.ContentPackForMinimumVersion}, but the explicit set provides {provider.Manifest.Version}.");
                }
            }

            foreach (ProjectReviewDependency dependency in artifact.Manifest.RequiredDependencies)
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

    private static ProjectReviewStagingResult ReadReviewOwnership(
        LiveLabPaths singlePaths,
        string topology,
        bool allowMissingStagingPaths,
        bool requireContentIdentity)
    {
        string ownershipPath = ReviewOwnershipPath(singlePaths, topology);
        try
        {
            using FileStream stream = new(
                ownershipPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            ProjectReviewStaging? staging = JsonSerializer.Deserialize<ProjectReviewStaging>(
                stream,
                JsonOptions);
            if (staging is null)
            {
                throw new InvalidDataException(
                    "The retained project-review ownership marker is empty.");
            }

            staging = staging with { OwnershipPath = ownershipPath };
            ProjectReviewProblem? validation = ValidateOwnedReview(
                staging,
                singlePaths,
                topology,
                allowMissingStagingPaths,
                requireContentIdentity);
            return validation is null
                ? new ProjectReviewStagingResult(staging, null)
                : new ProjectReviewStagingResult(null, validation);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return ReviewFailure(
                "reviewStagingOwnershipInvalid",
                RelativePath(singlePaths.ProjectRoot, ownershipPath),
                $"The retained project-review staging could not be proven as SDVKit-owned: {exception.Message}");
        }
    }

    private static ProjectReviewProblem? ValidateOwnedReview(
        ProjectReviewStaging staging,
        LiveLabPaths singlePaths,
        string topology,
        bool allowMissingStagingPaths,
        bool requireContentIdentity)
    {
        ReviewRolePaths[] expectedRoles = ResolveReviewRolePaths(singlePaths, topology);
        if (staging.SchemaVersion != ReviewOwnershipSchemaVersion
            || !string.Equals(staging.Topology, topology, StringComparison.Ordinal)
            || staging.Artifacts is null
            || staging.Artifacts.Count == 0
            || staging.Artifacts.Any(artifact => artifact is null)
            || staging.Artifacts.Count(artifact => string.Equals(
                artifact.Role,
                ProjectReviewArtifactRole.Target,
                StringComparison.Ordinal)) != 1
            || staging.Artifacts.Any(artifact => artifact.Manifest is null
                || artifact.RoleStagingPaths is null
                || artifact.RoleStagingPaths.Count != expectedRoles.Length
                || artifact.RoleStagingPaths.Any(path => path is null)
                || !IsSafeSegment(artifact.TopLevelDirectory)
                || !ModBuildIdentity.IsValid(artifact.BuildIdentity)
                || artifact.CpRefresh is not null && (topology != LiveLabState.SingleTopology
                    || artifact.Role != ProjectReviewArtifactRole.Target
                    || !string.Equals(artifact.Manifest.ContentPackFor, ProjectReviewCpDiagnosis.ProviderId, StringComparison.OrdinalIgnoreCase)
                    || !ModBuildIdentity.IsValid(artifact.CpRefresh.StagedBuildIdentity)
                    || !ModBuildIdentity.IsValid(artifact.CpRefresh.PreviousBuildIdentity)
                    || !Guid.TryParseExact(artifact.CpRefresh.LaunchId, "N", out _)
                    || !Guid.TryParseExact(artifact.CpRefresh.RefreshId, "N", out _)
                    || !ProjectReviewCpRefresh.ValidFiles(artifact.CpRefresh.Files))
                || artifact.ProjectFile is not null && (artifact.Manifest.Kind != ProjectInspectionReport.SmapiMod
                    || !Path.IsPathFullyQualified(artifact.ProjectFile)
                    || !IsBelow(artifact.SourceRoot, artifact.ProjectFile)
                    || !string.Equals(Path.GetExtension(artifact.ProjectFile), ".csproj", StringComparison.OrdinalIgnoreCase))
                || !IsValidOwnedReviewArtifact(artifact)))
        {
            return ReviewProblem(
                "reviewStagingOwnershipInvalid",
                null,
                "The retained project-review ownership marker is structurally invalid.");
        }

        ProjectReviewRoleStagingPath[] allStagingPaths = staging.Artifacts
            .SelectMany(artifact => artifact.RoleStagingPaths)
            .ToArray();
        if (allStagingPaths.Select(path => path.StagingPath)
                .Distinct(PathComparer()).Count() != allStagingPaths.Length
            || staging.Artifacts.Select(artifact => artifact.Manifest.UniqueId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != staging.Artifacts.Count)
        {
            return ReviewProblem(
                "reviewStagingOwnershipInvalid",
                null,
                "The retained project-review ownership marker contains duplicate artifacts or paths.");
        }

        if (File.Exists(SmokeOwnershipPath(singlePaths, topology)))
        {
            return ReviewProblem(
                "smokeStagingOwnershipPresent",
                RelativePath(
                    singlePaths.ProjectRoot,
                    SmokeOwnershipPath(singlePaths, topology)),
                "A retained project-smoke staging blocks project review.");
        }

        var expectedPathsByRole = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal);
        foreach (ReviewRolePaths expectedRole in expectedRoles)
        {
            expectedPathsByRole.Add(
                expectedRole.Role,
                new HashSet<string>(PathComparer()));
        }

        foreach (ProjectReviewOwnedArtifact artifact in staging.Artifacts)
        {
            if (artifact.RoleStagingPaths.Select(path => path.Role)
                    .Distinct(StringComparer.Ordinal).Count() != expectedRoles.Length)
            {
                return ReviewProblem(
                    "reviewStagingOwnershipInvalid",
                    null,
                    "A retained project-review artifact does not contain exactly one path for each topology role.");
            }

            foreach (ReviewRolePaths expectedRole in expectedRoles)
            {
                ProjectReviewRoleStagingPath? roleStaging = artifact.RoleStagingPaths
                    .SingleOrDefault(path => string.Equals(
                        path.Role,
                        expectedRole.Role,
                        StringComparison.Ordinal));
                if (roleStaging is null)
                {
                    return ReviewProblem(
                        "reviewStagingOwnershipInvalid",
                        null,
                        "A retained project-review artifact does not contain exactly one path for each topology role.");
                }

                string stagingPath = Path.GetFullPath(roleStaging.StagingPath);
                if (!PathEquals(Path.GetDirectoryName(stagingPath)!, expectedRole.Paths.ModsPath)
                    || !string.Equals(
                        Path.GetFileName(stagingPath),
                        artifact.TopLevelDirectory,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal)
                    || PathEquals(stagingPath, expectedRole.Paths.AlwaysOnModPath)
                    || File.Exists(stagingPath))
                {
                    return ReviewProblem(
                        "reviewStagingOwnershipInvalid",
                        null,
                        "A retained review staging path is unsafe or outside its exact isolated role mod group.");
                }

                expectedPathsByRole[expectedRole.Role].Add(stagingPath);
                if (!Directory.Exists(stagingPath))
                {
                    if (allowMissingStagingPaths)
                    {
                        continue;
                    }

                    return ReviewProblem(
                        "reviewStagingOwnershipInvalid",
                        null,
                        "A retained review staging path is missing from its exact isolated role mod group.");
                }

                if ((File.GetAttributes(stagingPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return ReviewProblem(
                        "reviewStagingOwnershipInvalid",
                        null,
                        "A retained review staging path is a reparse point and was left untouched.");
                }

                LiveLabPaths.RejectReparsePointsBelow(stagingPath);
                if (requireContentIdentity)
                {
                    ProjectReviewManifest? manifest = ReadReviewManifest(
                        Path.Combine(stagingPath, "manifest.json"),
                        allowVersionToken: false,
                        out _);
                    if (manifest is null
                        || !OwnedManifestMatchesFresh(artifact.Manifest, manifest)
                        || !ModBuildIdentity.MatchesFileSet(
                            stagingPath,
                            artifact.StagedBuildIdentity,
                            allowNewRootConfigJson: string.Equals(
                                artifact.Manifest.Kind,
                                ProjectInspectionReport.SmapiMod,
                                StringComparison.Ordinal)))
                    {
                        return ReviewProblem(
                            "reviewStagingOwnershipDrifted",
                            null,
                            $"The retained {expectedRole.Role} project-review staging differs from its ownership marker and was left untouched."
                            + (artifact.CpRefresh?.RequiresRestart == true ? " An incomplete CP refresh requires exact project review stop, reset, and start; do not retry reload." : ""));
                    }
                }
            }
        }

        foreach (ReviewRolePaths expectedRole in expectedRoles)
        {
            if (!Directory.Exists(expectedRole.Paths.ModsPath))
            {
                continue;
            }

            string? foreignEntry = Directory
                .EnumerateFileSystemEntries(expectedRole.Paths.ModsPath)
                .FirstOrDefault(entry => !PathEquals(
                        entry,
                        expectedRole.Paths.AlwaysOnModPath)
                    && !expectedPathsByRole[expectedRole.Role].Contains(
                        Path.GetFullPath(entry)));
            if (foreignEntry is not null)
            {
                return ReviewProblem(
                    "foreignLabModCollision",
                    RelativePath(singlePaths.ProjectRoot, foreignEntry),
                    $"The isolated {expectedRole.Role} mod group contains an artifact outside the exact retained project-review ownership set; it was left untouched.");
            }
        }

        return null;
    }

    private static bool IsValidOwnedReviewArtifact(
        ProjectReviewOwnedArtifact artifact)
    {
        ProjectReviewManifest manifest = artifact.Manifest;
        if (!IsModId(manifest.UniqueId)
            || !IsSemanticVersion(manifest.Version, allowToken: false))
        {
            return false;
        }

        return artifact.Role switch
        {
            ProjectReviewArtifactRole.Target =>
                IsCodeModManifest(manifest) || IsContentPackManifest(manifest),
            ProjectReviewArtifactRole.Companion => IsCodeModManifest(manifest),
            ProjectReviewArtifactRole.ContentPack => IsContentPackManifest(manifest),
            _ => false,
        };
    }

    private static bool IsCodeModManifest(ProjectReviewManifest manifest) =>
        string.Equals(
            manifest.Kind,
            ProjectInspectionReport.SmapiMod,
            StringComparison.Ordinal)
        && manifest.ContentPackFor is null
        && manifest.ContentPackForMinimumVersion is null;

    private static bool IsContentPackManifest(ProjectReviewManifest manifest) =>
        string.Equals(
            manifest.Kind,
            ProjectInspectionReport.ContentPack,
            StringComparison.Ordinal)
        && IsModId(manifest.ContentPackFor)
        && (manifest.ContentPackForMinimumVersion is null
            || IsSemanticVersion(
                manifest.ContentPackForMinimumVersion,
                allowToken: false));

    private static bool OwnedManifestMatchesFresh(
        ProjectReviewManifest owned,
        ProjectReviewManifest fresh) =>
        string.Equals(owned.Kind, fresh.Kind, StringComparison.Ordinal)
        && string.Equals(
            owned.UniqueId,
            fresh.UniqueId,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(owned.Version, fresh.Version, StringComparison.Ordinal)
        && string.Equals(
            owned.ContentPackFor,
            fresh.ContentPackFor,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            owned.ContentPackForMinimumVersion,
            fresh.ContentPackForMinimumVersion,
            StringComparison.Ordinal);

    internal static void WriteReviewOwnership(
        string path,
        ProjectReviewStaging staging,
        bool replace = false)
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

            if (File.Exists(path)) RefuseReparsePoint(path);
            File.Move(temporary, path, overwrite: replace);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static ReviewRolePaths[] ResolveReviewRolePaths(
        LiveLabPaths singlePaths,
        string topology)
    {
        if (string.Equals(
                topology,
                LiveLabState.SingleTopology,
                StringComparison.Ordinal))
        {
            return [new ReviewRolePaths(LiveLabState.SingleTopology, singlePaths)];
        }

        if (!string.Equals(
                topology,
                NetworkTwoContract.Topology,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported project-review topology: {topology}");
        }

        return
        [
            new ReviewRolePaths(
                NetworkTwoContract.HostRole,
                LiveLabPaths.ResolveNetworkRole(
                    singlePaths,
                    NetworkTwoContract.HostRole)),
            new ReviewRolePaths(
                NetworkTwoContract.FarmhandRole,
                LiveLabPaths.ResolveNetworkRole(
                    singlePaths,
                    NetworkTwoContract.FarmhandRole)),
        ];
    }

    private static string ReviewTopologyRoot(
        LiveLabPaths singlePaths,
        string topology)
    {
        if (string.Equals(
                topology,
                LiveLabState.SingleTopology,
                StringComparison.Ordinal))
        {
            return singlePaths.SingleRoot;
        }

        if (string.Equals(
                topology,
                NetworkTwoContract.Topology,
                StringComparison.Ordinal))
        {
            return Path.Combine(
                singlePaths.ProjectRoot,
                ".sdvkit",
                "lab",
                NetworkTwoContract.Topology);
        }

        throw new InvalidDataException(
            $"Unsupported project-review topology: {topology}");
    }

    private static string ReviewOwnershipPath(
        LiveLabPaths singlePaths,
        string topology) =>
        Path.Combine(
            ReviewTopologyRoot(singlePaths, topology),
            ReviewOwnershipFileName);

    private static string SmokeOwnershipPath(
        LiveLabPaths singlePaths,
        string topology) =>
        Path.Combine(
            ReviewTopologyRoot(singlePaths, topology),
            OwnershipFileName);

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

    private sealed record ReviewRolePaths(
        string Role,
        LiveLabPaths Paths);
}
