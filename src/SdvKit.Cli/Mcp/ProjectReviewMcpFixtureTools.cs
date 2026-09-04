using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate LiveLabCommandResult ProjectReviewMcpFixtureQueryRunner(
    ReviewFixtureQuery query,
    ProjectReviewMcpRuntimeSnapshot expected,
    CancellationToken cancellationToken);

internal static class ProjectReviewMcpFixtureTools
{
    internal const string StatusToolName = "stardew_fixture_status_get";
    internal const string EnterToolName = "stardew_fixture_enter";
    internal const string FarmToolName = "stardew_fixture_farm";
    internal const string BuildingToolName = "stardew_fixture_building_ensure";
    internal const string AnimalToolName = "stardew_fixture_animal_ensure";
    internal const string SaveToolName = "stardew_fixture_save";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonElement EmptyInputSchema = ParseSchema(
        """{ "type": "object", "additionalProperties": false }""");
    private static readonly JsonElement EnterInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["building"],
          "properties": {
            "building": { "type": "string", "minLength": 1, "maxLength": 128 }
          }
        }
        """);
    private static readonly JsonElement BuildingInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["alias", "kind", "x", "y"],
          "properties": {
            "alias": { "type": "string", "pattern": "^[a-z][a-z0-9_-]{0,31}$" },
            "kind": { "type": "string", "minLength": 1, "maxLength": 128 },
            "x": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
            "y": { "type": "integer", "minimum": 0, "maximum": 2147483647 }
          }
        }
        """);
    private static readonly JsonElement AnimalInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["building", "kind"],
          "properties": {
            "building": { "type": "string", "minLength": 1, "maxLength": 128 },
            "kind": { "type": "string", "minLength": 1, "maxLength": 128 }
          }
        }
        """);
    private static readonly JsonElement OutputSchemaTemplate = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "state", "operation", "launchId", "topology", "role", "completedAtUtc", "fixtureId", "saveId", "message", "problems", "commandWritten", "mayHaveRun", "cancellationRequested"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "state": { "type": "string", "enum": ["ready", "blocked"] },
            "operation": { "type": "string" },
            "launchId": { "type": "string", "pattern": "^(?:|[0-9a-f]{32})$" },
            "topology": { "type": "string", "enum": ["single", "network-2"] },
            "role": { "type": ["string", "null"], "enum": [null, "host", "farmhand"] },
            "completedAtUtc": { "type": "string", "format": "date-time" },
            "fixtureId": { "type": ["string", "null"] },
            "saveId": { "type": ["string", "null"] },
            "message": { "type": "string", "minLength": 1, "maxLength": 4096 },
            "problems": {
              "type": "array",
              "maxItems": 8,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["code", "message"],
                "properties": {
                  "code": { "type": "string", "minLength": 1, "maxLength": 64 },
                  "message": { "type": "string", "minLength": 1, "maxLength": 4096 }
                }
              }
            },
            "commandWritten": { "type": "boolean" },
            "mayHaveRun": { "type": "boolean" },
            "cancellationRequested": { "type": "boolean" },
            "status": {
              "type": "object",
              "additionalProperties": false,
              "required": ["locationId", "playerId", "mainPlayer", "multiplayer", "buildings"],
              "properties": {
                "locationId": { "type": "string" },
                "playerId": { "type": "integer" },
                "mainPlayer": { "type": "boolean" },
                "multiplayer": { "type": "boolean" },
                "buildings": { "type": "array", "items": { "$ref": "#/$defs/building" } }
              }
            },
            "navigation": {
              "type": "object",
              "additionalProperties": false,
              "required": ["locationId", "tileX", "tileY", "changed"],
              "properties": {
                "locationId": { "type": "string" },
                "tileX": { "type": "integer" },
                "tileY": { "type": "integer" },
                "changed": { "type": "boolean" }
              }
            },
            "building": { "$ref": "#/$defs/building" },
            "animal": {
              "type": "object",
              "additionalProperties": false,
              "required": ["animalId", "canonicalKind", "canonicalToken", "homeBuildingId", "assigned", "changed"],
              "properties": {
                "animalId": { "type": "integer" },
                "canonicalKind": { "type": "string" },
                "canonicalToken": { "type": "string" },
                "homeBuildingId": { "type": "string", "format": "uuid" },
                "assigned": { "type": "boolean", "const": true },
                "changed": { "type": "boolean" }
              }
            },
            "save": {
              "type": "object",
              "additionalProperties": false,
              "required": ["saveId", "persistedAtUtc"],
              "properties": {
                "saveId": { "type": "string", "minLength": 1 },
                "persistedAtUtc": { "type": "string", "format": "date-time" }
              }
            }
          },
          "$defs": {
            "building": {
              "type": "object",
              "additionalProperties": false,
              "required": ["alias", "buildingId", "canonicalKind", "canonicalToken", "x", "y", "interiorLocationId", "mapAsset", "ownedObjects", "ownedAnimals", "changed"],
              "properties": {
                "alias": { "type": "string", "pattern": "^[a-z][a-z0-9_-]{0,31}$" },
                "buildingId": { "type": "string", "format": "uuid" },
                "canonicalKind": { "type": "string" },
                "canonicalToken": { "type": "string" },
                "x": { "type": "integer", "minimum": 0 },
                "y": { "type": "integer", "minimum": 0 },
                "interiorLocationId": { "type": ["string", "null"] },
                "mapAsset": { "type": ["string", "null"] },
                "ownedObjects": { "type": "integer", "minimum": 0 },
                "ownedAnimals": { "type": "integer", "minimum": 0 },
                "changed": { "type": "boolean" }
              }
            }
          }
        }
        """);
    private static readonly JsonElement StatusOutputSchema = CreateOutputSchema(
        ReviewFixtureTransportContract.StatusOperation,
        "status");
    private static readonly JsonElement EnterOutputSchema = CreateOutputSchema(
        ReviewFixtureTransportContract.EnterOperation,
        "navigation");
    private static readonly JsonElement FarmOutputSchema = CreateOutputSchema(
        ReviewFixtureTransportContract.FarmOperation,
        "navigation");
    private static readonly JsonElement BuildingOutputSchema = CreateOutputSchema(
        ReviewFixtureTransportContract.BuildingEnsureOperation,
        "building");
    private static readonly JsonElement AnimalOutputSchema = CreateOutputSchema(
        ReviewFixtureTransportContract.AnimalEnsureOperation,
        "animal");
    private static readonly JsonElement SaveOutputSchema = CreateOutputSchema(
        ReviewFixtureTransportContract.SaveOperation,
        "save");

    public static IReadOnlyList<McpServerTool> Create(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpFixtureQueryRunner runQuery,
        string topology,
        string? role)
    {
        ArgumentNullException.ThrowIfNull(runtimeReader);
        ArgumentNullException.ThrowIfNull(runQuery);
        var tools = new List<McpServerTool>
        {
            new FixtureMcpTool(
                runtimeReader,
                runQuery,
                topology,
                role,
                StatusToolName,
                ReviewFixtureTransportContract.StatusOperation,
                "Read the exact owned disposable fixture status and stable owned-building identities.",
                EmptyInputSchema,
                StatusOutputSchema,
                readOnly: true,
                destructive: false,
                idempotent: true,
                arguments => HasOnly(arguments, [])
                    ? new ReviewFixtureQuery(ReviewFixtureTransportContract.StatusOperation)
                    : null),
            new FixtureMcpTool(
                runtimeReader,
                runQuery,
                topology,
                role,
                EnterToolName,
                ReviewFixtureTransportContract.EnterOperation,
                "Enter one exact owned fixture building or the greenhouse through its natural warp.",
                EnterInputSchema,
                EnterOutputSchema,
                readOnly: false,
                destructive: false,
                idempotent: true,
                arguments => HasOnly(arguments, ["building"])
                    && TryString(arguments, "building", out string? building)
                    ? new ReviewFixtureQuery(
                        ReviewFixtureTransportContract.EnterOperation,
                        Building: building)
                    : null),
            new FixtureMcpTool(
                runtimeReader,
                runQuery,
                topology,
                role,
                FarmToolName,
                ReviewFixtureTransportContract.FarmOperation,
                "Return from an allowed fixture interior to the Farm through its natural warp.",
                EmptyInputSchema,
                FarmOutputSchema,
                readOnly: false,
                destructive: false,
                idempotent: true,
                arguments => HasOnly(arguments, [])
                    ? new ReviewFixtureQuery(ReviewFixtureTransportContract.FarmOperation)
                    : null),
        };

        if (string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || string.Equals(role, NetworkTwoContract.HostRole, StringComparison.Ordinal))
        {
            tools.Add(new FixtureMcpTool(
                runtimeReader,
                runQuery,
                topology,
                role,
                BuildingToolName,
                ReviewFixtureTransportContract.BuildingEnsureOperation,
                "Idempotently ensure one canonical building in the exact owned disposable fixture.",
                BuildingInputSchema,
                BuildingOutputSchema,
                readOnly: false,
                destructive: true,
                idempotent: true,
                TryBuilding));
            tools.Add(new FixtureMcpTool(
                runtimeReader,
                runQuery,
                topology,
                role,
                AnimalToolName,
                ReviewFixtureTransportContract.AnimalEnsureOperation,
                "Idempotently ensure one canonical animal in an exact owned fixture building.",
                AnimalInputSchema,
                AnimalOutputSchema,
                readOnly: false,
                destructive: false,
                idempotent: true,
                TryAnimal));
            tools.Add(new FixtureMcpTool(
                runtimeReader,
                runQuery,
                topology,
                role,
                SaveToolName,
                ReviewFixtureTransportContract.SaveOperation,
                "Durably save only the exact owned disposable fixture through Stardew's supported save iterator.",
                EmptyInputSchema,
                SaveOutputSchema,
                readOnly: false,
                destructive: false,
                idempotent: false,
                arguments => HasOnly(arguments, [])
                    ? new ReviewFixtureQuery(ReviewFixtureTransportContract.SaveOperation)
                    : null));
        }

        return tools;
    }

    private sealed class FixtureMcpTool(
        ProjectReviewMcpRuntimeReader runtimeReader,
        ProjectReviewMcpFixtureQueryRunner runQuery,
        string topology,
        string? role,
        string name,
        string operation,
        string description,
        JsonElement inputSchema,
        JsonElement outputSchema,
        bool readOnly,
        bool destructive,
        bool idempotent,
        Func<IDictionary<string, JsonElement>?, ReviewFixtureQuery?> createQuery)
        : McpServerTool
    {
        public override Tool ProtocolTool { get; } = Tool(
            name,
            description,
            inputSchema,
            outputSchema,
            readOnly,
            destructive,
            idempotent);

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            ReviewFixtureQuery? query = createQuery(request.Params?.Arguments);
            if (query is null
                || !string.Equals(query.Operation, operation, StringComparison.Ordinal)
                || ProjectReviewFixtureService.Validate(query, topology, role) is not null)
            {
                return ValueTask.FromResult(Error($"Invalid arguments for {name}."));
            }

            ProjectReviewMcpReadResult preflight = runtimeReader.Read();
            if (!preflight.Succeeded
                || preflight.Snapshot?.TestSave is null
                || !preflight.Snapshot.Runtime.WorldReady)
            {
                return ValueTask.FromResult(Error(
                    $"SDVKit fixture unavailable [{preflight.ErrorCode ?? "fixtureTestSaveRequired"}]: "
                    + (preflight.ErrorMessage
                        ?? "The exact ready SDVKit-owned test save is required.")));
            }

            LiveLabCommandResult result = runQuery(
                query,
                preflight.Snapshot,
                cancellationToken);
            if (result.ExitCode != 0
                || result.Report is not ReviewFixtureReport report
                || !MatchesReadyReport(query, report, preflight.Snapshot))
            {
                return ValueTask.FromResult(FixtureError(result.Report as ReviewFixtureReport));
            }

            JsonElement structured = JsonSerializer.SerializeToElement(report, JsonOptions);
            return ValueTask.FromResult(new CallToolResult
            {
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
            });
        }
    }

    private static Tool Tool(
        string name,
        string description,
        JsonElement inputSchema,
        JsonElement outputSchema,
        bool readOnly,
        bool destructive,
        bool idempotent) =>
        new()
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = outputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = readOnly,
                DestructiveHint = destructive,
                IdempotentHint = idempotent,
                OpenWorldHint = false,
            },
        };

    private static ReviewFixtureQuery? TryBuilding(
        IDictionary<string, JsonElement>? arguments)
    {
        if (!HasOnly(arguments, ["alias", "kind", "x", "y"])
            || !TryString(arguments, "alias", out string? alias)
            || !TryString(arguments, "kind", out string? kind)
            || !TryInt(arguments, "x", out int x)
            || !TryInt(arguments, "y", out int y))
        {
            return null;
        }

        return new ReviewFixtureQuery(
            ReviewFixtureTransportContract.BuildingEnsureOperation,
            Alias: alias,
            Kind: kind,
            X: x,
            Y: y);
    }

    private static ReviewFixtureQuery? TryAnimal(
        IDictionary<string, JsonElement>? arguments)
    {
        if (!HasOnly(arguments, ["building", "kind"])
            || !TryString(arguments, "building", out string? building)
            || !TryString(arguments, "kind", out string? kind))
        {
            return null;
        }

        return new ReviewFixtureQuery(
            ReviewFixtureTransportContract.AnimalEnsureOperation,
            Building: building,
            Kind: kind);
    }

    private static bool TryString(
        IDictionary<string, JsonElement>? arguments,
        string name,
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
        return value is not null;
    }

    private static bool TryInt(
        IDictionary<string, JsonElement>? arguments,
        string name,
        out int value)
    {
        value = 0;
        return arguments is not null
            && arguments.TryGetValue(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private static bool HasOnly(
        IDictionary<string, JsonElement>? arguments,
        IReadOnlyList<string> allowed) =>
        arguments is null || arguments.Keys.All(allowed.Contains);

    private static bool MatchesReadyReport(
        ReviewFixtureQuery query,
        ReviewFixtureReport report,
        ProjectReviewMcpRuntimeSnapshot expected)
    {
        if (report.SchemaVersion != ReviewFixtureTransportContract.SchemaVersion
            || !string.Equals(report.State, "ready", StringComparison.Ordinal)
            || !string.Equals(report.Operation, query.Operation, StringComparison.Ordinal)
            || !string.Equals(report.LaunchId, expected.LaunchId, StringComparison.Ordinal)
            || !string.Equals(report.Topology, expected.Topology, StringComparison.Ordinal)
            || !string.Equals(report.Role, expected.Role, StringComparison.Ordinal)
            || !string.Equals(report.FixtureId, expected.TestSave!.FixtureId, StringComparison.Ordinal)
            || !string.Equals(report.SaveId, expected.TestSave.SaveId, StringComparison.Ordinal)
            || !report.CommandWritten
            || report.MayHaveRun
            || report.Problems is null
            || report.Problems.Count != 0)
        {
            return false;
        }

        return query.Operation switch
        {
            ReviewFixtureTransportContract.StatusOperation => report.Status is not null,
            ReviewFixtureTransportContract.EnterOperation
                or ReviewFixtureTransportContract.FarmOperation => report.Navigation is not null,
            ReviewFixtureTransportContract.BuildingEnsureOperation => report.Building is not null,
            ReviewFixtureTransportContract.AnimalEnsureOperation => report.Animal is not null,
            ReviewFixtureTransportContract.SaveOperation => report.Save is not null,
            _ => false,
        };
    }

    private static CallToolResult FixtureError(ReviewFixtureReport? report)
    {
        if (report is not null)
        {
            JsonElement structured = JsonSerializer.SerializeToElement(report, JsonOptions);
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
            };
        }

        return Error("SDVKit fixture action failed [fixtureResponseInvalid].");
    }

    private static CallToolResult Error(string message) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
        };

    private static JsonElement CreateOutputSchema(
        string operation,
        string payloadName)
    {
        JsonObject schema = JsonNode.Parse(OutputSchemaTemplate.GetRawText())!
            .AsObject();
        JsonObject properties = schema["properties"]!.AsObject();
        properties["operation"] = new JsonObject
        {
            ["type"] = "string",
            ["const"] = operation,
        };
        foreach (string candidate in new[]
        {
            "status",
            "navigation",
            "building",
            "animal",
            "save",
        })
        {
            if (!string.Equals(candidate, payloadName, StringComparison.Ordinal))
            {
                properties.Remove(candidate);
            }
        }

        var readyProperties = new JsonObject
        {
            ["state"] = new JsonObject { ["const"] = "ready" },
            ["launchId"] = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = "^[0-9a-f]{32}$",
            },
            ["fixtureId"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
            },
            ["saveId"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
            },
            ["problems"] = new JsonObject { ["maxItems"] = 0 },
            ["commandWritten"] = new JsonObject { ["const"] = true },
            ["mayHaveRun"] = new JsonObject { ["const"] = false },
        };
        var blockedProperties = new JsonObject
        {
            ["state"] = new JsonObject { ["const"] = "blocked" },
            ["problems"] = new JsonObject { ["minItems"] = 1 },
        };
        schema["oneOf"] = new JsonArray
        {
            new JsonObject
            {
                ["required"] = new JsonArray(payloadName),
                ["properties"] = readyProperties,
            },
            new JsonObject
            {
                ["properties"] = blockedProperties,
                ["not"] = new JsonObject
                {
                    ["required"] = new JsonArray(payloadName),
                },
            },
        };
        return ParseSchema(schema.ToJsonString());
    }

    private static JsonElement ParseSchema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
