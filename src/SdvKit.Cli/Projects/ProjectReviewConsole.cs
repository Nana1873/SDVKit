using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static partial class ProjectReviewService
{
    internal static LiveLabCommandResult ExecuteCommand(
        string command,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null) =>
        ExecuteCommand(
            command,
            LiveLabState.SingleTopology,
            role: null,
            labRoot,
            inputSender);

    internal static LiveLabCommandResult ExecuteCommand(
        string command,
        string topology,
        string? role,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null,
        LiveLabOperationLock? heldOperationLock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);

        bool networkTwo = string.Equals(
            topology,
            NetworkTwoContract.Topology,
            StringComparison.Ordinal);
        if ((!networkTwo && (!string.Equals(
                    topology,
                    LiveLabState.SingleTopology,
                    StringComparison.Ordinal)
                || role is not null))
            || (networkTwo && (role is null || !NetworkTwoContract.IsRole(role))))
        {
            return networkTwo
                ? NetworkCommandFailure(
                    SafeFullPath(labRoot),
                    role ?? string.Empty,
                    [Problem(
                        "reviewCommandRoleInvalid",
                        null,
                        "Network-2 review commands require exactly one --role host or --role farmhand.")])
                : CommandFailure(
                    SafeFullPath(labRoot),
                    [Problem(
                        "reviewCommandTopologyInvalid",
                        null,
                        "Single review commands do not accept a role, and the topology must be single or network-2.")]);
        }

        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            IReadOnlyList<ProjectReviewProblem> problems =
                [Problem("reviewConsoleCommandInvalid", null, validationError)];
            return networkTwo
                ? NetworkCommandFailure(SafeFullPath(labRoot), role!, problems)
                : CommandFailure(SafeFullPath(labRoot), problems);
        }

        bool canRunBeforeScenarioReady =
            ProjectReviewConsoleLine.CanRunBeforeScenarioReady(command);

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(labRoot);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            IReadOnlyList<ProjectReviewProblem> problems =
                [Problem("labPathInvalid", null, exception.Message)];
            return networkTwo
                ? NetworkCommandFailure(SafeFullPath(labRoot), role!, problems)
                : CommandFailure(SafeFullPath(labRoot), problems);
        }

        if (networkTwo)
        {
            if (heldOperationLock is not null)
                throw new InvalidOperationException("Held-operation console dispatch supports single only.");
            return ExecuteNetworkCommand(
                command,
                role!,
                paths,
                inputSender,
                canRunBeforeScenarioReady);
        }

        try
        {
            heldOperationLock?.RequireHeldFor(paths.ProjectRoot);
            using LiveLabOperationLock? operationLock =
                heldOperationLock is null ? LiveLabOperationLock.TryAcquire(paths.ProjectRoot) : null;
            if (operationLock is null && heldOperationLock is null)
            {
                return CommandResult(
                    paths,
                    null,
                    "blocked",
                    null,
                    commandWritten: false,
                    [Problem(
                        "labBusy",
                        null,
                        "Another live-lab operation is still running for this lab root.")]);
            }

            ProjectReviewStagingResult staged = ProjectModStager.ReadReview(paths);
            if (staged.Problem is not null)
            {
                return CommandResult(
                    paths,
                    null,
                    "blocked",
                    null,
                    commandWritten: false,
                    [staged.Problem]);
            }

            var stateStore = new JsonLiveLabStateStore(paths.StatePath);
            LiveLabState? state = stateStore.Read();
            ProjectReviewProblem? bindingProblem = ReviewBindingProblem(
                state,
                staged.Staging,
                paths);
            if (bindingProblem is not null)
            {
                return CommandResult(
                    paths,
                    staged.Staging,
                    "blocked",
                    null,
                    commandWritten: false,
                    [bindingProblem]);
            }

            var service = new LiveLabService(
                paths,
                stateStore,
                new AlwaysOnBuilder(),
                new WindowsLabProcessHost(),
                () => throw new InvalidOperationException(
                    "Project-review console input must not run installation discovery."));
            LiveLabCommandResult status = service.StatusProjectReview();
            LiveLabReport lab = (LiveLabReport)status.Report;
            if (!string.Equals(lab.State, "running", StringComparison.Ordinal)
                || (!canRunBeforeScenarioReady && status.ExitCode != Success))
            {
                IReadOnlyList<ProjectReviewProblem> problems = LabProblems(lab).ToArray();
                return CommandResult(
                    paths,
                    staged.Staging,
                    "blocked",
                    lab,
                    commandWritten: false,
                    problems.Count > 0
                        ? problems
                        : [Problem(
                            "reviewConsoleNotRunning",
                            null,
                            "The exact owned project-review process is not running; no console input was written.")]);
            }

            LiveLabState exactState = state!;
            if (!ProjectModReadyForConsole(lab.AlwaysOn, exactState.ProjectMod!))
            {
                return CommandResult(
                    paths,
                    staged.Staging,
                    "blocked",
                    lab,
                    commandWritten: false,
                    [Problem(
                        "reviewConsoleTargetNotReady",
                        null,
                        "The exact target mod has not reached its fully confirmed loaded state; no console input was written.")]);
            }

            if (exactState.TestSave is not null
                && !canRunBeforeScenarioReady
                && !TestSaveReadyForConsole(lab.AlwaysOn, exactState.TestSave))
            {
                return CommandResult(
                    paths,
                    staged.Staging,
                    "blocked",
                    lab,
                    commandWritten: false,
                    [Problem(
                        "reviewConsoleTestSaveNotReady",
                        null,
                        "The exact owned review fixture is not currently loaded and identity-verified; no console input was written.")]);
            }

            ProjectReviewConsoleInputResult sent =
                (inputSender ?? new WindowsProjectReviewConsoleInputSender()).SendLine(
                    exactState.OwnedProcessIdentity,
                    command);
            ProjectReviewProblem? inputProblem = ConsoleInputProblem(sent);
            return CommandResult(
                paths,
                staged.Staging,
                inputProblem is null ? "running" : "blocked",
                lab,
                sent.CommandWritten,
                inputProblem is null ? [] : [inputProblem]);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return CommandResult(
                paths,
                null,
                "blocked",
                null,
                commandWritten: false,
                [Problem("projectReviewConsoleFailed", null, exception.Message)]);
        }
    }

    private static LiveLabCommandResult ExecuteNetworkCommand(
        string command,
        string role,
        LiveLabPaths paths,
        IProjectReviewConsoleInputSender? inputSender,
        bool canRunBeforeScenarioReady)
    {
        try
        {
            using LiveLabOperationLock? operationLock =
                LiveLabOperationLock.TryAcquire(paths.ProjectRoot);
            if (operationLock is null)
            {
                return NetworkCommandResult(
                    paths,
                    null,
                    role,
                    "blocked",
                    null,
                    commandWritten: false,
                    [Problem(
                        "labBusy",
                        null,
                        "Another live-lab operation is still running for this lab root.")]);
            }

            ProjectReviewStagingResult staged = ProjectModStager.ReadReview(
                paths,
                NetworkTwoContract.Topology);
            if (staged.Problem is not null || staged.Staging is null)
            {
                return NetworkCommandResult(
                    paths,
                    staged.Staging,
                    role,
                    "blocked",
                    null,
                    commandWritten: false,
                    [staged.Problem ?? Problem(
                        "reviewOwnershipIncomplete",
                        null,
                        "No exact retained network-2 project-review staging is available; no console input was written.")]);
            }

            LiveLabCommandResult pairStatus = NetworkTwoSmokeService.StatusReviewWithinLock(
                paths.ProjectRoot,
                staged.Staging.TargetLaunchState);
            NetworkTwoSmokeReport network = RequireNetworkReport(pairStatus);
            bool pairReady = pairStatus.ExitCode == Success
                && string.Equals(network.State, "running", StringComparison.Ordinal);
            if (!pairReady
                && (!canRunBeforeScenarioReady
                    || !NetworkPairRunningForPreScenarioCommand(network)))
            {
                IReadOnlyList<ProjectReviewProblem> problems = NetworkProblems(network).ToArray();
                return NetworkCommandResult(
                    paths,
                    staged.Staging,
                    role,
                    "blocked",
                    null,
                    commandWritten: false,
                    problems.Count > 0
                        ? problems
                        : [Problem(
                            "reviewConsoleNotRunning",
                            null,
                            "The exact host/farmhand review pair is not running; no console input was written.")]);
            }

            LiveLabPaths rolePaths = LiveLabPaths.ResolveNetworkRole(paths, role);
            var stateStore = new JsonLiveLabStateStore(rolePaths.StatePath);
            LiveLabState? state = stateStore.Read();
            ProjectReviewProblem? bindingProblem = NetworkReviewBindingProblem(
                state,
                staged.Staging,
                rolePaths,
                role);
            if (bindingProblem is not null)
            {
                return NetworkCommandResult(
                    paths,
                    staged.Staging,
                    role,
                    "blocked",
                    null,
                    commandWritten: false,
                    [bindingProblem]);
            }

            var service = new LiveLabService(
                rolePaths,
                stateStore,
                new AlwaysOnBuilder(),
                new WindowsLabProcessHost(),
                () => throw new InvalidOperationException(
                    "Network-2 review console input must not run installation discovery."),
                reportTopology: NetworkTwoContract.Topology);
            LiveLabCommandResult roleStatus = service.StatusNetwork();
            LiveLabReport lab = (LiveLabReport)roleStatus.Report;
            if (!string.Equals(lab.State, "running", StringComparison.Ordinal)
                || (!canRunBeforeScenarioReady && roleStatus.ExitCode != Success)
                || !ProjectModReadyForConsole(lab.AlwaysOn, state!.ProjectMod!))
            {
                IReadOnlyList<ProjectReviewProblem> problems = LabProblems(lab).ToArray();
                return NetworkCommandResult(
                    paths,
                    staged.Staging,
                    role,
                    "blocked",
                    lab,
                    commandWritten: false,
                    problems.Count > 0
                        ? problems
                        : [Problem(
                            "reviewConsoleTargetNotReady",
                            null,
                            "The exact role and target mod have not reached their confirmed loaded state; no console input was written.")]);
            }

            ProjectReviewConsoleInputResult sent =
                (inputSender ?? new WindowsProjectReviewConsoleInputSender()).SendLine(
                    state!.OwnedProcessIdentity,
                    command);
            ProjectReviewProblem? inputProblem = ConsoleInputProblem(sent);
            return NetworkCommandResult(
                paths,
                staged.Staging,
                role,
                inputProblem is null ? "running" : "blocked",
                lab,
                sent.CommandWritten,
                inputProblem is null ? [] : [inputProblem]);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return NetworkCommandResult(
                paths,
                null,
                role,
                "blocked",
                null,
                commandWritten: false,
                [Problem("projectReviewConsoleFailed", null, exception.Message)]);
        }
    }

    private static bool ProjectModReadyForConsole(
        AlwaysOnStatusReport? alwaysOn,
        ProjectModLaunchState expected)
    {
        ProjectModStatusReport? projectMod = alwaysOn?.ProjectMod;
        return string.Equals(alwaysOn?.State, "active", StringComparison.Ordinal)
            && alwaysOn?.PauseWhenOutOfFocus == false
            && projectMod is not null
            && string.Equals(projectMod.State, "ready", StringComparison.Ordinal)
            && string.Equals(
                projectMod.Phase,
                ProjectModContract.LoadedPhase,
                StringComparison.Ordinal)
            && projectMod.LoadConfirmed == true
            && string.Equals(
                projectMod.LoadedUniqueId,
                expected.UniqueId,
                StringComparison.Ordinal)
            && string.Equals(
                projectMod.LoadedVersion,
                expected.Version,
                StringComparison.Ordinal)
            && string.Equals(
                projectMod.BuildIdentity,
                expected.BuildIdentity,
                StringComparison.Ordinal);
    }

    private static bool TestSaveReadyForConsole(
        AlwaysOnStatusReport? alwaysOn,
        TestSaveLaunchState expected)
    {
        TestSaveStatusReport? testSave = alwaysOn?.TestSave;
        return string.Equals(
                expected.Mode,
                TestSaveContract.ReviewMode,
                StringComparison.Ordinal)
            && testSave is not null
            && string.Equals(testSave.State, "ready", StringComparison.Ordinal)
            && string.Equals(
                testSave.Mode,
                TestSaveContract.ReviewMode,
                StringComparison.Ordinal)
            && string.Equals(testSave.Phase, "passed", StringComparison.Ordinal)
            && testSave.IdentityVerified == true
            && string.Equals(
                testSave.FixtureId,
                expected.Identity.FixtureId,
                StringComparison.Ordinal)
            && string.Equals(
                testSave.SaveId,
                expected.Identity.SaveId,
                StringComparison.Ordinal)
            && string.Equals(
                testSave.ScenarioLogPath,
                expected.ScenarioLogPath,
                PathComparison());
    }

    private static ProjectReviewProblem? ConsoleInputProblem(
        ProjectReviewConsoleInputResult result)
    {
        if (result.Status == ProjectReviewConsoleInputStatus.Written)
        {
            return null;
        }

        string message = result.Error ?? result.Status switch
        {
            ProjectReviewConsoleInputStatus.WrittenDetachFailed =>
                "The command was fully enqueued, but the one-shot console worker did not detach cleanly; do not retry it automatically.",
            ProjectReviewConsoleInputStatus.WrittenProcessExited =>
                "The command was fully enqueued, but the exact SMAPI process exited before delivery could be rechecked; do not retry it automatically.",
            ProjectReviewConsoleInputStatus.WrittenProcessUnreadable =>
                "The command was fully enqueued, but the exact SMAPI process became unreadable before delivery could be rechecked; do not retry it automatically.",
            ProjectReviewConsoleInputStatus.WrittenConsoleChanged =>
                "The command was fully enqueued, but the console process set changed before delivery could be rechecked; do not retry it automatically.",
            ProjectReviewConsoleInputStatus.ProcessExited =>
                "The exact owned SMAPI process exited before console input.",
            ProjectReviewConsoleInputStatus.ProcessIdentityMismatch =>
                "The PID no longer identifies the exact owned SMAPI process; no console input was written.",
            ProjectReviewConsoleInputStatus.ProcessUnreadable =>
                "The exact owned SMAPI process could not be verified; no console input was written.",
            ProjectReviewConsoleInputStatus.AttachFailed =>
                "The one-shot worker could not attach to the exact owned SMAPI console; no console input was written.",
            ProjectReviewConsoleInputStatus.SharedConsole =>
                "The SMAPI console has unexpected attached processes; no console input was written.",
            ProjectReviewConsoleInputStatus.InputBusy =>
                "The SMAPI console has pending input; no console input was written.",
            ProjectReviewConsoleInputStatus.InputOpenFailed =>
                "The exact SMAPI console input buffer could not be opened; no console input was written.",
            ProjectReviewConsoleInputStatus.WriteFailed =>
                "Windows did not enqueue any console input records.",
            ProjectReviewConsoleInputStatus.PartialWrite =>
                "Windows may have enqueued only part of the command; delivery is unknown and must not be retried automatically.",
            ProjectReviewConsoleInputStatus.WorkerTimedOut =>
                "The one-shot console worker timed out; delivery is unknown and must not be retried automatically.",
            ProjectReviewConsoleInputStatus.WorkerStartFailed =>
                "The one-shot console worker could not be started; no console input was written.",
            ProjectReviewConsoleInputStatus.WorkerParentMismatch =>
                "The internal console worker was not started by the exact SDVKit parent process; no console input was written.",
            _ => "The one-shot console worker failed; delivery is unknown and must not be retried automatically.",
        };
        string code = result.Status switch
        {
            ProjectReviewConsoleInputStatus.WrittenDetachFailed => "reviewConsoleDetachFailed",
            ProjectReviewConsoleInputStatus.WrittenProcessExited => "reviewConsoleProcessExitedAfterWrite",
            ProjectReviewConsoleInputStatus.WrittenProcessUnreadable => "reviewConsoleProcessUnreadableAfterWrite",
            ProjectReviewConsoleInputStatus.WrittenConsoleChanged => "reviewConsoleOwnershipChangedAfterWrite",
            ProjectReviewConsoleInputStatus.ProcessExited => "reviewConsoleProcessExited",
            ProjectReviewConsoleInputStatus.ProcessIdentityMismatch => "reviewConsoleIdentityMismatch",
            ProjectReviewConsoleInputStatus.ProcessUnreadable => "reviewConsoleProcessUnreadable",
            ProjectReviewConsoleInputStatus.AttachFailed => "reviewConsoleAttachFailed",
            ProjectReviewConsoleInputStatus.SharedConsole => "reviewConsoleShared",
            ProjectReviewConsoleInputStatus.InputBusy => "reviewConsoleInputBusy",
            ProjectReviewConsoleInputStatus.InputOpenFailed => "reviewConsoleInputOpenFailed",
            ProjectReviewConsoleInputStatus.WriteFailed => "reviewConsoleWriteFailed",
            ProjectReviewConsoleInputStatus.PartialWrite => "reviewConsolePartialWrite",
            ProjectReviewConsoleInputStatus.WorkerTimedOut => "reviewConsoleWorkerTimedOut",
            ProjectReviewConsoleInputStatus.WorkerStartFailed => "reviewConsoleWorkerStartFailed",
            ProjectReviewConsoleInputStatus.WorkerParentMismatch => "reviewConsoleWorkerParentMismatch",
            ProjectReviewConsoleInputStatus.InvalidRequest => "reviewConsoleCommandInvalid",
            _ => "reviewConsoleWorkerFailed",
        };
        return Problem(code, null, message);
    }

    private static LiveLabCommandResult NetworkCommandResult(
        LiveLabPaths paths,
        ProjectReviewStaging? staging,
        string role,
        string state,
        LiveLabReport? lab,
        bool? commandWritten,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        var report = new ProjectNetworkReviewCommandReport(
            1,
            NetworkTwoContract.Topology,
            staging?.Target.SourceRoot,
            paths.ProjectRoot,
            role,
            state,
            lab,
            commandWritten,
            problems,
            [.. NetworkWarnings, .. CommandWarnings]);
        return new LiveLabCommandResult(
            problems.Count == 0 && commandWritten == true
                ? Success
                : OperationFailed,
            report);
    }

    private static LiveLabCommandResult NetworkCommandFailure(
        string labRoot,
        string role,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        var report = new ProjectNetworkReviewCommandReport(
            1,
            NetworkTwoContract.Topology,
            null,
            labRoot,
            role,
            "blocked",
            null,
            false,
            problems,
            [.. NetworkWarnings, .. CommandWarnings]);
        return new LiveLabCommandResult(OperationFailed, report);
    }

    private static LiveLabCommandResult CommandResult(
        LiveLabPaths paths,
        ProjectReviewStaging? staging,
        string state,
        LiveLabReport? lab,
        bool? commandWritten,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        var report = new ProjectReviewCommandReport(
            1,
            staging?.Target.SourceRoot,
            paths.ProjectRoot,
            state,
            lab,
            commandWritten,
            problems,
            [.. Warnings, .. CommandWarnings]);
        return new LiveLabCommandResult(
            problems.Count == 0 && commandWritten == true
                ? Success
                : OperationFailed,
            report);
    }

    private static LiveLabCommandResult CommandFailure(
        string labRoot,
        IReadOnlyList<ProjectReviewProblem> problems)
    {
        var report = new ProjectReviewCommandReport(
            1,
            null,
            labRoot,
            "blocked",
            null,
            false,
            problems,
            [.. Warnings, .. CommandWarnings]);
        return new LiveLabCommandResult(OperationFailed, report);
    }
}
