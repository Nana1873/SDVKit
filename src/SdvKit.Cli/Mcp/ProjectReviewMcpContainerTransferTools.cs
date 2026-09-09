using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpContainerTransferTools
{
    internal const string ToolName = "stardew_container_transfer";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","capturedAtUtc","data"],
         "properties":{"schemaVersion":{"const":1},"state":{"enum":["completed","refused","partial","uncertain"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},"topology":{"const":"single"},
          "role":{"type":"null"},"capturedAtUtc":{"type":"string","format":"date-time"},
          "data":{"type":["object","null"]}}}
        """).RootElement.Clone();
    internal static McpServerTool Create(Func<ReviewContainerTransferQuery, CancellationToken, ReviewContainerTransferReport> run) => new TransferTool(run);

    private sealed class TransferTool(Func<ReviewContainerTransferQuery, CancellationToken, ReviewContainerTransferReport> run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Transfer one exact bounded quantity between one selected backpack slot and the currently open supported vanilla chest. Requires --allow-container-transfer and fresh stardew_container_get identities. Uses native right-click menu behavior; never retries an uncertain or partial action.",
            InputSchema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["direction","sourceSlot","quantity","qualifiedItemId","selectionIdentity","containerRevision","instanceIdentity","itemRevision"],"properties":{"direction":{"enum":["deposit","withdraw"]},"sourceSlot":{"type":"integer","minimum":0,"maximum":143},"quantity":{"type":"integer","minimum":1,"maximum":99},"qualifiedItemId":{"type":"string","minLength":4,"maxLength":256},"selectionIdentity":{"type":"string","pattern":"^sha256:[0-9a-f]{64}$"},"containerRevision":{"type":"string","pattern":"^sha256:[0-9a-f]{64}$"},"instanceIdentity":{"type":"string","pattern":"^sha256:[0-9a-f]{64}$"},"itemRevision":{"type":"string","pattern":"^sha256:[0-9a-f]{64}$"}}}""").RootElement.Clone(),
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false }
        };
        public override IReadOnlyList<object> Metadata => [];
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
        {
            try
            {
                JsonElement json = JsonSerializer.SerializeToElement(request.Params?.Arguments);
                ReviewContainerTransferQuery? query = json.Deserialize<ReviewContainerTransferQuery>(JsonOptions);
                if (ReviewContainerTransferContract.Validate(query) is not null) throw new JsonException();
                ReviewContainerTransferReport result = run(query!, cancellationToken);
                JsonElement output = JsonSerializer.SerializeToElement(result, JsonOptions);
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = result.State != "completed",
                    StructuredContent = output,
                    Content = [new TextContentBlock { Text = output.GetRawText() }]
                });
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "stardew_container_transfer requires one exact valid transfer request." }]
                });
            }
        }
    }
}
