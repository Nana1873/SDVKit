using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewShopService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-shop");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };

    internal static ReviewShopReport Execute(ProjectReviewMcpRuntimeReader reader,
        Func<string, LiveLabCommandResult>? send = null, TimeSpan? responseTimeout = null,
        Func<DateTimeOffset>? utcNow = null, CancellationToken cancellationToken = default)
    {
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewShopReport Failure(string code) => new(1, "unavailable", code, null,
            reader.Topology, reader.Role, clock(), null);
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.Topology != "single" || reader.Role is not null) return Failure("shopTopologyUnsupported");
        ProjectReviewMcpReadResult before = reader.Read();
        if (!before.Succeeded) return Failure(before.ErrorCode!);
        if (!before.Snapshot!.Runtime.WorldReady) return Failure("shopWorldNotReady");
        try
        {
            string runtimePath = ProjectReviewInputService.RuntimePath(reader.ProjectRoot, reader.Topology, reader.Role);
            string requestId = Guid.NewGuid().ToString("N");
            DateTimeOffset started = clock();
            ProjectReviewResponseTransportResult<ReviewShopResponseEnvelope> result = ProjectReviewResponseTransport.Execute(
                $"sdvkit shop {requestId} {before.Snapshot.LaunchId}",
                ReviewShopContract.ResponsePath(runtimePath, requestId), ReviewShopContract.MaximumResponseBytes,
                "shop", "review-shop", reader.ProjectRoot, DeserializeResponse,
                response => response.SchemaVersion == 1 && response.RequestId == requestId
                    && ValidResponse(response.Report, before.Snapshot, started, clock()),
                responseTimeout: responseTimeout, topology: reader.Topology, role: reader.Role,
                send: send, cancellationToken: cancellationToken);
            if (result.Response is null)
                return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "shopResponseInvalid");
            ProjectReviewMcpReadResult after = reader.Read();
            if (!after.Succeeded || !ProjectReviewMenuService.SameBinding(before.Snapshot, after.Snapshot!)
                || !after.Snapshot!.Runtime.WorldReady) return Failure("reviewBindingChanged");
            if (!ValidResponse(result.Response.Report, after.Snapshot!, started, clock())) return Failure("shopResponseStale");
            return result.Response.Report;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return Failure("shopReadUnavailable");
        }
    }

    internal static ReviewShopResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        JsonElement root = document.RootElement;
        ResponseJson.RequireExactObject(root, ["schemaVersion", "requestId", "report"]);
        JsonElement report = root.GetProperty("report");
        ResponseJson.RequireExactObject(report, ["schemaVersion", "state", "errorCode", "launchId", "topology", "role", "capturedAtUtc", "data"]);
        JsonElement data = report.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Null)
        {
            ResponseJson.RequireExactObject(data, ["shopId", "currency", "identityScope", "playerId", "scrollIndex", "offers", "money", "inventory", "heldItem"]);
            ResponseJson.ValidateRequiredArray(data.GetProperty("offers"), ReviewShopContract.MaximumOffers, offer =>
            {
                ResponseJson.RequireExactObject(offer, ["forSaleIndex", "availability", "reason", "item", "price", "stock", "unlimitedStock"]);
                Item(offer.GetProperty("item"));
            });
            ResponseJson.ValidateRequiredArray(data.GetProperty("inventory"), ReviewShopContract.MaximumInventorySlots, slot =>
            {
                ResponseJson.RequireExactObject(slot, ["slot", "item"]);
                Item(slot.GetProperty("item"));
            });
            Item(data.GetProperty("heldItem"));
        }
        return JsonSerializer.Deserialize<ReviewShopResponseEnvelope>(bytes, JsonOptions);

        static void Item(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Null)
                ResponseJson.RequireExactObject(item, ["qualifiedItemId", "stack", "quality"]);
        }
    }

    internal static bool ValidResponse(ReviewShopReport? report, ProjectReviewMcpRuntimeSnapshot expected,
        DateTimeOffset started, DateTimeOffset now) => report is not null && report.SchemaVersion == 1
        && report.LaunchId == expected.LaunchId && report.Topology == "single" && report.Topology == expected.Topology
        && report.Role is null && expected.Role is null && report.CapturedAtUtc.Offset == TimeSpan.Zero
        && report.CapturedAtUtc >= started && report.CapturedAtUtc <= now.AddSeconds(5)
        && now - report.CapturedAtUtc <= TimeSpan.FromSeconds(5)
        && (report.State == "ready" ? report.ErrorCode is null && ReviewShopContract.DataValid(report.Data)
            : report.State == "unavailable" && report.Data is null && report.ErrorCode is
                ("shopReviewBindingInvalid" or "shopWorldNotReady" or "shopCaptureFailed" or "shopResponseLimit"
                or "shopMenuUnsupported" or "shopFamilyUnsupported" or "shopCurrencyUnsupported"
                or "shopBehaviorUnsupported" or "shopCaptureLimit" or "shopInventoryUnsupported" or "shopValuesInvalid"));
}
