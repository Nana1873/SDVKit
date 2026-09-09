using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewContainerTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    internal static ReviewContainerTransferReport Execute(ReviewContainerTransferQuery query,
        ProjectReviewMcpRuntimeReader reader, Func<string, LiveLabCommandResult>? send = null,
        TimeSpan? responseTimeout = null, Func<DateTimeOffset>? utcNow = null,
        ProjectReviewMcpRuntimeSnapshot? expectedSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewContainerTransferReport Failure(string code, bool canceled = false) => new(1, "refused", code,
            null, reader.Topology, reader.Role, clock(), null);
        if (ReviewContainerTransferContract.Validate(query) is string invalid) return Failure(invalid);
        if (cancellationToken.IsCancellationRequested) return Failure("containerTransferRequestCanceled", true);
        if (reader.Topology != "single" || reader.Role is not null) return Failure("containerTransferTopologyUnsupported");
        using ProjectReviewActionLock? actionLock = ProjectReviewActionLock.TryAcquire(
            ProjectReviewInputService.RuntimePath(reader.ProjectRoot, reader.Topology, reader.Role));
        if (actionLock is null) return Failure("containerTransferBusy");
        ProjectReviewMcpReadResult before = reader.Read();
        if (!before.Succeeded) return Failure(before.ErrorCode!);
        if (expectedSnapshot is not null
            && !PermissionBindingValid(expectedSnapshot, before.Snapshot!))
            return Failure("containerTransferPermissionBindingChanged");
        if (!before.Snapshot!.Runtime.WorldReady) return Failure("containerTransferWorldNotReady");
        if (before.Snapshot.TestSave is null) return Failure("containerTransferTestSaveRequired");
        ReviewContainerReport observedBefore = ProjectReviewContainerService.Execute(reader, send,
            cancellationToken: cancellationToken);
        if (observedBefore.State != "ready" || observedBefore.Data is not { } beforeValues)
            return Failure(observedBefore.ErrorCode ?? "containerTransferCaptureFailed");
        if (beforeValues.SelectionIdentity != query.SelectionIdentity
            || beforeValues.ContainerRevision != query.ContainerRevision)
            return Failure("containerTransferSelectionStale");
        string requestId = Guid.NewGuid().ToString("N");
        string runtimePath = ProjectReviewInputService.RuntimePath(reader.ProjectRoot, reader.Topology, reader.Role);
        string command = string.Join(' ', "sdvkit", "container-transfer", requestId, before.Snapshot.LaunchId,
            query.Direction, query.SourceSlot, query.Quantity, query.QualifiedItemId, query.SelectionIdentity,
            query.ContainerRevision, query.InstanceIdentity, query.ItemRevision);
        ProjectReviewResponseTransportResult<ReviewContainerTransferResponseEnvelope> transported =
            ProjectReviewResponseTransport.Execute(command,
                ReviewContainerTransferContract.ResponsePath(runtimePath, requestId),
                ReviewContainerTransferContract.MaximumResponseBytes, "containerTransfer", "container transfer",
                reader.ProjectRoot, Deserialize,
                response => response.SchemaVersion == 1 && response.RequestId == requestId
                    && Valid(response.Report, before.Snapshot, query, clock()),
                responseTimeout: responseTimeout, send: send, topology: "single",
                drainAfterDispatchOnCancellation: true,
                onCancellation: () =>
                {
                    if (send is not null) send($"sdvkit container-transfer cancel {requestId}");
                    else ProjectReviewService.ExecuteCommand($"sdvkit container-transfer cancel {requestId}", "single", null, reader.ProjectRoot);
                },
                cancellationToken: cancellationToken);
        if (transported.Response is null)
        {
            string code = transported.Problems.Count > 0 ? transported.Problems[0].Code : "containerTransferResponseInvalid";
            return TransportFailure(query, before.Snapshot, beforeValues, code,
                transported.CommandMayHaveBeenWritten, transported.CancellationRequested, clock());
        }
        ReviewContainerTransferReport report = transported.Response.Report;
        if (transported.CancellationRequested && report.Data is { } data && !data.CancellationRequested)
            report = report with { Data = data with { CancellationRequested = true } };
        ProjectReviewMcpReadResult afterBinding = reader.Read();
        if (report.Data is { Dispatched: true } delivered
            && (!afterBinding.Succeeded || !PermissionBindingValid(before.Snapshot, afterBinding.Snapshot!)))
        {
            return BindingChangedFailure(report, transported.CancellationRequested, clock());
        }
        return report;
    }

    internal static ReviewContainerTransferResponseEnvelope? Deserialize(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<ReviewContainerTransferResponseEnvelope>(bytes, JsonOptions);
    }

    internal static bool Valid(ReviewContainerTransferReport? report, ProjectReviewMcpRuntimeSnapshot expected,
        ReviewContainerTransferQuery query, DateTimeOffset now)
    {
        if (report is null || report.SchemaVersion != 1 || report.LaunchId != expected.LaunchId
            || report.Topology != "single" || report.Role is not null
            || report.CapturedAtUtc > now.AddSeconds(5) || now - report.CapturedAtUtc > TimeSpan.FromSeconds(15))
            return false;
        if (report.Data is not { } data)
            return report.State == "refused" && report.ErrorCode is not null;
        if (data.Direction != query.Direction
            || data.SourceSide != ReviewContainerTransferContract.SourceSide(query.Direction)
            || data.SourceSlot != query.SourceSlot || data.RequestedQuantity != query.Quantity
            || data.QualifiedItemId != query.QualifiedItemId || report.State != data.Outcome
            || data.Outcome is not ("completed" or "refused" or "partial" or "uncertain")
            || !ReviewContainerContract.DataValid(data.Before, expected.LaunchId)
            || data.After is not null && !ReviewContainerContract.DataValid(data.After, expected.LaunchId))
            return false;
        if (!data.Dispatched)
            return data.Outcome == "refused" && report.ErrorCode is not null
                && data.ObservedQuantity == 0 && data.After is null;
        if (ReviewContainerTransferContract.ObservationProblem(query, data.Before) is not null)
            return false;
        if (data.Outcome == "uncertain")
            return report.ErrorCode is not null && data.ObservedQuantity is null && data.After is null
                && data.Limitations.Contains("afterObservationUnavailable", StringComparer.Ordinal);
        if (data.After is not { } after || (report.ErrorCode is null) != (data.Outcome == "completed")
            || after.SelectionIdentity != data.Before.SelectionIdentity
            || !ReviewContainerTransferContract.Conserved(data.Before, after, query))
            return false;
        int observed = ReviewContainerTransferContract.ObservedQuantity(data.Before, after, query);
        if (data.ObservedQuantity != observed || observed > query.Quantity)
            return false;
        return data.Outcome switch
        {
            "completed" => observed == query.Quantity,
            "partial" => observed is > 0 && observed < query.Quantity,
            "refused" => observed == 0,
            _ => false,
        };
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON member.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicates(item);
    }

    internal static ReviewContainerTransferReport TransportFailure(ReviewContainerTransferQuery query,
        ProjectReviewMcpRuntimeSnapshot snapshot, ReviewContainerValues before, string code,
        bool commandMayHaveBeenWritten, bool cancellationRequested, DateTimeOffset observedAt) =>
        commandMayHaveBeenWritten
            ? new(1, "uncertain", code, snapshot.LaunchId, "single", null, observedAt,
                new(query.Direction, ReviewContainerTransferContract.SourceSide(query.Direction), query.SourceSlot,
                    query.Quantity, null, query.QualifiedItemId, true, cancellationRequested,
                    "uncertain", before, null, ["afterObservationUnavailable"]))
            : new(1, "refused", code, null, snapshot.Topology, snapshot.Role, observedAt, null);

    internal static bool PermissionBindingValid(ProjectReviewMcpRuntimeSnapshot expected,
        ProjectReviewMcpRuntimeSnapshot current) => expected.TestSave is not null
        && current.TestSave is not null && ProjectReviewMenuService.SameBinding(expected, current);

    internal static ReviewContainerTransferReport BindingChangedFailure(ReviewContainerTransferReport report,
        bool cancellationRequested, DateTimeOffset observedAt)
    {
        ReviewContainerTransferValues delivered = report.Data
            ?? throw new ArgumentException("A delivered transfer report is required.", nameof(report));
        return new(1, "uncertain", "containerTransferBindingChanged", report.LaunchId,
            "single", null, observedAt, delivered with
            {
                Outcome = "uncertain",
                CancellationRequested = delivered.CancellationRequested || cancellationRequested,
                Limitations = delivered.Limitations.Concat(["postBindingChanged"]).Distinct(StringComparer.Ordinal).ToArray()
            });
    }
}
