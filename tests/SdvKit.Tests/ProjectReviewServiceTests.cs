using System.Diagnostics;
using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommandWritesOnlyToTheExactRunningReadyLoadedReviewWithoutMutatingOwnership(
        bool isActive)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState, isActive);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));

                LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                    "sic-review set greenhouse fixture",
                    temporary.Path,
                    sender);

                ProjectReviewCommandReport report =
                    Assert.IsType<ProjectReviewCommandReport>(result.Report);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("running", report.State);
                Assert.True(report.CommandWritten);
                Assert.Empty(report.Problems);
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.Equal("sic-review set greenhouse fixture", sender.Line);
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Theory]
    [InlineData(
        ProjectModContract.WaitingForGameLaunchPhase,
        false,
        "reviewConsoleTargetNotReady")]
    [InlineData(ProjectModContract.LoadedPhase, false, "projectModStatusMismatch")]
    public void CommandDoesNotSendUntilTheExactRunningReviewIsReadyAndLoadConfirmed(
        string phase,
        bool loadConfirmed,
        string expectedProblem)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteStatus(
                    paths,
                    state,
                    staging.TargetLaunchState,
                    phase,
                    loadConfirmed);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));

                LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                    "sic-review set greenhouse fixture",
                    temporary.Path,
                    sender);

                ProjectReviewCommandReport report =
                    Assert.IsType<ProjectReviewCommandReport>(result.Report);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("blocked", report.State);
                Assert.False(report.CommandWritten);
                Assert.Equal(
                    expectedProblem,
                    Assert.Single(report.Problems).Code);
                Assert.Equal(0, sender.CallCount);
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
    }

    [Fact]
    public void CommandDoesNotSendWhenReviewOwnershipDoesNotBindExactly()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(
            paths,
            staging.TargetLaunchState with { UniqueId = "Nana.OtherTarget" });
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        string stateBefore = FileSnapshot(paths.StatePath);
        string stagingBefore = TreeSnapshot(paths.ModsPath);
        var sender = new RecordingConsoleInputSender(
            new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written));

        LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
            "sic-review set greenhouse fixture",
            temporary.Path,
            sender);

        ProjectReviewCommandReport report =
            Assert.IsType<ProjectReviewCommandReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.CommandWritten);
        Assert.Equal("reviewOwnershipMismatch", Assert.Single(report.Problems).Code);
        Assert.Equal(0, sender.CallCount);
        Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
        Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
    }

    [Fact]
    public void CommandDoesNotSendWhenTheExactlyBoundProcessIsNotRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        LiveLabState state = ReviewState(paths, staging.TargetLaunchState);
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        WriteLoadedStatus(paths, state, staging.TargetLaunchState);
        string stateBefore = FileSnapshot(paths.StatePath);
        string stagingBefore = TreeSnapshot(paths.ModsPath);
        var sender = new RecordingConsoleInputSender(
            new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written));

        LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
            "sic-review set greenhouse fixture",
            temporary.Path,
            sender);

        ProjectReviewCommandReport report =
            Assert.IsType<ProjectReviewCommandReport>(result.Report);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("blocked", report.State);
        Assert.False(report.CommandWritten);
        Assert.Equal("ownedProcessExited", Assert.Single(report.Problems).Code);
        Assert.Equal(0, sender.CallCount);
        Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
        Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
    }

    [Fact]
    public void CommandServiceRejectsControlCharactersAndOverlongLinesBeforeSending()
    {
        using TemporaryDirectory temporary = new();
        var sender = new RecordingConsoleInputSender(
            new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written));

        foreach (string command in new[]
        {
            "sic-review\nset",
            new string('x', ProjectReviewConsoleLine.MaximumLength + 1),
        })
        {
            LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                command,
                temporary.Path,
                sender);

            ProjectReviewCommandReport report =
                Assert.IsType<ProjectReviewCommandReport>(result.Report);
            Assert.Equal(3, result.ExitCode);
            Assert.Equal("blocked", report.State);
            Assert.False(report.CommandWritten);
            Assert.Equal(
                "reviewConsoleCommandInvalid",
                Assert.Single(report.Problems).Code);
        }

        Assert.Equal(0, sender.CallCount);
    }

    [Theory]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WorkerStartFailed,
        false,
        "reviewConsoleWorkerStartFailed")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WorkerParentMismatch,
        false,
        "reviewConsoleWorkerParentMismatch")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WrittenDetachFailed,
        true,
        "reviewConsoleDetachFailed")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WrittenProcessExited,
        true,
        "reviewConsoleProcessExitedAfterWrite")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WrittenProcessUnreadable,
        true,
        "reviewConsoleProcessUnreadableAfterWrite")]
    [InlineData(
        (int)ProjectReviewConsoleInputStatus.WrittenConsoleChanged,
        true,
        "reviewConsoleOwnershipChangedAfterWrite")]
    public void CommandMapsWorkerAndPostWriteFailuresWithoutMutatingOwnership(
        int statusValue,
        bool expectedCommandWritten,
        string expectedProblem)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = StageTarget(paths, temporary.Path);
        (OwnedProcessIdentity identity, Process child) = StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ReviewState(paths, staging.TargetLaunchState, identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                WriteLoadedStatus(paths, state, staging.TargetLaunchState);
                string stateBefore = FileSnapshot(paths.StatePath);
                string stagingBefore = TreeSnapshot(paths.ModsPath);
                var sender = new RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        (ProjectReviewConsoleInputStatus)statusValue));

                LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
                    "sic-review set greenhouse fixture",
                    temporary.Path,
                    sender);

                ProjectReviewCommandReport report =
                    Assert.IsType<ProjectReviewCommandReport>(result.Report);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("blocked", report.State);
                Assert.Equal(expectedCommandWritten, report.CommandWritten);
                Assert.Equal(expectedProblem, Assert.Single(report.Problems).Code);
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(identity, sender.Identity);
                Assert.Equal(stateBefore, FileSnapshot(paths.StatePath));
                Assert.Equal(stagingBefore, TreeSnapshot(paths.ModsPath));
            }
            finally
            {
                EnsureExited(child);
            }
        }
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
        ProjectModLaunchState target,
        OwnedProcessIdentity? processIdentity = null) =>
        new(
            LiveLabState.CurrentSchemaVersion,
            LiveLabState.SingleTopology,
            LaunchId,
            processIdentity ?? new OwnedProcessIdentity(
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
        ProjectModLaunchState target,
        bool isActive = true)
    {
        WriteStatus(
            paths,
            state,
            target,
            ProjectModContract.LoadedPhase,
            loadConfirmed: true,
            isActive: isActive);
    }

    private static void WriteStatus(
        LiveLabPaths paths,
        LiveLabState state,
        ProjectModLaunchState target,
        string phase,
        bool loadConfirmed,
        bool isActive = true)
    {
        var marker = new AlwaysOnStatusMarker(
            1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            "active",
            600,
            IsActive: isActive,
            PauseWhenOutOfFocus: false,
            DateTimeOffset.UtcNow,
            ProjectMod: new ProjectModStatusMarker(
                ProjectModContract.SchemaVersion,
                phase,
                target.UniqueId,
                target.Version,
                loadConfirmed ? target.UniqueId : null,
                loadConfirmed ? target.Version : null,
                target.BuildIdentity,
                LoadConfirmed: loadConfirmed,
                loadConfirmed ? "Loaded by SMAPI." : "Waiting for game launch."));
        File.WriteAllText(
            paths.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    private static (OwnedProcessIdentity Identity, Process Process) StartRunningProcess(
        string projectRoot)
    {
        var host = new WindowsLabProcessHost();
        string executable = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        LabProcessStartResult started = host.Start(new LabProcessStartSpec(
            executable,
            Path.GetDirectoryName(executable)!,
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-WindowStyle",
                "Hidden",
                "-Command",
                "Start-Sleep -Seconds 30",
            ],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Path.Combine(projectRoot, ".sdvkit", "test-runtime", "stdout.log"),
            Path.Combine(projectRoot, ".sdvkit", "test-runtime", "stderr.log")));
        Assert.Equal(LabProcessStartStatus.Started, started.Status);
        OwnedProcessIdentity identity = Assert.IsType<OwnedProcessIdentity>(started.Identity);
        return (identity, Process.GetProcessById(identity.ProcessId));
    }

    private static string FileSnapshot(string path) =>
        Convert.ToBase64String(File.ReadAllBytes(path));

    private static string TreeSnapshot(string root)
    {
        return string.Join(
            "\n",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                    $"{Path.GetRelativePath(root, path)}:{FileSnapshot(path)}"));
    }

    private static void EnsureExited(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The short-lived game-free test process already exited.
        }
    }

    private sealed class RecordingConsoleInputSender(
        ProjectReviewConsoleInputResult result) : IProjectReviewConsoleInputSender
    {
        public int CallCount { get; private set; }

        public OwnedProcessIdentity? Identity { get; private set; }

        public string? Line { get; private set; }

        public ProjectReviewConsoleInputResult SendLine(
            OwnedProcessIdentity expected,
            string line)
        {
            CallCount++;
            Identity = expected;
            Line = line;
            return result;
        }
    }
}
