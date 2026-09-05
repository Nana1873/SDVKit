using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal static class ProjectReviewMenuService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-menu");
    private static readonly HashSet<string> EnvelopeFields = ["schemaVersion", "requestId", "report"];
    private static readonly HashSet<string> ReportFields = ["schemaVersion", "state", "errorCode", "launchId",
        "topology", "role", "capturedAtUtc", "identityScope", "viewport", "menuOpen", "complete", "truncated", "limitations", "menus"];
    private static readonly HashSet<string> NodeFields = ["id", "parentId", "relationship", "type", "assembly", "adapter",
        "coverage", "bounds", "currentTab", "scrollIndex", "components"];
    private static readonly HashSet<string> ComponentFields = ["id", "kind", "controllerId", "bounds",
        "visibleFlag", "intersectsViewport", "controllerFocused"];
    private static readonly HashSet<string> RectangleFields = ["x", "y", "width", "height"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };

    internal static ReviewMenuReport Execute(ProjectReviewMcpRuntimeReader reader,
        Func<string, LiveLabCommandResult>? send = null, TimeSpan? responseTimeout = null,
        Func<DateTimeOffset>? utcNow = null, CancellationToken cancellationToken = default)
    {
        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReviewMenuReport Failure(string code) => new(1, "unavailable", code, null,
            reader.Topology, reader.Role, clock(), null, null, false, false, false, [], []);
        cancellationToken.ThrowIfCancellationRequested();
        ProjectReviewMcpReadResult before = reader.Read();
        if (!before.Succeeded)
        {
            return Failure(before.ErrorCode!);
        }
        if (!before.Snapshot!.Runtime.WorldReady)
        {
            return Failure("menuWorldNotReady");
        }
        try
        {
            string runtimePath = ProjectReviewInputService.RuntimePath(reader.ProjectRoot, reader.Topology, reader.Role);
            string requestId = Guid.NewGuid().ToString("N");
            DateTimeOffset started = clock();
            ProjectReviewResponseTransportResult<ReviewMenuResponseEnvelope> result = ProjectReviewResponseTransport.Execute(
                $"sdvkit menu {requestId} {before.Snapshot.LaunchId}",
                ReviewMenuContract.ResponsePath(runtimePath, requestId), ReviewMenuContract.MaximumResponseBytes,
                "menu", "review-menu", reader.ProjectRoot,
                DeserializeResponse,
                response => response.SchemaVersion == 1 && response.RequestId == requestId
                    && ValidResponse(response.Report, before.Snapshot, started, clock()),
                responseTimeout: responseTimeout, topology: reader.Topology, role: reader.Role,
                send: send, cancellationToken: cancellationToken);
            if (result.Response is null)
            {
                return Failure(result.Problems.Count > 0 ? result.Problems[0].Code : "menuResponseInvalid");
            }
            ProjectReviewMcpReadResult after = reader.Read();
            if (!after.Succeeded || !SameBinding(before.Snapshot, after.Snapshot!)
                || !after.Snapshot!.Runtime.WorldReady)
            {
                return Failure("reviewBindingChanged");
            }
            if (!ValidResponse(result.Response.Report, after.Snapshot!, started, clock()))
            {
                return Failure("menuResponseStale");
            }
            return result.Response.Report;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return Failure("menuReadUnavailable");
        }
    }

    internal static bool SameBinding(ProjectReviewMcpRuntimeSnapshot before, ProjectReviewMcpRuntimeSnapshot after) =>
        before.LaunchId == after.LaunchId && before.Topology == after.Topology && before.Role == after.Role
        && before.Target == after.Target && before.TestSave == after.TestSave;

    internal static ReviewMenuResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        JsonElement root = document.RootElement;
        ResponseJson.RequireExactObject(root, EnvelopeFields);
        JsonElement report = root.GetProperty("report");
        ResponseJson.RequireExactObject(report, ReportFields);
        if (report.GetProperty("viewport").ValueKind != JsonValueKind.Null)
        {
            ResponseJson.RequireExactObject(report.GetProperty("viewport"), RectangleFields);
        }
        ResponseJson.ValidateRequiredArray(report.GetProperty("menus"), ReviewMenuContract.MaximumNodes, node =>
        {
            ResponseJson.RequireExactObject(node, NodeFields);
            ResponseJson.RequireExactObject(node.GetProperty("bounds"), RectangleFields);
            ResponseJson.ValidateRequiredArray(node.GetProperty("components"), ReviewMenuContract.MaximumComponents, component =>
            {
                ResponseJson.RequireExactObject(component, ComponentFields);
                ResponseJson.RequireExactObject(component.GetProperty("bounds"), RectangleFields);
            });
        });
        return JsonSerializer.Deserialize<ReviewMenuResponseEnvelope>(bytes, JsonOptions);
    }

    internal static bool ValidResponse(ReviewMenuReport? report, ProjectReviewMcpRuntimeSnapshot expected,
        DateTimeOffset started, DateTimeOffset now)
    {
        if (report is null || report.SchemaVersion != 1 || report.LaunchId != expected.LaunchId
            || report.Topology != expected.Topology || report.Role != expected.Role
            || report.CapturedAtUtc.Offset != TimeSpan.Zero
            || report.CapturedAtUtc < started || report.CapturedAtUtc > now.AddSeconds(5)
            || now - report.CapturedAtUtc > TimeSpan.FromSeconds(5)
            || report.Menus is null || report.Limitations is null || report.Limitations.Count > 8
            || report.Limitations.Any(x => x is not ("menuTreeLimit" or "repeatedMenuReference"
                or "publicBaseOnly" or "componentScanLimit" or "typeIdentifierWithheld" or "componentLimit")))
        {
            return false;
        }
        if (report.State == "unavailable")
        {
            return report.ErrorCode is "menuReviewBindingInvalid" or "menuWorldNotReady"
                or "menuCaptureFailed" or "menuResponseLimit"
                && report.Menus.Count == 0 && !report.Complete;
        }
        if (report.State != "ready" || report.ErrorCode is not null || report.Viewport is null
            || report.Menus.Count > ReviewMenuContract.MaximumNodes
            || report.MenuOpen != (report.Menus.Count > 0)
            || (report.MenuOpen ? !ReviewTransportToken.IsRequestId(report.IdentityScope) : report.IdentityScope is not null)
            || report.Complete != (report.Limitations.Count == 0)
            || (report.Truncated && report.Complete))
        {
            return false;
        }
        var depths = new Dictionary<long, int>();
        int count = 0;
        foreach (ReviewMenuNode node in report.Menus)
        {
            if (node is null || node.Id < 1 || depths.ContainsKey(node.Id) || node.Bounds is null
                || node.Type is not { Length: > 0 and <= ReviewMenuContract.MaximumTypeLength }
                || node.Type.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '.' or '+' or '`'))
                || node.Assembly is not { Length: > 0 and <= ReviewMenuContract.MaximumTypeLength }
                || node.Assembly.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '.' or '+' or '`' or '-' or ' '))
                || node.Adapter is not ("publicBase" or "gameMenu" or "inventoryPage" or "inventoryMenu" or "shopMenu")
                || node.Coverage != (node.Adapter == "publicBase" ? "partial" : "declaredFields")
                || (node.Adapter == "publicBase" && !report.Limitations.Contains("publicBaseOnly"))
                || node.Relationship is not ("root" or "activePage" or "inventory" or "child")
                || node.Components is null)
            {
                return false;
            }
            int depth = 1;
            if (node.ParentId is long parent)
            {
                if (!depths.TryGetValue(parent, out depth) || ++depth > ReviewMenuContract.MaximumDepth
                    || node.Relationship == "root")
                {
                    return false;
                }
            }
            else if (depths.Count != 0 || node.Relationship != "root")
            {
                return false;
            }
            depths.Add(node.Id, depth);
            long previous = 0;
            foreach (ReviewMenuComponent component in node.Components)
            {
                if (++count > ReviewMenuContract.MaximumComponents || component is null
                    || component.Id <= previous || component.Bounds is null
                    || component.Kind is not ("tab" or "equipment" or "portrait" or "trashCan" or "organize"
                        or "junimoNote" or "inventorySlot" or "saleRow" or "scrollUp" or "scrollDown"
                        or "scrollBar" or "close" or "publicComponent"))
                {
                    return false;
                }
                previous = component.Id;
            }
        }
        return true;
    }
}
