using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewInventoryService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-inventory");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 10,
    };

    internal static ReviewInventoryReport Execute(ProjectReviewMcpRuntimeReader reader,
        Func<string, LiveLabCommandResult>? send = null, TimeSpan? responseTimeout = null,
        Func<DateTimeOffset>? utcNow = null, CancellationToken cancellationToken = default)
    {
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewInventoryReport Failure(string code) => new(ReviewInventoryContract.SchemaVersion,
            "unavailable", code, null, reader.Topology, reader.Role, clock(), null);
        cancellationToken.ThrowIfCancellationRequested();
        ProjectReviewMcpReadResult before = reader.Read();
        if (!before.Succeeded) return Failure(before.ErrorCode!);
        if (!before.Snapshot!.Runtime.WorldReady) return Failure("inventoryWorldNotReady");
        try
        {
            string runtimePath = ProjectReviewInputService.RuntimePath(reader.ProjectRoot, reader.Topology, reader.Role);
            string requestId = Guid.NewGuid().ToString("N");
            DateTimeOffset started = clock();
            ProjectReviewResponseTransportResult<ReviewInventoryResponseEnvelope> result = ProjectReviewResponseTransport.Execute(
                reader.SelectCommand($"sdvkit inventory {requestId} {before.Snapshot.LaunchId}"),
                ReviewInventoryContract.ResponsePath(runtimePath, requestId), ReviewInventoryContract.MaximumResponseBytes,
                "inventory", "review-inventory", reader.ProjectRoot, DeserializeResponse,
                response => response.SchemaVersion == ReviewInventoryContract.SchemaVersion && response.RequestId == requestId
                    && ValidResponse(response.Report, before.Snapshot, requestId, started, clock()),
                responseTimeout: responseTimeout, topology: reader.Topology, role: reader.Role,
                send: send, cancellationToken: cancellationToken);
            if (result.Response is null)
                return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "inventoryResponseInvalid");
            ProjectReviewMcpReadResult after = reader.Read();
            if (!after.Succeeded || !ProjectReviewMenuService.SameBinding(before.Snapshot, after.Snapshot!)
                || !after.Snapshot!.Runtime.WorldReady) return Failure("reviewBindingChanged");
            if (!ValidResponse(result.Response.Report, after.Snapshot!, requestId, started, clock()))
                return Failure("inventoryResponseStale");
            return result.Response.Report;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return Failure("inventoryReadUnavailable");
        }
    }

    internal static ReviewInventoryResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 10 });
        JsonElement root = document.RootElement;
        ResponseJson.RequireExactObject(root, ["schemaVersion", "requestId", "report"]);
        JsonElement report = root.GetProperty("report");
        ResponseJson.RequireExactObject(report,
            ["schemaVersion", "state", "errorCode", "launchId", "topology", "role", "capturedAtUtc", "data"]);
        JsonElement data = report.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Null)
        {
            ResponseJson.RequireExactObject(data,
                ["captureId", "inventoryRevision", "playerId", "capacity", "selectedSlot", "complete", "limitations", "slots"]);
            ResponseJson.ValidateRequiredArray(data.GetProperty("limitations"), 1, _ => { });
            ResponseJson.ValidateRequiredArray(data.GetProperty("slots"), ReviewInventoryContract.MaximumSlots, slot =>
            {
                ResponseJson.RequireExactObject(slot, ["slot", "state", "reason", "item"]);
                JsonElement item = slot.GetProperty("item");
                if (item.ValueKind != JsonValueKind.Null)
                    ResponseJson.RequireExactObject(item, ["qualifiedItemId", "stack", "quality"]);
            });
        }
        return JsonSerializer.Deserialize<ReviewInventoryResponseEnvelope>(bytes, JsonOptions);
    }

    internal static bool ValidResponse(ReviewInventoryReport? report, ProjectReviewMcpRuntimeSnapshot expected,
        string requestId, DateTimeOffset started, DateTimeOffset now) => report is not null
        && report.SchemaVersion == ReviewInventoryContract.SchemaVersion
        && report.LaunchId == expected.LaunchId && report.Topology == expected.Topology
        && report.Role == expected.Role && report.CapturedAtUtc.Offset == TimeSpan.Zero
        && report.CapturedAtUtc >= started && report.CapturedAtUtc <= now.AddSeconds(5)
        && now - report.CapturedAtUtc <= TimeSpan.FromSeconds(5)
        && (report.State == "ready" ? report.ErrorCode is null && report.Data is { } data
                && data.CaptureId == requestId && ReviewInventoryContract.DataValid(data, expected.LaunchId)
                && data.PlayerId == expected.Runtime.LocalPlayer?.Data?.PlayerId
            : report.State == "unavailable" && report.Data is null && report.ErrorCode is
                ("inventoryReviewBindingInvalid" or "inventoryWorldNotReady" or "inventoryCaptureFailed"
                or "inventoryResponseLimit" or "inventoryCapacityUnsupported" or "inventoryValuesInvalid"));
}
