using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate ProjectReviewScreenshotResult ProjectReviewMcpScreenshotRunner(
    ReviewScreenshotCaptureQuery query,
    CancellationToken cancellationToken);

internal static class ProjectReviewMcpScreenshotTools
{
    internal const string CaptureToolName = "stardew_screenshot_capture";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private static readonly JsonElement InputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["mode", "label"],
          "properties": {
            "mode": { "type": "string", "enum": ["map", "viewport"] },
            "label": { "type": "string", "minLength": 1, "maxLength": 64, "pattern": "^[A-Za-z0-9_-]+$" }
          }
        }
        """);
    private static readonly JsonElement OutputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "launchId", "topology", "role", "mode", "label", "fileName", "capturedAtUtc", "width", "height", "encodedBytes", "sha256", "mimeType"],
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
            "mode": { "type": "string", "enum": ["map", "viewport"] },
            "label": { "type": "string", "minLength": 1, "maxLength": 64, "pattern": "^[A-Za-z0-9_-]+$" },
            "fileName": { "type": "string", "pattern": "^SDVKit-[A-Za-z0-9_-]{1,64}\\.png$", "maxLength": 75 },
            "capturedAtUtc": { "type": "string", "format": "date-time" },
            "width": { "type": "integer", "minimum": 1, "maximum": 8192 },
            "height": { "type": "integer", "minimum": 1, "maximum": 8192 },
            "encodedBytes": { "type": "integer", "minimum": 1, "maximum": 16777216 },
            "sha256": { "type": "string", "pattern": "^sha256:[0-9a-f]{64}$" },
            "mimeType": { "type": "string", "const": "image/png" }
          }
        }
        """);

    public static IReadOnlyList<McpServerTool> Create(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpScreenshotRunner runCapture)
    {
        ArgumentNullException.ThrowIfNull(runtimeReader);
        ArgumentNullException.ThrowIfNull(runCapture);
        return [new ScreenshotMcpTool(runtimeReader, runCapture)];
    }

    private sealed class ScreenshotMcpTool(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpScreenshotRunner runCapture)
        : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = CaptureToolName,
            Description = "Capture one bounded map or viewport PNG from the exact selected SDVKit review role and return it as MCP image content.",
            InputSchema = InputSchema,
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = false,
                DestructiveHint = false,
                IdempotentHint = false,
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
            if (!TryCreateQuery(
                    request.Params?.Arguments,
                    out ReviewScreenshotCaptureQuery? query))
            {
                return ValueTask.FromResult(Error(
                    "Invalid arguments: stardew_screenshot_capture requires exactly mode 'map' or 'viewport' and a safe 1-64 character label."));
            }

            ProjectReviewMcpReadResult preflight = runtimeReader.Read();
            if (!preflight.Succeeded)
            {
                return ValueTask.FromResult(Error(
                    $"SDVKit review unavailable [{preflight.ErrorCode}]: {preflight.ErrorMessage}"));
            }

            ProjectReviewScreenshotResult result = runCapture(
                query!,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Succeeded || result.Capture is null)
            {
                ProjectReviewScreenshotProblem? problem = result.Problems.Count == 0
                    ? null
                    : result.Problems[0];
                return ValueTask.FromResult(Error(problem is null
                    ? "SDVKit screenshot unavailable [screenshotCaptureFailed]."
                    : $"SDVKit screenshot unavailable [{problem.Code}]."));
            }

            ProjectReviewMcpReadResult postflight = runtimeReader.Read();
            if (!postflight.Succeeded
                || !SameBinding(preflight.Snapshot!, postflight.Snapshot!))
            {
                return ValueTask.FromResult(Error(
                    "SDVKit screenshot unavailable [reviewBindingChanged]: The exact launch or selected role changed during capture."));
            }

            ProjectReviewScreenshotCapture capture = result.Capture;
            var metadata = new
            {
                schemaVersion = ReviewScreenshotContract.SchemaVersion,
                launchId = preflight.Snapshot!.LaunchId,
                topology = preflight.Snapshot.Topology,
                role = preflight.Snapshot.Role,
                capture.Mode,
                capture.Label,
                capture.FileName,
                capture.CapturedAtUtc,
                capture.Width,
                capture.Height,
                capture.EncodedBytes,
                capture.Sha256,
                mimeType = ReviewScreenshotContract.MimeType,
            };
            JsonElement structured = JsonSerializer.SerializeToElement(
                metadata,
                JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                StructuredContent = structured,
                Content =
                [
                    new TextContentBlock { Text = structured.GetRawText() },
                    ImageContentBlock.FromBytes(
                        capture.PngBytes,
                        ReviewScreenshotContract.MimeType),
                ],
            });
        }
    }

    private static bool TryCreateQuery(
        IDictionary<string, JsonElement>? arguments,
        out ReviewScreenshotCaptureQuery? query)
    {
        query = null;
        if (arguments is null
            || arguments.Count != 2
            || !arguments.TryGetValue("mode", out JsonElement modeElement)
            || !arguments.TryGetValue("label", out JsonElement labelElement)
            || modeElement.ValueKind != JsonValueKind.String
            || labelElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? mode = modeElement.GetString();
        string? label = labelElement.GetString();
        var candidate = new ReviewScreenshotCaptureQuery(mode!, label!);
        if (ProjectReviewScreenshotService.ValidateQuery(candidate) is not null)
        {
            return false;
        }

        query = candidate;
        return true;
    }

    private static bool SameBinding(
        ProjectReviewMcpRuntimeSnapshot before,
        ProjectReviewMcpRuntimeSnapshot after) =>
        string.Equals(before.LaunchId, after.LaunchId, StringComparison.Ordinal)
        && string.Equals(before.Topology, after.Topology, StringComparison.Ordinal)
        && string.Equals(before.Role, after.Role, StringComparison.Ordinal)
        && string.Equals(before.Target.UniqueId, after.Target.UniqueId, StringComparison.Ordinal)
        && string.Equals(before.Target.Version, after.Target.Version, StringComparison.Ordinal)
        && string.Equals(
            before.Target.BuildIdentity,
            after.Target.BuildIdentity,
            StringComparison.Ordinal)
        && string.Equals(
            before.TestSave?.FixtureId,
            after.TestSave?.FixtureId,
            StringComparison.Ordinal)
        && string.Equals(
            before.TestSave?.SaveId,
            after.TestSave?.SaveId,
            StringComparison.Ordinal);

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static JsonElement ParseSchema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
