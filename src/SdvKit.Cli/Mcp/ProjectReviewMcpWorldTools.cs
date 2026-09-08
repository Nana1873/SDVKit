using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpWorldTools
{
    internal const string ToolName = "stardew_world_area_get";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["x","y","width","height"],"properties":{
          "x":{"type":"integer","minimum":-100000,"maximum":100000},
          "y":{"type":"integer","minimum":-100000,"maximum":100000},
          "width":{"type":"integer","minimum":1,"maximum":32},
          "height":{"type":"integer","minimum":1,"maximum":32}}}
        """).RootElement.Clone();
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","capturedAtUtc","data"],
         "properties":{
          "schemaVersion":{"const":1},"state":{"enum":["ready","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"const":"single"},"role":{"type":"null"},
          "capturedAtUtc":{"type":"string","format":"date-time"},
          "data":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
            "required":["locationName","locationInstanceId","playerId","captureTick","area","complete","tiles"],
            "properties":{
             "locationName":{"type":"string","minLength":1,"maxLength":128},
             "locationInstanceId":{"$ref":"#/$defs/token"},"playerId":{"type":"string","maxLength":20},
             "captureTick":{"type":"integer","minimum":0},"complete":{"const":true},
             "area":{"$ref":"#/$defs/area"},
             "tiles":{"type":"array","maxItems":256,"items":{"$ref":"#/$defs/tile"}}}}]}},
         "$defs":{
          "token":{"type":"string","pattern":"^[0-9a-f]{32}$"},
          "qid":{"type":"string","minLength":4,"maxLength":256},
          "area":{"type":"object","additionalProperties":false,"required":["x","y","width","height"],
            "properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer","minimum":1,"maximum":32},"height":{"type":"integer","minimum":1,"maximum":32}}},
          "item":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
            "required":["qualifiedItemId","stack","quality"],"properties":{"qualifiedItemId":{"$ref":"#/$defs/qid"},"stack":{"type":"integer","minimum":1},"quality":{"type":["integer","null"],"minimum":0}}}]},
          "observation":{"type":"object","additionalProperties":false,"required":["state","reason","item"],
            "properties":{"state":{"enum":["absent","available","unavailable"]},"reason":{"enum":[null,"worldItemFactsUnavailable"]},"item":{"$ref":"#/$defs/item"}}},
          "cropData":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
            "required":["instanceId","revision","seedItemId","harvestItemId","currentPhase","dayOfCurrentPhase","phaseCount","fullyGrown","dead","readyForHarvest","regrowsAfterHarvest"],
            "properties":{"instanceId":{"$ref":"#/$defs/token"},"revision":{"$ref":"#/$defs/token"},"seedItemId":{"$ref":"#/$defs/qid"},"harvestItemId":{"$ref":"#/$defs/qid"},"currentPhase":{"type":"integer","minimum":0},"dayOfCurrentPhase":{"type":"integer","minimum":0},"phaseCount":{"type":"integer","minimum":1,"maximum":100},"fullyGrown":{"type":"boolean"},"dead":{"type":"boolean"},"readyForHarvest":{"type":"boolean"},"regrowsAfterHarvest":{"type":"boolean"}}}]},
          "crop":{"type":"object","additionalProperties":false,"required":["state","reason","data"],
            "properties":{"state":{"enum":["missing","available","unsupported","unavailable"]},"reason":{"enum":[null,"worldCropFamilyUnsupported","worldCropPropertiesUnavailable"]},"data":{"$ref":"#/$defs/cropData"}}},
          "soil":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
            "required":["instanceId","revision","watered","needsWatering","fertilizerItemId","crop"],
            "properties":{"instanceId":{"$ref":"#/$defs/token"},"revision":{"$ref":"#/$defs/token"},"watered":{"type":"boolean"},"needsWatering":{"type":"boolean"},"fertilizerItemId":{"anyOf":[{"type":"null"},{"$ref":"#/$defs/qid"}]},"crop":{"$ref":"#/$defs/crop"}}}]},
          "machine":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
            "required":["instanceId","revision","qualifiedItemId","state","readyForHarvest","minutesUntilReady","input","output"],
             "properties":{"instanceId":{"$ref":"#/$defs/token"},"revision":{"$ref":"#/$defs/token"},"qualifiedItemId":{"$ref":"#/$defs/qid"},"state":{"enum":["idle","processing","ready"]},"readyForHarvest":{"type":"boolean"},"minutesUntilReady":{"type":"integer","minimum":-1,"maximum":1000000},"input":{"$ref":"#/$defs/observation"},"output":{"$ref":"#/$defs/observation"}}}]},
          "tile":{"type":"object","additionalProperties":false,
            "required":["x","y","soil","objectState","objectReason","objectQualifiedItemId","machine"],
            "properties":{"x":{"type":"integer"},"y":{"type":"integer"},"soil":{"$ref":"#/$defs/soil"},"objectState":{"enum":["missing","machine","unsupported","unavailable"]},"objectReason":{"enum":[null,"worldObjectFamilyUnsupported","worldObjectPropertiesUnavailable"]},"objectQualifiedItemId":{"anyOf":[{"type":"null"},{"$ref":"#/$defs/qid"}]},"machine":{"$ref":"#/$defs/machine"}}}
         }}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpRuntimeReader reader,
        Func<ReviewWorldArea, CancellationToken, ReviewWorldReport>? run = null) =>
        new WorldTool(run ?? ((area, token) => ProjectReviewWorldService.Execute(reader, area,
            cancellationToken: token)));

    private sealed class WorldTool(Func<ReviewWorldArea, CancellationToken, ReviewWorldReport> run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Read one complete bounded row-major rectangle of current-location tilled soil, crops, and ordinary data-backed vanilla machines from an exact world-ready single review. Missing, unsupported, and unavailable object state is explicit. Instance identities detect replacement within the process; revisions change with exposed state. No mutation.",
            InputSchema = InputSchema,
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            },
        };
        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryArea(request.Params?.Arguments, out ReviewWorldArea? area))
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "stardew_world_area_get requires integer x, y, width, and height; width/height are 1-32 and area is at most 256 tiles." }]
                });
            ReviewWorldReport result = run(area!, cancellationToken);
            JsonElement json = JsonSerializer.SerializeToElement(result, JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = result.State != "ready",
                StructuredContent = json,
                Content = [new TextContentBlock { Text = json.GetRawText() }]
            });
        }
    }

    private static bool TryArea(IDictionary<string, JsonElement>? arguments, out ReviewWorldArea? area)
    {
        area = null;
        if (arguments is null || arguments.Count != 4
            || !TryInt(arguments, "x", out int x) || !TryInt(arguments, "y", out int y)
            || !TryInt(arguments, "width", out int width) || !TryInt(arguments, "height", out int height))
            return false;
        area = new(x, y, width, height);
        return ReviewWorldContract.QueryProblem(area) is null;
    }

    private static bool TryInt(IDictionary<string, JsonElement> values, string name, out int value)
    {
        value = 0;
        return values.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}
