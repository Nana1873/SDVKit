using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate ReviewWorldMoveReport ProjectReviewMcpWorldMoveRunner(
    ReviewWorldMoveQuery query, CancellationToken cancellationToken);

internal static class ProjectReviewMcpWorldMoveTools
{
    internal const string ToolName = "stardew_world_move";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["direction"],
         "properties":{"direction":{"enum":["up","right","down","left"]},
          "turnOnly":{"type":"boolean","default":false,"description":"Only turn to face the direction (one input tick) instead of moving one tile."}}}
        """).RootElement.Clone();
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","observedAtUtc",
          "direction","turnOnly","dispatchState","button","startTick","endTick",
          "locationId","tileX","tileY","position","targetX","targetY","reached"],
         "properties":{"schemaVersion":{"const":1},"state":{"enum":["completed","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"const":"single"},"role":{"type":"null"},"observedAtUtc":{"type":"string"},
          "direction":{"enum":["up","right","down","left"]},"turnOnly":{"type":"boolean"},
          "dispatchState":{"enum":["notDispatched","completed","mayHaveRun"]},
          "button":{"type":["string","null"]},
          "startTick":{"type":["integer","null"]},"endTick":{"type":["integer","null"]},
          "locationId":{"type":["string","null"]},
          "tileX":{"type":["integer","null"]},"tileY":{"type":["integer","null"]},
          "position":{"type":["object","null"],"additionalProperties":false,
           "required":["facing","standingX","standingY","grabTileX","grabTileY","toolTileX","toolTileY"],
           "properties":{"facing":{"enum":["up","right","down","left"]},
            "standingX":{"type":"number"},"standingY":{"type":"number"},
            "grabTileX":{"type":"integer"},"grabTileY":{"type":"integer"},
            "toolTileX":{"type":"integer"},"toolTileY":{"type":"integer"}}},
          "targetX":{"type":["integer","null"]},"targetY":{"type":["integer","null"]},
          "reached":{"type":"boolean"}}}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpWorldMoveRunner run) => new WorldMoveTool(run);

    private sealed class WorldMoveTool(ProjectReviewMcpWorldMoveRunner run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Move the player exactly one tile up, right, down or left in the owned disposable single-player review, or with turnOnly just face that direction. SDVKit holds the game's own movement key until the player stands on the next tile past its centre, then releases it, so repeated calls land one tile apart; a turn presses the key for one tick and shifts the player a few pixels inside its tile. Refused with worldMoveTargetBlocked when the next tile is off the map or blocked for the player (objects, features, buildings, villagers, map collisions) and with worldActionPlayerBusy during a menu, event, tool use or while already moving. The result reports locationId, tileX/tileY, position and reached (true only on the target tile of the same location, facing the direction; a warp on the target changes the location instead). No pathfinding; read stardew_world_area_get to plan a route.",
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
            if (!TryQuery(request.Params?.Arguments, out ReviewWorldMoveQuery? query))
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "stardew_world_move requires direction up, right, down or left and an optional boolean turnOnly." }]
                });
            ReviewWorldMoveReport report = run(query!, cancellationToken);
            JsonElement json = JsonSerializer.SerializeToElement(report, JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = report.State != "completed",
                StructuredContent = json,
                Content = [new TextContentBlock { Text = json.GetRawText() }]
            });
        }
    }

    internal static bool TryQuery(IDictionary<string, JsonElement>? values, out ReviewWorldMoveQuery? query)
    {
        query = null;
        if (values is null || values.Count is < 1 or > 2
            || !values.TryGetValue("direction", out JsonElement direction) || direction.ValueKind != JsonValueKind.String)
            return false;
        bool turnOnly = false;
        if (values.Count == 2)
        {
            if (!values.TryGetValue("turnOnly", out JsonElement turn)
                || turn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            turnOnly = turn.ValueKind == JsonValueKind.True;
        }
        query = new(direction.GetString() ?? "", turnOnly);
        return ReviewWorldMoveContract.Validate(query) is null;
    }
}
