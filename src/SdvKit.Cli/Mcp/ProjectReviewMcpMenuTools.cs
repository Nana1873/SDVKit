using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpMenuTools
{
    internal const string ToolName = "stardew_menu_get";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","capturedAtUtc","identityScope","viewport","menuOpen","complete","truncated","limitations","menus"],
         "properties":{
          "schemaVersion":{"const":1},"state":{"enum":["ready","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"enum":["single","network-2"]},"role":{"enum":[null,"host","farmhand"]},
          "capturedAtUtc":{"type":"string","format":"date-time"},"identityScope":{"type":["string","null"]},
          "viewport":{"anyOf":[{"$ref":"#/$defs/rectangle"},{"type":"null"}]},
          "menuOpen":{"type":"boolean"},"complete":{"type":"boolean"},"truncated":{"type":"boolean"},
          "limitations":{"type":"array","maxItems":8,"items":{"type":"string"}},
          "menus":{"type":"array","maxItems":16,"items":{"type":"object","additionalProperties":false,
            "required":["id","parentId","relationship","type","assembly","adapter","coverage","bounds","currentTab","scrollIndex","components"],
            "properties":{
              "id":{"type":"integer","minimum":1},"parentId":{"type":["integer","null"],"minimum":1},
              "relationship":{"enum":["root","activePage","inventory","child"]},
              "type":{"type":"string","minLength":1,"maxLength":128},
              "assembly":{"type":"string","minLength":1,"maxLength":128},
              "adapter":{"enum":["publicBase","gameMenu","inventoryPage","inventoryMenu","shopMenu"]},
              "coverage":{"enum":["declaredFields","partial"]},"bounds":{"$ref":"#/$defs/rectangle"},
              "currentTab":{"type":["integer","null"]},"scrollIndex":{"type":["integer","null"]},
              "components":{"type":"array","maxItems":128,"items":{"type":"object","additionalProperties":false,
                "required":["id","kind","controllerId","bounds","visibleFlag","intersectsViewport","controllerFocused"],
                "properties":{
                  "id":{"type":"integer","minimum":1},"controllerId":{"type":"integer"},
                  "kind":{"enum":["tab","equipment","portrait","trashCan","organize","junimoNote","inventorySlot","saleRow","scrollUp","scrollDown","scrollBar","close","publicComponent"]},
                  "bounds":{"$ref":"#/$defs/rectangle"},"visibleFlag":{"type":"boolean"},
                  "intersectsViewport":{"type":"boolean"},"controllerFocused":{"type":"boolean"}}}}}}}},
         "$defs":{"rectangle":{"type":"object","additionalProperties":false,"required":["x","y","width","height"],
           "properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer"},"height":{"type":"integer"}}}}}
        """).RootElement.Clone();
    internal static McpServerTool Create(ProjectReviewMcpRuntimeReader reader,
        Func<CancellationToken, ReviewMenuReport>? run = null) =>
        new MenuTool(run ?? (token => ProjectReviewMenuService.Execute(reader, cancellationToken: token)));

    private sealed class MenuTool(Func<CancellationToken, ReviewMenuReport> run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Read fresh bounded active-menu geometry and public controls from the exact world-ready review role. Inventory and shop adapters; unknown mod menus have explicitly partial base coverage. IDs are scoped to root-menu lifetime and launch. Does not click or infer hover, selection, enabled state or clickability.",
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
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "stardew_menu_get accepts no arguments." }],
                });
            }
            ReviewMenuReport result = run(cancellationToken);
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
