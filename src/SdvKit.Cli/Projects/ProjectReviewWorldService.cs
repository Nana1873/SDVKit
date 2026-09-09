using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewWorldService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-world");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 14,
    };

    internal static ReviewWorldReport Execute(ProjectReviewMcpRuntimeReader reader, ReviewWorldArea area,
        Func<string, LiveLabCommandResult>? send = null, TimeSpan? responseTimeout = null,
        Func<DateTimeOffset>? utcNow = null, CancellationToken cancellationToken = default)
    {
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewWorldReport Failure(string code) => new(1, "unavailable", code, null,
            reader.Topology, reader.Role, clock(), null);
        cancellationToken.ThrowIfCancellationRequested();
        string? queryProblem = ReviewWorldContract.QueryProblem(area);
        if (queryProblem is not null) return Failure(queryProblem);
        ProjectReviewMcpReadResult before = reader.Read();
        if (!before.Succeeded) return Failure(before.ErrorCode!);
        if (!before.Snapshot!.Runtime.WorldReady) return Failure("worldNotReady");
        if (before.Snapshot.Runtime.LocationId is null
            || before.Snapshot.Runtime.LocalPlayer is not { Availability: "available", Data: not null })
            return Failure("worldPlayerBindingUnavailable");
        try
        {
            string runtimePath = ProjectReviewInputService.RuntimePath(reader.ProjectRoot, reader.Topology, reader.Role);
            string requestId = Guid.NewGuid().ToString("N");
            DateTimeOffset started = clock();
            string line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"sdvkit world {requestId} {before.Snapshot.LaunchId} {area.X} {area.Y} {area.Width} {area.Height}");
            line = reader.SelectCommand(line);
            ProjectReviewResponseTransportResult<ReviewWorldResponseEnvelope> result = ProjectReviewResponseTransport.Execute(
                line, ReviewWorldContract.ResponsePath(runtimePath, requestId), ReviewWorldContract.MaximumResponseBytes,
                "world", "review-world", reader.ProjectRoot, DeserializeResponse,
                response => response.SchemaVersion == 1 && response.RequestId == requestId
                    && ValidResponse(response.Report, before.Snapshot, area, started, clock()),
                responseTimeout: responseTimeout, topology: reader.Topology, role: reader.Role,
                send: send, cancellationToken: cancellationToken);
            if (result.Response is null)
                return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "worldResponseInvalid");
            ProjectReviewMcpReadResult after = reader.Read();
            if (!after.Succeeded || !ProjectReviewMenuService.SameBinding(before.Snapshot, after.Snapshot!)
                || !after.Snapshot!.Runtime.WorldReady) return Failure("reviewBindingChanged");
            if (!ValidResponse(result.Response.Report, after.Snapshot!, area, started, clock()))
                return Failure("worldResponseStale");
            return result.Response.Report;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return Failure("worldReadUnavailable");
        }
    }

    internal static ReviewWorldResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 14 });
        JsonElement root = document.RootElement;
        ResponseJson.RequireExactObject(root, ["schemaVersion", "requestId", "report"]);
        JsonElement report = root.GetProperty("report");
        ResponseJson.RequireExactObject(report,
            ["schemaVersion", "state", "errorCode", "launchId", "topology", "role", "capturedAtUtc", "data"]);
        JsonElement data = report.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Null)
        {
            ResponseJson.RequireExactObject(data,
                ["locationName", "locationInstanceId", "playerId", "captureTick", "area", "complete", "tiles"]);
            ResponseJson.RequireExactObject(data.GetProperty("area"), ["x", "y", "width", "height"]);
            ResponseJson.ValidateRequiredArray(data.GetProperty("tiles"), ReviewWorldContract.MaximumTiles, tile =>
            {
                ResponseJson.RequireExactObject(tile,
                    ["x", "y", "soil", "objectState", "objectReason", "objectQualifiedItemId", "machine"]);
                JsonElement soil = tile.GetProperty("soil");
                if (soil.ValueKind != JsonValueKind.Null)
                {
                    ResponseJson.RequireExactObject(soil,
                        ["instanceId", "revision", "watered", "needsWatering", "fertilizerItemId", "crop"]);
                    JsonElement crop = soil.GetProperty("crop");
                    ResponseJson.RequireExactObject(crop, ["state", "reason", "data"]);
                    JsonElement cropData = crop.GetProperty("data");
                    if (cropData.ValueKind != JsonValueKind.Null)
                        ResponseJson.RequireExactObject(cropData,
                            ["instanceId", "revision", "seedItemId", "harvestItemId", "currentPhase",
                                "dayOfCurrentPhase", "phaseCount", "fullyGrown", "dead", "readyForHarvest",
                                "regrowsAfterHarvest"]);
                }
                JsonElement machine = tile.GetProperty("machine");
                if (machine.ValueKind != JsonValueKind.Null)
                {
                    ResponseJson.RequireExactObject(machine,
                        ["instanceId", "revision", "qualifiedItemId", "state", "readyForHarvest", "minutesUntilReady", "input", "output"]);
                    ItemObservation(machine.GetProperty("input"));
                    ItemObservation(machine.GetProperty("output"));
                }
            });
        }
        return JsonSerializer.Deserialize<ReviewWorldResponseEnvelope>(bytes, JsonOptions);

        static void ItemObservation(JsonElement observation)
        {
            ResponseJson.RequireExactObject(observation, ["state", "reason", "item"]);
            JsonElement item = observation.GetProperty("item");
            if (item.ValueKind != JsonValueKind.Null)
                ResponseJson.RequireExactObject(item, ["qualifiedItemId", "stack", "quality"]);
        }
    }

    internal static bool ValidResponse(ReviewWorldReport? report, ProjectReviewMcpRuntimeSnapshot expected,
        ReviewWorldArea expectedArea, DateTimeOffset started, DateTimeOffset now) =>
        report is not null && report.SchemaVersion == 1
        && report.LaunchId == expected.LaunchId && report.Topology == expected.Topology
        && report.Role == expected.Role && report.CapturedAtUtc.Offset == TimeSpan.Zero
        && report.CapturedAtUtc >= started && report.CapturedAtUtc <= now.AddSeconds(5)
        && now - report.CapturedAtUtc <= TimeSpan.FromSeconds(5)
        && (report.State == "ready" ? report.ErrorCode is null && ReviewWorldContract.DataValid(report.Data)
                && report.Data!.LocationName == expected.Runtime.LocationId
                && report.Data.PlayerId == expected.Runtime.LocalPlayer?.Data?.PlayerId
                && report.Data.Area == expectedArea
            : report.State == "unavailable" && report.Data is null && report.ErrorCode is
                ("worldReviewBindingInvalid" or "worldTopologyUnsupported" or "worldNotReady"
                or "worldAreaInvalid" or "worldAreaLimit" or "worldCaptureFailed" or "worldResponseLimit"
                or "worldLocationUnavailable" or "worldMapUnavailable" or "worldAreaOutsideMap"
                or "worldPlayerBindingUnavailable" or "worldValuesInvalid"));
}
