using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectReviewService
{
    private const int Success = 0;
    private const int OperationFailed = 3;

    private static readonly string[] Warnings =
    [
        "Project review stages only the explicitly selected local target, companions, and content packs; SDVKit does not search for or download dependencies.",
        "The SMAPI process uses a separate interactive console, so stdout/stderr are not captured by SDVKit; SMAPI's own log and screenshots remain in the isolated single-role profile below .sdvkit.",
        "Review saves persist in the isolated single-role profile across process restarts. Normal saves and the normal or mod-manager-owned Mods directory are not selected or modified.",
        "When --test-save is selected, only the registered SDVKit-owned Work-Copy is mounted and loaded. A clean stop preserves it for restart; explicit single reset restores its registered baseline.",
        "This is process-level data isolation, not a Windows sandbox; reviewed mods can still access shared machine resources.",
    ];

    private static readonly string[] CommandWarnings =
    [
        "commandWritten=true confirms that one complete text line plus Enter was enqueued into the exact owned SMAPI console; it does not confirm that SMAPI accepted or completed the command.",
        "Submit console input only at an idle SMAPI prompt and do not type concurrently; classic Windows console input cannot prove that no partially typed cooked line already exists.",
    ];

    private static readonly string[] NetworkWarnings =
    [
        "This review is limited to exactly one local network-2 host and one local farmhand against the owned disposable fixture; it does not prove general multiplayer compatibility.",
        "AlwaysOn is required in both roles. Each role uses its own project-local Stardew data root, saves path, standard SMAPI log, and exact copy of the selected review set below .sdvkit.",
        "A clean network-2 review stop preserves the owned work fixture and exact staging for a real pair restart. Reset is explicit, requires both roles stopped, restores the baseline, and removes both role staging sets.",
        "This is process-level data isolation, not a Windows sandbox; reviewed mods can still access shared machine resources.",
    ];

    public static LiveLabCommandResult Execute(
        string action,
        string sourcePath,
        IReadOnlyList<string> companionPaths,
        IReadOnlyList<string> contentPackPaths,
        string labRoot,
        Func<DoctorReport> discoverInstallations) =>
        Execute(
            action,
            sourcePath,
            companionPaths,
            contentPackPaths,
            LiveLabState.SingleTopology,
            labRoot,
            discoverInstallations);

    public static LiveLabCommandResult Execute(
        string action,
        string sourcePath,
        IReadOnlyList<string> companionPaths,
        IReadOnlyList<string> contentPackPaths,
        string topology,
        string labRoot,
        Func<DoctorReport> discoverInstallations,
        bool useTestSave = false,
        string? projectFile = null,
        string? gamePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(companionPaths);
        ArgumentNullException.ThrowIfNull(contentPackPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        Func<DoctorReport> originalDiscovery = discoverInstallations;
        var frozenDoctor = new Lazy<DoctorReport>(originalDiscovery);
        discoverInstallations = () => frozenDoctor.Value;

        if (topology is not (LiveLabState.SingleTopology or NetworkTwoContract.Topology))
        {
            return Failure(
                SafeFullPath(sourcePath),
                SafeFullPath(labRoot),
                "blocked",
                [Problem("reviewTopologyInvalid", null, $"Unsupported project-review topology: {topology}")]);
        }

        if (useTestSave
            && (!string.Equals(action, "start", StringComparison.Ordinal)
                || !string.Equals(
                    topology,
                    LiveLabState.SingleTopology,
                    StringComparison.Ordinal)))
        {
            IReadOnlyList<ProjectReviewProblem> problems = [Problem(
                "reviewTestSaveTopologyInvalid",
                null,
                "--test-save is available only for a single project-review start.")];
            return string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                ? NetworkFailure(SafeFullPath(sourcePath), SafeFullPath(labRoot), problems)
                : Failure(
                    SafeFullPath(sourcePath),
                    SafeFullPath(labRoot),
                    "blocked",
                    problems);
        }

        if (string.Equals(action, "start", StringComparison.Ordinal)
            && string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
            && projectFile is null
            && IsContentPackTargetCandidate(sourcePath))
        {
            return NetworkFailure(
                SafeFullPath(sourcePath),
                SafeFullPath(labRoot),
                [Problem(
                    "reviewTargetTopologyUnsupported",
                    SafeFullPath(sourcePath),
                    "Content-pack targets and ready bundles support only topology single; nothing was launched or changed.")]);
        }

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(labRoot);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            IReadOnlyList<ProjectReviewProblem> problems =
                [Problem("labPathInvalid", null, exception.Message)];
            return string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                ? NetworkFailure(
                    SafeFullPath(sourcePath),
                    SafeFullPath(labRoot),
                    problems)
                : Failure(
                    SafeFullPath(sourcePath),
                    SafeFullPath(labRoot),
                    "blocked",
                    problems);
        }

        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(paths.ProjectRoot);
            if (operationLock is null)
            {
                IReadOnlyList<ProjectReviewProblem> problems = [Problem(
                    "labBusy",
                    null,
                    "Another live-lab operation is still running for this lab root.")];
                return string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                    ? NetworkResult(paths, null, "blocked", null, fixtureReset: false, stagingRemoved: false, problems)
                    : Failure(
                        SafeFullPath(sourcePath),
                        paths.ProjectRoot,
                        "blocked",
                        problems,
                        paths);
            }

            if (action == "start" && gamePath is not null)
            {
                ProjectProblem? selectionProblem = ProjectBuilder.GetGamePath(discoverInstallations(), out string? selectedGame);
                LiveLabState?[] states = topology == NetworkTwoContract.Topology
                    ? [new JsonLiveLabStateStore(LiveLabPaths.ResolveNetworkRole(paths, NetworkTwoContract.HostRole).StatePath).Read(),
                       new JsonLiveLabStateStore(LiveLabPaths.ResolveNetworkRole(paths, NetworkTwoContract.FarmhandRole).StatePath).Read()]
                    : [new JsonLiveLabStateStore(paths.StatePath).Read()];
                if (selectionProblem is not null || states.Any(state => state is not null && !string.Equals(
                    Path.GetDirectoryName(state.OwnedProcessIdentity.ExecutablePath), selectedGame, PathComparison())))
                {
                    ProjectReviewProblem problem = Problem(selectionProblem?.Code ?? "reviewGameSelectionMismatch", gamePath,
                        "Select a complete installation matching the retained review roles; stop/reset before changing installations. Run doctor --game-path <directory> --json for missing requirements.");
                    return topology == NetworkTwoContract.Topology
                        ? NetworkFailure(SafeFullPath(sourcePath), paths.ProjectRoot, [problem])
                        : Failure(SafeFullPath(sourcePath), paths.ProjectRoot, "blocked", [problem], paths, stagingRemoved: false);
                }
            }

            if (string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal))
            {
                return action switch
                {
                    "start" => StartNetwork(
                        sourcePath,
                        companionPaths,
                        contentPackPaths,
                        paths,
                        discoverInstallations,
                        projectFile),
                    "status" => StatusNetwork(paths),
                    "stop" => StopNetwork(paths),
                    "reset" => ResetNetwork(paths),
                    _ => throw new ArgumentOutOfRangeException(nameof(action)),
                };
            }

            if (string.Equals(action, "reset", StringComparison.Ordinal))
            {
                return ResetSingle(paths);
            }

            var stateStore = new JsonLiveLabStateStore(paths.StatePath);
            var service = new LiveLabService(
                paths,
                stateStore,
                new AlwaysOnBuilder(),
                new WindowsLabProcessHost(),
                discoverInstallations);
            return action switch
            {
                "start" => Start(
                    sourcePath,
                    companionPaths,
                    contentPackPaths,
                    paths,
                    stateStore,
                    service,
                    discoverInstallations,
                    useTestSave,
                    projectFile),
                "status" => Status(paths, stateStore, service),
                "stop" => Stop(paths, stateStore, service),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            IReadOnlyList<ProjectReviewProblem> problems =
                [Problem("projectReviewFailed", null, exception.Message)];
            return string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                ? NetworkResult(
                    paths,
                    null,
                    "blocked",
                    null,
                    fixtureReset: false,
                    stagingRemoved: false,
                    problems)
                : Failure(
                    SafeFullPath(sourcePath),
                    paths.ProjectRoot,
                    "blocked",
                    problems,
                    paths,
                    stagingRemoved: false);
        }
    }

    internal static ProjectReviewProblem? RetainedReviewGameProblem(ProjectReviewStaging staging, DoctorReport doctor)
    {
        if (staging.GamePath is null)
            return Problem("reviewGameSelectionUnknown", null,
                "This retained review predates installation binding. Stop/reset the owned review and start again to rebuild it against one validated installation.");
        ProjectProblem? gameProblem = ProjectBuilder.GetGamePath(doctor, out string? gamePath);
        if (gameProblem is not null)
            return Problem(gameProblem.Code, null, "Select the retained review installation with --game-path; run doctor --json for candidates and corrective actions.");
        return string.Equals(staging.GamePath, gamePath, PathComparison()) ? null
            : Problem("reviewGameSelectionMismatch", gamePath,
                "The retained artifacts were prepared for another game installation. Restart with that installation, or reset before rebuilding for a different one.");
    }

    internal static ProjectReviewProblem? ReviewSetRequestProblem(
        string sourcePath,
        IReadOnlyList<string> companionPaths,
        IReadOnlyList<string> contentPackPaths,
        ProjectReviewStaging staging,
        string? projectFile)
    {
        string? selected = projectFile is null ? null : Path.GetFullPath(projectFile, Path.GetFullPath(sourcePath));
        if (!string.Equals(selected, staging.Target.ProjectFile, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Problem("reviewSetMismatch", projectFile, "The retained review uses a different explicit project selection; reset before changing it.");
        var requested = new List<(string Role, string Path)>
        {
            (ProjectReviewArtifactRole.Target, SafeFullPath(sourcePath)),
        };
        requested.AddRange(companionPaths.Select(path => (
            ProjectReviewArtifactRole.Companion,
            SafeFullPath(path))));
        requested.AddRange(contentPackPaths.Select(path => (
            ProjectReviewArtifactRole.ContentPack,
            SafeFullPath(path))));

        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string[] requestedSet = requested
            .Select(item => $"{item.Role}\0{Path.TrimEndingDirectorySeparator(item.Path)}")
            .OrderBy(value => value, comparer)
            .ToArray();
        string[] retainedSet = staging.Artifacts
            .Select(artifact =>
                $"{artifact.Role}\0{Path.TrimEndingDirectorySeparator(artifact.SourceRoot)}")
            .OrderBy(value => value, comparer)
            .ToArray();
        return requestedSet.SequenceEqual(retainedSet, comparer)
            ? null
            : Problem(
                "reviewSetMismatch",
                null,
                "A stopped or running network-2 review can be resumed only with the exact same target, companions, and content packs; nothing was changed.");
    }

    private static ProjectReviewProblem Problem(
        string code,
        string? path,
        string message) =>
        new(code, path, message);

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

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

    private static bool IsContentPackTargetCandidate(string sourcePath)
    {
        ProjectInspectionReport inspection = ProjectInspector.Inspect(sourcePath);
        return inspection.Manifests.Any(manifest => string.Equals(
            manifest.Kind,
            ProjectInspectionReport.ContentPack,
            StringComparison.Ordinal));
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool PathEqualsBundleRoot(string root, string pack) =>
        string.Equals(Path.TrimEndingDirectorySeparator(root),
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(pack)), PathComparison());

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
