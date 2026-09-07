using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate LiveLabCommandResult ProjectReviewMcpMapQueryRunner(
    ReviewMapQuery query);

internal static class ProjectReviewMcpMapTools
{
    internal const string AssetsToolName = "stardew_map_assets_list";
    internal const string GetToolName = "stardew_map_get";
    internal const string LayersToolName = "stardew_map_layers_list";
    internal const string LayerToolName = "stardew_map_layer_get";
    internal const string TileSheetsToolName = "stardew_map_tilesheets_list";
    internal const string WarpsToolName = "stardew_map_warps_list";
    internal const string TileToolName = "stardew_map_tile_get";
    internal const string PropertyToolName = "stardew_map_property_get";

    private const string ValidationRequestId = "00000000000000000000000000000000";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonElement PageInputSchema = Schema(
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
            "asset": { "type": "string", "minLength": 1, "maxLength": 256 }
          }
        }
        """);
    private static readonly JsonElement AssetPageInputSchema = Schema(
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
    private static readonly JsonElement LayerInputSchema = Schema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["asset", "layer"],
          "properties": {
            "asset": { "type": "string", "minLength": 1, "maxLength": 256 },
            "layer": { "type": "string", "minLength": 1, "maxLength": 256 }
          }
        }
        """);
    private static readonly JsonElement TileInputSchema = Schema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["asset", "layer", "x", "y"],
          "properties": {
            "asset": { "type": "string", "minLength": 1, "maxLength": 256 },
            "layer": { "type": "string", "minLength": 1, "maxLength": 256 },
            "x": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "y": { "type": "integer", "minimum": 0, "maximum": 2147483647 }
          }
        }
        """);
    private static readonly JsonElement PropertyInputSchema = Schema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["asset", "scope", "source", "property"],
          "properties": {
            "asset": { "type": "string", "minLength": 1, "maxLength": 256 },
            "scope": { "type": "string", "enum": ["map", "layer", "tile"] },
            "source": { "type": "string", "enum": ["direct", "tile-index"] },
            "layer": { "type": "string", "minLength": 1, "maxLength": 256 },
            "x": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "y": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "frameIndex": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "property": { "type": "string", "minLength": 1, "maxLength": 256 }
          }
        }
        """);

    public static IReadOnlyList<McpServerTool> Create(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpMapQueryRunner runQuery)
    {
        ArgumentNullException.ThrowIfNull(runtimeReader);
        ArgumentNullException.ThrowIfNull(runQuery);
        return
        [
            new MapMcpTool(runtimeReader, runQuery, AssetsToolName,
                "List one bounded page of canonical map candidates and coverage.",
                PageInputSchema, ReviewMapContract.AssetsOperation, TryAssets),
            new MapMcpTool(runtimeReader, runQuery, GetToolName,
                "Read bounded metadata for one exact final-pipeline map.",
                AssetInputSchema, ReviewMapContract.GetOperation, TryAsset),
            new MapMcpTool(runtimeReader, runQuery, LayersToolName,
                "List one bounded page of layers from one exact final-pipeline map.",
                AssetPageInputSchema, ReviewMapContract.LayersOperation, TryAssetPage),
            new MapMcpTool(runtimeReader, runQuery, LayerToolName,
                "Read one exact layer from one final-pipeline map.",
                LayerInputSchema, ReviewMapContract.LayerOperation, TryLayer),
            new MapMcpTool(runtimeReader, runQuery, TileSheetsToolName,
                "List one bounded page of tilesheets from one exact final-pipeline map.",
                AssetPageInputSchema, ReviewMapContract.TileSheetsOperation, TryAssetPage),
            new MapMcpTool(runtimeReader, runQuery, WarpsToolName,
                "List one bounded page of parsed warps from one exact final-pipeline map.",
                AssetPageInputSchema, ReviewMapContract.WarpsOperation, TryAssetPage),
            new MapMcpTool(runtimeReader, runQuery, TileToolName,
                "Read one exact bounded tile from one final-pipeline map layer.",
                TileInputSchema, ReviewMapContract.TileOperation, TryTile),
            new MapMcpTool(runtimeReader, runQuery, PropertyToolName,
                "Read one exact bounded map, layer, direct-tile, or tile-index property.",
                PropertyInputSchema, ReviewMapContract.PropertyOperation, TryProperty),
        ];
    }

    private delegate bool QueryFactory(
        IDictionary<string, JsonElement>? arguments,
        string operation,
        out ReviewMapQuery? query);

    private sealed class MapMcpTool(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpMapQueryRunner runQuery,
        string name,
        string description,
        JsonElement inputSchema,
        string operation,
        QueryFactory createQuery) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = Tool(
            name, description, inputSchema, OutputSchema(operation));

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (!createQuery(request.Params?.Arguments, operation, out ReviewMapQuery? query))
            {
                return ValueTask.FromResult(Error($"Invalid arguments for {name}."));
            }

            ProjectReviewMcpContextResult before = runtimeReader.ReadContext();
            if (!before.Succeeded)
            {
                return ValueTask.FromResult(ReviewError(before));
            }

            LiveLabCommandResult result = runQuery(query!);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0
                || result.Report is not ReviewMapReport report
                || !string.Equals(report.State, "ready", StringComparison.Ordinal)
                || report.Problems.Count != 0
                || !ProjectReviewMapService.MatchesResponse(
                    new ReviewMapResponseEnvelope(
                        ReviewMapContract.SchemaVersion,
                        ValidationRequestId,
                        report),
                    ValidationRequestId,
                    query))
            {
                return ValueTask.FromResult(MapError(result.Report as ReviewMapReport));
            }

            ProjectReviewMcpContextResult after = runtimeReader.ReadContext();
            if (!ProjectReviewMcpAssetBinding.Same(before, after))
            {
                return ValueTask.FromResult(Error(
                    "SDVKit review map unavailable [reviewBindingChanged]."));
            }

            object? snapshot = Snapshot(report);
            if (snapshot is null)
            {
                return ValueTask.FromResult(Error(
                    "SDVKit review map unavailable [mapResponseInvalid]."));
            }

            JsonElement structured = JsonSerializer.SerializeToElement(snapshot, JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
            });
        }
    }

    private static object? Snapshot(ReviewMapReport report) => report.Operation switch
    {
        ReviewMapContract.AssetsOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.Assets,
            report.Page,
            report.Coverage,
        },
        ReviewMapContract.GetOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.DataType,
            report.Map,
        },
        ReviewMapContract.LayersOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.DataType,
            report.Layers,
            report.Page,
        },
        ReviewMapContract.LayerOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.DataType,
            report.Layer,
        },
        ReviewMapContract.TileSheetsOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.DataType,
            report.TileSheets,
            report.Page,
        },
        ReviewMapContract.WarpsOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.DataType,
            report.Warps,
            report.Page,
        },
        ReviewMapContract.TileOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.DataType,
            report.Tile,
        },
        ReviewMapContract.PropertyOperation => new
        {
            report.SchemaVersion,
            report.GameVersion,
            report.GameFileVersion,
            report.AssetName,
            report.DataType,
            report.Property,
        },
        _ => null,
    };

    private static bool TryAssets(IDictionary<string, JsonElement>? arguments,
        string operation, out ReviewMapQuery? query) =>
        TryPage(arguments, [], operation, asset: null, out query);

    private static bool TryAssetPage(IDictionary<string, JsonElement>? arguments,
        string operation, out ReviewMapQuery? query)
    {
        query = null;
        return TryString(arguments, "asset", ReviewMapContract.MaximumAssetLength, out string? asset)
            && TryPage(arguments, ["asset"], operation, asset, out query);
    }

    private static bool TryAsset(IDictionary<string, JsonElement>? arguments,
        string operation, out ReviewMapQuery? query)
    {
        query = null;
        if (!HasExactly(arguments, ["asset"])
            || !TryString(arguments, "asset", ReviewMapContract.MaximumAssetLength, out string? asset))
        {
            return false;
        }
        return Valid(new ReviewMapQuery(operation, asset, null, null, null,
            null, null, null, null, 0, 1), out query);
    }

    private static bool TryLayer(IDictionary<string, JsonElement>? arguments,
        string operation, out ReviewMapQuery? query)
    {
        query = null;
        if (!HasExactly(arguments, ["asset", "layer"])
            || !TryString(arguments, "asset", ReviewMapContract.MaximumAssetLength, out string? asset)
            || !TryString(arguments, "layer", ReviewMapContract.MaximumIdentityLength, out string? layer))
        {
            return false;
        }
        return Valid(new ReviewMapQuery(operation, asset, layer, null, null,
            null, null, null, null, 0, 1), out query);
    }

    private static bool TryTile(IDictionary<string, JsonElement>? arguments,
        string operation, out ReviewMapQuery? query)
    {
        query = null;
        if (!HasExactly(arguments, ["asset", "layer", "x", "y"])
            || !TryString(arguments, "asset", ReviewMapContract.MaximumAssetLength, out string? asset)
            || !TryString(arguments, "layer", ReviewMapContract.MaximumIdentityLength, out string? layer)
            || !TryInt(arguments, "x", out int x)
            || !TryInt(arguments, "y", out int y))
        {
            return false;
        }
        return Valid(new ReviewMapQuery(operation, asset, layer, x, y,
            null, null, null, null, 0, 1), out query);
    }

    private static bool TryProperty(IDictionary<string, JsonElement>? arguments,
        string operation, out ReviewMapQuery? query)
    {
        query = null;
        string[] allowed = ["asset", "scope", "source", "layer", "x", "y", "frameIndex", "property"];
        if (!HasOnly(arguments, allowed)
            || !TryString(arguments, "asset", ReviewMapContract.MaximumAssetLength, out string? asset)
            || !TryString(arguments, "scope", 16, out string? scope)
            || !TryString(arguments, "source", 16, out string? source)
            || !TryString(arguments, "property", ReviewMapContract.MaximumPropertyNameLength, out string? property)
            || !TryOptionalString(arguments, "layer", ReviewMapContract.MaximumIdentityLength, out string? layer)
            || !TryOptionalInt(arguments, "x", out int? x)
            || !TryOptionalInt(arguments, "y", out int? y)
            || !TryOptionalInt(arguments, "frameIndex", out int? frameIndex))
        {
            return false;
        }
        return Valid(new ReviewMapQuery(operation, asset, layer, x, y,
            scope, source, frameIndex, property, 0, 1), out query);
    }

    private static bool TryPage(IDictionary<string, JsonElement>? arguments,
        IReadOnlyList<string> additionallyAllowed, string operation, string? asset,
        out ReviewMapQuery? query)
    {
        query = null;
        string[] allowed = [.. additionallyAllowed, "offset", "limit"];
        if (!HasOnly(arguments, allowed)) return false;
        int offset = 0;
        int limit = ReviewMapContract.DefaultPageLimit;
        if (!TryOptionalInt(arguments, "offset", ref offset)
            || !TryOptionalInt(arguments, "limit", ref limit)) return false;
        return Valid(new ReviewMapQuery(operation, asset, null, null, null,
            null, null, null, null, offset, limit), out query);
    }

    private static bool Valid(ReviewMapQuery candidate, out ReviewMapQuery? query)
    {
        query = ProjectReviewMapService.Validate(candidate) is null ? candidate : null;
        return query is not null;
    }

    private static bool HasExactly(IDictionary<string, JsonElement>? arguments,
        IReadOnlyList<string> names) =>
        arguments is not null && arguments.Count == names.Count && HasOnly(arguments, names);

    private static bool HasOnly(IDictionary<string, JsonElement>? arguments,
        IReadOnlyList<string> names) =>
        arguments is null || arguments.Keys.All(names.Contains);

    private static bool TryString(IDictionary<string, JsonElement>? arguments,
        string name, int maximum, out string? value)
    {
        value = null;
        if (arguments is null || !arguments.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
            && !value.Any(char.IsControl) && ReviewTransportText.IsWellFormedUtf16(value);
    }

    private static bool TryOptionalString(IDictionary<string, JsonElement>? arguments,
        string name, int maximum, out string? value)
    {
        value = null;
        return arguments is null || !arguments.TryGetValue(name, out _)
            || TryString(arguments, name, maximum, out value);
    }

    private static bool TryInt(IDictionary<string, JsonElement>? arguments,
        string name, out int value)
    {
        value = 0;
        return arguments is not null && arguments.TryGetValue(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value)
            && value >= 0;
    }

    private static bool TryOptionalInt(IDictionary<string, JsonElement>? arguments,
        string name, out int? value)
    {
        value = null;
        if (arguments is null || !arguments.TryGetValue(name, out _)) return true;
        if (!TryInt(arguments, name, out int parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryOptionalInt(IDictionary<string, JsonElement>? arguments,
        string name, ref int value)
    {
        if (arguments is null || !arguments.TryGetValue(name, out _)) return true;
        if (!TryInt(arguments, name, out int parsed)) return false;
        value = parsed;
        return true;
    }

    private static Tool Tool(string name, string description,
        JsonElement inputSchema, JsonElement outputSchema) => new()
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

    private static JsonElement OutputSchema(string operation)
    {
        (string[] fields, string extra) = operation switch
        {
            ReviewMapContract.AssetsOperation => (new[] { "assets", "page", "coverage" }, "\"assets\": { \"type\": \"array\", \"maxItems\": 100, \"items\": { \"$ref\": \"#/$defs/asset\" } }, \"page\": { \"$ref\": \"#/$defs/page\" }, \"coverage\": { \"$ref\": \"#/$defs/coverage\" }"),
            ReviewMapContract.GetOperation => (["assetName", "dataType", "map"], "\"assetName\": { \"type\": \"string\", \"maxLength\": 256 }, \"dataType\": { \"type\": \"string\", \"maxLength\": 512 }, \"map\": { \"$ref\": \"#/$defs/summary\" }"),
            ReviewMapContract.LayersOperation => (["assetName", "dataType", "layers", "page"], "\"assetName\": { \"type\": \"string\", \"maxLength\": 256 }, \"dataType\": { \"type\": \"string\", \"maxLength\": 512 }, \"layers\": { \"type\": \"array\", \"maxItems\": 100, \"items\": { \"$ref\": \"#/$defs/layer\" } }, \"page\": { \"$ref\": \"#/$defs/page\" }"),
            ReviewMapContract.LayerOperation => (["assetName", "dataType", "layer"], "\"assetName\": { \"type\": \"string\", \"maxLength\": 256 }, \"dataType\": { \"type\": \"string\", \"maxLength\": 512 }, \"layer\": { \"$ref\": \"#/$defs/layer\" }"),
            ReviewMapContract.TileSheetsOperation => (["assetName", "dataType", "tileSheets", "page"], "\"assetName\": { \"type\": \"string\", \"maxLength\": 256 }, \"dataType\": { \"type\": \"string\", \"maxLength\": 512 }, \"tileSheets\": { \"type\": \"array\", \"maxItems\": 100, \"items\": { \"$ref\": \"#/$defs/tileSheet\" } }, \"page\": { \"$ref\": \"#/$defs/page\" }"),
            ReviewMapContract.WarpsOperation => (["assetName", "dataType", "warps", "page"], "\"assetName\": { \"type\": \"string\", \"maxLength\": 256 }, \"dataType\": { \"type\": \"string\", \"maxLength\": 512 }, \"warps\": { \"type\": \"array\", \"maxItems\": 100, \"items\": { \"$ref\": \"#/$defs/warp\" } }, \"page\": { \"$ref\": \"#/$defs/page\" }"),
            ReviewMapContract.TileOperation => (["assetName", "dataType", "tile"], "\"assetName\": { \"type\": \"string\", \"maxLength\": 256 }, \"dataType\": { \"type\": \"string\", \"maxLength\": 512 }, \"tile\": { \"$ref\": \"#/$defs/tile\" }"),
            _ => (["assetName", "dataType", "property"], "\"assetName\": { \"type\": \"string\", \"maxLength\": 256 }, \"dataType\": { \"type\": \"string\", \"maxLength\": 512 }, \"property\": { \"$ref\": \"#/$defs/property\" }"),
        };
        string[] allRequired = ["schemaVersion", "gameVersion", "gameFileVersion", .. fields];
        string required = string.Join(", ", allRequired.Select(value => $"\"{value}\""));
        return Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": [{{required}}],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "gameVersion": { "type": "string" },
            "gameFileVersion": { "type": "string" },
            {{extra}}
          },
          "$defs": {{MapDefinitions}}
        }
        """);
    }

    private const string MapDefinitions = """
    {
      "page": {
        "type": "object", "additionalProperties": false,
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
        "type": "object", "additionalProperties": false,
        "required": ["discovered", "classified", "mapAssets", "nonMapAssets", "supported", "unknown", "unclassified", "unsupported", "complete"],
        "properties": {
          "discovered": { "type": "integer", "minimum": 0, "maximum": 2048 },
          "classified": { "type": "integer", "minimum": 0, "maximum": 2048 },
          "mapAssets": { "type": "integer", "minimum": 0, "maximum": 2048 },
          "nonMapAssets": { "type": "integer", "minimum": 0, "maximum": 2048 },
          "supported": { "type": "integer", "minimum": 0, "maximum": 2048 },
          "unknown": { "type": "integer", "const": 0 },
          "unclassified": { "type": "integer", "const": 0 },
          "unsupported": { "type": "integer", "const": 0 },
          "complete": { "type": "boolean", "const": true }
        }
      },
      "summary": {
        "type": "object", "additionalProperties": false,
        "required": ["displayWidth", "displayHeight", "layerCount", "tileSheetCount", "warpCount", "propertyCount"],
        "properties": {
          "displayWidth": { "type": "integer", "minimum": 1, "maximum": 1048576 },
          "displayHeight": { "type": "integer", "minimum": 1, "maximum": 1048576 },
          "layerCount": { "type": "integer", "minimum": 1, "maximum": 256 },
          "tileSheetCount": { "type": "integer", "minimum": 0, "maximum": 512 },
          "warpCount": { "type": "integer", "minimum": 0, "maximum": 4096 },
          "propertyCount": { "type": "integer", "minimum": 0, "maximum": 4096 }
        }
      },
      "asset": {
        "type": "object", "additionalProperties": false,
        "required": ["assetName", "dataType", "kind", "map", "supported", "problemCode"],
        "properties": {
          "assetName": { "type": "string", "maxLength": 256 },
          "dataType": { "type": "string", "maxLength": 512 },
          "kind": { "type": "string", "enum": ["map", "nonMap"] },
          "map": { "anyOf": [{ "$ref": "#/$defs/summary" }, { "type": "null" }] },
          "supported": { "type": "boolean" },
          "problemCode": { "type": "null" }
        }
      },
      "layer": {
        "type": "object", "additionalProperties": false,
        "required": ["ordinal", "id", "width", "height", "tileWidth", "tileHeight", "visible", "propertyCount"],
        "properties": {
          "ordinal": { "type": "integer", "minimum": 0, "maximum": 255 },
          "id": { "type": "string", "maxLength": 256 },
          "width": { "type": "integer", "minimum": 1, "maximum": 4096 },
          "height": { "type": "integer", "minimum": 1, "maximum": 4096 },
          "tileWidth": { "type": "integer", "minimum": 1, "maximum": 1024 },
          "tileHeight": { "type": "integer", "minimum": 1, "maximum": 1024 },
          "visible": { "type": "boolean" },
          "propertyCount": { "type": "integer", "minimum": 0, "maximum": 4096 }
        }
      },
      "tileSheet": {
        "type": "object", "additionalProperties": false,
        "required": ["ordinal", "id", "imageSource", "sheetWidth", "sheetHeight", "tileWidth", "tileHeight", "marginWidth", "marginHeight", "spacingWidth", "spacingHeight", "tileCount", "propertyCount"],
        "properties": {
          "ordinal": { "type": "integer", "minimum": 0, "maximum": 511 },
          "id": { "type": "string", "maxLength": 256 },
          "imageSource": { "type": "string", "maxLength": 512 },
          "sheetWidth": { "type": "integer", "minimum": 1, "maximum": 4096 },
          "sheetHeight": { "type": "integer", "minimum": 1, "maximum": 4096 },
          "tileWidth": { "type": "integer", "minimum": 1, "maximum": 1024 },
          "tileHeight": { "type": "integer", "minimum": 1, "maximum": 1024 },
          "marginWidth": { "type": "integer", "minimum": 0 },
          "marginHeight": { "type": "integer", "minimum": 0 },
          "spacingWidth": { "type": "integer", "minimum": 0 },
          "spacingHeight": { "type": "integer", "minimum": 0 },
          "tileCount": { "type": "integer", "minimum": 1, "maximum": 4194304 },
          "propertyCount": { "type": "integer", "minimum": 0, "maximum": 4096 }
        }
      },
      "warp": {
        "type": "object", "additionalProperties": false,
        "required": ["ordinal", "sourceProperty", "sourceIndex", "kind", "fromX", "fromY", "targetName", "targetX", "targetY"],
        "properties": {
          "ordinal": { "type": "integer", "minimum": 0, "maximum": 4095 },
          "sourceProperty": { "type": "string", "enum": ["Warp", "NPCWarp"] },
          "sourceIndex": { "type": "integer", "minimum": 0, "maximum": 4095 },
          "kind": { "type": "string", "enum": ["playerAndNpc", "npc"] },
          "fromX": { "type": "integer" }, "fromY": { "type": "integer" },
          "targetName": { "type": "string", "maxLength": 256 },
          "targetX": { "type": "integer" }, "targetY": { "type": "integer" }
        }
      },
      "frame": {
        "type": "object", "additionalProperties": false,
        "required": ["ordinal", "tileSheetId", "tileIndex", "blendMode", "tileIndexPropertyCount"],
        "properties": {
          "ordinal": { "type": "integer", "minimum": 0, "maximum": 1023 },
          "tileSheetId": { "type": "string", "maxLength": 256 },
          "tileIndex": { "type": "integer", "minimum": 0, "maximum": 4194303 },
          "blendMode": { "type": "string", "enum": ["Alpha", "Additive"] },
          "tileIndexPropertyCount": { "type": "integer", "minimum": 0, "maximum": 4096 }
        }
      },
      "tile": {
        "type": "object", "additionalProperties": false,
        "required": ["layerId", "x", "y", "present", "kind", "tileSheetId", "tileIndex", "blendMode", "frameInterval", "frames", "directPropertyCount", "tileIndexPropertyCount", "problemCode"],
        "properties": {
          "layerId": { "type": "string", "maxLength": 256 },
          "x": { "type": "integer", "minimum": 0, "maximum": 4095 },
          "y": { "type": "integer", "minimum": 0, "maximum": 4095 },
          "present": { "type": "boolean" },
          "kind": { "type": ["string", "null"], "enum": [null, "static", "animated"] },
          "tileSheetId": { "type": ["string", "null"], "maxLength": 256 },
          "tileIndex": { "type": ["integer", "null"], "minimum": 0, "maximum": 4194303 },
          "blendMode": { "type": ["string", "null"], "enum": [null, "Alpha", "Additive"] },
          "frameInterval": { "type": ["integer", "null"], "minimum": 1 },
          "frames": { "type": ["array", "null"], "maxItems": 1024, "items": { "$ref": "#/$defs/frame" } },
          "directPropertyCount": { "type": "integer", "minimum": 0, "maximum": 4096 },
          "tileIndexPropertyCount": { "type": "integer", "minimum": 0, "maximum": 4096 },
          "problemCode": { "type": "null" }
        }
      },
      "property": {
        "type": "object", "additionalProperties": false,
        "required": ["scope", "source", "frameIndex", "name", "type", "value"],
        "properties": {
          "scope": { "type": "string", "enum": ["map", "layer", "tile"] },
          "source": { "type": "string", "enum": ["direct", "tile-index"] },
          "frameIndex": { "type": ["integer", "null"], "minimum": 0, "maximum": 1023 },
          "name": { "type": "string", "maxLength": 256 },
          "type": { "type": "string", "enum": ["string", "boolean", "integer", "float"] },
          "value": { "type": ["string", "boolean", "integer", "number"] }
        }
      }
    }
    """;

    private static CallToolResult MapError(ReviewMapReport? report)
    {
        string code = SafeProblemCode(report is { Problems.Count: > 0 }
            ? report.Problems[0].Code
            : null)
            ?? "mapQueryFailed";
        return Error($"SDVKit review map unavailable [{code}].");
    }

    private static string? SafeProblemCode(string? code) =>
        code is { Length: > 0 and <= 64 } && code.All(char.IsAsciiLetterOrDigit)
            ? code : null;

    private static CallToolResult ReviewError(ProjectReviewMcpContextResult result) =>
        Error($"SDVKit review unavailable [{result.ErrorCode}]: {result.ErrorMessage}");

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}

internal static class ProjectReviewMcpAssetBinding
{
    internal static bool Same(
        ProjectReviewMcpContextResult before,
        ProjectReviewMcpContextResult after) =>
        before.Succeeded
        && after.Succeeded
        && before.Context!.State == after.Context!.State
        && string.Equals(before.Context.Role, after.Context.Role, StringComparison.Ordinal)
        && before.Context.TestSave == after.Context.TestSave
        && before.Context.AllTargetsReady == after.Context.AllTargetsReady
        && OwnedReviewLogReader.SameStagedContent(
            before.Context.Staging,
            after.Context.Staging);
}
