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
        if (reader.Topology != "single" || reader.Role is not null) return Failure("worldActionTopologyUnsupported");
        ProjectReviewMcpContextResult context = reader.ReadContext();
        if (!context.Succeeded) return Failure(context.ErrorCode!);
        if (context.Context!.TestSave is null) return Failure("worldActionTestSaveRequired");
        ProjectReviewMcpVerifiedContext permission = expectedContext ?? context.Context;
        if (!permission.AllTargetsReady || permission.TestSave is null
            || !ProjectReviewCpRefresh.SamePermissionBinding(permission, context.Context))
            return Failure("worldActionStartupBindingChanged");
        beforeSnapshotRead?.Invoke();
        ProjectReviewMcpReadResult before = reader.Read();
        if (!before.Succeeded || !before.Snapshot!.Runtime.WorldReady) return Failure("worldActionWorldNotReady");
        if (!SamePermissionSnapshot(permission, before.Snapshot))
            return Failure("worldActionStartupBindingChanged");
        string requestId = Guid.NewGuid().ToString("N");
        string command = string.Create(CultureInfo.InvariantCulture,
            $"sdvkit world-action {requestId} {permission.State.LaunchId} {query.Action} {query.X} {query.Y} {query.TargetInstanceId} {query.TargetRevision} {query.InventoryRevision} single");
        DateTimeOffset started = clock();
        ProjectReviewResponseTransportResult<ReviewWorldActionResponseEnvelope> result =
            ProjectReviewResponseTransport.Execute(command,
                ReviewWorldActionContract.ResponsePath(ProjectReviewInputService.RuntimePath(
                    reader.ProjectRoot, reader.Topology, reader.Role), requestId),
                ReviewWorldActionContract.MaximumResponseBytes, "worldAction", "review-world-action",
                reader.ProjectRoot, Deserialize, response => Valid(response, requestId, query,
                    before.Snapshot, started, clock()), responseTimeout: responseTimeout,
                topology: reader.Topology, role: reader.Role, send: send,
                drainAfterDispatchOnCancellation: true, cancellationToken: cancellationToken,
                onCancellation: () => ProjectReviewInputService.DispatchCancellation(() =>
                    send is null
                        ? ProjectReviewService.ExecuteCommand($"sdvkit world-action cancel {requestId}",
                            reader.Topology, reader.Role, reader.ProjectRoot)
                        : send($"sdvkit world-action cancel {requestId}")));
        if (result.Response is null)
            return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "worldActionResponseInvalid",
                result.CommandMayHaveBeenWritten ? "mayHaveRun" : "notDispatched");
        if (result.CancellationRequested)
            return Failure("worldActionCancellationRequested", "mayHaveRun");
        ProjectReviewMcpContextResult after = reader.ReadContext();
        if (!after.Succeeded || !ProjectReviewCpRefresh.SamePermissionBinding(permission, after.Context!))
            return Failure("worldActionBindingChanged", "mayHaveRun");
        return result.Response.Report;
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
            && report.Topology == "single" && report.Role is null && report.Action == query.Action
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
