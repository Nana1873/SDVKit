using System.Globalization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

/// <summary>
/// Sends one faced-tile action (check or useTool) to the owned review's selected player. Shares the binding, transport and
/// report checks of <see cref="ProjectReviewWorldActionService"/>; the target has no identity, so the backpack
/// revision and the adjacency check bind the request.
/// </summary>
internal static class ProjectReviewWorldActService
{
    internal static ReviewWorldActionReport Execute(ProjectReviewMcpRuntimeReader reader,
        ReviewWorldActQuery query, Func<string, LiveLabCommandResult>? send = null,
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
        if (ReviewWorldActContract.Validate(query) is { } problem) return Failure(problem);
        if (ProjectReviewWorldActionService.Prepare(reader, expectedContext, beforeSnapshotRead,
                out ProjectReviewMcpVerifiedContext? permission, out ProjectReviewMcpRuntimeSnapshot? before) is { } refused)
            return Failure(refused);
        string requestId = Guid.NewGuid().ToString("N");
        string command = reader.SelectCommand(string.Create(CultureInfo.InvariantCulture,
            $"sdvkit world-act {requestId} {permission!.State.LaunchId} {query.Action} {query.X} {query.Y} {query.InventoryRevision} {ProjectReviewWorldActionService.SelectionToken(reader)}"));
        DateTimeOffset started = clock();
        ProjectReviewResponseTransportResult<ReviewWorldActionResponseEnvelope> result =
            ProjectReviewResponseTransport.Execute(command,
                ReviewWorldActContract.ResponsePath(ProjectReviewInputService.RuntimePath(
                    reader.ProjectRoot, reader.Topology, reader.Role), requestId),
                ReviewWorldActionContract.MaximumResponseBytes, "worldAction", "review-world-act",
                reader.ProjectRoot, ProjectReviewWorldActionService.Deserialize, response => Valid(response, requestId, query,
                    before!, started, clock()), responseTimeout: responseTimeout,
                topology: reader.Topology, role: reader.Role, send: send,
                drainAfterDispatchOnCancellation: true, cancellationToken: cancellationToken,
                onCancellation: () => ProjectReviewInputService.DispatchCancellation(() =>
                    send is null
                        ? ProjectReviewService.ExecuteCommand(reader.SelectCommand($"sdvkit world-act cancel {requestId}"),
                            reader.Topology, reader.Role, reader.ProjectRoot)
                        : send(reader.SelectCommand($"sdvkit world-act cancel {requestId}"))));
        if (result.Response is null)
            return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "worldActionResponseInvalid",
                result.CommandMayHaveBeenWritten ? "mayHaveRun" : "notDispatched");
        if (result.CancellationRequested)
            return Failure("worldActionCancellationRequested", "mayHaveRun");
        if (!ProjectReviewWorldActionService.BindingHeld(reader, permission))
            return Failure("worldActionBindingChanged", "mayHaveRun");
        return result.Response.Report;
    }

    internal static bool Valid(ReviewWorldActionResponseEnvelope? envelope, string requestId,
        ReviewWorldActQuery query, ProjectReviewMcpRuntimeSnapshot expected,
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
                    && report.Button == ReviewWorldActContract.ButtonFor(query.Action)
                    && report.StartTick is >= 0 && report.EndTick >= report.StartTick
                || report.State == "unavailable" && ReviewWorldActionContract.IsGameError(report.ErrorCode)
                    && report.DispatchState is "notDispatched" or "mayHaveRun");
    }
}
