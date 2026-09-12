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
              "then": {
                "required": ["sessionId"],
                "properties": {
                  "role": { "enum": ["host", "farmhand"] },
                  "sessionId": { "type": "string", "pattern": "^[0-9a-f]{32}$" }
                }
              }
            }
          ],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "launchId": { "type": "string", "pattern": "^[0-9a-f]{32}$" },
            "topology": { "type": "string", "enum": ["single", "network-2"] },
            "role": { "type": ["string", "null"], "enum": [null, "host", "farmhand"] },
            "sessionId": { "type": "string", "pattern": "^[0-9a-f]{32}$" },
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
            "screen": {
              "type": "object",
              "additionalProperties": false,
              "required": ["screenId", "farmerId", "contextId"],
              "properties": {
                "screenId": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
                "farmerId": { "type": "string", "pattern": "^-?[1-9][0-9]*$" },
                "contextId": { "type": "string", "pattern": "^[0-9a-f]{32}$" }
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
                "menuOpen": { "type": "boolean" },
                "localPlayer": { "$ref": "#/$defs/localPlayer" }
              }
            }
          },
          "$defs": {
            "localPlayer": {
              "type": "object",
              "additionalProperties": false,
              "required": ["schemaVersion", "availability", "reason", "data"],
              "properties": {
                "schemaVersion": { "const": 1 },
                "availability": { "enum": ["available", "worldNotReady", "unavailable", "unsupportedVersion", "error"] },
                "reason": { "enum": [null, "notPublished", "selectionUnavailable", "unsupportedSchema", "captureFailed", "invalidValues"] },
                "data": { "$ref": "#/$defs/playerValues" }
              },
              "allOf": [{
                "if": { "properties": { "availability": { "const": "available" } } },
                "then": { "properties": { "reason": { "type": "null" }, "data": { "type": "object" } } },
                "else": { "properties": { "data": { "type": "null" } } }
              }]
            },
            "playerValues": {
              "type": ["object", "null"],
              "additionalProperties": false,
              "required": ["playerId", "money", "health", "maxHealth", "stamina", "maxStamina", "selectedSlot", "selectedItem"],
              "properties": {
                "playerId": { "type": "string", "minLength": 1, "maxLength": 20, "pattern": "^-?[1-9][0-9]*$" },
                "money": { "$ref": "#/$defs/int32" },
                "health": { "$ref": "#/$defs/int32" },
                "maxHealth": { "$ref": "#/$defs/int32" },
                "stamina": { "type": "number", "description": "Finite single-precision game value." },
                "maxStamina": { "type": "number", "description": "Finite single-precision game value." },
                "selectedSlot": { "type": ["integer", "null"], "minimum": 0, "maximum": 2147483647 },
                "fishing": {
                  "type": ["object", "null"],
                  "additionalProperties": false,
                  "required": ["timingCast", "casting", "bobberInAir", "fishing", "nibbling", "hit", "pullingOut", "catchReady", "fromFishPond", "bobberTileX", "bobberTileY", "biteMilliseconds", "catchItemId", "catchQuantity"],
                  "properties": {
                    "timingCast": { "type": "boolean" },
                    "casting": { "type": "boolean" },
                    "bobberInAir": { "type": "boolean" },
                    "fishing": { "type": "boolean" },
                    "nibbling": { "type": "boolean" },
                    "hit": { "type": "boolean" },
                    "pullingOut": { "type": "boolean" },
                    "catchReady": { "type": "boolean" },
                    "fromFishPond": { "type": "boolean" },
                    "bobberTileX": { "type": "number" },
                    "bobberTileY": { "type": "number" },
                    "biteMilliseconds": { "type": "number" },
                    "catchItemId": { "type": ["string", "null"], "minLength": 4, "maxLength": 256 },
                    "catchQuantity": { "type": "integer", "minimum": 0, "maximum": 2147483647 }
                  }
                },
                "selectedItem": {
                  "type": ["object", "null"],
                  "additionalProperties": false,
                  "required": ["qualifiedItemId", "stack", "quality"],
                  "properties": {
                    "qualifiedItemId": { "type": "string", "minLength": 4, "maxLength": 256 },
                    "stack": { "$ref": "#/$defs/int32" },
                    "quality": { "type": ["integer", "null"], "minimum": -2147483648, "maximum": 2147483647 }
                  }
                }
              }
            },
            "int32": { "type": "integer", "minimum": -2147483648, "maximum": 2147483647 }
          }
        }
        """).RootElement.Clone();

    public static async Task<int> RunStdioAsync(
        string projectRoot,
        string topology,
        string? role,
        int? screenId,
        bool allowInput,
        bool allowFixtureActions,
        bool allowWorldActions,
        bool allowContainerTransfer,
        bool allowCpRefresh,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var reader = new ProjectReviewMcpRuntimeReader(
            projectRoot,
            topology,
            role,
            screenId: screenId);
        ProjectReviewMcpReadResult selectedPreflight = reader.Read();
        if (!selectedPreflight.Succeeded)
        {
            error.WriteLine(
                $"SDVKit MCP startup failed [{selectedPreflight.ErrorCode}]: {selectedPreflight.ErrorMessage}");
            return OperationFailed;
        }
        ProjectReviewMcpContextResult preflight = reader.ReadContext();
        if (!preflight.Succeeded)
        {
            error.WriteLine($"SDVKit MCP startup failed [{preflight.ErrorCode}]: {preflight.ErrorMessage}");
            return OperationFailed;
        }

        if (screenId is not null && (allowFixtureActions || allowWorldActions || allowContainerTransfer || allowCpRefresh))
        {
            error.WriteLine("SDVKit MCP startup failed [screenCapabilityUnsupported]: Screen-bound servers do not expose fixture actions, world interactions, container transfers, or CP refresh.");
            return OperationFailed;
        }

        if (allowFixtureActions && preflight.Context!.TestSave is null)
        {
            error.WriteLine(
                "SDVKit MCP startup failed [fixtureTestSaveRequired]: Fixture actions require the exact ready SDVKit-owned test save.");
            return OperationFailed;
        }

        if (allowWorldActions && (topology != LiveLabState.SingleTopology || role is not null
            || preflight.Context!.TestSave is null || !preflight.Context.AllTargetsReady))
        {
            error.WriteLine("SDVKit MCP startup failed [worldActionTestSaveRequired]: World interactions require the exact ready SDVKit-owned single-player test save.");
            return OperationFailed;
        }

        ProjectReviewMcpRuntimeSnapshot? transferPermission = null;
        if (allowContainerTransfer)
        {
            ProjectReviewMcpReadResult permissionRead = reader.Read();
            if (screenId is not null || preflight.Context!.TestSave is null || !permissionRead.Succeeded
                || permissionRead.Snapshot!.TestSave is null || !permissionRead.Snapshot.Runtime.WorldReady
                || topology != LiveLabState.SingleTopology || role is not null)
            {
                error.WriteLine("SDVKit MCP startup failed [containerTransferTestSaveRequired]: Container transfers require the exact ready owned unbound single disposable-world review.");
                return OperationFailed;
            }
            transferPermission = permissionRead.Snapshot;
        }

        if (allowCpRefresh && ProjectReviewMcpCpTools.RefreshPermissionError(preflight.Context!) is { } cpError)
        {
            error.WriteLine($"SDVKit MCP startup failed [{cpError}]: CP refresh requires the exact ready single root CP 2.9.1 selection.");
            return OperationFailed;
        }

        ProjectReviewMcpDataQueryRunner runData = query =>
            ProjectReviewDataService.Execute(query, reader);
        ProjectReviewMcpMapQueryRunner? runMap = screenId is null && string.Equals(
            topology,
            LiveLabState.SingleTopology,
            StringComparison.Ordinal)
                ? query => ProjectReviewMapService.Execute(query, projectRoot)
                : null;
        ProjectReviewMcpTextureQueryRunner? runTexture = screenId is null && string.Equals(
            topology,
            LiveLabState.SingleTopology,
            StringComparison.Ordinal)
                ? query => ProjectReviewTextureService.Execute(query, projectRoot)
                : null;
        ProjectReviewMcpAudioQueryRunner? runAudio = screenId is null && string.Equals(
            topology,
            LiveLabState.SingleTopology,
            StringComparison.Ordinal)
                ? query => ProjectReviewAudioService.Execute(query, projectRoot)
                : null;
        ProjectReviewMcpModAssetQueryRunner? runModAsset = screenId is null && string.Equals(
            topology,
            LiveLabState.SingleTopology,
            StringComparison.Ordinal)
                ? query => ProjectReviewModAssetService.Execute(query, projectRoot)
                : null;
        ProjectReviewMcpInputSession? inputSession = allowInput
            ? new ProjectReviewMcpInputSession(
                reader,
                ProjectReviewInputService.RuntimePath(projectRoot, topology, role),
                (query, token) => ProjectReviewInputService.Execute(
                    query,
                    projectRoot,
                    topology,
                    role,
                    screenId: screenId,
                    screenFarmerId: reader.BoundScreen?.FarmerId,
                    screenContextId: reader.BoundScreen?.ContextId,
                    cancellationToken: token))
            : null;
        ProjectReviewMcpFixtureQueryRunner? runFixture = allowFixtureActions
            ? (query, expected, token) => ProjectReviewFixtureService.Execute(
                query,
                topology,
                role,
                projectRoot,
                cancellationToken: token,
                expectedSnapshot: expected)
            : null;
        ProjectReviewMcpWorldActionRunner? runWorldAction = allowWorldActions
            ? (query, token) => ProjectReviewWorldActionService.Execute(reader, query,
                cancellationToken: token, expectedContext: preflight.Context)
            : null;
        Func<ReviewContainerTransferQuery, CancellationToken, ReviewContainerTransferReport>? runContainerTransfer =
            allowContainerTransfer && topology == LiveLabState.SingleTopology && role is null && screenId is null
                ? (query, token) => ProjectReviewContainerTransferService.Execute(query, reader,
                    cancellationToken: token, expectedSnapshot: transferPermission)
                : null;
        McpServerOptions options = CreateOptions(
            reader,
            runData,
            inputSession: inputSession,
            runFixture: runFixture,
            runMap: runMap,
            runTexture: runTexture,
            topology: topology,
            role: role,
            runAudio: runAudio,
            runModAsset: runModAsset,
            runWorldAction: runWorldAction,
            runContainerTransfer: runContainerTransfer,
            cpRefreshPermission: allowCpRefresh ? preflight.Context : null);
        var exitCode = 0;
        try
        {
            await using var transport = new StdioServerTransport(options);
            await using McpServer server = McpServer.Create(transport, options);
            await RunUntilDisconnectAsync(server, transport, inputSession, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            exitCode = CompleteInputCleanup(inputSession, error);
        }

        return exitCode;
    }

    internal static async Task RunUntilDisconnectAsync(
        McpServer server,
        ITransport transport,
        ProjectReviewMcpInputSession? inputSession,
        CancellationToken cancellationToken = default)
    {
        Task running = server.RunAsync(cancellationToken);
        try
        {
            await Task.WhenAny(running, transport.MessageReader.Completion).ConfigureAwait(false);
        }
        finally
        {
            // Signal the pending request before SDK disposal can wait for its handler.
            inputSession?.CancelPending();
        }
        await running.ConfigureAwait(false);
    }

    internal static int CompleteInputCleanup(
        ProjectReviewMcpInputSession? inputSession,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ReviewInputProblem? cleanupProblem = inputSession?.Cleanup();
        if (cleanupProblem is null)
        {
            return 0;
        }

        error.WriteLine(
            $"SDVKit MCP input cleanup failed [{cleanupProblem.Code}]: {cleanupProblem.Message}");
        return OperationFailed;
    }

    internal static McpServerOptions CreateOptions(
        ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpDataQueryRunner? runData = null,
        ProjectReviewMcpScreenshotRunner? runScreenshot = null,
        ProjectReviewMcpInputSession? inputSession = null,
        ProjectReviewMcpFixtureQueryRunner? runFixture = null,
        ProjectReviewMcpMapQueryRunner? runMap = null,
        ProjectReviewMcpTextureQueryRunner? runTexture = null,
        string topology = LiveLabState.SingleTopology,
        string? role = null,
        ProjectReviewMcpAudioQueryRunner? runAudio = null,
        ProjectReviewMcpModAssetQueryRunner? runModAsset = null,
        ProjectReviewMcpCpDiagnosisRunner? runCpDiagnosis = null,
        ProjectReviewMcpCpRefreshRunner? runCpRefresh = null,
        ProjectReviewMcpVerifiedContext? cpRefreshPermission = null,
        ProjectReviewMcpWorldActionRunner? runWorldAction = null,
        Func<ReviewContainerTransferQuery, CancellationToken, ReviewContainerTransferReport>? runContainerTransfer = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var tools = new List<McpServerTool> { new RuntimeMcpTool(reader) };
        bool containerTransferEnabled = runContainerTransfer is not null
            && reader.Topology == LiveLabState.SingleTopology
            && reader.Role is null
            && reader.ScreenId is null;
        tools.AddRange(ProjectReviewMcpDiagnosticsTools.Create(reader));
        tools.Add(ProjectReviewMcpLogTools.Create(reader));
        tools.Add(ProjectReviewMcpMenuTools.Create(reader));
        tools.Add(ProjectReviewMcpInventoryTools.Create(reader));
        tools.Add(ProjectReviewMcpContainerTools.Create(reader));
        tools.Add(ProjectReviewMcpWorldTools.Create(reader));
        if (reader.Topology == "single"
            && reader.Role is null
            && reader.ScreenId is null)
        {
            if (containerTransferEnabled)
                tools.Add(ProjectReviewMcpContainerTransferTools.Create(runContainerTransfer!));
            tools.Add(ProjectReviewMcpShopTools.Create(reader));
        }
        runCpDiagnosis ??= (pack, provider, asset, parse) => ProjectReviewCpDiagnosis.Execute(reader, pack, provider, asset, parse);
        if (cpRefreshPermission is not null)
        {
            runCpRefresh ??= (pack, provider, files, asset, key, permission) => ProjectReviewCpRefresh.Execute(
                reader.ProjectRoot, permission.Staging.Target.SourceRoot, pack, provider, files, asset, key, expectedContext: permission);
        }
        if (reader.ScreenId is null)
            tools.AddRange(ProjectReviewMcpCpTools.Create(reader, runCpDiagnosis, runCpRefresh, cpRefreshPermission));
        runScreenshot ??= (query, cancellationToken) =>
            ProjectReviewScreenshotService.Execute(
                query,
                reader.Topology,
                reader.Role,
                reader.ProjectRoot,
                screenId: reader.ScreenId,
                screenFarmerId: reader.BoundScreen?.FarmerId,
                screenContextId: reader.BoundScreen?.ContextId,
                cancellationToken: cancellationToken);
        tools.AddRange(ProjectReviewMcpScreenshotTools.Create(reader, runScreenshot));
        if (runData is not null)
        {
            tools.AddRange(ProjectReviewMcpDataTools.Create(reader, runData));
        }
        if (runMap is not null
            && reader.Topology == LiveLabState.SingleTopology
            && reader.Role is null
            && reader.ScreenId is null)
        {
            tools.AddRange(ProjectReviewMcpMapTools.Create(reader, runMap));
        }
        if (runTexture is not null
            && reader.Topology == LiveLabState.SingleTopology
            && reader.Role is null
            && reader.ScreenId is null)
        {
            tools.AddRange(ProjectReviewMcpTextureTools.Create(reader, runTexture));
        }
        if (reader.Topology == LiveLabState.SingleTopology
            && reader.Role is null
            && reader.ScreenId is null
            && runAudio is not null
            && runModAsset is not null)
        {
            tools.AddRange(ProjectReviewMcpAssetTools.Create(reader, runAudio, runModAsset));
        }
        if (inputSession is not null)
        {
            tools.AddRange(ProjectReviewMcpInputTools.Create(inputSession, allowText: reader.ScreenId is null));
        }
        if (runFixture is not null)
        {
            tools.AddRange(ProjectReviewMcpFixtureTools.Create(
                reader,
                runFixture,
                topology,
                role));
        }
        if (runWorldAction is not null
            && reader.Topology == LiveLabState.SingleTopology
            && reader.Role is null
            && reader.ScreenId is null)
            tools.Add(ProjectReviewMcpWorldActionTools.Create(runWorldAction));

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "sdvkit-project-review",
                Version = typeof(ProjectReviewMcpServer).Assembly
                    .GetName().Version?.ToString(3) ?? "0.10.1",
            },
            ServerInstructions =
                "Tools are bound to one exact active project review and expose only its selected role or local screen. A local-screen binding freezes its observed farmer and context identity; departure or replacement invalidates the server. Review diagnostics, bounded active-menu inspection, one screenshot capture, player-local backpack and chest reads, role-local world reads, and process-shared canonical Data reads are available for every valid selection. Data selection validates freshness and identity but does not create a role- or screen-local cache. Shop, map, texture, audio, mod-asset, Content Patcher, and mutation capabilities retain their narrower gates. Screenshot capture creates one non-overwriting PNG in the selected role's isolated profile and returns it as MCP image content. Texture preview returns its checked bounded PNG as MCP image content. "
                + (inputSession is null
                    ? "Input actions are disabled. "
                    : "Process-local input was explicitly enabled for this server and each typed action is bounded, acknowledged, and never retried automatically. ")
                + (runFixture is null
                    ? "Fixture actions are disabled. "
                    : "Fixture actions were explicitly enabled and remain limited to the verified disposable test save. ")
                + (runWorldAction is null
                    ? "World interactions are disabled. "
                    : "World interactions were separately enabled for this exact owned disposable single-player review; they require fresh world and inventory revisions and are never retried. ")
                + (!containerTransferEnabled
                    ? "Container transfers are disabled. "
                    : "Container transfers were separately enabled for this exact owned unbound disposable single-player review; they require fresh container and item revisions and are never retried. ")
                + (cpRefreshPermission is null
                    ? "CP refresh is disabled; input and fixture permissions do not authorize it. "
                    : "CP refresh was separately enabled for the exact startup launch and root pack. Select existing patch JSON files and one Data observation explicitly; retain incomplete receipts and never retry blindly. ")
                + "Re-check errors by starting or repairing that review; never infer access to normal saves, Mods, OS-wide input, or arbitrary console commands.",
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
