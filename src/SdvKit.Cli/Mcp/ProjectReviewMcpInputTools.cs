using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate ProjectReviewInputExecutionResult ProjectReviewMcpInputRunner(
    ReviewInputQuery query,
    CancellationToken cancellationToken);

internal sealed record ProjectReviewMcpInputAcknowledgement(
    int SchemaVersion,
    string LaunchId,
    string Topology,
    string? Role,
    DateTimeOffset ObservedAtUtc,
    int GameTick,
    string Action,
    bool Succeeded,
    string? Button,
    string? Direction,
    int? X,
    int? Y,
    bool CursorSet,
    bool MenuOpen,
    bool CancellationRequested,
    ReviewInputProblem? Problem);

internal sealed class ProjectReviewMcpInputSession
{
    private readonly ProjectReviewMcpRuntimeReader _reader;
    private readonly ProjectReviewMcpInputRunner _runInput;
    private readonly string _runtimePath;
    private readonly Action<TimeSpan> _delay;
    private readonly TimeSpan _postActionTimeout;
    private int _cleanupRequired;

    public ProjectReviewMcpInputSession(
        ProjectReviewMcpRuntimeReader reader,
        string runtimePath,
        ProjectReviewMcpInputRunner runInput,
        Action<TimeSpan>? delay = null,
        TimeSpan? postActionTimeout = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        _runtimePath = runtimePath;
        _runInput = runInput ?? throw new ArgumentNullException(nameof(runInput));
        _delay = delay ?? Thread.Sleep;
        _postActionTimeout = postActionTimeout ?? TimeSpan.FromSeconds(5);
        if (_postActionTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(postActionTimeout));
        }
    }

    public ProjectReviewMcpInputInvocation Execute(
        ReviewInputQuery query,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ProjectReviewMcpInputInvocation.Error(
                "inputRequestCanceled",
                "The review-input request was canceled before dispatch.");
        }

        ProjectReviewActionLock? actionLock;
        try
        {
            actionLock = ProjectReviewActionLock.TryAcquire(_runtimePath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or System.Security.SecurityException)
        {
            return ProjectReviewMcpInputInvocation.Error(
                "inputLockInvalid",
                "The exact review action lock could not be validated.");
        }

        if (actionLock is null)
        {
            return ProjectReviewMcpInputInvocation.Error(
                "inputBusy",
                "Another bounded MCP review action is already running; the request was rejected instead of queued.");
        }

        using (actionLock)
        {
            ProjectReviewMcpReadResult before = _reader.Read();
            if (!before.Succeeded)
            {
                return ProjectReviewMcpInputInvocation.Error(
                    before.ErrorCode!,
                    before.ErrorMessage!);
            }
            if (!HasPublishedForeground(before.Snapshot!))
            {
                return ProjectReviewMcpInputInvocation.Error(
                    "inputForegroundUnavailable",
                    "AlwaysOn has not published a valid foreground window handle and process ID for the exact review binding.");
            }

            ProjectReviewInputExecutionResult executed = _runInput(
                query,
                cancellationToken);
            if (executed.ActionMayHaveRun)
            {
                Interlocked.Exchange(ref _cleanupRequired, 1);
            }
            if (executed.Response is null)
            {
                ReviewInputProblem problem = executed.Problems.Count > 0
                    ? executed.Problems[0]
                    : new ReviewInputProblem(
                        "inputTransportFailed",
                        "The bounded review-input request did not return an acknowledgement.");
                return ProjectReviewMcpInputInvocation.Error(
                    problem.Code,
                    problem.Message,
                    actionMayHaveRun: executed.ActionMayHaveRun);
            }

            ReviewInputResponseEnvelope response = executed.Response;
            ProjectReviewMcpReadResult after = WaitForPostActionStatus(
                before.Snapshot!,
                response);
            if (!after.Succeeded)
            {
                return ProjectReviewMcpInputInvocation.Error(
                    after.ErrorCode!,
                    after.ErrorMessage!,
                    actionMayHaveRun: true);
            }

            bool cancellationRequested = executed.CancellationRequested
                || cancellationToken.IsCancellationRequested;
            if (string.Equals(
                    query.Action,
                    ReviewInputContract.CursorClearAction,
                    StringComparison.Ordinal)
                && response.Succeeded)
            {
                Interlocked.Exchange(ref _cleanupRequired, 0);
            }

            return new ProjectReviewMcpInputInvocation(
                new ProjectReviewMcpInputAcknowledgement(
                    ReviewInputContract.SchemaVersion,
                    after.Snapshot!.LaunchId,
                    after.Snapshot.Topology,
                    after.Snapshot.Role,
                    response.ObservedAtUtc,
                    response.GameTick,
                    response.Action,
                    response.Succeeded,
                    response.Button,
                    response.Direction,
                    response.X,
                    response.Y,
                    response.CursorSet,
                    response.MenuOpen,
                    cancellationRequested,
                    response.Problem),
                cancellationRequested
                    ? FindProblem(executed.Problems, "inputRequestCanceled")
                        ?? new ReviewInputProblem(
                            "inputRequestCanceled",
                            "The review-input request was canceled after dispatch; its validated acknowledgement and post-action binding were retained, and it was not retried.")
                    : response.Problem ?? FirstProblem(executed.Problems),
                ActionMayHaveRun: true);
        }
    }

    public ReviewInputProblem? Cleanup()
    {
        if (Interlocked.CompareExchange(ref _cleanupRequired, 0, 0) == 0)
        {
            return null;
        }

        ProjectReviewMcpInputInvocation cleanup = Execute(
            new ReviewInputQuery(
                ReviewInputContract.CursorClearAction,
                null,
                null,
                null,
                null),
            CancellationToken.None);
        return cleanup.Acknowledgement is { Succeeded: true }
            ? null
            : cleanup.Problem
                ?? new ReviewInputProblem(
                    "inputCleanupFailed",
                    "Transient review-input state could not be confirmed clear.");
    }

    private static bool SameBinding(
        ProjectReviewMcpRuntimeSnapshot before,
        ProjectReviewMcpRuntimeSnapshot after) =>
        string.Equals(before.LaunchId, after.LaunchId, StringComparison.Ordinal)
        && string.Equals(before.Topology, after.Topology, StringComparison.Ordinal)
        && string.Equals(before.Role, after.Role, StringComparison.Ordinal)
        && string.Equals(before.Target.UniqueId, after.Target.UniqueId, StringComparison.Ordinal)
        && string.Equals(before.Target.Version, after.Target.Version, StringComparison.Ordinal)
        && string.Equals(before.Target.BuildIdentity, after.Target.BuildIdentity, StringComparison.Ordinal)
        && before.ForegroundWindowHandle == after.ForegroundWindowHandle
        && before.ForegroundProcessId == after.ForegroundProcessId;

    private ProjectReviewMcpReadResult WaitForPostActionStatus(
        ProjectReviewMcpRuntimeSnapshot before,
        ReviewInputResponseEnvelope response)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            ProjectReviewMcpReadResult after = _reader.Read();
            if (after.Succeeded)
            {
                ProjectReviewMcpRuntimeSnapshot snapshot = after.Snapshot!;
                if (!SameBinding(before, snapshot)
                    || !HasPublishedForeground(snapshot))
                {
                    return new ProjectReviewMcpReadResult(
                        null,
                        "inputBindingChanged",
                        "The exact review or foreground-window binding changed while the bounded input action ran; do not retry it automatically.");
                }

                if (snapshot.StatusTick > before.StatusTick
                    && snapshot.StatusTick > response.GameTick
                    && snapshot.StatusObservedAtUtc > before.StatusObservedAtUtc
                    && snapshot.StatusObservedAtUtc > response.ObservedAtUtc)
                {
                    return after;
                }
            }

            if (stopwatch.Elapsed >= _postActionTimeout)
            {
                return new ProjectReviewMcpReadResult(
                    null,
                    "inputPostStateTimedOut",
                    "AlwaysOn did not publish a newer status tick after the acknowledgement; the action was not retried.");
            }

            _delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private static bool HasPublishedForeground(
        ProjectReviewMcpRuntimeSnapshot snapshot) =>
        snapshot.ForegroundWindowHandle is > 0
        && snapshot.ForegroundProcessId is > 0;

    private static ReviewInputProblem? FirstProblem(
        IReadOnlyList<ReviewInputProblem> problems) =>
        problems.Count == 0 ? null : problems[0];

    private static ReviewInputProblem? FindProblem(
        IReadOnlyList<ReviewInputProblem> problems,
        string code)
    {
        for (var index = 0; index < problems.Count; index++)
        {
            if (string.Equals(problems[index].Code, code, StringComparison.Ordinal))
            {
                return problems[index];
            }
        }

        return null;
    }
}

internal sealed record ProjectReviewMcpInputInvocation(
    ProjectReviewMcpInputAcknowledgement? Acknowledgement,
    ReviewInputProblem? Problem,
    bool ActionMayHaveRun)
{
    public static ProjectReviewMcpInputInvocation Error(
        string code,
        string message,
        bool actionMayHaveRun = false) =>
        new(null, new ReviewInputProblem(code, message), actionMayHaveRun);
}

internal static class ProjectReviewMcpInputTools
{
    internal const string PressToolName = "stardew_input_press";
    internal const string CursorSetToolName = "stardew_input_cursor_set";
    internal const string CursorClearToolName = "stardew_input_cursor_clear";
    internal const string WheelToolName = "stardew_input_wheel";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    private static readonly JsonElement PressInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["button"],
          "properties": {
            "button": { "type": "string", "pattern": "^[A-Za-z0-9]{1,64}$", "not": { "enum": ["MouseWheelUp", "MouseWheelDown"] } }
          }
        }
        """);
    private static readonly JsonElement CursorSetInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["x", "y"],
          "properties": {
            "x": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "y": { "type": "integer", "minimum": 0, "maximum": 2147483647 }
          }
        }
        """);
    private static readonly JsonElement EmptyInputSchema = ParseSchema(
        """{ "type": "object", "additionalProperties": false }""");
    private static readonly JsonElement WheelInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["direction"],
          "properties": {
            "direction": { "type": "string", "enum": ["up", "down"] }
          }
        }
        """);
    private static readonly JsonElement OutputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "launchId", "topology", "role", "observedAtUtc", "gameTick", "action", "succeeded", "button", "direction", "x", "y", "cursorSet", "menuOpen", "cancellationRequested", "problem"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "launchId": { "type": "string", "pattern": "^[0-9a-f]{32}$" },
            "topology": { "type": "string", "enum": ["single", "network-2"] },
            "role": { "type": ["string", "null"], "enum": [null, "host", "farmhand"] },
            "observedAtUtc": { "type": "string", "format": "date-time" },
            "gameTick": { "type": "integer", "minimum": 0 },
            "action": { "type": "string", "enum": ["press", "cursorSet", "cursorClear", "wheel"] },
            "succeeded": { "type": "boolean" },
            "button": { "type": ["string", "null"], "pattern": "^[A-Za-z0-9]{1,64}$" },
            "direction": { "type": ["string", "null"], "enum": [null, "up", "down"] },
            "x": { "type": ["integer", "null"], "minimum": 0 },
            "y": { "type": ["integer", "null"], "minimum": 0 },
            "cursorSet": { "type": "boolean" },
            "menuOpen": { "type": "boolean" },
            "cancellationRequested": { "type": "boolean" },
            "problem": {
              "type": ["object", "null"],
              "additionalProperties": false,
              "required": ["code", "message"],
              "properties": {
                "code": { "type": "string", "pattern": "^[A-Za-z0-9]{1,64}$" },
                "message": { "type": "string", "minLength": 1, "maxLength": 256 }
              }
            }
          }
        }
        """);

    public static IReadOnlyList<McpServerTool> Create(
        ProjectReviewMcpInputSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return
        [
            new InputMcpTool(
                session,
                Tool(
                    PressToolName,
                    "Press one exact SMAPI button for one bounded input tick in the selected review role.",
                    PressInputSchema,
                    destructive: true,
                    idempotent: false),
                TryPress),
            new InputMcpTool(
                session,
                Tool(
                    CursorSetToolName,
                    "Set the process-local virtual cursor at one UI coordinate without moving the physical pointer.",
                    CursorSetInputSchema,
                    destructive: false,
                    idempotent: true),
                TryCursorSet),
            new InputMcpTool(
                session,
                Tool(
                    CursorClearToolName,
                    "Clear the process-local virtual cursor and transient background input state.",
                    EmptyInputSchema,
                    destructive: false,
                    idempotent: true),
                TryCursorClear),
            new InputMcpTool(
                session,
                Tool(
                    WheelToolName,
                    "Send one directional virtual mouse-wheel notch to the active menu at the virtual cursor.",
                    WheelInputSchema,
                    destructive: false,
                    idempotent: false),
                TryWheel),
        ];
    }

    private sealed class InputMcpTool(
        ProjectReviewMcpInputSession session,
        Tool tool,
        TryCreateQuery tryCreateQuery)
        : McpServerTool
    {
        public override Tool ProtocolTool { get; } = tool;

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!tryCreateQuery(request.Params?.Arguments, out ReviewInputQuery? query))
            {
                return ValueTask.FromResult(Error(
                    "inputArgumentsInvalid",
                    $"Invalid arguments for {ProtocolTool.Name}."));
            }

            ProjectReviewMcpInputInvocation result = session.Execute(
                query!,
                cancellationToken);
            if (result.Acknowledgement is null)
            {
                ReviewInputProblem problem = result.Problem
                    ?? new ReviewInputProblem(
                        "inputUnavailable",
                        "The bounded review-input action is unavailable.");
                string suffix = result.ActionMayHaveRun
                    ? " The action may have run; do not retry it automatically."
                    : string.Empty;
                return ValueTask.FromResult(Error(
                    problem.Code,
                    problem.Message + suffix));
            }

            JsonElement structured = JsonSerializer.SerializeToElement(
                result.Acknowledgement,
                JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = result.Problem is not null
                    || !result.Acknowledgement.Succeeded,
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
            });
        }
    }

    private delegate bool TryCreateQuery(
        IDictionary<string, JsonElement>? arguments,
        out ReviewInputQuery? query);

    private static bool TryPress(
        IDictionary<string, JsonElement>? arguments,
        out ReviewInputQuery? query)
    {
        query = null;
        if (!HasOnly(arguments, ["button"])
            || !TryString(arguments!, "button", out string? button))
        {
            return false;
        }

        query = new ReviewInputQuery(
            ReviewInputContract.PressAction,
            button,
            null,
            null,
            null);
        return ProjectReviewInputService.Validate(query) is null;
    }

    private static bool TryCursorSet(
        IDictionary<string, JsonElement>? arguments,
        out ReviewInputQuery? query)
    {
        query = null;
        if (!HasOnly(arguments, ["x", "y"])
            || !TryInt32(arguments!, "x", out int x)
            || !TryInt32(arguments!, "y", out int y))
        {
            return false;
        }

        query = new ReviewInputQuery(
            ReviewInputContract.CursorSetAction,
            null,
            null,
            x,
            y);
        return ProjectReviewInputService.Validate(query) is null;
    }

    private static bool TryCursorClear(
        IDictionary<string, JsonElement>? arguments,
        out ReviewInputQuery? query)
    {
        query = null;
        if (arguments is { Count: > 0 })
        {
            return false;
        }

        query = new ReviewInputQuery(
            ReviewInputContract.CursorClearAction,
            null,
            null,
            null,
            null);
        return true;
    }

    private static bool TryWheel(
        IDictionary<string, JsonElement>? arguments,
        out ReviewInputQuery? query)
    {
        query = null;
        if (!HasOnly(arguments, ["direction"])
            || !TryString(arguments!, "direction", out string? direction))
        {
            return false;
        }

        query = new ReviewInputQuery(
            ReviewInputContract.WheelAction,
            null,
            direction,
            null,
            null);
        return ProjectReviewInputService.Validate(query) is null;
    }

    private static Tool Tool(
        string name,
        string description,
        JsonElement inputSchema,
        bool destructive,
        bool idempotent) => new()
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = false,
                DestructiveHint = destructive,
                IdempotentHint = idempotent,
                OpenWorldHint = false,
            },
        };

    private static bool HasOnly(
        IDictionary<string, JsonElement>? arguments,
        IReadOnlyCollection<string> names) =>
        arguments is not null
        && arguments.Count == names.Count
        && arguments.Keys.All(names.Contains);

    private static bool TryString(
        IDictionary<string, JsonElement> arguments,
        string name,
        out string? value)
    {
        value = null;
        if (!arguments.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null;
    }

    private static bool TryInt32(
        IDictionary<string, JsonElement> arguments,
        string name,
        out int value)
    {
        value = 0;
        return arguments.TryGetValue(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = $"SDVKit review input unavailable [{code}]: {message}",
            },
        ],
    };

    private static JsonElement ParseSchema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
