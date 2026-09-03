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

internal sealed class ProjectReviewMcpRuntimeReader
{
    private readonly string _projectRoot;
    private readonly ILabProcessHost _processHost;
    private readonly Func<DateTimeOffset> _utcNow;

    public ProjectReviewMcpRuntimeReader(
        string projectRoot,
        ILabProcessHost? processHost = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _projectRoot = ProjectPathCanonicalizer.CanonicalizeExistingDirectory(
            Path.GetFullPath(projectRoot));
        _processHost = processHost ?? new WindowsLabProcessHost();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public ProjectReviewMcpReadResult Read()
    {
        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(_projectRoot);
            if (operationLock is null)
            {
                return Failure(
                    "reviewBusy",
                    "Another live-lab operation currently owns the review lock.");
            }

            LiveLabPaths paths = LiveLabPaths.Resolve(_projectRoot);
            ProjectReviewStagingResult staged = ProjectModStager.ReadReview(paths);
            if (staged.Problem is not null || staged.Staging is null)
            {
                return Failure(
                    staged.Problem?.Code ?? "reviewOwnershipMissing",
                    "An exact SDVKit-owned single-player project review is not available.");
            }

            LiveLabState? state = new JsonLiveLabStateStore(paths.StatePath).Read();
            if (!HasExactBinding(state, staged.Staging, paths))
            {
                return Failure(
                    "reviewOwnershipMismatch",
                    "The retained review state does not match its exact owned target.");
            }

            LabProcessInspectResult process = _processHost.Inspect(
                state!.OwnedProcessIdentity);
            if (process.Status != LabProcessInspectStatus.Running)
            {
                return Failure(
                    process.Status switch
                    {
                        LabProcessInspectStatus.Exited => "reviewProcessExited",
                        LabProcessInspectStatus.IdentityMismatch => "reviewProcessMismatch",
                        _ => "reviewProcessUnreadable",
                    },
                    "The exact owned review process is not verifiably running.");
            }

            AlwaysOnStatusReport alwaysOn = AlwaysOnStatusReader.Read(
                state.StatusPath,
                state.LaunchId,
                state.OwnedProcessIdentity,
                _utcNow().ToUniversalTime(),
                state.TestSave,
                state.NetworkTwo,
                state.ProjectMod);
            if (!ProjectModReady(alwaysOn, state.ProjectMod!))
            {
                return Failure(
                    "reviewRuntimeNotReady",
                    "AlwaysOn has not confirmed the exact active target build.");
            }

            if (state.TestSave is not null
                && !TestSaveReady(alwaysOn, state.TestSave))
            {
                return Failure(
                    "reviewTestSaveNotReady",
                    "The exact owned review fixture is not ready.");
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
                    LiveLabState.SingleTopology,
                    runtime.ObservedAtUtc.Value,
                    new ProjectReviewMcpTarget(
                        target.UniqueId,
                        target.Version,
                        target.BuildIdentity),
                    state.TestSave is null
                        ? null
                        : new ProjectReviewMcpTestSave(
                            state.TestSave.Identity.FixtureId,
                            state.TestSave.Identity.SaveId),
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
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or System.Security.SecurityException)
        {
            return Failure(
                "reviewStateInvalid",
                "The exact review state could not be validated.");
        }
    }

    private static bool HasExactBinding(
        LiveLabState? state,
        ProjectReviewStaging staging,
        LiveLabPaths paths)
    {
        if (state is null
            || !string.Equals(staging.Topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || !string.Equals(state.Topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || state.NetworkTwo is not null
            || state.ProjectMod is null
            || !PathsEqual(state.ModsPath, paths.ModsPath))
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

    private static ProjectReviewMcpReadResult Failure(string code, string message) =>
        new(null, code, message);
}
