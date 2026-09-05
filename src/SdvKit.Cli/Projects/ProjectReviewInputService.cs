using System.Globalization;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal sealed record ProjectReviewInputExecutionResult(
    ReviewInputResponseEnvelope? Response,
    IReadOnlyList<ReviewInputProblem> Problems,
    bool ActionMayHaveRun,
    bool CancellationRequested)
{
    public bool Succeeded => Response is { Succeeded: true } && Problems.Count == 0;
}

internal static class ProjectReviewInputService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-input");

    private const int MaximumJsonDepth = 4;
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumResponseAge = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = MaximumJsonDepth,
    };
    private static readonly JsonDocumentOptions ResponseDocumentOptions = new()
    {
        MaxDepth = MaximumJsonDepth,
    };
    private static readonly HashSet<string> EnvelopeProperties = new(
    [
        "schemaVersion",
        "requestId",
        "observedAtUtc",
        "gameTick",
        "action",
        "succeeded",
        "button",
        "direction",
        "x",
        "y",
        "cursorSet",
        "menuOpen",
        "problem",
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> ProblemProperties = new(
        ["code", "message"],
        StringComparer.Ordinal);

    public static ProjectReviewInputExecutionResult Execute(
        ReviewInputQuery query,
        string labRoot,
        string topology,
        string? role,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? responseTimeout = null,
        Func<DateTimeOffset>? utcNow = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        Func<DateTimeOffset> now = utcNow ?? (() => DateTimeOffset.UtcNow);

        ReviewInputProblem? validationProblem = Validate(query);
        if (validationProblem is not null)
        {
            return Failure(validationProblem);
        }

        try
        {
            LiveLabPaths singlePaths = LiveLabPaths.Resolve(labRoot);
            LiveLabPaths actionPaths = string.Equals(
                    topology,
                    LiveLabState.SingleTopology,
                    StringComparison.Ordinal)
                && role is null
                    ? singlePaths
                    : string.Equals(
                            topology,
                            NetworkTwoContract.Topology,
                            StringComparison.Ordinal)
                        && role is not null
                        && NetworkTwoContract.IsRole(role)
                            ? LiveLabPaths.ResolveNetworkRole(singlePaths, role!)
                            : throw new ArgumentException(
                                "The review-input topology and role selection is invalid.");
            string requestId = Guid.NewGuid().ToString("N");
            string responsePath = ReviewInputContract.ResponsePath(
                actionPaths.RuntimePath,
                requestId);
            string command = BuildCommand(requestId, query);
            DateTimeOffset requestedAtUtc = now();
            ProjectReviewResponseTransportResult<ReviewInputResponseEnvelope> transported =
                ProjectReviewResponseTransport.Execute(
                    command,
                    responsePath,
                    ReviewInputContract.MaximumResponseBytes,
                    "input",
                    "review-input",
                    labRoot,
                    DeserializeResponse,
                    envelope => MatchesResponse(
                        envelope,
                        requestId,
                        query,
                        requestedAtUtc,
                        now()),
                    inputSender,
                    delay,
                    responseTimeout,
                    topology,
                    role,
                    drainAfterDispatchOnCancellation: true,
                    cancellationToken: cancellationToken);

            if (transported.Response is null)
            {
                return new ProjectReviewInputExecutionResult(
                    null,
                    transported.Problems
                        .Select(problem => new ReviewInputProblem(
                            problem.Code,
                            problem.Message))
                        .ToArray(),
                    transported.CommandMayHaveBeenWritten,
                    transported.CancellationRequested);
            }

            ReviewInputResponseEnvelope response = transported.Response;
            return new ProjectReviewInputExecutionResult(
                response,
                transported.Problems
                    .Select(problem => new ReviewInputProblem(
                        problem.Code,
                        problem.Message))
                    .Concat(response.Problem is null
                        ? []
                        : [response.Problem])
                    .ToArray(),
                transported.CommandMayHaveBeenWritten,
                transported.CancellationRequested);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(new ReviewInputProblem(
                "inputTransportInvalid",
                $"The bounded review-input request could not be prepared ({exception.GetType().Name})."));
        }
    }

    internal static ReviewInputProblem? Validate(ReviewInputQuery query)
    {
        if (string.Equals(query.Action, ReviewInputContract.PressAction, StringComparison.Ordinal))
        {
            return IsButton(query.Button)
                && !IsWheelButton(query.Button)
                && query.Direction is null
                && query.X is null
                && query.Y is null
                    ? null
                    : Problem("inputArgumentsInvalid", "A press requires one exact non-wheel SMAPI button name.");
        }

        if (string.Equals(query.Action, ReviewInputContract.WheelAction, StringComparison.Ordinal))
        {
            return query.Button is null
                && query.Direction is "up" or "down"
                && query.X is null
                && query.Y is null
                    ? null
                    : Problem("inputArgumentsInvalid", "A wheel action requires exactly direction 'up' or 'down'.");
        }

        if (string.Equals(query.Action, ReviewInputContract.CursorSetAction, StringComparison.Ordinal))
        {
            return query.Button is null
                && query.Direction is null
                && query.X is >= 0
                && query.Y is >= 0
                    ? null
                    : Problem("inputArgumentsInvalid", "A cursor-set action requires two non-negative UI coordinates.");
        }

        if (string.Equals(query.Action, ReviewInputContract.CursorClearAction, StringComparison.Ordinal))
        {
            return query.Button is null
                && query.Direction is null
                && query.X is null
                && query.Y is null
                    ? null
                    : Problem("inputArgumentsInvalid", "A cursor-clear action accepts no values.");
        }

        return Problem("inputActionUnknown", "The review-input action is unsupported.");
    }

    internal static string BuildCommand(string requestId, ReviewInputQuery query)
    {
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-input request ID is invalid.",
                nameof(requestId));
        }
        if (Validate(query) is ReviewInputProblem problem)
        {
            throw new ArgumentException(problem.Message, nameof(query));
        }

        string action = query.Action switch
        {
            ReviewInputContract.PressAction => $"press {query.Button}",
            ReviewInputContract.WheelAction => $"wheel {query.Direction}",
            ReviewInputContract.CursorSetAction => string.Create(
                CultureInfo.InvariantCulture,
                $"cursor {query.X} {query.Y}"),
            ReviewInputContract.CursorClearAction => "cursor clear",
            _ => throw new ArgumentException(
                "The review-input action is unsupported.",
                nameof(query)),
        };
        string command = $"sdvkit input request {requestId} {action}";
        string? commandProblem = ProjectReviewConsoleLine.ValidationError(command);
        if (commandProblem is not null)
        {
            throw new InvalidDataException(commandProblem);
        }

        return command;
    }

    internal static ReviewInputResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using (JsonDocument document = JsonDocument.Parse(bytes, ResponseDocumentOptions))
        {
            JsonElement root = document.RootElement;
            ResponseJson.RequireExactObject(root, EnvelopeProperties);
            RequireKind(root, "schemaVersion", JsonValueKind.Number);
            RequireKind(root, "requestId", JsonValueKind.String);
            RequireKind(root, "observedAtUtc", JsonValueKind.String);
            RequireKind(root, "gameTick", JsonValueKind.Number);
            RequireKind(root, "action", JsonValueKind.String);
            RequireBoolean(root, "succeeded");
            RequireNullableKind(root, "button", JsonValueKind.String);
            RequireNullableKind(root, "direction", JsonValueKind.String);
            RequireNullableKind(root, "x", JsonValueKind.Number);
            RequireNullableKind(root, "y", JsonValueKind.Number);
            RequireBoolean(root, "cursorSet");
            RequireBoolean(root, "menuOpen");
            JsonElement problem = root.GetProperty("problem");
            if (problem.ValueKind != JsonValueKind.Null)
            {
                ResponseJson.RequireExactObject(problem, ProblemProperties);
                RequireKind(problem, "code", JsonValueKind.String);
                RequireKind(problem, "message", JsonValueKind.String);
            }
        }

        return JsonSerializer.Deserialize<ReviewInputResponseEnvelope>(
            bytes,
            ResponseJsonOptions);
    }

    internal static bool MatchesResponse(
        ReviewInputResponseEnvelope? response,
        string requestId,
        ReviewInputQuery query,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (response is null
            || response.SchemaVersion != ReviewInputContract.SchemaVersion
            || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal)
            || !string.Equals(response.Action, query.Action, StringComparison.Ordinal)
            || response.GameTick < 0
            || response.ObservedAtUtc < requestedAtUtc - MaximumClockSkew
            || response.ObservedAtUtc > nowUtc + MaximumClockSkew
            || nowUtc - response.ObservedAtUtc > MaximumResponseAge
            || response.Succeeded != (response.Problem is null)
            || response.Problem is not null
                && (!IsProblemToken(response.Problem.Code)
                    || string.IsNullOrWhiteSpace(response.Problem.Message)
                    || response.Problem.Message.Length > ReviewInputContract.MaximumProblemLength
                    || !ReviewTransportText.IsWellFormedUtf16(response.Problem.Message)))
        {
            return false;
        }

        return query.Action switch
        {
            ReviewInputContract.PressAction =>
                IsButton(response.Button)
                && string.Equals(response.Button, query.Button, StringComparison.OrdinalIgnoreCase)
                && response.Direction is null
                && response.X is null
                && response.Y is null,
            ReviewInputContract.WheelAction =>
                response.Button is null
                && string.Equals(response.Direction, query.Direction, StringComparison.Ordinal)
                && response.X is null
                && response.Y is null
                && (!response.Succeeded || response.CursorSet && response.MenuOpen),
            ReviewInputContract.CursorSetAction =>
                response.Button is null
                && response.Direction is null
                && response.X == query.X
                && response.Y == query.Y
                && (!response.Succeeded || response.CursorSet),
            ReviewInputContract.CursorClearAction =>
                response.Button is null
                && response.Direction is null
                && response.X is null
                && response.Y is null
                && (!response.Succeeded || !response.CursorSet),
            _ => false,
        };
    }

    internal static string RuntimePath(
        string labRoot,
        string topology,
        string? role)
    {
        LiveLabPaths paths = LiveLabPaths.Resolve(labRoot);
        return string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            && role is null
                ? paths.RuntimePath
                : string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                    && role is not null
                    && NetworkTwoContract.IsRole(role)
                        ? LiveLabPaths.ResolveNetworkRole(paths, role!).RuntimePath
                        : throw new ArgumentException(
                            "The review-input topology and role selection is invalid.");
    }

    private static ProjectReviewInputExecutionResult Failure(
        params ReviewInputProblem[] problems) =>
        new(null, problems, ActionMayHaveRun: false, CancellationRequested: false);

    private static ReviewInputProblem Problem(string code, string message) =>
        new(code, message);

    private static bool IsButton(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value.All(character =>
            character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9');

    private static bool IsWheelButton(string? value) =>
        string.Equals(value, "MouseWheelUp", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "MouseWheelDown", StringComparison.OrdinalIgnoreCase);

    private static bool IsProblemToken(string value) =>
        value.Length is >= 1 and <= 64
        && value.All(character =>
            character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9');

    private static void RequireKind(
        JsonElement value,
        string name,
        JsonValueKind kind)
    {
        JsonElement property = value.GetProperty(name);
        if (property.ValueKind != kind
            || kind == JsonValueKind.Number && !property.TryGetInt32(out _))
        {
            throw new InvalidDataException(
                "The review-input response has an invalid member type.");
        }
    }

    private static void RequireNullableKind(
        JsonElement value,
        string name,
        JsonValueKind kind)
    {
        JsonElement property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Null
            && (property.ValueKind != kind
                || kind == JsonValueKind.Number && !property.TryGetInt32(out _)))
        {
            throw new InvalidDataException(
                "The review-input response has an invalid nullable member type.");
        }
    }

    private static void RequireBoolean(JsonElement value, string name)
    {
        if (value.GetProperty(name).ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                "The review-input response has an invalid Boolean member.");
        }
    }

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
