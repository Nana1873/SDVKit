using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewWorldActionService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-world-action");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 5,
    };

    internal static ReviewWorldActionReport Execute(ProjectReviewMcpRuntimeReader reader,
        ReviewWorldActionQuery query, Func<string, LiveLabCommandResult>? send = null,
        TimeSpan? responseTimeout = null, Func<DateTimeOffset>? utcNow = null,
        ProjectReviewMcpVerifiedContext? expectedContext = null,
        Action? beforeSnapshotRead = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewWorldActionReport Failure(string code, string dispatch = "notDispatched") =>
            new(1, "unavailable", code, null, reader.Topology, reader.Role, clock(), query.Action,
                query.X, query.Y, dispatch, null, null, null);
        if (ReviewWorldActionContract.Validate(query) is { } problem) return Failure(problem);
        if (Prepare(reader, expectedContext, beforeSnapshotRead, out ProjectReviewMcpVerifiedContext? permission,
                out ProjectReviewMcpRuntimeSnapshot? before) is { } refused)
            return Failure(refused);
        string requestId = Guid.NewGuid().ToString("N");
        string command = reader.SelectCommand(string.Create(CultureInfo.InvariantCulture,
            $"sdvkit world-action {requestId} {permission!.State.LaunchId} {query.Action} {query.X} {query.Y} {query.TargetInstanceId} {query.TargetRevision} {query.InventoryRevision} {SelectionToken(reader)}"));
        DateTimeOffset started = clock();
        ProjectReviewResponseTransportResult<ReviewWorldActionResponseEnvelope> result =
            ProjectReviewResponseTransport.Execute(command,
                ReviewWorldActionContract.ResponsePath(ProjectReviewInputService.RuntimePath(
                    reader.ProjectRoot, reader.Topology, reader.Role), requestId),
                ReviewWorldActionContract.MaximumResponseBytes, "worldAction", "review-world-action",
                reader.ProjectRoot, Deserialize, response => Valid(response, requestId, query,
                    before!, started, clock()), responseTimeout: responseTimeout,
                topology: reader.Topology, role: reader.Role, send: send,
                drainAfterDispatchOnCancellation: true, cancellationToken: cancellationToken,
                onCancellation: () => ProjectReviewInputService.DispatchCancellation(() =>
                    send is null
                        ? ProjectReviewService.ExecuteCommand(reader.SelectCommand($"sdvkit world-action cancel {requestId}"),
                            reader.Topology, reader.Role, reader.ProjectRoot)
                        : send(reader.SelectCommand($"sdvkit world-action cancel {requestId}"))));
        if (result.Response is null)
            return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "worldActionResponseInvalid",
                result.CommandMayHaveBeenWritten ? "mayHaveRun" : "notDispatched");
        if (result.CancellationRequested)
            return Failure("worldActionCancellationRequested", "mayHaveRun");
        if (!BindingHeld(reader, permission))
            return Failure("worldActionBindingChanged", "mayHaveRun");
        return result.Response.Report;
    }

    /// <summary>The selection token sent to the game: the role, local screen or unbound single review.</summary>
    internal static string SelectionToken(ProjectReviewMcpRuntimeReader reader) =>
        ReviewWorldActionContract.SelectionToken(reader.Role, reader.ScreenId);

    /// <summary>
    /// The checks every world action makes before dispatch: the exact ready test save of this review, the startup
    /// permission still naming this launch, role and staged target, and a fresh world-ready snapshot of the selected
    /// role or screen. A network-2 role also needs the joined and passed pair, and a local screen its unchanged
    /// farmer and context. Returns the refusal code, or null with the permission and snapshot.
    /// </summary>
    internal static string? Prepare(ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpVerifiedContext? expectedContext, Action? beforeSnapshotRead,
        out ProjectReviewMcpVerifiedContext? permission, out ProjectReviewMcpRuntimeSnapshot? snapshot)
    {
        permission = null;
        snapshot = null;
        ProjectReviewMcpContextResult context = reader.ReadContext();
        if (!context.Succeeded) return context.ErrorCode!;
        if (context.Context!.TestSave is null) return "worldActionTestSaveRequired";
        ProjectReviewMcpVerifiedContext startup = expectedContext ?? context.Context;
        if (!startup.AllTargetsReady || startup.TestSave is null
            || !ProjectReviewCpRefresh.SameReviewBinding(startup, context.Context))
            return "worldActionStartupBindingChanged";
        beforeSnapshotRead?.Invoke();
        ProjectReviewMcpReadResult before = reader.ReadForAction();
        if (!before.Succeeded)
            return before.ErrorCode is "reviewPairNotReady" or "reviewScreenBindingChanged"
                ? before.ErrorCode : "worldActionWorldNotReady";
        if (!before.Snapshot!.Runtime.WorldReady) return "worldActionWorldNotReady";
        if (!SamePermissionSnapshot(startup, before.Snapshot)) return "worldActionStartupBindingChanged";
        permission = startup;
        snapshot = before.Snapshot;
        return null;
    }

    /// <summary>
    /// After a response: the same launch, role and staged review, a network-2 role's pair still joined and passed,
    /// and for a local screen the same farmer and context.
    /// </summary>
    internal static bool BindingHeld(ProjectReviewMcpRuntimeReader reader, ProjectReviewMcpVerifiedContext permission)
    {
        ProjectReviewMcpContextResult after = reader.ReadContext();
        return after.Succeeded && ProjectReviewCpRefresh.SameReviewBinding(permission, after.Context!)
            && (reader.Role is null && reader.ScreenId is null || reader.ReadForAction().Succeeded);
    }

    internal static bool SamePermissionSnapshot(ProjectReviewMcpVerifiedContext permission,
        ProjectReviewMcpRuntimeSnapshot snapshot) =>
        snapshot.LaunchId == permission.State.LaunchId && snapshot.Topology == permission.Staging.Topology
        && snapshot.Role == permission.Role && snapshot.TestSave == permission.TestSave
        && snapshot.Target.UniqueId == permission.Staging.Target.Manifest.UniqueId
        && snapshot.Target.Version == permission.Staging.Target.Manifest.Version
        && snapshot.Target.BuildIdentity == permission.Staging.Target.BuildIdentity;

    internal static ReviewWorldActionResponseEnvelope? Deserialize(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
        ResponseJson.RequireExactObject(document.RootElement, ["schemaVersion", "requestId", "report"]);
        ResponseJson.RequireExactObject(document.RootElement.GetProperty("report"),
            ["schemaVersion", "state", "errorCode", "launchId", "topology", "role", "observedAtUtc",
                "action", "x", "y", "dispatchState", "button", "startTick", "endTick"]);
        return JsonSerializer.Deserialize<ReviewWorldActionResponseEnvelope>(bytes, JsonOptions);
    }

    internal static bool Valid(ReviewWorldActionResponseEnvelope? envelope, string requestId,
        ReviewWorldActionQuery query, ProjectReviewMcpRuntimeSnapshot expected,
        DateTimeOffset started, DateTimeOffset now)
    {
        if (envelope is null || envelope.SchemaVersion != 1 || envelope.RequestId != requestId) return false;
        ReviewWorldActionReport report = envelope.Report;
        return report is not null && report.SchemaVersion == 1 && report.LaunchId == expected.LaunchId
            && report.Topology == expected.Topology && report.Role == expected.Role && report.Action == query.Action
            && report.X == query.X && report.Y == query.Y && report.ObservedAtUtc.Offset == TimeSpan.Zero
            && report.ObservedAtUtc >= started && report.ObservedAtUtc <= now.AddSeconds(5)
            && now - report.ObservedAtUtc <= TimeSpan.FromSeconds(20)
            && (report.State == "completed" && report.ErrorCode is null && report.DispatchState == "completed"
                    && report.Button == (query.Action == ReviewWorldActionContract.Water ? "MouseLeft" : "MouseRight")
                    && report.StartTick is >= 0 && report.EndTick >= report.StartTick
                || report.State == "unavailable" && ReviewWorldActionContract.IsGameError(report.ErrorCode)
                    && report.DispatchState is "notDispatched" or "mayHaveRun");
    }
}
