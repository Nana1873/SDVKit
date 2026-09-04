using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal sealed record ProjectReviewMcpTarget(
    string UniqueId,
    string Version,
    string BuildIdentity);

internal sealed record ProjectReviewMcpTestSave(
    string FixtureId,
    string SaveId);

internal sealed record ProjectReviewMcpRuntime(
    int SchemaVersion,
    bool WorldReady,
    string? Season,
    int? DayOfMonth,
    int? Year,
    int? TimeOfDay,
    string? LocationId,
    int? TileX,
    int? TileY,
    bool MenuOpen);

internal sealed record ProjectReviewMcpRuntimeSnapshot(
    int SchemaVersion,
    string LaunchId,
    string Topology,
    string? Role,
    DateTimeOffset ObservedAtUtc,
    ProjectReviewMcpTarget Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ProjectReviewMcpTestSave? TestSave,
    ProjectReviewMcpRuntime Runtime);

internal sealed record ProjectReviewMcpReadResult(
    ProjectReviewMcpRuntimeSnapshot? Snapshot,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Snapshot is not null;
}

internal sealed record ProjectReviewMcpVerifiedContext(
    ProjectReviewStaging Staging,
    LiveLabState State,
    AlwaysOnStatusReport AlwaysOn,
    string? Role,
    ProjectReviewMcpTestSave? TestSave,
    bool AllTargetsReady);

internal sealed record ProjectReviewMcpContextResult(
    ProjectReviewMcpVerifiedContext? Context,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Context is not null;
}

internal sealed class ProjectReviewMcpRuntimeReader
{
    private readonly string _projectRoot;
    private readonly string _topology;
    private readonly string? _role;
    private readonly ILabProcessHost _processHost;
    private readonly Func<DateTimeOffset> _utcNow;

    internal string ProjectRoot => _projectRoot;

    internal string Topology => _topology;

    internal string? Role => _role;

    public ProjectReviewMcpRuntimeReader(
        string projectRoot,
        ILabProcessHost? processHost = null,
        Func<DateTimeOffset>? utcNow = null)
        : this(
            projectRoot,
            LiveLabState.SingleTopology,
            role: null,
            processHost,
            utcNow)
    {
    }

    public ProjectReviewMcpRuntimeReader(
        string projectRoot,
        string topology,
        string? role,
        ILabProcessHost? processHost = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _projectRoot = ProjectPathCanonicalizer.CanonicalizeExistingDirectory(
            Path.GetFullPath(projectRoot));
        if (!ValidSelection(topology, role))
        {
            throw new ArgumentException(
                "The MCP review topology and role selection is invalid.");
        }

        _topology = topology;
        _role = role;
        _processHost = processHost ?? new WindowsLabProcessHost();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public ProjectReviewMcpReadResult Read()
    {
        ProjectReviewMcpContextResult result = ReadContext();
        return result.Succeeded
            ? CreateSnapshot(result.Context!)
            : Failure(result.ErrorCode!, result.ErrorMessage!);
    }

    internal ProjectReviewMcpContextResult ReadContext()
    {
        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(_projectRoot);
            if (operationLock is null)
            {
                return ContextFailure(
                    "reviewBusy",
                    "Another live-lab operation currently owns the review lock.");
            }

            LiveLabPaths paths = LiveLabPaths.Resolve(_projectRoot);
            ProjectReviewStagingResult staged = ProjectModStager.ReadReview(
                paths,
                _topology);
            if (staged.Problem is not null || staged.Staging is null)
            {
                return ContextFailure(
                    staged.Problem?.Code ?? "reviewOwnershipMissing",
                    "An exact SDVKit-owned project review is not available.");
            }

            return string.Equals(
                    _topology,
                    LiveLabState.SingleTopology,
                    StringComparison.Ordinal)
                ? ReadSingle(paths, staged.Staging)
                : ReadNetworkTwo(paths, staged.Staging);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or System.Security.SecurityException)
        {
            return ContextFailure(
                "reviewStateInvalid",
                "The exact review state could not be validated.");
        }
    }

    private ProjectReviewMcpContextResult ReadSingle(
        LiveLabPaths paths,
        ProjectReviewStaging staging)
    {
        LiveLabState? state = new JsonLiveLabStateStore(paths.StatePath).Read();
        if (!HasExactSingleBinding(state, staging, paths))
        {
            return ContextFailure(
                "reviewOwnershipMismatch",
                "The retained review state does not match its exact owned target.");
        }

        LabProcessInspectResult process = _processHost.Inspect(
            state!.OwnedProcessIdentity);
        if (process.Status != LabProcessInspectStatus.Running)
        {
            return ContextProcessFailure(process.Status, pair: false);
        }

        AlwaysOnStatusReport alwaysOn = ReadAlwaysOn(
            state,
            _utcNow().ToUniversalTime());
        if (!StatusEnvelopeReady(alwaysOn))
        {
            return ContextFailure(
                "reviewRuntimeNotReady",
                "AlwaysOn has not confirmed a fresh active review status.");
        }

        if (state.TestSave is not null
            && !TestSaveReady(alwaysOn, state.TestSave))
        {
            return ContextFailure(
                "reviewTestSaveNotReady",
                "The exact owned review fixture is not ready.");
        }

        return new ProjectReviewMcpContextResult(
            new ProjectReviewMcpVerifiedContext(
                staging,
                state,
                alwaysOn,
                Role: null,
                state.TestSave is null
                    ? null
                    : new ProjectReviewMcpTestSave(
                        state.TestSave.Identity.FixtureId,
                        state.TestSave.Identity.SaveId),
                ProjectModReady(alwaysOn, state.ProjectMod!)),
            null,
            null);
    }

    private ProjectReviewMcpContextResult ReadNetworkTwo(
        LiveLabPaths paths,
        ProjectReviewStaging staging)
    {
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        LiveLabState? hostState =
            new JsonLiveLabStateStore(hostPaths.StatePath).Read();
        LiveLabState? farmhandState =
            new JsonLiveLabStateStore(farmhandPaths.StatePath).Read();
        if (!HasExactNetworkBinding(
                hostState,
                staging,
                hostPaths,
                paths.TestSaveWorkPath,
                NetworkTwoContract.HostRole)
            || !HasExactNetworkBinding(
                farmhandState,
                staging,
                farmhandPaths,
                paths.TestSaveWorkPath,
                NetworkTwoContract.FarmhandRole)
            || !NetworkStatesMatch(hostState!, farmhandState!))
        {
            return ContextFailure(
                "reviewOwnershipMismatch",
                "The retained network-2 states do not match the exact owned review pair.");
        }

        LiveLabState verifiedHostState = hostState!;
        LiveLabState verifiedFarmhandState = farmhandState!;

        foreach (LiveLabState state in new[] { verifiedHostState, verifiedFarmhandState })
        {
            LabProcessInspectResult process = _processHost.Inspect(
                state.OwnedProcessIdentity);
            if (process.Status != LabProcessInspectStatus.Running)
            {
                return ContextProcessFailure(process.Status, pair: true);
            }
        }

        DateTimeOffset nowUtc = _utcNow().ToUniversalTime();
        AlwaysOnStatusReport hostAlwaysOn = ReadAlwaysOn(verifiedHostState, nowUtc);
        AlwaysOnStatusReport farmhandAlwaysOn = ReadAlwaysOn(verifiedFarmhandState, nowUtc);
        if (!StatusEnvelopeReady(hostAlwaysOn)
            || !StatusEnvelopeReady(farmhandAlwaysOn))
        {
            return ContextFailure(
                "reviewRuntimeNotReady",
                "AlwaysOn has not confirmed fresh active review status for both roles.");
        }

        if (!TestSaveReady(hostAlwaysOn, verifiedHostState.TestSave!))
        {
            return ContextFailure(
                "reviewTestSaveNotReady",
                "The exact owned network-2 review fixture is not ready.");
        }

        if (!NetworkTwoPairVerifier.IsPassed(
                hostAlwaysOn,
                farmhandAlwaysOn,
                verifiedHostState.NetworkTwo!.BuildIdentity))
        {
            return ContextFailure(
                "reviewPairNotReady",
                "AlwaysOn has not confirmed the exact joined network-2 review pair.");
        }

        bool selectHost = string.Equals(
            _role,
            NetworkTwoContract.HostRole,
            StringComparison.Ordinal);
        LiveLabState selectedState = selectHost
            ? verifiedHostState
            : verifiedFarmhandState;
        AlwaysOnStatusReport selectedAlwaysOn =
            selectHost ? hostAlwaysOn : farmhandAlwaysOn;
        NetworkTwoLaunchState selectedNetwork = selectedState.NetworkTwo!;
        return new ProjectReviewMcpContextResult(
            new ProjectReviewMcpVerifiedContext(
                staging,
                selectedState,
                selectedAlwaysOn,
                _role,
                new ProjectReviewMcpTestSave(
                    selectedNetwork.FixtureId,
                    selectedNetwork.SaveId),
                ProjectModReady(hostAlwaysOn, verifiedHostState.ProjectMod!)
                    && ProjectModReady(
                        farmhandAlwaysOn,
                        verifiedFarmhandState.ProjectMod!)),
            null,
            null);
    }

    private static ProjectReviewMcpReadResult CreateSnapshot(
        ProjectReviewMcpVerifiedContext context)
    {
        LiveLabState state = context.State;
        AlwaysOnStatusReport alwaysOn = context.AlwaysOn;
        if (!context.AllTargetsReady)
        {
            return Failure(
                "reviewRuntimeNotReady",
                "AlwaysOn has not confirmed the exact active target build.");
        }

        RuntimeSnapshotReport? runtime = alwaysOn.Runtime;
        if (runtime is null
            || !string.Equals(runtime.State, "ready", StringComparison.Ordinal)
            || runtime.SchemaVersion != RuntimeSnapshotContract.SchemaVersion
            || runtime.WorldReady is null
            || runtime.MenuOpen is null
            || runtime.ObservedAtUtc is null)
        {
            return Failure(
                "reviewRuntimeSnapshotUnavailable",
                "A fresh, valid runtime snapshot is not available.");
        }

        ProjectModLaunchState target = state.ProjectMod!;
        return new ProjectReviewMcpReadResult(
            new ProjectReviewMcpRuntimeSnapshot(
                1,
                state.LaunchId,
                state.Topology,
                context.Role,
                runtime.ObservedAtUtc.Value,
                new ProjectReviewMcpTarget(
                    target.UniqueId,
                    target.Version,
                    target.BuildIdentity),
                context.TestSave,
                new ProjectReviewMcpRuntime(
                    runtime.SchemaVersion.Value,
                    runtime.WorldReady.Value,
                    runtime.Season,
                    runtime.DayOfMonth,
                    runtime.Year,
                    runtime.TimeOfDay,
                    runtime.LocationId,
                    runtime.TileX,
                    runtime.TileY,
                    runtime.MenuOpen.Value)),
            null,
            null);
    }

    private static bool HasExactSingleBinding(
        LiveLabState? state,
        ProjectReviewStaging staging,
        LiveLabPaths paths)
    {
        if (state is null
            || !string.Equals(staging.Topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || !string.Equals(state.Topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || state.NetworkTwo is not null
            || state.ProjectMod is null
            || !StatePathsMatch(state, paths))
        {
            return false;
        }

        ProjectModLaunchState target = staging.TargetLaunchState;
        bool targetMatches = string.Equals(
                state.ProjectMod.UniqueId,
                target.UniqueId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(state.ProjectMod.Version, target.Version, StringComparison.Ordinal)
            && string.Equals(
                state.ProjectMod.BuildIdentity,
                target.BuildIdentity,
                StringComparison.Ordinal);
        TestSaveLaunchState? testSave = state.TestSave;
        bool testSaveMatches = testSave is null
            || (string.Equals(testSave.Mode, TestSaveContract.ReviewMode, StringComparison.Ordinal)
                && PathsEqual(testSave.WorkPath, paths.TestSaveWorkPath)
                && PathsEqual(testSave.ScenarioLogPath, paths.TestSaveScenarioLogPath)
                && PathsEqual(
                    testSave.SlotPath,
                    Path.Combine(paths.SavesPath, testSave.Identity.SaveId)));
        return targetMatches && testSaveMatches;
    }

    private static bool HasExactNetworkBinding(
        LiveLabState? state,
        ProjectReviewStaging staging,
        LiveLabPaths rolePaths,
        string testSaveWorkPath,
        string role)
    {
        if (state is null
            || !string.Equals(staging.Topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
            || !string.Equals(state.Topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
            || !string.Equals(state.NetworkTwo?.Role, role, StringComparison.Ordinal)
            || state.ProjectMod is null
            || !StatePathsMatch(state, rolePaths))
        {
            return false;
        }

        ProjectModLaunchState target = staging.TargetLaunchState;
        NetworkTwoLaunchState network = state.NetworkTwo!;
        bool host = string.Equals(role, NetworkTwoContract.HostRole, StringComparison.Ordinal);
        bool testSaveMatches = host
            ? state.TestSave is not null
                && TestSaveBindingMatches(
                    state.TestSave,
                    rolePaths,
                    testSaveWorkPath)
                && string.Equals(
                    state.TestSave.Identity.FixtureId,
                    network.FixtureId,
                    StringComparison.Ordinal)
                && string.Equals(
                    state.TestSave.Identity.SaveId,
                    network.SaveId,
                    StringComparison.Ordinal)
            : state.TestSave is null;
        return TargetMatches(state.ProjectMod, target)
            && PathsEqual(
                network.NetworkLogPath,
                Path.Combine(rolePaths.RuntimePath, "network-2.log"))
            && testSaveMatches;
    }

    private static bool NetworkStatesMatch(
        LiveLabState host,
        LiveLabState farmhand)
    {
        NetworkTwoLaunchState hostNetwork = host.NetworkTwo!;
        NetworkTwoLaunchState farmhandNetwork = farmhand.NetworkTwo!;
        return !string.Equals(host.LaunchId, farmhand.LaunchId, StringComparison.Ordinal)
            && host.OwnedProcessIdentity.ProcessId
                != farmhand.OwnedProcessIdentity.ProcessId
            && hostNetwork.ExpectedFarmhandId is null
            && farmhandNetwork.ExpectedFarmhandId is not (null or 0)
            && string.Equals(
                hostNetwork.BuildIdentity,
                farmhandNetwork.BuildIdentity,
                StringComparison.Ordinal)
            && string.Equals(
                hostNetwork.FixtureId,
                farmhandNetwork.FixtureId,
                StringComparison.Ordinal)
            && string.Equals(
                hostNetwork.SaveId,
                farmhandNetwork.SaveId,
                StringComparison.Ordinal);
    }

    private static AlwaysOnStatusReport ReadAlwaysOn(
        LiveLabState state,
        DateTimeOffset nowUtc) =>
        AlwaysOnStatusReader.Read(
            state.StatusPath,
            state.LaunchId,
            state.OwnedProcessIdentity,
            nowUtc,
            state.TestSave,
            state.NetworkTwo,
            state.ProjectMod);

    private static bool StatePathsMatch(LiveLabState state, LiveLabPaths paths) =>
        PathsEqual(state.ModsPath, paths.ModsPath)
        && PathsEqual(state.StatusPath, paths.StatusPath)
        && PathsEqual(state.StopRequestPath, paths.StopRequestPath);

    private static bool TargetMatches(
        ProjectModLaunchState actual,
        ProjectModLaunchState expected) =>
        string.Equals(actual.UniqueId, expected.UniqueId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(actual.Version, expected.Version, StringComparison.Ordinal)
        && string.Equals(actual.BuildIdentity, expected.BuildIdentity, StringComparison.Ordinal);

    private static bool TestSaveBindingMatches(
        TestSaveLaunchState testSave,
        LiveLabPaths paths,
        string workPath) =>
        string.Equals(testSave.Mode, TestSaveContract.ReviewMode, StringComparison.Ordinal)
        && PathsEqual(testSave.WorkPath, workPath)
        && PathsEqual(testSave.ScenarioLogPath, paths.TestSaveScenarioLogPath)
        && PathsEqual(
            testSave.SlotPath,
            Path.Combine(paths.SavesPath, testSave.Identity.SaveId));

    private static bool ProjectModReady(
        AlwaysOnStatusReport alwaysOn,
        ProjectModLaunchState expected)
    {
        ProjectModStatusReport? projectMod = alwaysOn.ProjectMod;
        return string.Equals(alwaysOn.State, "active", StringComparison.Ordinal)
            && alwaysOn.PauseWhenOutOfFocus == false
            && projectMod is not null
            && string.Equals(projectMod.State, "ready", StringComparison.Ordinal)
            && string.Equals(projectMod.Phase, ProjectModContract.LoadedPhase, StringComparison.Ordinal)
            && projectMod.LoadConfirmed == true
            && string.Equals(projectMod.LoadedUniqueId, expected.UniqueId, StringComparison.Ordinal)
            && string.Equals(projectMod.LoadedVersion, expected.Version, StringComparison.Ordinal)
            && string.Equals(projectMod.BuildIdentity, expected.BuildIdentity, StringComparison.Ordinal);
    }

    private static bool StatusEnvelopeReady(AlwaysOnStatusReport alwaysOn)
    =>
        string.Equals(alwaysOn.State, "active", StringComparison.Ordinal)
            && alwaysOn.PauseWhenOutOfFocus == false
            && alwaysOn.ObservedAtUtc is not null;

    private static bool TestSaveReady(
        AlwaysOnStatusReport alwaysOn,
        TestSaveLaunchState expected)
    {
        TestSaveStatusReport? testSave = alwaysOn.TestSave;
        return testSave is not null
            && string.Equals(testSave.State, "ready", StringComparison.Ordinal)
            && string.Equals(testSave.Mode, TestSaveContract.ReviewMode, StringComparison.Ordinal)
            && string.Equals(testSave.Phase, "passed", StringComparison.Ordinal)
            && testSave.IdentityVerified == true
            && string.Equals(testSave.FixtureId, expected.Identity.FixtureId, StringComparison.Ordinal)
            && string.Equals(testSave.SaveId, expected.Identity.SaveId, StringComparison.Ordinal)
            && PathsEqual(testSave.ScenarioLogPath!, expected.ScenarioLogPath);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ValidSelection(string topology, string? role) =>
        string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            ? role is null
            : string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                && role is not null
                && NetworkTwoContract.IsRole(role);

    private static ProjectReviewMcpContextResult ContextProcessFailure(
        LabProcessInspectStatus status,
        bool pair) =>
        ContextFailure(
            status switch
            {
                LabProcessInspectStatus.Exited =>
                    pair ? "reviewPairProcessExited" : "reviewProcessExited",
                LabProcessInspectStatus.IdentityMismatch =>
                    pair ? "reviewPairProcessMismatch" : "reviewProcessMismatch",
                _ => pair ? "reviewPairProcessUnreadable" : "reviewProcessUnreadable",
            },
            pair
                ? "Both exact owned review processes are not verifiably running."
                : "The exact owned review process is not verifiably running.");

    private static ProjectReviewMcpContextResult ContextFailure(
        string code,
        string message) =>
        new(null, code, message);

    private static ProjectReviewMcpReadResult Failure(string code, string message) =>
        new(null, code, message);
}
