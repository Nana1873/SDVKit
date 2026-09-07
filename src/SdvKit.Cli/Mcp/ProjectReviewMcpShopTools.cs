using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpShopTools
{
    internal const string ToolName = "stardew_shop_get";
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
            "required":["shopId","currency","identityScope","playerId","scrollIndex","offers","money","inventory","heldItem"],
            "properties":{
              "shopId":{"const":"SeedShop"},"currency":{"const":"Gold"},
              "identityScope":{"type":"string","pattern":"^[0-9a-f]{32}$"},
              "playerId":{"type":"string","maxLength":20},"scrollIndex":{"type":"integer","minimum":0,"maximum":256},
              "money":{"type":"integer"},"heldItem":{"$ref":"#/$defs/item"},
              "inventory":{"type":"array","maxItems":144,"items":{"type":"object","additionalProperties":false,
                "required":["slot","item"],"properties":{"slot":{"type":"integer","minimum":0,"maximum":143},"item":{"$ref":"#/$defs/item"}}}},
              "offers":{"type":"array","maxItems":256,"items":{"type":"object","additionalProperties":false,
                "required":["forSaleIndex","availability","reason","item","price","stock","unlimitedStock"],
                "properties":{
                  "forSaleIndex":{"type":"integer","minimum":0,"maximum":255},"availability":{"enum":["available","unavailable"]},
                  "reason":{"enum":[null,"shopItemUnsupported","shopOfferBehaviorUnsupported","shopOfferValuesInvalid","shopStockMissing"]},
                  "item":{"$ref":"#/$defs/item"},"price":{"type":["integer","null"],"minimum":0},
                  "stock":{"type":["integer","null"],"minimum":0,"maximum":2147483646},"unlimitedStock":{"type":["boolean","null"]}}}}
            }}]}},
         "$defs":{"item":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
           "required":["qualifiedItemId","stack","quality"],"properties":{
             "qualifiedItemId":{"type":"string","minLength":4,"maxLength":256},
             "stack":{"type":"integer","minimum":1},"quality":{"type":["integer","null"],"minimum":0}}}]}}}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpRuntimeReader reader,
        Func<CancellationToken, ReviewShopReport>? run = null) =>
        new ShopTool(run ?? (token => ProjectReviewShopService.Execute(reader, cancellationToken: token)));

    private sealed class ShopTool(Func<CancellationToken, ReviewShopReport> run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Read fresh SeedShop Gold offers, player money, inventory and held item from an exact world-ready single review. Other shops and custom behavior are unavailable; unsupported offers are explicit. Point-in-time evidence, no purchase guarantee or input. Compare captures from the same launch, player and menu identity scope; price/stock are not protected by a menu input revision.",
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
                    Content = [new TextContentBlock { Text = "stardew_shop_get accepts no arguments." }],
                });
            ReviewShopReport result = run(cancellationToken);
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
