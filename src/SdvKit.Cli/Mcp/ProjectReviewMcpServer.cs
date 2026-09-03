using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpServer
{
    internal const string RuntimeToolName = "stardew_runtime_get";
    private const int OperationFailed = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private static readonly JsonElement EmptyInputSchema = JsonDocument.Parse(
        """{ "type": "object", "additionalProperties": false }""")
        .RootElement.Clone();
    private static readonly JsonElement OutputSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "launchId", "topology", "role", "observedAtUtc", "target", "runtime"],
          "allOf": [
            {
              "if": { "properties": { "topology": { "const": "single" } } },
              "then": { "properties": { "role": { "type": "null" } } }
            },
            {
              "if": { "properties": { "topology": { "const": "network-2" } } },
              "then": { "properties": { "role": { "enum": ["host", "farmhand"] } } }
            }
          ],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "launchId": { "type": "string", "pattern": "^[0-9a-f]{32}$" },
            "topology": { "type": "string", "enum": ["single", "network-2"] },
            "role": { "type": ["string", "null"], "enum": [null, "host", "farmhand"] },
            "observedAtUtc": { "type": "string", "format": "date-time" },
            "target": {
              "type": "object",
              "additionalProperties": false,
              "required": ["uniqueId", "version", "buildIdentity"],
              "properties": {
                "uniqueId": { "type": "string" },
                "version": { "type": "string" },
                "buildIdentity": { "type": "string", "pattern": "^sha256:[0-9a-f]{64}$" }
              }
            },
            "testSave": {
              "type": ["object", "null"],
              "additionalProperties": false,
              "required": ["fixtureId", "saveId"],
              "properties": {
                "fixtureId": { "type": "string" },
                "saveId": { "type": "string" }
              }
            },
            "runtime": {
              "type": "object",
              "additionalProperties": false,
              "required": ["schemaVersion", "worldReady", "season", "dayOfMonth", "year", "timeOfDay", "locationId", "tileX", "tileY", "menuOpen"],
              "properties": {
                "schemaVersion": { "type": "integer", "const": 1 },
                "worldReady": { "type": "boolean" },
                "season": { "type": ["string", "null"] },
                "dayOfMonth": { "type": ["integer", "null"] },
                "year": { "type": ["integer", "null"] },
                "timeOfDay": { "type": ["integer", "null"] },
                "locationId": { "type": ["string", "null"] },
                "tileX": { "type": ["integer", "null"] },
                "tileY": { "type": ["integer", "null"] },
                "menuOpen": { "type": "boolean" }
              }
            }
          }
        }
        """).RootElement.Clone();

    public static async Task<int> RunStdioAsync(
        string projectRoot,
        string topology,
        string? role,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var reader = new ProjectReviewMcpRuntimeReader(
            projectRoot,
            topology,
            role);
        ProjectReviewMcpContextResult preflight = reader.ReadContext();
        if (!preflight.Succeeded)
        {
            error.WriteLine(
                $"SDVKit MCP startup failed [{preflight.ErrorCode}]: {preflight.ErrorMessage}");
            return OperationFailed;
        }

        ProjectReviewMcpDataQueryRunner? runData = string.Equals(
            topology,
            LiveLabState.SingleTopology,
            StringComparison.Ordinal)
                ? query => ProjectReviewDataService.Execute(query, projectRoot)
                : null;
        McpServerOptions options = CreateOptions(reader, runData);
        await using var transport = new StdioServerTransport(options);
        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    internal static McpServerOptions CreateOptions(
        ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpDataQueryRunner? runData = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var tools = new List<McpServerTool> { new RuntimeMcpTool(reader) };
        tools.AddRange(ProjectReviewMcpDiagnosticsTools.Create(reader));
        if (runData is not null)
        {
            tools.AddRange(ProjectReviewMcpDataTools.Create(reader, runData));
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "sdvkit-project-review",
                Version = typeof(ProjectReviewMcpServer).Assembly
                    .GetName().Version?.ToString(3) ?? "0.6.1",
            },
            ServerInstructions =
                "Tools are bound to one exact active project review and expose only its selected role. Review and loaded-mod diagnostics are available for every topology; canonical Data tools remain single-only. Re-check errors by starting or repairing that review; never infer access to normal saves or Mods.",
            ToolCollection = [.. tools],
        };
    }

    private sealed class RuntimeMcpTool(ProjectReviewMcpRuntimeReader reader)
        : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = RuntimeToolName,
            Description = "Read the selected role's fresh runtime state from the exact active SDVKit project review.",
            InputSchema = EmptyInputSchema,
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

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Params?.Arguments is { Count: > 0 })
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = "Invalid arguments: stardew_runtime_get accepts an empty object only.",
                        },
                    ],
                });
            }

            return ValueTask.FromResult(ReadRuntime(reader));
        }
    }

    private static CallToolResult ReadRuntime(ProjectReviewMcpRuntimeReader reader)
    {
        ProjectReviewMcpReadResult result = reader.Read();
        if (!result.Succeeded)
        {
            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"SDVKit review unavailable [{result.ErrorCode}]: {result.ErrorMessage}",
                    },
                ],
            };
        }

        JsonElement structured = JsonSerializer.SerializeToElement(
            result.Snapshot,
            JsonOptions);
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
        };
    }
}
