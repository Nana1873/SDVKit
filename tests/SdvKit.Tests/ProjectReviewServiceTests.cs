using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewServiceTests
{
    private const string LaunchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void StatusWithoutStateOrStagingIsStoppedWithoutDiscovery()
    {
        using TemporaryDirectory temporary = new();
        var doctorCalled = false;

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Status must not run doctor.");
            });

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.StagingRemoved);
        Assert.Empty(report.Artifacts);
        Assert.Null(report.Lab);
        Assert.True(report.InteractiveConsole);
        Assert.Equal(
            ".sdvkit/lab/profiles/single/AppData/Roaming/StardewValley/Saves",
            report.PersistentSavesPath);
        Assert.Empty(report.Problems);
        Assert.False(doctorCalled);
    }

    [Fact]
    public void BindingMismatchRetainsExactStateAndStaging()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(
            paths,
            staging.TargetLaunchState with { UniqueId = "Nana.OtherTarget" });
        new JsonLiveLabStateStore(paths.StatePath).Write(state);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.StagingRemoved);
        Assert.Equal(
            "reviewOwnershipMismatch",
            Assert.Single(report.Problems).Code);
        Assert.True(File.Exists(paths.StatePath));
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.True(Directory.Exists(staging.Target.StagingPath));
    }

    [Fact]
    public void StagingWithoutRuntimeStateIsRetainedFailClosed()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.StagingRemoved);
        Assert.Equal(
            "reviewOwnershipIncomplete",
            Assert.Single(report.Problems).Code);
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.True(Directory.Exists(staging.Target.StagingPath));
    }

    [Fact]
    public void StatusCleansExactStagingAfterTheOwnedReviewProcessExited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        string alwaysOnSentinel = WriteSentinel(
            paths.AlwaysOnModPath,
            "always-on.txt",
            "always-on");
        string saveSentinel = WriteSentinel(
            Path.Combine(paths.SavesPath, "SICQA_Review"),
            "SaveGameInfo",
            "persistent-save");
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(paths, staging.TargetLaunchState);
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        WriteLoadedStatus(paths, state, staging.TargetLaunchState);
        File.WriteAllText(paths.StopRequestPath, LaunchId);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.StagingRemoved);
        Assert.Equal("stopped", Assert.IsType<LiveLabReport>(report.Lab).State);
        Assert.False(File.Exists(paths.StatePath));
        Assert.False(File.Exists(paths.StopRequestPath));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.False(Directory.Exists(staging.Target.StagingPath));
        Assert.Equal("always-on", File.ReadAllText(alwaysOnSentinel));
        Assert.Equal("persistent-save", File.ReadAllText(saveSentinel));
    }

    [Fact]
    public void StatusCleansExitedReviewButFailsWithoutTargetLoadConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        new JsonLiveLabStateStore(paths.StatePath).Write(
            ReviewState(paths, staging.TargetLaunchState));
        File.WriteAllText(paths.StopRequestPath, LaunchId);

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("stopped", report.State);
        Assert.True(report.StagingRemoved);
        Assert.Equal(
            "projectModLoadUnconfirmed",
            Assert.Single(report.Problems).Code);
        Assert.False(File.Exists(paths.StatePath));
        Assert.False(File.Exists(paths.StopRequestPath));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.False(Directory.Exists(staging.Target.StagingPath));
    }

    [Fact]
    public void StatusReportsMarkerlessPartialStagingFailClosed()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        string partial = WriteSentinel(
            Path.Combine(paths.ModsPath, "PartialTarget"),
            "partial.dll",
            "partial");

        LiveLabCommandResult result = ProjectReviewService.Execute(
            "status",
            temporary.Path,
            [],
            [],
            temporary.Path,
            () => throw new InvalidOperationException("Status must not run doctor."));

        ProjectReviewReport report = Assert.IsType<ProjectReviewReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.StagingRemoved);
        Assert.Equal(
            "reviewStagingOwnershipMissing",
            Assert.Single(report.Problems).Code);
        Assert.Equal("partial", File.ReadAllText(partial));
    }

    private static ProjectReviewStaging StageTarget(
        LiveLabPaths paths,
        string fixtureRoot)
    {
        ProjectReviewPreparedArtifact target = ProjectReviewStagerTests.Artifact(
            fixtureRoot,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewStagingResult result = ProjectModStager.StageReview([target], paths);
        Assert.Null(result.Problem);
        return Assert.IsType<ProjectReviewStaging>(result.Staging);
    }

    private static LiveLabState ReviewState(
        LiveLabPaths paths,
        ProjectModLaunchState target) =>
        new(
            LiveLabState.CurrentSchemaVersion,
            LiveLabState.SingleTopology,
            LaunchId,
            new OwnedProcessIdentity(
                int.MaxValue,
                new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero),
                Path.Combine(paths.ProjectRoot, "StardewModdingAPI.exe")),
            paths.ModsPath,
            paths.StatusPath,
            paths.StopRequestPath,
            ProjectMod: target);

    private static string WriteSentinel(
        string directory,
        string fileName,
        string contents)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void WriteLoadedStatus(
        LiveLabPaths paths,
        LiveLabState state,
        ProjectModLaunchState target)
    {
        var marker = new AlwaysOnStatusMarker(
            1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            "active",
            600,
            IsActive: true,
            PauseWhenOutOfFocus: false,
            state.OwnedProcessIdentity.StartTimeUtc.AddSeconds(10),
            ProjectMod: new ProjectModStatusMarker(
                ProjectModContract.SchemaVersion,
                ProjectModContract.LoadedPhase,
                target.UniqueId,
                target.Version,
                target.UniqueId,
                target.Version,
                target.BuildIdentity,
                LoadConfirmed: true,
                "Loaded by SMAPI."));
        File.WriteAllText(
            paths.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }
}
