using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate ReviewWorldActionReport ProjectReviewMcpWorldActRunner(
    ReviewWorldActQuery query, CancellationToken cancellationToken);

internal static class ProjectReviewMcpWorldActTools
{
    internal const string ToolName = "stardew_world_act";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["action","x","y","inventoryRevision"],
         "properties":{"action":{"enum":["check","useTool"]},
          "x":{"type":"integer","minimum":-100000,"maximum":100000},
          "y":{"type":"integer","minimum":-100000,"maximum":100000},
          "inventoryRevision":{"type":"string","pattern":"^sha256:[0-9a-f]{64}$"}}}
        """).RootElement.Clone();
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","observedAtUtc",
          "action","x","y","dispatchState","button","startTick","endTick"],
         "properties":{"schemaVersion":{"const":1},"state":{"enum":["completed","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"enum":["single","network-2"]},"role":{"enum":[null,"host","farmhand"]},"observedAtUtc":{"type":"string"},
          "action":{"enum":["check","useTool"]},
          "x":{"type":"integer"},"y":{"type":"integer"},
          "dispatchState":{"enum":["notDispatched","completed","mayHaveRun"]},
          "button":{"enum":[null,"MouseLeft","MouseRight"]},
          "startTick":{"type":["integer","null"]},"endTick":{"type":["integer","null"]}}}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpWorldActRunner run) => new WorldActTool(run);

    private sealed class WorldActTool(ProjectReviewMcpWorldActRunner run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Press the action button (check: open, talk, give, read, eat, place) or use the selected tool (useTool) exactly once on a tile next to the player as the selected player (role or local screen) of the owned disposable review. SDVKit aims the virtual cursor at that tile; Stardew may still fall back to the held item, tiles above or below it or the player's tile, and weapons and the rod turn toward the tile and act in that direction. Refused with worldActionCursorAimUnavailable in gamepad mode or with a hidden mouse. Requires a fresh inventoryRevision from stardew_inventory_get. Completion proves input delivery only; read menu, world or inventory again to prove the effect.",
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
            if (!TryQuery(request.Params?.Arguments, out ReviewWorldActQuery? query))
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "stardew_world_act requires action check or useTool, bounded x/y, and an exact sha256 inventory revision." }]
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

    internal static bool TryQuery(IDictionary<string, JsonElement>? values, out ReviewWorldActQuery? query)
    {
        query = null;
        if (values is null || values.Count != 4
            || !values.TryGetValue("action", out JsonElement action) || action.ValueKind != JsonValueKind.String
            || !values.TryGetValue("x", out JsonElement x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int tileX)
            || !values.TryGetValue("y", out JsonElement y) || y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out int tileY)
            || !values.TryGetValue("inventoryRevision", out JsonElement inventory) || inventory.ValueKind != JsonValueKind.String)
            return false;
        query = new(action.GetString() ?? "", tileX, tileY, inventory.GetString() ?? "");
        return ReviewWorldActContract.Validate(query) is null;
    }
}
