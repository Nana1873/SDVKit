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
                new GameSideBuilder(),
                new WindowsLabProcessHost(),
                () => throw new InvalidOperationException(
                    "Project-review commands must not run installation discovery."));
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
                            "The exact owned project-review process is not running; no command was sent.")]);
            }

            LiveLabState exactState = state!;
            if (!ProjectModReadyForConsole(lab.GameSide, exactState.ProjectMod!))
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
                        "The exact target mod has not reached its fully confirmed loaded state; no command was sent.")]);
            }

            if (exactState.TestSave is not null
                && !canRunBeforeScenarioReady
                && !TestSaveReadyForConsole(lab.GameSide, exactState.TestSave))
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
                        "The exact owned review fixture is not currently loaded and identity-verified; no command was sent.")]);
            }

            ProjectReviewConsoleInputResult sent =
                (inputSender ?? HttpProjectReviewCommandSender.For(exactState, lab.GameSide)).SendLine(
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
                        "No exact retained network-2 project-review staging is available; no command was sent.")]);
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
                            "The exact host/farmhand review pair is not running; no command was sent.")]);
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
                new GameSideBuilder(),
                new WindowsLabProcessHost(),
                () => throw new InvalidOperationException(
                    "Network-2 review commands must not run installation discovery."),
                reportTopology: NetworkTwoContract.Topology);
            LiveLabCommandResult roleStatus = service.StatusNetwork();
            LiveLabReport lab = (LiveLabReport)roleStatus.Report;
            if (!string.Equals(lab.State, "running", StringComparison.Ordinal)
                || (!canRunBeforeScenarioReady && roleStatus.ExitCode != Success)
                || !ProjectModReadyForConsole(lab.GameSide, state!.ProjectMod!))
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
                            "The exact role and target mod have not reached their confirmed loaded state; no command was sent.")]);
            }

            ProjectReviewConsoleInputResult sent =
                (inputSender ?? HttpProjectReviewCommandSender.For(state!, lab.GameSide)).SendLine(
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
        GameSideStatusReport? gameSide,
        ProjectModLaunchState expected)
    {
        ProjectModStatusReport? projectMod = gameSide?.ProjectMod;
        return string.Equals(gameSide?.State, "active", StringComparison.Ordinal)
            && gameSide?.PauseWhenOutOfFocus == false
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
        GameSideStatusReport? gameSide,
        TestSaveLaunchState expected)
    {
        TestSaveStatusReport? testSave = gameSide?.TestSave;
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

        string code = result.Status switch
        {
            ProjectReviewConsoleInputStatus.InvalidRequest => "reviewConsoleCommandInvalid",
            ProjectReviewConsoleInputStatus.EndpointUnavailable => "reviewCommandEndpointUnavailable",
            ProjectReviewConsoleInputStatus.Rejected => "reviewCommandRejected",
            ProjectReviewConsoleInputStatus.NotExecuted => "reviewCommandNotExecuted",
            ProjectReviewConsoleInputStatus.ScreenUnavailable => "reviewCommandScreenUnavailable",
            ProjectReviewConsoleInputStatus.ExecutedWithFailure => "reviewCommandFailed",
            _ => "reviewCommandDeliveryUnknown",
        };
        string message = result.Error ?? result.Status switch
        {
            ProjectReviewConsoleInputStatus.EndpointUnavailable =>
                "The exact review's command endpoint is unavailable; no command was sent.",
            ProjectReviewConsoleInputStatus.Rejected =>
                "The exact review's command endpoint rejected the request; the command did not run.",
            ProjectReviewConsoleInputStatus.NotExecuted =>
                "The game thread did not run the command in time; it was withdrawn and will not run.",
            ProjectReviewConsoleInputStatus.ScreenUnavailable =>
                "The selected local screen is not active; the command did not run.",
            ProjectReviewConsoleInputStatus.ExecutedWithFailure =>
                "The command ran, but its dispatcher failed; inspect the effect before any retry.",
            _ => "Delivery of the command is unknown; it must not be retried automatically.",
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
