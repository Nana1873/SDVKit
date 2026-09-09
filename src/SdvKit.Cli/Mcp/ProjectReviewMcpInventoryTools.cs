using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpInventoryTools
{
    internal const string ToolName = "stardew_inventory_get";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","capturedAtUtc","data"],
         "properties":{
          "schemaVersion":{"const":1},"state":{"enum":["ready","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"enum":["single","network-2"]},"role":{"enum":[null,"host","farmhand"]},
          "capturedAtUtc":{"type":"string","format":"date-time"},
          "data":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
            "required":["captureId","inventoryRevision","playerId","capacity","selectedSlot","complete","limitations","slots"],
            "properties":{
              "captureId":{"type":"string","pattern":"^[0-9a-f]{32}$"},
              "inventoryRevision":{"type":"string","pattern":"^sha256:[0-9a-f]{64}$"},
              "playerId":{"type":"string","maxLength":20},
              "capacity":{"type":"integer","minimum":1,"maximum":144},
              "selectedSlot":{"type":["integer","null"],"minimum":0,"maximum":143},
              "complete":{"type":"boolean"},
              "limitations":{"type":"array","maxItems":1,"uniqueItems":true,"items":{"const":"itemDataUnavailable"}},
              "slots":{"type":"array","minItems":1,"maxItems":144,"items":{"type":"object","additionalProperties":false,
                "required":["slot","state","reason","item"],"properties":{
                  "slot":{"type":"integer","minimum":0,"maximum":143},
                  "state":{"enum":["empty","occupied","unavailable"]},
                  "reason":{"enum":[null,"itemDataUnavailable"]},
                  "item":{"$ref":"#/$defs/item"}}}}
            }}]}},
         "$defs":{"item":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
           "required":["qualifiedItemId","stack","quality"],"properties":{
             "qualifiedItemId":{"type":"string","minLength":4,"maxLength":256},
             "stack":{"type":"integer","minimum":1},"quality":{"type":["integer","null"],"minimum":0}}}]}}}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpRuntimeReader reader,
        Func<CancellationToken, ReviewInventoryReport>? run = null) =>
        new InventoryTool(run ?? (token => ProjectReviewInventoryService.Execute(reader, cancellationToken: token)));

    private sealed class InventoryTool(Func<CancellationToken, ReviewInventoryReport> run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Read every bounded backpack slot for the exact selected world-ready player in an owned review role or local screen. There is no peer-player fallback. Slots are explicitly empty, occupied, or unavailable. The capture ID is request-scoped; inventoryRevision compares the visible supported facts within the launch and is not an item-instance handle. Read-only and does not require a menu.",
            InputSchema = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""").RootElement.Clone(),
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
            },
        };
        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Params?.Arguments is { Count: > 0 })
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "stardew_inventory_get accepts no arguments." }],
                });
            ReviewInventoryReport result = run(cancellationToken);
            JsonElement json = JsonSerializer.SerializeToElement(result, JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = result.State != "ready",
                StructuredContent = json,
                Content = [new TextContentBlock { Text = json.GetRawText() }],
            });
        }
    }
}
