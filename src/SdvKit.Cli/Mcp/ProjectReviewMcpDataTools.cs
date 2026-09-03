using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate LiveLabCommandResult ProjectReviewMcpDataQueryRunner(
    ReviewDataQuery query);

internal static class ProjectReviewMcpDataTools
{
    internal const string AssetsToolName = "stardew_data_assets_list";
    internal const string KeysToolName = "stardew_data_keys_list";
    internal const string RecordToolName = "stardew_data_record_get";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonElement AssetsInputSchema = ParseSchema(
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

    private static readonly JsonElement KeysInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["asset"],
          "properties": {
            "asset": { "type": "string", "minLength": 1, "maxLength": 256 },
            "offset": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "limit": { "type": "integer", "minimum": 1, "maximum": 100 }
          }
        }
        """);

    private static readonly JsonElement RecordInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["asset", "key"],
          "properties": {
            "asset": { "type": "string", "minLength": 1, "maxLength": 256 },
            "key": { "type": "string", "minLength": 1, "maxLength": 2048 }
          }
        }
        """);

    private static readonly JsonElement AssetsOutputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "gameVersion", "gameFileVersion", "assets", "page", "coverage"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "gameVersion": { "type": "string" },
            "gameFileVersion": { "type": "string" },
            "assets": {
              "type": "array",
              "maxItems": 100,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["assetName", "dataType", "shape", "keyKind", "recordCount", "supported", "problemCode"],
                "properties": {
                  "assetName": { "type": "string", "pattern": "^Data/", "maxLength": 256 },
                  "dataType": { "type": "string" },
                  "shape": { "type": "string", "enum": ["dictionary", "list", "singleton"] },
                  "keyKind": { "type": "string", "enum": ["string", "integer", "index", "singleton"] },
                  "recordCount": { "type": "integer", "minimum": 0 },
                  "supported": { "type": "boolean", "const": true },
                  "problemCode": { "type": "null" }
                }
              }
            },
            "page": { "$ref": "#/$defs/page" },
            "coverage": { "$ref": "#/$defs/coverage" }
          },
          "$defs": {
            "page": {
              "type": "object",
              "additionalProperties": false,
              "required": ["offset", "limit", "returned", "total", "nextOffset"],
              "properties": {
                "offset": { "type": "integer", "minimum": 0 },
                "limit": { "type": "integer", "minimum": 1, "maximum": 100 },
                "returned": { "type": "integer", "minimum": 0, "maximum": 100 },
                "total": { "type": "integer", "minimum": 0 },
                "nextOffset": { "type": ["integer", "null"], "minimum": 0 }
              }
            },
            "coverage": {
              "type": "object",
              "additionalProperties": false,
              "required": ["discovered", "classified", "supported", "unknown", "unclassified", "unsupported", "complete"],
              "properties": {
                "discovered": { "type": "integer", "minimum": 0 },
                "classified": { "type": "integer", "minimum": 0 },
                "supported": { "type": "integer", "minimum": 0 },
                "unknown": { "type": "integer", "minimum": 0 },
                "unclassified": { "type": "integer", "minimum": 0 },
                "unsupported": { "type": "integer", "minimum": 0 },
                "complete": { "type": "boolean", "const": true }
              }
            }
          }
        }
        """);

    private static readonly JsonElement KeysOutputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "gameVersion", "gameFileVersion", "assetName", "dataType", "shape", "keyKind", "keys", "page"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "gameVersion": { "type": "string" },
            "gameFileVersion": { "type": "string" },
            "assetName": { "type": "string", "pattern": "^Data/", "maxLength": 256 },
            "dataType": { "type": "string" },
            "shape": { "type": "string", "enum": ["dictionary", "list", "singleton"] },
            "keyKind": { "type": "string", "enum": ["string", "integer", "index", "singleton"] },
            "keys": {
              "type": "array",
              "maxItems": 100,
              "items": { "type": "string", "maxLength": 2048 }
            },
            "page": {
              "type": "object",
              "additionalProperties": false,
              "required": ["offset", "limit", "returned", "total", "nextOffset"],
              "properties": {
                "offset": { "type": "integer", "minimum": 0 },
                "limit": { "type": "integer", "minimum": 1, "maximum": 100 },
                "returned": { "type": "integer", "minimum": 0, "maximum": 100 },
                "total": { "type": "integer", "minimum": 0 },
                "nextOffset": { "type": ["integer", "null"], "minimum": 0 }
              }
            }
          }
        }
        """);

    private static readonly JsonElement RecordOutputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "gameVersion", "gameFileVersion", "assetName", "dataType", "shape", "keyKind", "key", "record"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "gameVersion": { "type": "string" },
            "gameFileVersion": { "type": "string" },
            "assetName": { "type": "string", "pattern": "^Data/", "maxLength": 256 },
            "dataType": { "type": "string" },
            "shape": { "type": "string", "enum": ["dictionary", "list", "singleton"] },
            "keyKind": { "type": "string", "enum": ["string", "integer", "index", "singleton"] },
            "key": { "type": "string", "maxLength": 2048 },
            "record": {}
          }
        }
        """);

    public static IReadOnlyList<McpServerTool> Create(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpDataQueryRunner runQuery)
    {
        ArgumentNullException.ThrowIfNull(runtimeReader);
        ArgumentNullException.ThrowIfNull(runQuery);
        return
        [
            new AssetsMcpTool(runtimeReader, runQuery),
            new KeysMcpTool(runtimeReader, runQuery),
            new RecordMcpTool(runtimeReader, runQuery),
        ];
    }

    private abstract class DataMcpTool(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpDataQueryRunner runQuery)
        : McpServerTool
    {
        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCreateQuery(request.Params?.Arguments, out ReviewDataQuery? query))
            {
                return ValueTask.FromResult(Error(
                    $"Invalid arguments for {ProtocolTool.Name}."));
            }

            ProjectReviewMcpReadResult preflight = runtimeReader.Read();
            if (!preflight.Succeeded)
            {
                return ValueTask.FromResult(Error(
                    $"SDVKit review unavailable [{preflight.ErrorCode}]: {preflight.ErrorMessage}"));
            }

            LiveLabCommandResult result = runQuery(query!);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0
                || result.Report is not ReviewDataReport report
                || !string.Equals(report.State, "ready", StringComparison.Ordinal)
                || report.Problems.Count != 0)
            {
                return ValueTask.FromResult(DataError(result.Report as ReviewDataReport));
            }

            if (!TryCreateSnapshot(query!, report, out object? snapshot))
            {
                return ValueTask.FromResult(Error(
                    "SDVKit review data unavailable [dataResponseInvalid]."));
            }

            JsonElement structured = JsonSerializer.SerializeToElement(
                snapshot,
                JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
            });
        }

        protected abstract bool TryCreateQuery(
            IDictionary<string, JsonElement>? arguments,
            out ReviewDataQuery? query);

        protected abstract bool TryCreateSnapshot(
            ReviewDataQuery query,
            ReviewDataReport report,
            out object? snapshot);
    }

    private sealed class AssetsMcpTool(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpDataQueryRunner runQuery)
        : DataMcpTool(runtimeReader, runQuery)
    {
        public override Tool ProtocolTool { get; } = Tool(
            AssetsToolName,
            "List one bounded page of installed canonical Stardew Data assets and complete coverage metadata.",
            AssetsInputSchema,
            AssetsOutputSchema);

        protected override bool TryCreateQuery(
            IDictionary<string, JsonElement>? arguments,
            out ReviewDataQuery? query)
        {
            query = null;
            if (!TryPage(arguments, ["offset", "limit"], out int offset, out int limit))
            {
                return false;
            }

            query = new ReviewDataQuery(
                ReviewDataContract.AssetsOperation,
                null,
                null,
                offset,
                limit);
            return true;
        }

        protected override bool TryCreateSnapshot(
            ReviewDataQuery query,
            ReviewDataReport report,
            out object? snapshot)
        {
            snapshot = null;
            if (!CommonSuccess(report, ReviewDataContract.AssetsOperation)
                || report.Assets is null
                || report.Page is null
                || report.Coverage is null
                || !report.Coverage.Complete
                || !PageMatches(query, report.Page, report.Assets.Count)
                || report.AssetName is not null
                || report.Keys is not null
                || report.Record is not null)
            {
                return false;
            }

            snapshot = new ProjectReviewMcpDataAssetsSnapshot(
                report.SchemaVersion,
                report.GameVersion!,
                report.GameFileVersion!,
                report.Assets,
                report.Page,
                report.Coverage);
            return true;
        }
    }

    private sealed class KeysMcpTool(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpDataQueryRunner runQuery)
        : DataMcpTool(runtimeReader, runQuery)
    {
        public override Tool ProtocolTool { get; } = Tool(
            KeysToolName,
            "List one bounded page of stable keys for one canonical Stardew Data asset.",
            KeysInputSchema,
            KeysOutputSchema);

        protected override bool TryCreateQuery(
            IDictionary<string, JsonElement>? arguments,
            out ReviewDataQuery? query)
        {
            query = null;
            if (!TryPage(arguments, ["asset", "offset", "limit"], out int offset, out int limit)
                || !TryRequiredString(
                    arguments,
                    "asset",
                    ReviewDataContract.MaximumAssetLength,
                    out string? asset))
            {
                return false;
            }

            query = new ReviewDataQuery(
                ReviewDataContract.KeysOperation,
                asset,
                null,
                offset,
                limit);
            return true;
        }

        protected override bool TryCreateSnapshot(
            ReviewDataQuery query,
            ReviewDataReport report,
            out object? snapshot)
        {
            snapshot = null;
            if (!CommonSuccess(report, ReviewDataContract.KeysOperation)
                || !AssetMetadataReady(report)
                || report.Keys is null
                || report.Page is null
                || !AssetMatches(query.Asset!, report.AssetName!)
                || !PageMatches(query, report.Page, report.Keys.Count)
                || report.Assets is not null
                || report.Coverage is not null
                || report.Record is not null)
            {
                return false;
            }

            snapshot = new ProjectReviewMcpDataKeysSnapshot(
                report.SchemaVersion,
                report.GameVersion!,
                report.GameFileVersion!,
                report.AssetName!,
                report.DataType!,
                report.Shape!,
                report.KeyKind!,
                report.Keys,
                report.Page);
            return true;
        }
    }

    private sealed class RecordMcpTool(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpDataQueryRunner runQuery)
        : DataMcpTool(runtimeReader, runQuery)
    {
        public override Tool ProtocolTool { get; } = Tool(
            RecordToolName,
            "Read one exact canonical Stardew Data record by its stable internal key.",
            RecordInputSchema,
            RecordOutputSchema);

        protected override bool TryCreateQuery(
            IDictionary<string, JsonElement>? arguments,
            out ReviewDataQuery? query)
        {
            query = null;
            if (!HasOnly(arguments, ["asset", "key"])
                || !TryRequiredString(
                    arguments,
                    "asset",
                    ReviewDataContract.MaximumAssetLength,
                    out string? asset)
                || !TryRequiredString(
                    arguments,
                    "key",
                    ReviewDataContract.MaximumKeyLength,
                    out string? key))
            {
                return false;
            }

            query = new ReviewDataQuery(
                ReviewDataContract.GetOperation,
                asset,
                key,
                0,
                1);
            return true;
        }

        protected override bool TryCreateSnapshot(
            ReviewDataQuery query,
            ReviewDataReport report,
            out object? snapshot)
        {
            snapshot = null;
            if (!CommonSuccess(report, ReviewDataContract.GetOperation)
                || !AssetMetadataReady(report)
                || string.IsNullOrEmpty(report.Key)
                || report.Record is null
                || !AssetMatches(query.Asset!, report.AssetName!)
                || !KeyMatches(query.Key!, report.Key, report.KeyKind!)
                || report.Assets is not null
                || report.Keys is not null
                || report.Page is not null
                || report.Coverage is not null)
            {
                return false;
            }

            snapshot = new ProjectReviewMcpDataRecordSnapshot(
                report.SchemaVersion,
                report.GameVersion!,
                report.GameFileVersion!,
                report.AssetName!,
                report.DataType!,
                report.Shape!,
                report.KeyKind!,
                report.Key,
                report.Record.Value);
            return true;
        }
    }

    private static Tool Tool(
        string name,
        string description,
        JsonElement inputSchema,
        JsonElement outputSchema) =>
        new()
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = outputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
            },
        };

    private static bool TryPage(
        IDictionary<string, JsonElement>? arguments,
        IReadOnlyList<string> allowed,
        out int offset,
        out int limit)
    {
        offset = 0;
        limit = ReviewDataContract.DefaultPageLimit;
        return HasOnly(arguments, allowed)
            && TryOptionalInt(arguments, "offset", 0, int.MaxValue, ref offset)
            && TryOptionalInt(
                arguments,
                "limit",
                1,
                ReviewDataContract.MaximumPageLimit,
                ref limit);
    }

    private static bool HasOnly(
        IDictionary<string, JsonElement>? arguments,
        IReadOnlyList<string> allowed) =>
        arguments is null || arguments.Keys.All(allowed.Contains);

    private static bool TryOptionalInt(
        IDictionary<string, JsonElement>? arguments,
        string name,
        int minimum,
        int maximum,
        ref int value)
    {
        if (arguments is null || !arguments.TryGetValue(name, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryRequiredString(
        IDictionary<string, JsonElement>? arguments,
        string name,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (arguments is null
            || !arguments.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && !value.Any(char.IsControl);
    }

    private static bool CommonSuccess(ReviewDataReport report, string operation) =>
        report.SchemaVersion == ReviewDataContract.SchemaVersion
        && string.Equals(report.Operation, operation, StringComparison.Ordinal)
        && string.Equals(report.State, "ready", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(report.GameVersion)
        && !string.IsNullOrWhiteSpace(report.GameFileVersion)
        && report.Problems.Count == 0;

    private static bool AssetMetadataReady(ReviewDataReport report) =>
        !string.IsNullOrWhiteSpace(report.AssetName)
        && !string.IsNullOrWhiteSpace(report.DataType)
        && report.Shape is "dictionary" or "list" or "singleton"
        && report.KeyKind is "string" or "integer" or "index" or "singleton";

    private static bool PageMatches(
        ReviewDataQuery query,
        ReviewDataPage page,
        int itemCount)
    {
        long available = Math.Max(0L, (long)page.Total - query.Offset);
        int expectedCount = (int)Math.Min(query.Limit, available);
        long endOffset = (long)query.Offset + expectedCount;
        return page.Offset == query.Offset
            && page.Limit == query.Limit
            && page.Total >= 0
            && page.Returned == expectedCount
            && itemCount == expectedCount
            && page.NextOffset == (endOffset < page.Total
                ? (int)endOffset
                : null);
    }

    private static bool AssetMatches(string requested, string canonical)
    {
        string normalized = StableIdentityNormalizer.Normalize(requested);
        return normalized.Length > 0
            && string.Equals(
                normalized,
                StableIdentityNormalizer.Normalize(canonical),
                StringComparison.Ordinal);
    }

    private static bool KeyMatches(
        string requested,
        string canonical,
        string keyKind) =>
        keyKind is "string" or "singleton"
            ? AssetMatches(requested, canonical)
            : string.Equals(requested, canonical, StringComparison.Ordinal);

    private static CallToolResult DataError(ReviewDataReport? report)
    {
        string? candidate = report is { Problems.Count: > 0 }
            ? report.Problems[0].Code
            : null;
        string code = candidate is { Length: > 0 and <= 64 }
            && candidate.All(char.IsAsciiLetterOrDigit)
                ? candidate
                : "dataQueryFailed";
        return Error($"SDVKit review data unavailable [{code}].");
    }

    private static CallToolResult Error(string message) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
        };

    private static JsonElement ParseSchema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}

internal sealed record ProjectReviewMcpDataAssetsSnapshot(
    int SchemaVersion,
    string GameVersion,
    string GameFileVersion,
    IReadOnlyList<ReviewDataAssetReport> Assets,
    ReviewDataPage Page,
    ReviewDataCoverageReport Coverage);

internal sealed record ProjectReviewMcpDataKeysSnapshot(
    int SchemaVersion,
    string GameVersion,
    string GameFileVersion,
    string AssetName,
    string DataType,
    string Shape,
    string KeyKind,
    IReadOnlyList<string> Keys,
    ReviewDataPage Page);

internal sealed record ProjectReviewMcpDataRecordSnapshot(
    int SchemaVersion,
    string GameVersion,
    string GameFileVersion,
    string AssetName,
    string DataType,
    string Shape,
    string KeyKind,
    string Key,
    JsonElement Record);
