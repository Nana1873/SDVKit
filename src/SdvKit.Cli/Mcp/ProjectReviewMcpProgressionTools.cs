using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpProgressionTools
{
    internal const string ToolName = "stardew_progression_get";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["npcId","mailId","questId"],
         "properties":{"npcId":{"$ref":"#/$defs/id"},"mailId":{"$ref":"#/$defs/id"},"questId":{"$ref":"#/$defs/id"}},
         "$defs":{"id":{"type":"string","minLength":1,"maxLength":128,"pattern":"^[A-Za-z0-9_.-]+$"}}}
        """).RootElement.Clone();
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","capturedAtUtc","data"],
         "properties":{"schemaVersion":{"const":1},"state":{"enum":["ready","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"const":"single"},"role":{"type":"null"},"capturedAtUtc":{"type":"string","format":"date-time"},
          "data":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
           "required":["playerId","fixtureId","captureTick","selection","npc","mail","quest"],
           "properties":{"playerId":{"type":"string","maxLength":20},"fixtureId":{"type":"string","pattern":"^[0-9a-f]{32}$"},
            "captureTick":{"type":"integer","minimum":0},"selection":{"type":"object","additionalProperties":false,
             "required":["npcId","mailId","questId"],"properties":{"npcId":{"$ref":"#/$defs/id"},"mailId":{"$ref":"#/$defs/id"},"questId":{"$ref":"#/$defs/id"}}},
            "npc":{"type":"object","additionalProperties":false,"required":["state","friendshipPoints"],
             "properties":{"state":{"enum":["available","missing","noFriendship"]},"friendshipPoints":{"type":["integer","null"]}}},
            "mail":{"type":"object","additionalProperties":false,"required":["received","tomorrow","mailboxCount"],
             "properties":{"received":{"type":"boolean"},"tomorrow":{"type":"boolean"},"mailboxCount":{"type":"integer","minimum":0,"maximum":100}}},
            "quest":{"type":"object","additionalProperties":false,"required":["matchCount","acceptedCount","completedCount"],
             "properties":{"matchCount":{"type":"integer","minimum":0,"maximum":100},"acceptedCount":{"type":"integer","minimum":0,"maximum":100},"completedCount":{"type":"integer","minimum":0,"maximum":100}}}
          }}]}},"$defs":{"id":{"type":"string","minLength":1,"maxLength":128,"pattern":"^[A-Za-z0-9_.-]+$"}}}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpRuntimeReader reader,
        Func<ReviewProgressionSelection, CancellationToken, ReviewProgressionReport>? run = null) =>
        new ProgressionTool(run ?? ((selection, token) => ProjectReviewProgressionService.Execute(reader, selection,
            cancellationToken: token)));

    private sealed class ProgressionTool(Func<ReviewProgressionSelection, CancellationToken, ReviewProgressionReport> run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Read only one explicit NPC friendship, mail ID and quest ID in the exact owned disposable single-player review. Mail fields are exact membership and mailbox count; quest counts describe current questLog entries, not historical completion. Missing NPC and absent friendship are explicit. No enumeration, mutation, peer or previous-capture fallback.",
            InputSchema = InputSchema,
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
        };
        public override IReadOnlyList<object> Metadata => [];
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDictionary<string, JsonElement>? arguments = request.Params?.Arguments;
            if (arguments is null || arguments.Count != 3 || !Id(arguments, "npcId", out string? npc)
                || !Id(arguments, "mailId", out string? mail) || !Id(arguments, "questId", out string? quest))
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "stardew_progression_get requires npcId, mailId and questId: case-sensitive ASCII letters, digits, dot, underscore or hyphen; 1-128 characters." }]
                });
            ReviewProgressionReport result = run(new(npc!, mail!, quest!), cancellationToken);
            JsonElement json = JsonSerializer.SerializeToElement(result, JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = result.State != "ready",
                StructuredContent = json,
                Content = [new TextContentBlock { Text = json.GetRawText() }]
            });
        }

        private static bool Id(IDictionary<string, JsonElement> arguments, string name, out string? id)
        {
            id = arguments.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return ReviewProgressionContract.IdValid(id);
        }
    }
}
