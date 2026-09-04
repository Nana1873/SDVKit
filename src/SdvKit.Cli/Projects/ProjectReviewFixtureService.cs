using System.Globalization;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewFixtureService
{
    private const int OperationFailed = 3;
    private static readonly TimeSpan FixtureResponseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SaveResponseTimeout =
        TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static LiveLabCommandResult Execute(
        ReviewFixtureQuery query,
        string topology,
        string? role,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? responseTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);

        ReviewFixtureProblem? queryProblem = Validate(query, topology, role);
        if (queryProblem is not null)
        {
            return Failure(query.Operation, topology, role, queryProblem);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ProjectReviewMcpReadResult preflight;
        LiveLabPaths paths;
        try
        {
            preflight = new ProjectReviewMcpRuntimeReader(labRoot, topology, role).Read();
            if (!preflight.Succeeded
                || preflight.Snapshot?.TestSave is null
                || !preflight.Snapshot.Runtime.WorldReady)
            {
                return Failure(
                    query.Operation,
                    topology,
                    role,
                    Problem(
                        preflight.ErrorCode ?? "fixtureTestSaveRequired",
                        preflight.ErrorMessage
                            ?? "Fixture actions require the exact ready SDVKit-owned test save."));
            }

            LiveLabPaths singlePaths = LiveLabPaths.Resolve(labRoot);
            paths = string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                ? LiveLabPaths.ResolveNetworkRole(singlePaths, role!)
                : singlePaths;
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                query.Operation,
                topology,
                role,
                Problem("fixturePreflightInvalid", exception.Message));
        }

        using ProjectReviewActionLock? actionLock =
            ProjectReviewActionLock.TryAcquire(paths.RuntimePath);
        if (actionLock is null)
        {
            return Failure(
                query.Operation,
                topology,
                role,
                Problem(
                    "fixtureBusy",
                    "Another MCP review action is already in progress for this exact role."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewFixtureTransportContract.ResponsePath(
            paths.RuntimePath,
            requestId);
        DateTimeOffset requestedAtUtc = DateTimeOffset.UtcNow;
        ProjectReviewMcpRuntimeSnapshot expected = preflight.Snapshot!;
        var binding = new ReviewFixtureRequestBinding(
            expected.LaunchId,
            expected.Topology,
            expected.Role,
            expected.TestSave!.FixtureId,
            expected.TestSave.SaveId);
        string command = BuildCommand(requestId, binding, query);
        TimeSpan operationTimeout = responseTimeout
            ?? ResponseTimeoutFor(query.Operation);
        ProjectReviewResponseTransportResult<ReviewFixtureResponseEnvelope> transported =
            ProjectReviewResponseTransport.Execute(
                command,
                responsePath,
                ReviewFixtureTransportContract.MaximumResponseBytes,
                "fixture",
                "review-fixture",
                labRoot,
                bytes => JsonSerializer.Deserialize<ReviewFixtureResponseEnvelope>(
                    bytes,
                    ResponseJsonOptions),
                envelope => Matches(
                    envelope,
                    requestId,
                    binding,
                    query,
                    expected,
                    requestedAtUtc),
                inputSender,
                delay,
                operationTimeout,
                topology,
                role,
                drainAfterDispatchOnCancellation: true,
                cancellationToken: cancellationToken);
        if (transported.Response is null)
        {
            return Failure(
                query.Operation,
                topology,
                role,
                commandWritten: transported.CommandWritten,
                mayHaveRun: transported.CommandMayHaveBeenWritten,
                cancellationRequested: transported.CancellationRequested,
                problems: transported.Problems
                    .Select(problem => Problem(problem.Code, problem.Message))
                    .ToArray());
        }

        ReviewFixtureReport report = transported.Response.Report with
        {
            CommandWritten = transported.CommandWritten,
            MayHaveRun = false,
            CancellationRequested = transported.CancellationRequested,
        };
        return new LiveLabCommandResult(
            report.Problems.Count == 0
                && string.Equals(report.State, "ready", StringComparison.Ordinal)
                    ? 0
                    : OperationFailed,
            report);
    }

    internal static TimeSpan ResponseTimeoutFor(string operation) =>
        operation == ReviewFixtureTransportContract.SaveOperation
            ? SaveResponseTimeout
            : FixtureResponseTimeout;

    internal static string BuildCommand(
        string requestId,
        ReviewFixtureRequestBinding binding,
        ReviewFixtureQuery query)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(query);
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-fixture request ID is invalid.",
                nameof(requestId));
        }

        if (!ReviewTransportToken.IsRequestId(binding.LaunchId)
            || string.IsNullOrWhiteSpace(binding.FixtureId)
            || string.IsNullOrWhiteSpace(binding.SaveId)
            || (!string.Equals(
                    binding.Topology,
                    LiveLabState.SingleTopology,
                    StringComparison.Ordinal)
                && !string.Equals(
                    binding.Topology,
                    NetworkTwoContract.Topology,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The review-fixture identity binding is invalid.",
                nameof(binding));
        }

        string roleToken = binding.Role
            ?? ReviewFixtureTransportContract.SingleRoleToken;
        if ((string.Equals(
                    binding.Topology,
                    LiveLabState.SingleTopology,
                    StringComparison.Ordinal)
                && binding.Role is not null)
            || (string.Equals(
                    binding.Topology,
                    NetworkTwoContract.Topology,
                    StringComparison.Ordinal)
                && (binding.Role is null
                    || !NetworkTwoContract.IsRole(binding.Role))))
        {
            throw new ArgumentException(
                "The review-fixture role binding is invalid.",
                nameof(binding));
        }

        var tokens = new List<string>
        {
            "sdvkit",
            "fixture",
            requestId,
            binding.LaunchId,
            binding.Topology,
            roleToken,
            ReviewTransportToken.Encode(binding.FixtureId),
            ReviewTransportToken.Encode(binding.SaveId),
            query.Operation,
        };
        switch (query.Operation)
        {
            case ReviewFixtureTransportContract.EnterOperation:
                tokens.Add(ReviewTransportToken.Encode(query.Building!));
                break;
            case ReviewFixtureTransportContract.BuildingEnsureOperation:
                tokens.Add(ReviewTransportToken.Encode(query.Alias!));
                tokens.Add(ReviewTransportToken.Encode(query.Kind!));
                tokens.Add(query.X!.Value.ToString(CultureInfo.InvariantCulture));
                tokens.Add(query.Y!.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case ReviewFixtureTransportContract.AnimalEnsureOperation:
                tokens.Add(ReviewTransportToken.Encode(query.Building!));
                tokens.Add(ReviewTransportToken.Encode(query.Kind!));
                break;
        }

        string command = string.Join(" ", tokens);
        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        return command;
    }

    internal static ReviewFixtureProblem? Validate(
        ReviewFixtureQuery query,
        string topology,
        string? role)
    {
        if (!ReviewFixtureTransportContract.IsOperation(query.Operation))
        {
            return Problem("fixtureOperationUnknown", "The fixture operation is unknown.");
        }

        bool single = string.Equals(
            topology,
            LiveLabState.SingleTopology,
            StringComparison.Ordinal);
        bool network = string.Equals(
            topology,
            NetworkTwoContract.Topology,
            StringComparison.Ordinal);
        if ((!single && !network)
            || (single && role is not null)
            || (network && (role is null || !NetworkTwoContract.IsRole(role))))
        {
            return Problem(
                "fixtureSelectionInvalid",
                "The fixture topology and role selection is invalid.");
        }

        bool mutatesWorld = query.Operation is
            ReviewFixtureTransportContract.BuildingEnsureOperation
            or ReviewFixtureTransportContract.AnimalEnsureOperation
            or ReviewFixtureTransportContract.SaveOperation;
        if (mutatesWorld
            && network
            && !string.Equals(role, NetworkTwoContract.HostRole, StringComparison.Ordinal))
        {
            return Problem(
                "fixtureRoleDenied",
                "Fixture world mutations and saves are available only to single-player or the network-2 host.");
        }

        bool noOperands = query.Building is null
            && query.Alias is null
            && query.Kind is null
            && query.X is null
            && query.Y is null;
        if (query.Operation is ReviewFixtureTransportContract.StatusOperation
            or ReviewFixtureTransportContract.FarmOperation
            or ReviewFixtureTransportContract.SaveOperation)
        {
            return noOperands
                ? null
                : Problem("fixtureRequestInvalid", "The fixture operation has unexpected operands.");
        }

        if (query.Operation == ReviewFixtureTransportContract.EnterOperation)
        {
            return IsValidBuildingToken(query.Building)
                && query.Alias is null
                && query.Kind is null
                && query.X is null
                && query.Y is null
                    ? null
                    : Problem(
                        "fixtureBuildingInvalid",
                        "A fixture building must be identified by a valid alias or exact GUID.");
        }

        if (query.Operation == ReviewFixtureTransportContract.BuildingEnsureOperation)
        {
            return IsValidAlias(query.Alias)
                && IsValidKind(query.Kind)
                && query.Building is null
                && query.X is >= 0
                && query.Y is >= 0
                    ? null
                    : Problem(
                        "fixtureBuildingRequestInvalid",
                        "Building ensure requires a valid alias, bounded kind, and non-negative coordinates.");
        }

        return IsValidBuildingToken(query.Building)
            && IsValidKind(query.Kind)
            && query.Alias is null
            && query.X is null
            && query.Y is null
                ? null
                : Problem(
                    "fixtureAnimalRequestInvalid",
                    "Animal ensure requires an exact fixture building and bounded kind.");
    }

    internal static bool Matches(
        ReviewFixtureResponseEnvelope envelope,
        string requestId,
        ReviewFixtureRequestBinding binding,
        ReviewFixtureQuery query,
        ProjectReviewMcpRuntimeSnapshot expected,
        DateTimeOffset requestedAtUtc)
    {
        ReviewFixtureReport? report = envelope.Report;
        if (envelope.SchemaVersion != ReviewFixtureTransportContract.SchemaVersion
            || !string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal)
            || envelope.Binding is null
            || envelope.Binding != binding
            || report is null
            || report.SchemaVersion != ReviewFixtureTransportContract.SchemaVersion
            || !string.Equals(report.Operation, query.Operation, StringComparison.Ordinal)
            || report.CompletedAtUtc < requestedAtUtc
            || report.CompletedAtUtc > DateTimeOffset.UtcNow.AddSeconds(5)
            || !ReviewTransportText.IsWellFormedUtf16(report.Message)
            || report.Message.Length is < 1 or > 4096
            || report.Problems is null
            || report.Problems.Count > 8
            || report.Problems.Any(problem =>
                string.IsNullOrWhiteSpace(problem.Code)
                || problem.Code.Length > 64
                || !ReviewTransportText.IsWellFormedUtf16(problem.Message)
                || problem.Message.Length is < 1 or > 4096))
        {
            return false;
        }

        bool bindingChanged = string.Equals(report.State, "blocked", StringComparison.Ordinal)
            && report.Problems.Any(problem => string.Equals(
                problem.Code,
                "fixtureBindingChanged",
                StringComparison.Ordinal));
        if (bindingChanged
            && (!ReviewTransportToken.IsRequestId(report.LaunchId)
                || (!string.Equals(
                        report.Topology,
                        LiveLabState.SingleTopology,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        report.Topology,
                        NetworkTwoContract.Topology,
                        StringComparison.Ordinal))))
        {
            return false;
        }

        if (!bindingChanged
            && (!string.Equals(report.LaunchId, expected.LaunchId, StringComparison.Ordinal)
                || !string.Equals(report.Topology, expected.Topology, StringComparison.Ordinal)
                || !string.Equals(report.Role, expected.Role, StringComparison.Ordinal)
                || !string.Equals(report.FixtureId, expected.TestSave!.FixtureId, StringComparison.Ordinal)
                || !string.Equals(report.SaveId, expected.TestSave.SaveId, StringComparison.Ordinal)))
        {
            return false;
        }

        if (string.Equals(report.State, "blocked", StringComparison.Ordinal))
        {
            return report.Problems.Count > 0
                && report.Status is null
                && report.Navigation is null
                && report.Building is null
                && report.Animal is null
                && report.Save is null;
        }

        if (!string.Equals(report.State, "ready", StringComparison.Ordinal)
            || report.Problems.Count != 0)
        {
            return false;
        }

        return query.Operation switch
        {
            ReviewFixtureTransportContract.StatusOperation =>
                report.Status is not null
                && report.Status.Buildings is not null
                && report.Navigation is null
                && report.Building is null
                && report.Animal is null
                && report.Save is null,
            ReviewFixtureTransportContract.EnterOperation
                or ReviewFixtureTransportContract.FarmOperation =>
                report.Navigation is not null
                && report.Status is null
                && report.Building is null
                && report.Animal is null
                && report.Save is null,
            ReviewFixtureTransportContract.BuildingEnsureOperation =>
                report.Building is not null
                && string.Equals(report.Building.Alias, query.Alias, StringComparison.Ordinal)
                && report.Building.X == query.X
                && report.Building.Y == query.Y
                && report.Status is null
                && report.Navigation is null
                && report.Animal is null
                && report.Save is null,
            ReviewFixtureTransportContract.AnimalEnsureOperation =>
                report.Animal is not null
                && report.Animal.Assigned
                && string.Equals(
                    report.Animal.CanonicalToken,
                    StableIdentityNormalizer.Normalize(query.Kind!),
                    StringComparison.Ordinal)
                && report.Status is null
                && report.Navigation is null
                && report.Building is null
                && report.Save is null,
            ReviewFixtureTransportContract.SaveOperation =>
                report.Save is not null
                && string.Equals(report.Save.SaveId, expected.TestSave!.SaveId, StringComparison.Ordinal)
                && report.Save.PersistedAtUtc >= requestedAtUtc
                && report.Status is null
                && report.Navigation is null
                && report.Building is null
                && report.Animal is null,
            _ => false,
        };
    }

    private static bool IsValidKind(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= ReviewFixtureTransportContract.MaximumTokenLength
        && !value.Any(char.IsControl);

    private static bool IsValidAlias(string? alias) =>
        alias is not null
        && alias.Length is >= 1 and <= 32
        && alias[0] is >= 'a' and <= 'z'
        && alias.All(character =>
            (character >= 'a' && character <= 'z')
            || (character >= '0' && character <= '9')
            || character is '-' or '_');

    private static bool IsValidBuildingToken(string? value) =>
        IsValidAlias(value)
        || (Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty);

    private static LiveLabCommandResult Failure(
        string operation,
        string topology,
        string? role,
        params ReviewFixtureProblem[] problems) =>
        Failure(
            operation,
            topology,
            role,
            commandWritten: false,
            mayHaveRun: false,
            cancellationRequested: false,
            problems: problems);

    private static LiveLabCommandResult Failure(
        string operation,
        string topology,
        string? role,
        bool commandWritten,
        bool mayHaveRun,
        bool cancellationRequested,
        params ReviewFixtureProblem[] problems) =>
        new(
            OperationFailed,
            new ReviewFixtureReport(
                ReviewFixtureTransportContract.SchemaVersion,
                "blocked",
                operation,
                string.Empty,
                topology,
                role,
                DateTimeOffset.UtcNow,
                null,
                null,
                problems.FirstOrDefault()?.Message ?? "The fixture action failed closed.",
                problems,
                CommandWritten: commandWritten,
                MayHaveRun: mayHaveRun,
                CancellationRequested: cancellationRequested));

    private static ReviewFixtureProblem Problem(string code, string message) =>
        new(code, message);

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or JsonException
            or NotSupportedException
            or PathTooLongException
            or SecurityException
            or UnauthorizedAccessException;
}
