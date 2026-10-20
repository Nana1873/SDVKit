using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

/// <summary>
/// Moves the player one tile in a direction, or turns it, in the owned single review. Shares the binding,
/// transport and report checks of <see cref="ProjectReviewWorldActService"/>; the target is the tile next to
/// the player in that direction, derived in the game at dispatch.
/// </summary>
internal static class ProjectReviewWorldMoveService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-world-move");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 6,
    };

    internal static ReviewWorldMoveReport Execute(ProjectReviewMcpRuntimeReader reader,
        ReviewWorldMoveQuery query, Func<string, LiveLabCommandResult>? send = null,
        TimeSpan? responseTimeout = null, Func<DateTimeOffset>? utcNow = null,
        ProjectReviewMcpVerifiedContext? expectedContext = null,
        Action? beforeSnapshotRead = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewWorldMoveReport Failure(string code, string dispatch = "notDispatched") =>
            new(1, "unavailable", code, null, reader.Topology, reader.Role, clock(), query.Direction,
                query.TurnOnly, dispatch, null, null, null, null, null, null, null, null, null, false);
        if (ReviewWorldMoveContract.Validate(query) is { } problem) return Failure(problem);
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
        if (!ProjectReviewWorldActionService.SamePermissionSnapshot(permission, before.Snapshot))
            return Failure("worldActionStartupBindingChanged");
        string requestId = Guid.NewGuid().ToString("N");
        string command = string.Create(CultureInfo.InvariantCulture,
            $"sdvkit world-move {requestId} {permission.State.LaunchId} {query.Direction} {(query.TurnOnly ? "true" : "false")} single");
        DateTimeOffset started = clock();
        ProjectReviewResponseTransportResult<ReviewWorldMoveResponseEnvelope> result =
            ProjectReviewResponseTransport.Execute(command,
                ReviewWorldMoveContract.ResponsePath(ProjectReviewInputService.RuntimePath(
                    reader.ProjectRoot, reader.Topology, reader.Role), requestId),
                ReviewWorldMoveContract.MaximumResponseBytes, "worldAction", "review-world-move",
                reader.ProjectRoot, Deserialize, response => Valid(response, requestId, query,
                    before.Snapshot, started, clock()), responseTimeout: responseTimeout,
                topology: reader.Topology, role: reader.Role, send: send,
                drainAfterDispatchOnCancellation: true, cancellationToken: cancellationToken,
                onCancellation: () => ProjectReviewInputService.DispatchCancellation(() =>
                    send is null
                        ? ProjectReviewService.ExecuteCommand($"sdvkit world-move cancel {requestId}",
                            reader.Topology, reader.Role, reader.ProjectRoot)
                        : send($"sdvkit world-move cancel {requestId}")));
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

    internal static ReviewWorldMoveResponseEnvelope? Deserialize(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 6 });
        ResponseJson.RequireExactObject(document.RootElement, ["schemaVersion", "requestId", "report"]);
        ResponseJson.RequireExactObject(document.RootElement.GetProperty("report"),
            ["schemaVersion", "state", "errorCode", "launchId", "topology", "role", "observedAtUtc",
                "direction", "turnOnly", "dispatchState", "button", "startTick", "endTick",
                "locationId", "tileX", "tileY", "position", "targetX", "targetY", "reached"]);
        return JsonSerializer.Deserialize<ReviewWorldMoveResponseEnvelope>(bytes, JsonOptions);
    }

    internal static bool Valid(ReviewWorldMoveResponseEnvelope? envelope, string requestId,
        ReviewWorldMoveQuery query, ProjectReviewMcpRuntimeSnapshot expected,
        DateTimeOffset started, DateTimeOffset now)
    {
        if (envelope is null || envelope.SchemaVersion != 1 || envelope.RequestId != requestId) return false;
        ReviewWorldMoveReport report = envelope.Report;
        if (report is null || report.SchemaVersion != 1 || report.LaunchId != expected.LaunchId
            || report.Topology != "single" || report.Role is not null || report.Direction != query.Direction
            || report.TurnOnly != query.TurnOnly || report.ObservedAtUtc.Offset != TimeSpan.Zero
            || report.ObservedAtUtc < started || report.ObservedAtUtc > now.AddSeconds(5)
            || now - report.ObservedAtUtc > TimeSpan.FromSeconds(20)
            || !ObservationValid(report))
            return false;
        if (report.State == "completed")
            return report.ErrorCode is null && report.DispatchState == "completed" && report.Button is not null
                && report.StartTick is >= 0 && report.EndTick >= report.StartTick
                && report.TileX is not null && report.TileY is not null && report.LocationId is not null
                && report.TargetX is not null && report.TargetY is not null
                && (!report.Reached || report.LocationId is not null && report.Position is not null);
        return report.State == "unavailable" && ReviewWorldMoveContract.IsGameError(report.ErrorCode)
            && report.DispatchState is "notDispatched" or "mayHaveRun" && !report.Reached;
    }

    private static bool ObservationValid(ReviewWorldMoveReport report) =>
        (report.TileX is null || Math.Abs((long)report.TileX.Value) <= ReviewWorldContract.MaximumTileCoordinate)
        && (report.TileY is null || Math.Abs((long)report.TileY.Value) <= ReviewWorldContract.MaximumTileCoordinate)
        && (report.TargetX is null || Math.Abs((long)report.TargetX.Value) <= ReviewWorldContract.MaximumTileCoordinate)
        && (report.TargetY is null || Math.Abs((long)report.TargetY.Value) <= ReviewWorldContract.MaximumTileCoordinate)
        && (report.LocationId is null || report.LocationId.Length is > 0 and <= 256)
        && (report.Position is null || LocalPlayerSnapshotContract.PositionValid(report.Position));
}
