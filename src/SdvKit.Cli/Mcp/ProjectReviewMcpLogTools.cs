using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpLogTools
{
    internal const string ToolName = "stardew_mod_diagnostics";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["modId"],"properties":{
          "modId":{"type":"string","minLength":1,"maxLength":256,"pattern":"^[A-Za-z0-9_.-]+$"},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":20}}}
        """).RootElement.Clone();
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["schemaVersion","state","errorCode","launchId","topology","role","modId","buildIdentity","source","counts","limit","truncated","diagnostics"],
         "properties":{
          "schemaVersion":{"const":1},"state":{"enum":["ready","unavailable"]},
          "errorCode":{"type":["string","null"]},"launchId":{"type":["string","null"]},
          "topology":{"enum":["single","network-2"]},"role":{"enum":[null,"host","farmhand"]},
          "modId":{"type":"string","maxLength":256},"buildIdentity":{"type":["string","null"]},
          "limit":{"type":"integer","minimum":1,"maximum":100},"truncated":{"type":"boolean"},
          "source":{"type":["object","null"],"additionalProperties":false,
            "required":["name","totalBytes","scannedBytes","scanTruncated","incompleteLineWithheld","lastWrittenAtUtc"],
            "properties":{"name":{"const":"isolatedSmapiLatest"},"totalBytes":{"type":"integer","minimum":0},
              "scannedBytes":{"type":"integer","minimum":0,"maximum":4194304},"scanTruncated":{"type":"boolean"},
              "incompleteLineWithheld":{"type":"boolean"},"lastWrittenAtUtc":{"type":"string","format":"date-time"}}},
          "counts":{"type":["object","null"],"additionalProperties":false,
            "required":["total","matching","returned","totalIsExact"],
            "properties":{"total":{"type":"integer","minimum":0},"matching":{"type":"integer","minimum":0},
              "returned":{"type":"integer","minimum":0,"maximum":100},"totalIsExact":{"type":"boolean"}}},
          "diagnostics":{"type":"array","maxItems":100,"items":{"type":"object","additionalProperties":false,
            "required":["time","severity","attribution","phase","lines","withheldLines","truncated"],
            "properties":{"time":{"type":"string","maxLength":8},"severity":{"enum":["WARN","ERROR","ALERT"]},
              "attribution":{"enum":["logger","ambiguousLogger","sharedMention"]},"phase":{"enum":["unknown","loading","runtime"]},
              "lines":{"type":"array","maxItems":32,"items":{"type":"string","maxLength":1024}},
              "withheldLines":{"type":"integer","minimum":0},"truncated":{"type":"boolean"}}}}
         }}
        """).RootElement.Clone();

    internal static McpServerTool Create(ProjectReviewMcpRuntimeReader reader) => new LogTool(reader);

    private sealed class LogTool(ProjectReviewMcpRuntimeReader reader) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Description = "Read bounded selected-mod warnings and multiline exceptions from the exact role's owned SMAPI log. Attribution is not causality; withheld context and truncation are explicit. Does not upload logs or accept paths.",
            InputSchema = InputSchema,
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
            IDictionary<string, JsonElement>? args = request.Params?.Arguments;
            int limit = ProjectReviewLogDiagnostics.DefaultLimit;
            if (args is null || args.Keys.Any(k => k is not ("modId" or "limit"))
                || !args.TryGetValue("modId", out JsonElement mod) || mod.ValueKind != JsonValueKind.String
                || (args.TryGetValue("limit", out JsonElement bound)
                    && (bound.ValueKind != JsonValueKind.Number || !bound.TryGetInt32(out limit)))
                || !ProjectReviewLogDiagnostics.ValidQuery(mod.GetString(), limit))
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "Invalid arguments for stardew_mod_diagnostics." }],
                });
            }
            ReviewLogDiagnosticsResult result = ProjectReviewLogDiagnostics.Execute(reader, mod.GetString()!, limit);
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
