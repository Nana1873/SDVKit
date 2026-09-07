using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate LiveLabCommandResult ProjectReviewMcpTextureQueryRunner(
    ReviewTextureQuery query);

internal static class ProjectReviewMcpTextureTools
{
    internal const string AssetsToolName = "stardew_texture_assets_list";
    internal const string GetToolName = "stardew_texture_get";
    internal const string PreviewToolName = "stardew_texture_preview";
    internal const string MimeType = "image/png";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private static readonly JsonElement AssetsInputSchema = Schema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "offset": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "limit": { "type": "integer", "minimum": 1, "maximum": 100 }
          }
        }
        """);
    private static readonly JsonElement AssetInputSchema = Schema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["asset"],
          "properties": {
            "asset": { "type": "string", "minLength": 1, "maxLength": 512 }
          }
        }
        """);
    private static readonly JsonElement AssetsOutputSchema = Schema(
        $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "gameVersion", "gameFileVersion", "sourceCategory", "provenance", "assets", "page", "coverage"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "gameVersion": { "type": "string" },
            "gameFileVersion": { "type": "string" },
            "sourceCategory": { "type": "string", "const": "canonical-game-content" },
            "provenance": { "$ref": "#/$defs/provenance" },
            "assets": { "type": "array", "maxItems": 100, "items": { "$ref": "#/$defs/asset" } },
            "page": { "$ref": "#/$defs/page" },
            "coverage": { "$ref": "#/$defs/coverage" }
          },
          "$defs": {{TextureDefinitions}}
        }
        """);
    private static readonly JsonElement GetOutputSchema = Schema(
        $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "gameVersion", "gameFileVersion", "assetName", "sourceCategory", "available", "metadata", "provenance"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "gameVersion": { "type": "string" },
            "gameFileVersion": { "type": "string" },
            "assetName": { "type": "string", "maxLength": 512 },
            "sourceCategory": { "type": "string", "const": "canonical-game-content" },
            "available": { "type": "boolean", "const": true },
            "metadata": { "$ref": "#/$defs/metadata" },
            "provenance": { "$ref": "#/$defs/provenance" }
          },
          "$defs": {{TextureDefinitions}}
        }
        """);
    private static readonly JsonElement PreviewOutputSchema = Schema(
        $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "gameVersion", "gameFileVersion", "assetName", "sourceCategory", "available", "metadata", "provenance", "preview"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "gameVersion": { "type": "string" },
            "gameFileVersion": { "type": "string" },
            "assetName": { "type": "string", "maxLength": 512 },
            "sourceCategory": { "type": "string", "const": "canonical-game-content" },
            "available": { "type": "boolean", "const": true },
            "metadata": { "$ref": "#/$defs/previewMetadata" },
            "provenance": { "$ref": "#/$defs/provenance" },
            "preview": {
              "type": "object",
              "additionalProperties": false,
              "required": ["width", "height", "encodedBytes", "sha256", "mimeType"],
              "properties": {
                "width": { "type": "integer", "minimum": 1, "maximum": 512 },
                "height": { "type": "integer", "minimum": 1, "maximum": 512 },
                "encodedBytes": { "type": "integer", "minimum": 57, "maximum": 2097152 },
                "sha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
                "mimeType": { "type": "string", "const": "image/png" }
              }
            }
          },
          "$defs": {{TextureDefinitions}}
        }
        """);

    private const string TextureDefinitions = """
    {
      "metadata": {
        "type": "object", "additionalProperties": false,
        "required": ["width", "height", "runtimeFormat", "levelCount", "hasMipMaps"],
        "properties": {
          "width": { "type": "integer", "minimum": 1, "maximum": 2147483647 },
          "height": { "type": "integer", "minimum": 1, "maximum": 2147483647 },
          "runtimeFormat": { "type": "string", "maxLength": 128 },
          "levelCount": { "type": "integer", "minimum": 1 },
          "hasMipMaps": { "type": "boolean" }
        }
      },
      "previewMetadata": {
        "type": "object", "additionalProperties": false,
        "required": ["width", "height", "runtimeFormat", "levelCount", "hasMipMaps"],
        "properties": {
          "width": { "type": "integer", "minimum": 1, "maximum": 8192 },
          "height": { "type": "integer", "minimum": 1, "maximum": 8192 },
          "runtimeFormat": { "type": "string", "const": "Color" },
          "levelCount": { "type": "integer", "minimum": 1 },
          "hasMipMaps": { "type": "boolean" }
        }
      },
      "provenance": {
        "type": "object", "additionalProperties": false,
        "required": ["pipelineStage", "detailedProviderAvailable", "detail"],
        "properties": {
          "pipelineStage": { "type": "string", "const": "final-post-pipeline" },
          "detailedProviderAvailable": { "type": "boolean", "const": false },
          "detail": { "type": "string" }
        }
      },
      "asset": {
        "type": "object", "additionalProperties": false,
        "required": ["assetName", "sourceCategory", "available"],
        "properties": {
          "assetName": { "type": "string", "maxLength": 512 },
          "sourceCategory": { "type": "string", "const": "canonical-game-content" },
          "available": { "type": "boolean", "const": true }
        }
      },
      "page": {
        "type": "object", "additionalProperties": false,
        "required": ["offset", "limit", "returned", "total", "nextOffset"],
        "properties": {
          "offset": { "type": "integer", "minimum": 0 },
          "limit": { "type": "integer", "minimum": 1, "maximum": 100 },
          "returned": { "type": "integer", "minimum": 0, "maximum": 100 },
          "total": { "type": "integer", "minimum": 0, "maximum": 8192 },
          "nextOffset": { "type": ["integer", "null"], "minimum": 0 }
        }
      },
      "coverage": {
        "type": "object", "additionalProperties": false,
        "required": ["candidates", "classified", "textures", "nonTextures", "gaps", "complete"],
        "properties": {
          "candidates": { "type": "integer", "minimum": 0, "maximum": 8192 },
          "classified": { "type": "integer", "minimum": 0, "maximum": 8192 },
          "textures": { "type": "integer", "minimum": 0, "maximum": 8192 },
          "nonTextures": { "type": "integer", "minimum": 0, "maximum": 8192 },
          "gaps": { "type": "integer", "const": 0 },
          "complete": { "type": "boolean", "const": true }
        }
      }
    }
    """;

    public static IReadOnlyList<McpServerTool> Create(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpTextureQueryRunner runQuery)
    {
        ArgumentNullException.ThrowIfNull(runtimeReader);
        ArgumentNullException.ThrowIfNull(runQuery);
        return
        [
            new TextureMcpTool(runtimeReader, runQuery, AssetsToolName,
                "List one bounded page of canonical texture candidates and coverage.",
                AssetsInputSchema, AssetsOutputSchema, ReviewTextureContract.AssetsOperation),
            new TextureMcpTool(runtimeReader, runQuery, GetToolName,
                "Read bounded metadata and final-pipeline provenance for one exact texture.",
                AssetInputSchema, GetOutputSchema, ReviewTextureContract.GetOperation),
            new TextureMcpTool(runtimeReader, runQuery, PreviewToolName,
                "Render one bounded final-pipeline texture preview and return the checked PNG as MCP image content.",
                AssetInputSchema, PreviewOutputSchema, ReviewTextureContract.PreviewOperation),
        ];
    }

    private sealed class TextureMcpTool(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpTextureQueryRunner runQuery,
        string name,
        string description,
        JsonElement inputSchema,
        JsonElement outputSchema,
        string operation) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = outputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = operation != ReviewTextureContract.PreviewOperation,
                DestructiveHint = false,
                IdempotentHint = operation != ReviewTextureContract.PreviewOperation,
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
            if (!TryCreateQuery(request.Params?.Arguments, operation, out ReviewTextureQuery? query))
            {
                return ValueTask.FromResult(Error($"Invalid arguments for {name}."));
            }

            ProjectReviewMcpContextResult before = runtimeReader.ReadContext();
            if (!before.Succeeded)
            {
                return ValueTask.FromResult(Error(
                    $"SDVKit review unavailable [{before.ErrorCode}]: {before.ErrorMessage}"));
            }

            LiveLabCommandResult result = runQuery(query!);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0
                || result.Report is not ReviewTextureReport report
                || !string.Equals(report.State, "ready", StringComparison.Ordinal)
                || report.Problems.Count != 0)
            {
                return ValueTask.FromResult(TextureError(result.Report as ReviewTextureReport));
            }

            if (!ProjectReviewTextureService.TryReadMcpPreviewBytes(
                    query!, report, runtimeReader.ProjectRoot, out byte[]? pngBytes))
            {
                return ValueTask.FromResult(Error(
                    "SDVKit review texture unavailable [textureResponseInvalid]."));
            }

            ProjectReviewMcpContextResult after = runtimeReader.ReadContext();
            if (!ProjectReviewMcpAssetBinding.Same(before, after))
            {
                return ValueTask.FromResult(Error(
                    "SDVKit review texture unavailable [reviewBindingChanged]."));
            }

            object snapshot = Snapshot(report);
            JsonElement structured = JsonSerializer.SerializeToElement(snapshot, JsonOptions);
            List<ContentBlock> content =
            [
                new TextContentBlock { Text = structured.GetRawText() },
            ];
            if (pngBytes is not null)
            {
                content.Add(ImageContentBlock.FromBytes(pngBytes, MimeType));
            }
            return ValueTask.FromResult(new CallToolResult
            {
                StructuredContent = structured,
                Content = content,
            });
        }
    }

    private static object Snapshot(ReviewTextureReport report) => report.Operation switch
    {
        ReviewTextureContract.AssetsOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.SourceCategory,
            report.Provenance,
            report.Assets,
            report.Page,
            report.Coverage,
        },
        ReviewTextureContract.GetOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.SourceCategory,
            report.Available,
            report.Metadata,
            report.Provenance,
        },
        _ => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.SourceCategory,
            report.Available,
            report.Metadata,
            report.Provenance,
            preview = new
            {
                report.Preview!.Width,
                report.Preview.Height,
                report.Preview.EncodedBytes,
                report.Preview.Sha256,
                mimeType = MimeType,
            },
        },
    };

    private static bool TryCreateQuery(
        IDictionary<string, JsonElement>? arguments,
        string operation,
        out ReviewTextureQuery? query)
    {
        query = null;
        if (operation == ReviewTextureContract.AssetsOperation)
        {
            if (arguments is not null
                && arguments.Keys.Any(key => key is not "offset" and not "limit"))
            {
                return false;
            }
            int offset = 0;
            int limit = ReviewTextureContract.DefaultPageLimit;
            if (!TryOptionalInt(arguments, "offset", 0, int.MaxValue, ref offset)
                || !TryOptionalInt(arguments, "limit", 1,
                    ReviewTextureContract.MaximumPageLimit, ref limit))
            {
                return false;
            }
            query = new ReviewTextureQuery(operation, null, offset, limit);
            return true;
        }

        if (arguments is null || arguments.Count != 1
            || !arguments.TryGetValue("asset", out JsonElement assetElement)
            || assetElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string? asset = assetElement.GetString();
        if (!ReviewTextureContract.IsCanonicalAssetName(asset)) return false;
        query = new ReviewTextureQuery(operation, asset, 0, 1);
        return true;
    }

    private static bool TryOptionalInt(IDictionary<string, JsonElement>? arguments,
        string name, int minimum, int maximum, ref int value)
    {
        if (arguments is null || !arguments.TryGetValue(name, out JsonElement element))
            return true;
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int parsed)
            || parsed < minimum || parsed > maximum) return false;
        value = parsed;
        return true;
    }

    private static CallToolResult TextureError(ReviewTextureReport? report)
    {
        string? candidate = report is { Problems.Count: > 0 }
            ? report.Problems[0].Code
            : null;
        string code = candidate is { Length: > 0 and <= 64 }
            && candidate.All(char.IsAsciiLetterOrDigit)
                ? candidate : "textureQueryFailed";
        return Error($"SDVKit review texture unavailable [{code}].");
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
