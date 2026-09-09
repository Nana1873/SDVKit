using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewContainerService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-container");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };

    internal static ReviewContainerReport Execute(ProjectReviewMcpRuntimeReader reader,
        Func<string, LiveLabCommandResult>? send = null, TimeSpan? responseTimeout = null,
        Func<DateTimeOffset>? utcNow = null, CancellationToken cancellationToken = default)
    {
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewContainerReport Failure(string code) => new(ReviewContainerContract.SchemaVersion,
            "unavailable", code, null, reader.Topology, reader.Role, clock(), null);
        cancellationToken.ThrowIfCancellationRequested();
        ProjectReviewMcpReadResult before = reader.Read();
        if (!before.Succeeded) return Failure(before.ErrorCode!);
        if (!before.Snapshot!.Runtime.WorldReady) return Failure("containerWorldNotReady");
        try
        {
            string runtimePath = ProjectReviewInputService.RuntimePath(reader.ProjectRoot, reader.Topology, reader.Role);
            string requestId = Guid.NewGuid().ToString("N");
            DateTimeOffset started = clock();
            ProjectReviewResponseTransportResult<ReviewContainerResponseEnvelope> result = ProjectReviewResponseTransport.Execute(
                reader.SelectCommand($"sdvkit container {requestId} {before.Snapshot.LaunchId}"),
                ReviewContainerContract.ResponsePath(runtimePath, requestId), ReviewContainerContract.MaximumResponseBytes,
                "container", "review-container", reader.ProjectRoot, DeserializeResponse,
                response => response.SchemaVersion == ReviewContainerContract.SchemaVersion && response.RequestId == requestId
                    && ValidResponse(response.Report, before.Snapshot, requestId, started, clock()),
                responseTimeout: responseTimeout, topology: reader.Topology, role: reader.Role,
                send: send, cancellationToken: cancellationToken);
            if (result.Response is null)
                return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "containerResponseInvalid");
            ProjectReviewMcpReadResult after = reader.Read();
            if (!after.Succeeded || !ProjectReviewMenuService.SameBinding(before.Snapshot, after.Snapshot!)
                || !after.Snapshot!.Runtime.WorldReady) return Failure("reviewBindingChanged");
            if (!ValidResponse(result.Response.Report, after.Snapshot!, requestId, started, clock()))
                return Failure("containerResponseStale");
            return result.Response.Report;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return Failure("containerReadUnavailable");
        }
    }

    internal static ReviewContainerResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        JsonElement root = document.RootElement;
        ResponseJson.RequireExactObject(root, ["schemaVersion", "requestId", "report"]);
        JsonElement report = root.GetProperty("report");
        ResponseJson.RequireExactObject(report,
            ["schemaVersion", "state", "errorCode", "launchId", "topology", "role", "capturedAtUtc", "data"]);
        JsonElement data = report.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Null)
        {
            ResponseJson.RequireExactObject(data, ["captureId", "selectionIdentity", "containerRevision",
                "identityScope", "backingIdentity", "playerId", "locationName", "tileX", "tileY", "chestItemId",
                "player", "container", "heldItem", "complete", "limitations"]);
            ValidateSide(data.GetProperty("player"), ReviewContainerContract.MaximumPlayerSlots);
            ValidateSide(data.GetProperty("container"), ReviewContainerContract.MaximumContainerSlots);
            ValidateObservedItem(data.GetProperty("heldItem"));
            ResponseJson.ValidateRequiredArray(data.GetProperty("limitations"), 1, _ => { });
        }
        return JsonSerializer.Deserialize<ReviewContainerResponseEnvelope>(bytes, JsonOptions);

        static void ValidateSide(JsonElement side, int maximum)
        {
            ResponseJson.RequireExactObject(side, ["side", "capacity", "slots"]);
            ResponseJson.ValidateRequiredArray(side.GetProperty("slots"), maximum, slot =>
            {
                ResponseJson.RequireExactObject(slot, ["slot", "state", "reason", "item", "identity"]);
                ValidateItem(slot.GetProperty("item"));
                ValidateIdentity(slot.GetProperty("identity"));
            });
        }
        static void ValidateObservedItem(JsonElement item)
        {
            ResponseJson.RequireExactObject(item, ["state", "reason", "item", "identity"]);
            ValidateItem(item.GetProperty("item"));
            ValidateIdentity(item.GetProperty("identity"));
        }
        static void ValidateItem(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Null)
                ResponseJson.RequireExactObject(item, ["qualifiedItemId", "stack", "quality"]);
        }
        static void ValidateIdentity(JsonElement identity)
        {
            if (identity.ValueKind != JsonValueKind.Null)
                ResponseJson.RequireExactObject(identity, ["instanceIdentity", "itemRevision"]);
        }
    }

    internal static bool ValidResponse(ReviewContainerReport? report, ProjectReviewMcpRuntimeSnapshot expected,
        string requestId, DateTimeOffset started, DateTimeOffset now) => report is not null
        && report.SchemaVersion == ReviewContainerContract.SchemaVersion
        && report.LaunchId == expected.LaunchId && report.Topology == expected.Topology
        && report.Role == expected.Role && report.CapturedAtUtc.Offset == TimeSpan.Zero
        && report.CapturedAtUtc >= started && report.CapturedAtUtc <= now.AddSeconds(5)
        && now - report.CapturedAtUtc <= TimeSpan.FromSeconds(5)
        && (report.State == "ready" ? report.ErrorCode is null && report.Data is { } data
                && data.CaptureId == requestId && ReviewContainerContract.DataValid(data, expected.LaunchId)
                && data.PlayerId == expected.Runtime.LocalPlayer?.Data?.PlayerId
                && data.LocationName == expected.Runtime.LocationId
            : report.State == "unavailable" && report.Data is null && report.ErrorCode is
                ("containerReviewBindingInvalid" or "containerWorldNotReady" or "containerCaptureFailed"
                or "containerResponseLimit" or "containerMenuUnsupported" or "containerBackingAmbiguous"
                or "containerFamilyUnsupported" or "containerLocationUnavailable"
                or "containerCapacityUnsupported" or "containerIdentityUnavailable" or "containerValuesInvalid"));
}
