using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpContainerTools
{
    internal const string ToolName = "stardew_container_get";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","capturedAtUtc","data"],
         "properties":{
          "schemaVersion":{"const":1},"state":{"enum":["ready","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"const":"single"},"role":{"type":"null"},
          "capturedAtUtc":{"type":"string","format":"date-time"},
          "data":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
            "required":["captureId","selectionIdentity","containerRevision","identityScope","backingIdentity","playerId","locationName","tileX","tileY","chestItemId","player","container","heldItem","complete","limitations"],
            "properties":{
              "captureId":{"$ref":"#/$defs/token"},"selectionIdentity":{"$ref":"#/$defs/revision"},
              "containerRevision":{"$ref":"#/$defs/revision"},"identityScope":{"$ref":"#/$defs/token"},
              "backingIdentity":{"$ref":"#/$defs/revision"},
              "playerId":{"type":"string","maxLength":20},"locationName":{"type":"string","minLength":1,"maxLength":256},
              "tileX":{"type":"integer"},"tileY":{"type":"integer"},"chestItemId":{"enum":["(BC)130","(BC)232"]},
              "player":{"$ref":"#/$defs/side"},"container":{"$ref":"#/$defs/side"},
              "heldItem":{"$ref":"#/$defs/observedItem"},"complete":{"type":"boolean"},
              "limitations":{"type":"array","maxItems":1,"uniqueItems":true,"items":{"const":"itemDataUnavailable"}}
            }}]}},
         "$defs":{
          "token":{"type":"string","pattern":"^[0-9a-f]{32}$"},
          "revision":{"type":"string","pattern":"^sha256:[0-9a-f]{64}$"},
          "item":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
            "required":["qualifiedItemId","stack","quality"],"properties":{"qualifiedItemId":{"type":"string","minLength":4,"maxLength":256},"stack":{"type":"integer","minimum":1},"quality":{"type":["integer","null"],"minimum":0}}}]},
          "identity":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,"required":["instanceIdentity","itemRevision"],"properties":{"instanceIdentity":{"$ref":"#/$defs/revision"},"itemRevision":{"$ref":"#/$defs/revision"}}}]},
          "slot":{"type":"object","additionalProperties":false,"required":["slot","state","reason","item","identity"],
            "properties":{"slot":{"type":"integer","minimum":0,"maximum":143},"state":{"enum":["empty","occupied","unavailable"]},"reason":{"enum":[null,"itemDataUnavailable"]},"item":{"$ref":"#/$defs/item"},"identity":{"$ref":"#/$defs/identity"}}},
          "side":{"type":"object","additionalProperties":false,"required":["side","capacity","slots"],
            "properties":{"side":{"enum":["player","container"]},"capacity":{"type":"integer","minimum":1,"maximum":144},"slots":{"type":"array","minItems":1,"maxItems":144,"items":{"$ref":"#/$defs/slot"}}}},
          "observedItem":{"type":"object","additionalProperties":false,"required":["state","reason","item","identity"],
            "properties":{"state":{"enum":["empty","occupied","unavailable"]},"reason":{"enum":[null,"itemDataUnavailable"]},"item":{"$ref":"#/$defs/item"},"identity":{"$ref":"#/$defs/identity"}}}
         }}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpRuntimeReader reader,
        Func<CancellationToken, ReviewContainerReport>? run = null) =>
        new ContainerTool(run ?? (token => ProjectReviewContainerService.Execute(reader, cancellationToken: token)));

    private sealed class ContainerTool(Func<CancellationToken, ReviewContainerReport> run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Read both inventory sides and the held item for the exact open supported vanilla chest in an owned single review. selectionIdentity binds the open menu and native placed-chest reference; containerRevision additionally binds every item reference, the supported public facts, and vanilla stacking semantics. Tokens are comparison evidence, not action handles. Read-only and never opens or mutates a chest.",
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
                    Content = [new TextContentBlock { Text = "stardew_container_get accepts no arguments." }],
                });
            ReviewContainerReport result = run(cancellationToken);
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
