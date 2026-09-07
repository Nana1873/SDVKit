using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    ReviewInputProblem? Problem,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Buttons = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationTicks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StartTick = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndTick = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Released = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Modifiers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Notches = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndY = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CompletedSteps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FinalX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FinalY = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DeliveredScalars = null);


internal sealed class ProjectReviewMcpInputSession
{
    private readonly ProjectReviewMcpRuntimeReader _reader;
    private readonly ProjectReviewMcpInputRunner _runInput;
    private readonly string _runtimePath;
    private readonly Action<TimeSpan> _delay;
    private readonly TimeSpan _postActionTimeout;
    private readonly TimeSpan _cleanupTimeout;
    private readonly object _executionSync = new();
    private CancellationTokenSource? _activeCancellation;
    private TaskCompletionSource? _activeCompletion;
    private bool _stopping;
    private int _cleanupRequired;

    public ProjectReviewMcpInputSession(
        ProjectReviewMcpRuntimeReader reader,
        string runtimePath,
        ProjectReviewMcpInputRunner runInput,
        Action<TimeSpan>? delay = null,
        TimeSpan? postActionTimeout = null,
        TimeSpan? cleanupTimeout = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        _runtimePath = runtimePath;
        _runInput = runInput ?? throw new ArgumentNullException(nameof(runInput));
        _delay = delay ?? Thread.Sleep;
        _postActionTimeout = postActionTimeout ?? TimeSpan.FromSeconds(5);
        _cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(25);
        if (_postActionTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(postActionTimeout));
        }
        if (_cleanupTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        }
    }

    public ProjectReviewMcpInputInvocation Execute(
        ReviewInputQuery query,
        CancellationToken cancellationToken) =>
        ExecuteWithLifetime(query, cleanup: false, cancellationToken);

    private ProjectReviewMcpInputInvocation ExecuteWithLifetime(
        ReviewInputQuery query,
        bool cleanup,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_executionSync)
        {
            if (_stopping && !cleanup)
            {
                return ProjectReviewMcpInputInvocation.Error(
                    "inputSessionStopping", "The input session is closing; no new action was dispatched.");
            }
            if (_activeCancellation is not null)
            {
                return ProjectReviewMcpInputInvocation.Error(
                    "inputBusy", "Another bounded input action is already running; the request was not queued.");
            }
            _activeCancellation = linked;
            _activeCompletion = completion;
        }

        try
        {
            return ExecuteLocked(query, linked.Token);
        }
        finally
        {
            lock (_executionSync)
            {
                _activeCancellation = null;
                _activeCompletion = null;
                completion.SetResult();
            }
        }
    }

    private ProjectReviewMcpInputInvocation ExecuteLocked(
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

            int previousCleanup = Interlocked.Exchange(ref _cleanupRequired, 1);
            ProjectReviewInputExecutionResult executed = _runInput(
                query,
                cancellationToken);
            if (!executed.ActionMayHaveRun)
            {
                Interlocked.Exchange(ref _cleanupRequired, previousCleanup);
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
                    response.Problem,
                    response.Buttons,
                    response.DurationTicks,
                    response.StartTick,
                    response.EndTick,
                    response.Released, response.Modifiers, response.Count, response.Notches,
                    response.EndX, response.EndY, response.CompletedSteps, response.FinalX, response.FinalY, response.DeliveredScalars),
                cancellationRequested
                    ? FindProblem(executed.Problems, "inputCancellationNotConfirmed")
                        ?? FindProblem(executed.Problems, "inputRequestCanceled")
                        ?? new ReviewInputProblem(
                            "inputRequestCanceled",
                            "The review-input request was canceled after dispatch; its validated acknowledgement and post-action binding were retained, and it was not retried.")
                    : response.Problem ?? FirstProblem(executed.Problems),
                ActionMayHaveRun: true);
        }
    }

    public ReviewInputProblem? Cleanup()
    {
        Task? pending = StopInput();
        if (pending is not null && !pending.Wait(_cleanupTimeout))
        {
            return new ReviewInputProblem(
                "inputCleanupTimedOut",
                "The pending input action did not finish its bounded cancellation drain; cleanup was not confirmed.");
        }
        if (Interlocked.CompareExchange(ref _cleanupRequired, 0, 0) == 0)
        {
            return null;
        }

        ProjectReviewMcpInputInvocation cleanup = ExecuteWithLifetime(
            new ReviewInputQuery(
                ReviewInputContract.CursorClearAction,
                null,
                null,
                null,
                null),
            cleanup: true,
            CancellationToken.None);
        return cleanup.Acknowledgement is { Succeeded: true }
            ? null
            : cleanup.Problem
                ?? new ReviewInputProblem(
                    "inputCleanupFailed",
                    "Transient review-input state could not be confirmed clear.");
    }

    internal void CancelPending() => StopInput();

    private Task? StopInput()
    {
        CancellationTokenSource? cancellation;
        Task? completion;
        lock (_executionSync)
        {
            _stopping = true;
            cancellation = _activeCancellation;
            completion = _activeCompletion?.Task;
        }
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The exact action finished between observing it and requesting cancellation.
        }
        return completion;
    }

    private static bool SameBinding(
        ProjectReviewMcpRuntimeSnapshot before,
        ProjectReviewMcpRuntimeSnapshot after) =>
        string.Equals(before.LaunchId, after.LaunchId, StringComparison.Ordinal)
        && before.UseStatusPipe == after.UseStatusPipe
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
    internal const string ClickToolName = "stardew_input_click";
    internal const string ScrollToolName = "stardew_input_scroll";
    internal const string DragToolName = "stardew_input_drag";
    internal const string TextToolName = "stardew_input_text";
    internal const string ChordToolName = "stardew_input_chord";
    internal const string PressToolName = "stardew_input_press";
    internal const string CursorSetToolName = "stardew_input_cursor_set";
    internal const string CursorClearToolName = "stardew_input_cursor_clear";
    internal const string WheelToolName = "stardew_input_wheel";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    private static readonly JsonElement ChordInputSchema = ParseSchema(
        """
        {"type":"object","additionalProperties":false,"required":["buttons","durationTicks","uiRevision"],
         "properties":{
          "buttons":{"type":"array","minItems":1,"maxItems":8,"uniqueItems":true,
            "items":{"type":"string","pattern":"^[A-Za-z][A-Za-z0-9]{0,63}$"}},
          "durationTicks":{"type":"integer","minimum":1,"maximum":120},
          "uiRevision":{"type":"string","pattern":"^[0-9a-f]{64}$"}}}
        """);
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
            new InputMcpTool(session, Tool(ClickToolName,
                "Click an active menu at one UI coordinate once or twice, with separate press/release edges and optional Shift/Control/Alt modifiers; requires a current uiRevision.",
                GestureInputSchema(ReviewInputContract.ClickAction), true, false), TryClick),
            new InputMcpTool(session, Tool(ScrollToolName,
                "Scroll an active menu at one UI coordinate by -20 to -1 or 1 to 20 notches, one verified notch per input update; requires a current uiRevision.",
                GestureInputSchema(ReviewInputContract.ScrollAction), false, false), TryScroll),
            new InputMcpTool(session, Tool(DragToolName,
                "Drag an active menu from x,y to endX,endY over 1-120 movement updates after the initial press, then release at the endpoint; requires a current uiRevision.",
                GestureInputSchema(ReviewInputContract.DragAction), true, false), TryDrag),
            new InputMcpTool(session,
                Tool(TextToolName,
                    "Deliver 1-256 BMP Unicode characters through the native text event queue to an exact selected NamingMenu TextBox from stardew_menu_get. Requires fresh uiRevision and textField.id; no controls, supplementary scalars, paste or secret fields. Delivery acknowledgement does not prove accepted or persisted text.",
                    ParseSchema("""{"type":"object","additionalProperties":false,"required":["text","fieldId","uiRevision"],"properties":{"text":{"type":"string","minLength":1,"maxLength":256},"fieldId":{"type":"integer","minimum":1},"uiRevision":{"type":"string","pattern":"^[0-9a-f]{64}$"}}}"""), true, false), TryText),
            new InputMcpTool(session,
                Tool(ChordToolName,
                    "Press 1-8 buttons atomically for 1-120 input updates using a fresh stardew_menu_get uiRevision; acknowledge only after release. Ctrl+V is unsupported.",
                    ChordInputSchema, destructive: true, idempotent: false), TryChord),
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

            if (ProjectReviewInputService.Validate(query!) is { } invalid)
                return ValueTask.FromResult(Error(invalid.Code, invalid.Message));

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

    private static bool TryClick(IDictionary<string, JsonElement>? arguments, out ReviewInputQuery? query) =>
        TryGesture(ReviewInputContract.ClickAction, arguments, out query);
    private static bool TryScroll(IDictionary<string, JsonElement>? arguments, out ReviewInputQuery? query) =>
        TryGesture(ReviewInputContract.ScrollAction, arguments, out query);
    private static bool TryDrag(IDictionary<string, JsonElement>? arguments, out ReviewInputQuery? query) =>
        TryGesture(ReviewInputContract.DragAction, arguments, out query);

    private static bool TryGesture(string action, IDictionary<string, JsonElement>? arguments, out ReviewInputQuery? query)
    {
        query = null;
        if (arguments is null || !TryInt32(arguments, "x", out int x) || !TryInt32(arguments, "y", out int y)
            || !TryString(arguments, "uiRevision", out string? revision)) return false;
        var required = new HashSet<string>(StringComparer.Ordinal) { "x", "y", "uiRevision" };
        string? button = null;
        string[]? modifiers = null;
        if (action != ReviewInputContract.ScrollAction)
        {
            required.Add("button");
            if (!TryString(arguments, "button", out button)) return false;
            modifiers = [];
            if (arguments.TryGetValue("modifiers", out JsonElement value))
            {
                required.Add("modifiers");
                if (value.ValueKind != JsonValueKind.Null)
                {
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 6
                        || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) return false;
                    modifiers = value.EnumerateArray().Select(v => v.GetString()!).ToArray();
                }
            }
        }
        int? count = null, notches = null, duration = null, endX = null, endY = null;
        string[] numbers = action switch
        {
            ReviewInputContract.ClickAction => ["count"],
            ReviewInputContract.ScrollAction => ["notches"],
            _ => ["durationTicks", "endX", "endY"],
        };
        foreach (string name in numbers)
        {
            required.Add(name);
            if (!TryInt32(arguments, name, out int number)) return false;
            switch (name)
            {
                case "count": count = number; break;
                case "notches": notches = number; break;
                case "durationTicks": duration = number; break;
                case "endX": endX = number; break;
                case "endY": endY = number; break;
            }
        }
        if (!HasOnly(arguments, required)) return false;
        query = new(action, button, null, x, y, DurationTicks: duration, UiRevision: revision,
            Modifiers: modifiers, Count: count, Notches: notches, EndX: endX, EndY: endY);
        return ProjectReviewInputService.Validate(query) is null;
    }

    private static JsonElement GestureInputSchema(string action)
    {
        JsonNode schema = JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"x\",\"y\",\"uiRevision\"],\"properties\":{}}")!;
        JsonObject props = schema["properties"]!.AsObject();
        props["x"] = JsonNode.Parse("{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}");
        props["y"] = props["x"]!.DeepClone();
        props["uiRevision"] = JsonNode.Parse("{\"type\":\"string\",\"pattern\":\"^[0-9a-f]{64}$\"}");
        if (action != ReviewInputContract.ScrollAction)
        {
            props["button"] = JsonNode.Parse("{\"type\":\"string\",\"enum\":[\"MouseLeft\",\"MouseRight\",\"MouseMiddle\",\"MouseX1\",\"MouseX2\"]}");
            props["modifiers"] = JsonNode.Parse("{\"type\":[\"array\",\"null\"],\"maxItems\":6,\"uniqueItems\":true,\"items\":{\"type\":\"string\",\"enum\":[\"LeftShift\",\"RightShift\",\"LeftControl\",\"RightControl\",\"LeftAlt\",\"RightAlt\"]}}");
            schema["required"]!.AsArray().Add("button");
        }
        string[] numbers = action switch
        {
            ReviewInputContract.ClickAction => ["count"],
            ReviewInputContract.ScrollAction => ["notches"],
            _ => ["durationTicks", "endX", "endY"],
        };
        foreach (string name in numbers)
        {
            props[name] = name switch
            {
                "count" => JsonNode.Parse("{\"type\":\"integer\",\"enum\":[1,2]}"),
                "notches" => JsonNode.Parse("{\"type\":\"integer\",\"minimum\":-20,\"maximum\":20,\"not\":{\"const\":0}}"),
                "durationTicks" => JsonNode.Parse("{\"type\":\"integer\",\"minimum\":1,\"maximum\":120}"),
                _ => props["x"]!.DeepClone(),
            };
            schema["required"]!.AsArray().Add(name);
        }
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonElement GestureOutputSchema(string action)
    {
        JsonNode schema = JsonNode.Parse(OutputSchema.GetRawText())!;
        JsonObject props = schema["properties"]!.AsObject();
        props["action"] = JsonSerializer.SerializeToNode(new { type = "string", @const = action });
        JsonNode input = JsonNode.Parse(GestureInputSchema(action).GetRawText())!;
        foreach (string name in new[] { "modifiers", "count", "notches", "durationTicks", "endX", "endY" })
        {
            if (input["properties"]![name] is not JsonNode property) continue;
            props[name] = property.DeepClone();
            schema["required"]!.AsArray().Add(name);
        }
        foreach (string name in new[] { "startTick", "endTick", "finalX", "finalY", "completedSteps" })
            props[name] = JsonNode.Parse("{\"type\":\"integer\",\"minimum\":0}");
        props["released"] = JsonNode.Parse("{\"type\":\"boolean\"}");
        schema["required"]!.AsArray().Add("completedSteps");
        schema["required"]!.AsArray().Add("released");
        return JsonSerializer.SerializeToElement(schema);
    }

    private static bool TryText(IDictionary<string, JsonElement>? arguments, out ReviewInputQuery? query)
    {
        query = null;
        if (!HasOnly(arguments, ["text", "fieldId", "uiRevision"])
            || !TryString(arguments!, "text", out string? text)
            || !TryString(arguments!, "uiRevision", out string? revision)
            || arguments!["fieldId"].ValueKind != JsonValueKind.Number
            || !arguments["fieldId"].TryGetInt64(out long field)) return false;
        query = new(ReviewInputContract.TextAction, null, null, null, null,
            UiRevision: revision, Text: text, FieldId: field);
        return true;
    }

    private static JsonElement TextOutputSchema()
    {
        JsonNode schema = JsonNode.Parse(OutputSchema.GetRawText())!;
        schema["properties"]!["action"] = JsonNode.Parse("""{"const":"text"}""");
        schema["properties"]!["deliveredScalars"] = JsonNode.Parse("""{"type":"integer","minimum":0,"maximum":256}""");
        schema["required"]!.AsArray().Add("deliveredScalars");
        return JsonSerializer.SerializeToElement(schema);
    }

    private static bool TryChord(IDictionary<string, JsonElement>? arguments, out ReviewInputQuery? query)
    {
        query = null;
        if (!HasOnly(arguments, ["buttons", "durationTicks", "uiRevision"])
            || !TryInt32(arguments!, "durationTicks", out int duration)
            || !TryString(arguments!, "uiRevision", out string? revision)
            || arguments!["buttons"].ValueKind != JsonValueKind.Array) return false;
        JsonElement buttons = arguments["buttons"];
        if (buttons.GetArrayLength() is < 1 or > 8
            || buttons.EnumerateArray().Any(b => b.ValueKind != JsonValueKind.String)) return false;
        query = new(ReviewInputContract.ChordAction, null, null, null, null,
            buttons.EnumerateArray().Select(b => b.GetString()!).ToArray(), duration, revision);
        return ProjectReviewInputService.Validate(query) is null;
    }

    private static JsonElement ChordOutputSchema()
    {
        JsonNode schema = JsonNode.Parse(OutputSchema.GetRawText())!;
        JsonObject properties = schema["properties"]!.AsObject();
        properties["action"] = JsonNode.Parse("{\"type\":\"string\",\"const\":\"chord\"}");
        properties["buttons"] = JsonNode.Parse("{\"type\":\"array\",\"minItems\":1,\"maxItems\":8,\"items\":{\"type\":\"string\"}}");
        properties["durationTicks"] = JsonNode.Parse("{\"type\":\"integer\",\"minimum\":1,\"maximum\":120}");
        properties["startTick"] = JsonNode.Parse("{\"type\":\"integer\",\"minimum\":0}");
        properties["endTick"] = JsonNode.Parse("{\"type\":\"integer\",\"minimum\":0}");
        properties["released"] = JsonNode.Parse("{\"type\":\"boolean\"}");
        foreach (string field in new[] { "buttons", "durationTicks", "released" }) schema["required"]!.AsArray().Add(field);
        return JsonSerializer.SerializeToElement(schema);
    }

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
            OutputSchema = name switch
            {
                ClickToolName => GestureOutputSchema(ReviewInputContract.ClickAction),
                ScrollToolName => GestureOutputSchema(ReviewInputContract.ScrollAction),
                DragToolName => GestureOutputSchema(ReviewInputContract.DragAction),
                ChordToolName => ChordOutputSchema(),
                TextToolName => TextOutputSchema(),
                _ => OutputSchema,
            },
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

        try { value = element.GetString(); }
        catch (InvalidOperationException) { return false; }
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
