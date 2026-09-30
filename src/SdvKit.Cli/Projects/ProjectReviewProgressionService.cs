using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewProgressionService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-progression");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    internal static ReviewProgressionReport Execute(ProjectReviewMcpRuntimeReader reader,
        ReviewProgressionSelection selection, Func<string, LiveLabCommandResult>? send = null,
        TimeSpan? responseTimeout = null, Func<DateTimeOffset>? utcNow = null,
        CancellationToken cancellationToken = default)
    {
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewProgressionReport Failure(string code) => new(1, "unavailable", code, null,
            reader.Topology, reader.Role, clock(), null);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReviewProgressionContract.QueryProblem(selection) is { } problem) return Failure(problem);
        if (reader.Topology != "single" || reader.Role is not null || reader.ScreenId is not null)
            return Failure("progressionTopologyUnsupported");
        ProjectReviewMcpReadResult before = reader.Read();
        if (!before.Succeeded) return Failure(before.ErrorCode!);
        if (!before.Snapshot!.Runtime.WorldReady) return Failure("progressionWorldNotReady");
        if (before.Snapshot.TestSave is null) return Failure("progressionTestSaveRequired");
        if (before.Snapshot.Runtime.LocalPlayer is not { Availability: "available", Data: not null })
            return Failure("progressionPlayerBindingUnavailable");
        try
        {
            string runtimePath = ProjectReviewInputService.RuntimePath(reader.ProjectRoot, reader.Topology, reader.Role);
            string requestId = Guid.NewGuid().ToString("N");
            DateTimeOffset started = clock();
            var result = ProjectReviewResponseTransport.Execute(
                $"sdvkit progression {requestId} {before.Snapshot.LaunchId} {selection.NpcId} {selection.MailId} {selection.QuestId}",
                ReviewProgressionContract.ResponsePath(runtimePath, requestId), ReviewProgressionContract.MaximumResponseBytes,
                "progression", "review-progression", reader.ProjectRoot, DeserializeResponse,
                response => response.SchemaVersion == 1 && response.RequestId == requestId
                    && ValidResponse(response.Report, before.Snapshot, selection, started, clock()),
                responseTimeout: responseTimeout, topology: reader.Topology, role: reader.Role,
                send: send, cancellationToken: cancellationToken);
            if (result.Response is null)
                return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "progressionResponseInvalid");
            ProjectReviewMcpReadResult after = reader.Read();
            if (!after.Succeeded || !ProjectReviewResponseTransport.SameBinding(before.Snapshot, after.Snapshot!)
                || !after.Snapshot!.Runtime.WorldReady) return Failure("reviewBindingChanged");
            if (!ValidResponse(result.Response.Report, after.Snapshot!, selection, started, clock()))
                return Failure("progressionResponseStale");
            return result.Response.Report;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        { return Failure("progressionReadUnavailable"); }
    }

    internal static ReviewProgressionResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        ResponseJson.RequireExactObject(root, ["schemaVersion", "requestId", "report"]);
        JsonElement report = root.GetProperty("report");
        ResponseJson.RequireExactObject(report, ["schemaVersion", "state", "errorCode", "launchId", "topology", "role", "capturedAtUtc", "data"]);
        JsonElement data = report.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Null)
        {
            ResponseJson.RequireExactObject(data, ["playerId", "fixtureId", "captureTick", "selection", "npc", "mail", "quest"]);
            ResponseJson.RequireExactObject(data.GetProperty("selection"), ["npcId", "mailId", "questId"]);
            ResponseJson.RequireExactObject(data.GetProperty("npc"), ["state", "friendshipPoints"]);
            ResponseJson.RequireExactObject(data.GetProperty("mail"), ["received", "tomorrow", "mailboxCount"]);
            ResponseJson.RequireExactObject(data.GetProperty("quest"), ["matchCount", "acceptedCount", "completedCount"]);
        }
        return JsonSerializer.Deserialize<ReviewProgressionResponseEnvelope>(bytes, JsonOptions);
    }

    internal static bool ValidResponse(ReviewProgressionReport? report, ProjectReviewMcpRuntimeSnapshot expected,
        ReviewProgressionSelection selection, DateTimeOffset started, DateTimeOffset now) =>
        report is not null && report.SchemaVersion == 1 && report.Topology == "single" && report.Role is null
        && expected.Topology == "single" && expected.Role is null && expected.Screen is null
        && expected.TestSave is not null
        && ProjectReviewResponseTransport.MatchesCaptureBinding(report.LaunchId, report.Topology, report.Role,
            report.CapturedAtUtc, expected, started, now)
        && (report.State == "ready" ? report.ErrorCode is null && ReviewProgressionContract.DataValid(report.Data)
            && report.Data!.Selection == selection && report.Data.FixtureId == expected.TestSave.FixtureId
            && report.Data.PlayerId == expected.Runtime.LocalPlayer?.Data?.PlayerId
            : report.State == "unavailable" && report.Data is null && report.ErrorCode is
                ("progressionReviewBindingInvalid" or "progressionWorldNotReady" or "progressionTestSaveRequired"
                or "progressionSelectionInvalid" or "progressionCaptureFailed" or "progressionResponseLimit"
                or "progressionValuesInvalid"));
}
