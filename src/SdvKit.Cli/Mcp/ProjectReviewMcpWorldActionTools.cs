using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate ReviewWorldActionReport ProjectReviewMcpWorldActionRunner(
    ReviewWorldActionQuery query, CancellationToken cancellationToken);

internal static class ProjectReviewMcpWorldActionTools
{
    internal const string ToolName = "stardew_world_interact";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["action","x","y","targetInstanceId","targetRevision","inventoryRevision"],
         "properties":{"action":{"enum":["water","harvest","machineInsert","machineCollect"]},
          "x":{"type":"integer","minimum":-100000,"maximum":100000},
          "y":{"type":"integer","minimum":-100000,"maximum":100000},
          "targetInstanceId":{"type":"string","pattern":"^[0-9a-f]{32}$"},
          "targetRevision":{"type":"string","pattern":"^[0-9a-f]{32}$"},
          "inventoryRevision":{"type":"string","pattern":"^sha256:[0-9a-f]{64}$"}}}
        """).RootElement.Clone();
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","observedAtUtc",
          "action","x","y","dispatchState","button","startTick","endTick"],
         "properties":{"schemaVersion":{"const":1},"state":{"enum":["completed","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"const":"single"},"role":{"type":"null"},"observedAtUtc":{"type":"string"},
          "action":{"enum":["water","harvest","machineInsert","machineCollect"]},
          "x":{"type":"integer"},"y":{"type":"integer"},
          "dispatchState":{"enum":["notDispatched","completed","mayHaveRun"]},
          "button":{"enum":[null,"MouseLeft","MouseRight"]},
          "startTick":{"type":["integer","null"]},"endTick":{"type":["integer","null"]}}}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpWorldActionRunner run) => new WorldActionTool(run);

    private sealed class WorldActionTool(ProjectReviewMcpWorldActionRunner run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Dispatch exactly one native adjacent water, harvest, machine-insert, or machine-collect interaction in the owned disposable single-player review. Requires fresh target identity/revision from stardew_world_area_get and inventoryRevision from stardew_inventory_get. Completion proves input delivery only; read both again to prove the effect.",
            InputSchema = InputSchema,
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = false,
                DestructiveHint = true,
                IdempotentHint = false,
                OpenWorldHint = false
            },
        };
        public override IReadOnlyList<object> Metadata => [];
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            if (!TryQuery(request.Params?.Arguments, out ReviewWorldActionQuery? query))
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "stardew_world_interact requires one supported action, bounded x/y, exact 32-hex target instance/revision, and exact sha256 inventory revision." }]
                });
            ReviewWorldActionReport report = run(query!, cancellationToken);
            JsonElement json = JsonSerializer.SerializeToElement(report, JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = report.State != "completed",
                StructuredContent = json,
                Content = [new TextContentBlock { Text = json.GetRawText() }]
            });
        }
    }

    internal static bool TryQuery(IDictionary<string, JsonElement>? values, out ReviewWorldActionQuery? query)
    {
        query = null;
        if (values is null || values.Count != 6 || !Text(values, "action", out string action)
            || !Number(values, "x", out int x) || !Number(values, "y", out int y)
            || !Text(values, "targetInstanceId", out string instance)
            || !Text(values, "targetRevision", out string revision)
            || !Text(values, "inventoryRevision", out string inventory)) return false;
        query = new(action, x, y, instance, revision, inventory);
        return ReviewWorldActionContract.Validate(query) is null;
    }
    private static bool Text(IDictionary<string, JsonElement> values, string name, out string value)
    {
        value = ""; return values.TryGetValue(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
        && (value = e.GetString() ?? "").Length > 0;
    }
    private static bool Number(IDictionary<string, JsonElement> values, string name, out int value)
    {
        value = 0; return values.TryGetValue(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number
        && e.TryGetInt32(out value);
    }
}
