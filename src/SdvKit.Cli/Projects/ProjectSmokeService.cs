using System.Security;
using System.Text;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static class ProjectSmokeService
{
    private const int Success = 0;
    private const int OperationFailed = 3;
    private const string SingleRole = "single";

    private static readonly string[] Warnings =
    [
        "The build identity hashes the controlled staged package file set; it is echoed by the game-side marker, not measured from the runtime DLL in memory.",
        "A passed project smoke proves that SMAPI loaded the expected UniqueID and version and completed the bounded 120-tick smoke; it does not prove that every mod feature is functionally correct.",
        "SDVKit controls the isolated SMAPI mod group, exact disposable fixture, and project-owned Stardew data root; it does not select personal data or the normal Mods directory, but the tested mod is not sandboxed.",
    ];

    public static LiveLabCommandResult Execute(
        string sourcePath,
        string topology,
        string labRoot,
        Func<DoctorReport> discoverInstallations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        ProjectInspectionReport inspection = ProjectInspector.Inspect(sourcePath);
        string reportRoot = inspection.Root;
        string reportLabRoot = SafeFullPath(labRoot);
        if (inspection.Problems.Count > 0)
        {
            return Failure(
                reportRoot,
                reportLabRoot,
                topology,
                "failed",
                inspection.Problems.Select(problem => FromProjectProblem(
                    problem,
                    "Project inspection failed.")));
        }

        if (!string.Equals(
                inspection.Kind,
                ProjectInspectionReport.SmapiMod,
                StringComparison.Ordinal)
            || inspection.Manifests.Count != 1)
        {
            return Failure(
                reportRoot,
                reportLabRoot,
                topology,
                "unsupported",
                [Problem(
                    "unsupportedProjectKind",
                    null,
                    "Project smoke V1 supports exactly one standalone SMAPI C# code-mod manifest; content packs and hybrid projects are unsupported.")]);
        }

        ModBuildTargetResolution resolution = ProjectBuilder.ResolveTarget(reportRoot);
        if (resolution.Target is null)
        {
            return Failure(
                reportRoot,
                reportLabRoot,
                topology,
                "unsupported",
                resolution.Problems.Select(problem => FromProjectProblem(
                    problem,
                    "Project smoke V1 requires exactly one C# project paired with the one code-mod manifest.")));
        }

        ProjectModManifestReadResult sourceManifest =
            ProjectModStager.ReadSourceManifest(resolution.Target);
        if (sourceManifest.Manifest is null)
        {
            return Failure(
                reportRoot,
                reportLabRoot,
                topology,
                "unsupported",
                [sourceManifest.Problem ?? Problem(
                    "invalidProjectModManifest",
                    resolution.Target.Manifest.Path,
                    "The project manifest is invalid for project smoke.")]);
        }

        if (string.Equals(
                sourceManifest.Manifest.UniqueId,
                "SDVKit.AlwaysOn",
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                reportRoot,
                reportLabRoot,
                topology,
                "unsupported",
                [Problem(
                    "reservedModIdentity",
                    resolution.Target.Manifest.Path,
                    "Project smoke cannot target the reserved SDVKit.AlwaysOn mod identity.")]);
        }

        string[] unavailableDependencies =
            ProjectModStager.FindUnavailableRequiredDependencies(sourceManifest.Manifest);
        if (unavailableDependencies.Length > 0)
        {
            return Failure(
                reportRoot,
                reportLabRoot,
                topology,
                "unsupported",
                [Problem(
                    "runtimeDependencyUnavailable",
                    resolution.Target.Manifest.Path,
                    $"Required runtime dependencies are not provided by the isolated lab: {string.Join(", ", unavailableDependencies)}. SDVKit does not acquire dependencies automatically.")]);
        }

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(labRoot);
            reportLabRoot = paths.ProjectRoot;
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                reportRoot,
                reportLabRoot,
                topology,
                "blocked",
                [Problem("labPathInvalid", null, exception.Message)]);
        }

        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(paths.ProjectRoot);
            if (operationLock is null)
            {
                return Failure(
                    reportRoot,
                    reportLabRoot,
                    topology,
                    "blocked",
                    [Problem(
                        "labBusy",
                        null,
                        "Another live-lab operation is still running for this lab root.")]);
            }

            ProjectSmokeProblem? retained = RetainedLabProblem(paths);
            if (retained is not null)
            {
                return Failure(
                    reportRoot,
                    reportLabRoot,
                    topology,
                    "blocked",
                    [retained]);
            }

            DoctorReport doctor = discoverInstallations();
            Func<DoctorReport> frozenDoctor = () => doctor;
            ProjectBuildReport build = ProjectBuilder.Build(reportRoot, frozenDoctor);
            if (build.Problems.Count > 0)
            {
                return Failure(
                    reportRoot,
                    reportLabRoot,
                    topology,
                    "failed",
                    build.Problems.Select(problem => FromProjectProblem(
                        problem,
                        "The isolated Release build failed.")));
            }

            ProjectPackageReport package = ProjectPackager.Package(reportRoot, frozenDoctor);
            if (package.Problems.Count > 0)
            {
                return Failure(
                    reportRoot,
                    reportLabRoot,
                    topology,
                    "failed",
                    package.Problems.Select(problem => FromProjectProblem(
                        problem,
                        "The validated Release package could not be produced.")));
            }

            ProjectModStagingResult staged = ProjectModStager.Stage(
                package,
                resolution.Target,
                topology,
                paths);
            if (staged.Staging is null)
            {
                return Failure(
                    reportRoot,
                    reportLabRoot,
                    topology,
                    staged.Problem?.Code is "modStagingCollision"
                        or "modIdentityCollision"
                        or "foreignLabModCollision"
                        or "stagingOwnershipInvalid"
                        or "stagingOwnershipDrifted"
                        or "stagingOwnershipPresent"
                        or "reservedModStagingPath"
                        or "preparedStagingCleanupIncomplete"
                        or "projectStagingRollbackIncomplete"
                        ? "blocked"
                        : "failed",
                    [staged.Problem ?? Problem(
                        "projectStagingFailed",
                        null,
                        "The validated package could not be staged in the isolated lab.")],
                    stagingRemoved: staged.Problem?.Code
                        is not ("stagingOwnershipInvalid"
                            or "stagingOwnershipDrifted"
                            or "stagingOwnershipPresent"
                            or "preparedStagingCleanupIncomplete"
                            or "projectStagingRollbackIncomplete"));
            }

            try
            {
                return RunStaged(
                    reportRoot,
                    reportLabRoot,
                    topology,
                    paths,
                    build,
                    package,
                    staged.Staging,
                    frozenDoctor);
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                return Failure(
                    reportRoot,
                    reportLabRoot,
                    topology,
                    "blocked",
                    [
                        Problem("projectSmokeFailed", null, exception.Message),
                        Problem(
                            "projectStagingCleanupDeferred",
                            null,
                            "The live-lab outcome is uncertain, so the exact owned target staging was retained."),
                    ],
                    stagingRemoved: false);
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                reportRoot,
                reportLabRoot,
                topology,
                "failed",
                [Problem("projectSmokeFailed", null, exception.Message)]);
        }
    }

    private static LiveLabCommandResult RunStaged(
        string root,
        string labRoot,
        string topology,
        LiveLabPaths paths,
        ProjectBuildReport build,
        ProjectPackageReport package,
        ProjectModStaging staging,
        Func<DoctorReport> discoverInstallations)
    {
        LiveLabCommandResult labResult;
        AlwaysOnStatusReport? singleAlwaysOn = null;
        if (string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal))
        {
            var service = new LiveLabService(
                paths,
                new JsonLiveLabStateStore(paths.StatePath),
                new AlwaysOnBuilder(),
                new WindowsLabProcessHost(),
                discoverInstallations);
            labResult = service.RunProjectTestSave(staging.LaunchState);
            singleAlwaysOn = service.LastAlwaysOn;
        }
        else
        {
            labResult = NetworkTwoSmokeService.ExecuteWithinLock(
                paths.ProjectRoot,
                discoverInstallations,
                staging.LaunchState);
        }

        IReadOnlyList<ProjectSmokeRoleReport> roles = CreateRoles(
            labResult.Report,
            singleAlwaysOn,
            staging,
            labRoot);
        bool fixtureReset = labResult.Report switch
        {
            TestSaveWorkflowReport single => labResult.ExitCode == Success
                && string.Equals(single.State, "passed", StringComparison.Ordinal),
            NetworkTwoSmokeReport network => network.FixtureReset,
            _ => false,
        };
        var problems = LabProblems(labResult.Report).ToList();
        bool mayRemove = !HasRetainedLabState(paths)
            && (labResult.ExitCode == Success
                || CanSafelyRemoveAfterFailure(problems));
        ProjectModCleanupResult cleanup = mayRemove
            ? ProjectModStager.Remove(staging)
            : new ProjectModCleanupResult(
                false,
                Problem(
                    "projectStagingCleanupDeferred",
                    null,
                    "The exact lab processes are not all confirmed stopped, so the target staging was retained."));
        if (cleanup.Problem is not null)
        {
            problems.Add(cleanup.Problem);
        }

        string[] logPaths = roles
            .SelectMany(role => role.LogPaths)
            .Distinct(PathComparer())
            .ToArray();
        string[] loadErrors = ReadTargetLoadErrors(
            labRoot,
            staging.Artifact,
            logPaths,
            paths,
            topology);
        bool rolesPassed = roles.Count == (topology == LiveLabState.SingleTopology ? 1 : 2)
            && roles.All(role => role.LoadConfirmed
                && role.ObservedTicks >= role.RequiredTicks
                && string.Equals(
                    role.StagedBuildIdentity,
                    staging.Artifact.BuildIdentity,
                    StringComparison.Ordinal));
        bool passed = labResult.ExitCode == Success
            && fixtureReset
            && rolesPassed
            && cleanup.Removed
            && problems.Count == 0;
        string state = passed
            ? "passed"
            : (!mayRemove || !cleanup.Removed || HasRetainedLabState(paths))
                ? "blocked"
                : "failed";

        var artifact = new ProjectSmokeArtifactReport(
            staging.Artifact.Manifest.UniqueId,
            staging.LaunchState.Version,
            staging.Artifact.Manifest.Version,
            staging.Artifact.ArchiveRelativePath,
            staging.Artifact.Entries,
            staging.Artifact.PackageHash,
            staging.Artifact.BuildIdentity,
            build.Log,
            package.Log);
        var report = new ProjectSmokeReport(
            1,
            root,
            labRoot,
            topology,
            state,
            artifact,
            roles,
            fixtureReset,
            cleanup.Removed,
            loadErrors,
            problems,
            MergeLabWarnings(labResult.Report));
        return new LiveLabCommandResult(passed ? Success : OperationFailed, report);
    }

    internal static IReadOnlyList<string> MergeLabWarnings(object report)
    {
        IEnumerable<string> labWarnings = report switch
        {
            TestSaveWorkflowReport single => single.Warnings,
            NetworkTwoSmokeReport network => network.Warnings
                .Concat(network.Host.Warnings)
                .Concat(network.Farmhand.Warnings),
            _ => [],
        };

        return Warnings
            .Concat(labWarnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ProjectSmokeRoleReport> CreateRoles(
        object report,
        AlwaysOnStatusReport? singleAlwaysOn,
        ProjectModStaging staging,
        string labRoot)
    {
        if (report is TestSaveWorkflowReport single)
        {
            return
            [
                Role(
                    SingleRole,
                    single.State,
                    staging.StagingPaths[0],
                    staging.Artifact.BuildIdentity,
                    singleAlwaysOn?.ProjectMod,
                    single.ObservedTicks,
                    single.LogPaths,
                    labRoot),
            ];
        }

        if (report is NetworkTwoSmokeReport network && staging.StagingPaths.Count == 2)
        {
            return
            [
                Role(
                    NetworkTwoContract.HostRole,
                    network.Host.State,
                    staging.StagingPaths[0],
                    staging.Artifact.BuildIdentity,
                    network.Host.AlwaysOn?.ProjectMod,
                    network.Host.AlwaysOn?.NetworkTwo?.JoinedTicks,
                    network.Host.LogPaths,
                    labRoot),
                Role(
                    NetworkTwoContract.FarmhandRole,
                    network.Farmhand.State,
                    staging.StagingPaths[1],
                    staging.Artifact.BuildIdentity,
                    network.Farmhand.AlwaysOn?.ProjectMod,
                    network.Farmhand.AlwaysOn?.NetworkTwo?.JoinedTicks,
                    network.Farmhand.LogPaths,
                    labRoot),
            ];
        }

        return [];
    }

    private static ProjectSmokeRoleReport Role(
        string role,
        string state,
        string stagingPath,
        string buildIdentity,
        ProjectModStatusReport? projectMod,
        int? observedTicks,
        IReadOnlyList<string> logPaths,
        string labRoot)
    {
        return new ProjectSmokeRoleReport(
            role,
            state,
            RelativePath(labRoot, stagingPath),
            buildIdentity,
            projectMod?.State == "ready"
                && projectMod.Phase == ProjectModContract.LoadedPhase
                && projectMod.LoadConfirmed == true,
            projectMod?.LoadedUniqueId,
            projectMod?.LoadedVersion,
            TestSaveContract.RequiredScenarioTicks,
            observedTicks,
            logPaths.Select(path => RelativePath(labRoot, path))
                .Distinct(PathComparer())
                .ToArray());
    }

    private static IEnumerable<ProjectSmokeProblem> LabProblems(object report)
    {
        return report switch
        {
            TestSaveWorkflowReport single => single.Problems.Select(problem =>
                Problem(problem.Code, null, problem.Message)),
            NetworkTwoSmokeReport network => network.Problems.Select(problem =>
                Problem(problem.Code, null, problem.Message)),
            LiveLabReport live => live.Problems.Select(problem =>
                Problem(problem.Code, null, problem.Message)),
            _ => [Problem(
                "projectSmokeLabResultInvalid",
                null,
                "The reused live-lab workflow returned an unexpected report type.")],
        };
    }

    private static bool CanSafelyRemoveAfterFailure(
        IReadOnlyList<ProjectSmokeProblem> problems)
    {
        string[] unsafeFragments =
        [
            "ownership",
            "processUnreadable",
            "unverifiedChild",
            "cleanupDeferred",
            "cleanStop",
            "CleanupUnconfirmed",
            "StopFailed",
        ];
        return !problems.Any(problem => unsafeFragments.Any(fragment =>
            problem.Code.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static ProjectSmokeProblem? RetainedLabProblem(LiveLabPaths paths)
    {
        return HasRetainedLabState(paths)
            ? Problem(
                "labNotStopped",
                null,
                "Project smoke requires every retained process in the selected isolated lab topology to be stopped first.")
            : null;
    }

    private static bool HasRetainedLabState(LiveLabPaths paths)
    {
        if (new JsonLiveLabStateStore(paths.StatePath).Read() is not null)
        {
            return true;
        }

        LiveLabPaths host = LiveLabPaths.ResolveNetworkRole(paths, NetworkTwoContract.HostRole);
        LiveLabPaths farmhand = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        return new JsonLiveLabStateStore(host.StatePath).Read() is not null
            || new JsonLiveLabStateStore(farmhand.StatePath).Read() is not null;
    }

    private static string[] ReadTargetLoadErrors(
        string labRoot,
        ProjectModArtifact artifact,
        IReadOnlyList<string> reportedLogPaths,
        LiveLabPaths paths,
        string topology)
    {
        var candidates = new List<string>();
        candidates.AddRange(reportedLogPaths.Select(path => Path.GetFullPath(
            FromSlashPath(path),
            labRoot)));
        candidates.Add(paths.StandardOutputPath);
        candidates.Add(paths.StandardErrorPath);
        if (string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal))
        {
            foreach (string role in new[]
            {
                NetworkTwoContract.HostRole,
                NetworkTwoContract.FarmhandRole,
            })
            {
                LiveLabPaths rolePaths = LiveLabPaths.ResolveNetworkRole(paths, role);
                candidates.Add(rolePaths.StandardOutputPath);
                candidates.Add(rolePaths.StandardErrorPath);
            }
        }

        string[] exactTargetTokens =
        [
            artifact.Manifest.UniqueId,
            artifact.Manifest.EntryDll,
        ];
        string[] boundedTargetTokens =
        [
            artifact.Manifest.Name,
            artifact.TopLevelDirectory,
        ];
        string[] errorTokens =
        [
            "error",
            "failed",
            "skipped",
            "couldn't",
            "could not",
            "exception",
            "incompatible",
            "missing",
            "requires",
            "not loaded",
        ];
        var matches = new List<string>();
        foreach (string path in candidates.Distinct(PathComparer()))
        {
            if (!File.Exists(path)
                || (!path.EndsWith("stdout.log", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith("stderr.log", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                foreach (string line in ReadLogLines(path))
                {
                    if ((exactTargetTokens.Any(token =>
                                ContainsDelimitedToken(
                                    line,
                                    token,
                                    identityToken: true))
                            || boundedTargetTokens.Any(token =>
                                token.Length >= 4
                                && ContainsDelimitedToken(
                                    line,
                                    token,
                                    identityToken: false)))
                        && errorTokens.Any(token => line.Contains(
                            token,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        matches.Add($"{RelativePath(labRoot, path)}: {line.Trim()}");
                        if (matches.Count >= 30)
                        {
                            return matches.Distinct(StringComparer.Ordinal).ToArray();
                        }
                    }
                }
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                // Missing diagnostics never replace the primary launch-bound status proof.
            }
        }

        return matches.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> ReadLogLines(string path)
    {
        byte[] prefix = new byte[512];
        int count;
        using (FileStream probe = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            count = probe.Read(prefix, 0, prefix.Length);
        }

        int oddNulls = Enumerable.Range(1, Math.Max(0, count - 1) / 2)
            .Count(index => prefix[(index * 2) - 1] == 0);
        bool utf16LittleEndian = count >= 4
            && oddNulls >= Math.Max(1, count / 8);
        Encoding encoding = utf16LittleEndian
            ? Encoding.Unicode
            : new UTF8Encoding(false, throwOnInvalidBytes: false);
        return File.ReadLines(path, encoding);
    }

    internal static bool ContainsDelimitedToken(
        string text,
        string token,
        bool identityToken)
    {
        int start = 0;
        while ((start = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + token.Length;
            bool leftDelimited = start == 0
                || !IsTokenCharacter(text[start - 1], identityToken);
            bool rightDelimited = end == text.Length
                || !IsTokenCharacter(text[end], identityToken);
            if (leftDelimited && rightDelimited)
            {
                return true;
            }

            start++;
        }

        return false;
    }

    private static bool IsTokenCharacter(char value, bool identityToken) =>
        char.IsLetterOrDigit(value)
        || (identityToken && value is '_' or '.' or '-');

    private static LiveLabCommandResult Failure(
        string root,
        string labRoot,
        string topology,
        string state,
        IEnumerable<ProjectSmokeProblem> problems,
        bool stagingRemoved = true)
    {
        var report = new ProjectSmokeReport(
            1,
            root,
            labRoot,
            topology,
            state,
            null,
            [],
            false,
            stagingRemoved,
            [],
            problems.ToArray(),
            Warnings);
        return new LiveLabCommandResult(OperationFailed, report);
    }

    private static ProjectSmokeProblem FromProjectProblem(
        ProjectProblem problem,
        string message) =>
        Problem(problem.Code, problem.Path, message);

    private static ProjectSmokeProblem Problem(string code, string? path, string message) =>
        new(code, path, message);

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return path;
        }
    }

    private static string RelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string absolute = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(FromSlashPath(path), root);
        return Path.GetRelativePath(root, absolute)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string FromSlashPath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar);

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or PlatformNotSupportedException
            or SecurityException
            or UnauthorizedAccessException
            or JsonException;
}
