using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal static class ProjectReviewMcpDiagnosticsTools
{
    internal const string ReviewToolName = "stardew_review_get";
    internal const string ModsToolName = "stardew_mods_list";

    private const int DefaultPageLimit = 50;
    private const int MaximumPageLimit = 100;
    private const int MaximumReviewArtifacts = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonElement EmptyInputSchema = ParseSchema(
        """{ "type": "object", "additionalProperties": false }""");

    private static readonly JsonElement ModsInputSchema = ParseSchema(
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

    private static readonly JsonElement ReviewOutputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "state", "launchId", "topology", "role", "process", "target", "testSave", "stagedArtifacts"],
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
            "state": { "type": "string", "const": "ready" },
            "launchId": { "type": "string", "pattern": "^[0-9a-f]{32}$" },
            "topology": { "type": "string", "enum": ["single", "network-2"] },
            "role": { "type": ["string", "null"], "enum": [null, "host", "farmhand"] },
            "process": {
              "type": "object",
              "additionalProperties": false,
              "required": ["state", "identityVerified", "statusFresh", "observedAtUtc"],
              "properties": {
                "state": { "type": "string", "const": "running" },
                "identityVerified": { "type": "boolean", "const": true },
                "statusFresh": { "type": "boolean", "const": true },
                "observedAtUtc": { "type": "string", "format": "date-time" }
              }
            },
            "target": {
              "type": "object",
              "additionalProperties": false,
              "required": ["uniqueId", "version", "kind", "buildIdentity", "loadStatus"],
              "properties": {
                "uniqueId": { "$ref": "#/$defs/uniqueId" },
                "version": { "$ref": "#/$defs/version" },
                "kind": { "$ref": "#/$defs/kind" },
                "buildIdentity": { "$ref": "#/$defs/buildIdentity" },
                "loadStatus": { "$ref": "#/$defs/loadStatus" }
              }
            },
            "testSave": {
              "type": ["object", "null"],
              "additionalProperties": false,
              "required": ["fixtureId", "saveId", "identityVerified"],
              "properties": {
                "fixtureId": { "type": "string", "pattern": "^[0-9a-f]{32}$" },
                "saveId": { "type": "string", "minLength": 1, "maxLength": 256 },
                "identityVerified": { "type": "boolean", "const": true }
              }
            },
            "stagedArtifacts": {
              "type": "array",
              "maxItems": 256,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["role", "kind", "uniqueId", "version", "contentPackFor", "buildIdentity"],
                "properties": {
                  "role": { "type": "string", "enum": ["target", "companion", "contentPack"] },
                  "kind": { "$ref": "#/$defs/kind" },
                  "uniqueId": { "$ref": "#/$defs/uniqueId" },
                  "version": { "$ref": "#/$defs/version" },
                  "contentPackFor": { "anyOf": [{ "$ref": "#/$defs/uniqueId" }, { "type": "null" }] },
                  "buildIdentity": { "$ref": "#/$defs/buildIdentity" }
                }
              }
            }
          },
          "$defs": {
            "uniqueId": { "type": "string", "minLength": 1, "maxLength": 256, "pattern": "^[A-Za-z0-9_.-]+$" },
            "version": { "type": "string", "minLength": 1, "maxLength": 128 },
            "kind": { "type": "string", "enum": ["smapiMod", "contentPack"] },
            "buildIdentity": { "type": "string", "pattern": "^sha256:[0-9a-f]{64}$" },
            "loadStatus": { "type": "string", "enum": ["loaded", "notLoaded", "versionMismatch", "kindMismatch"] }
          }
        }
        """);

    private static readonly JsonElement ModsOutputSchema = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "launchId", "topology", "role", "statusObservedAtUtc", "capturedAtUtc", "mods", "page"],
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
            "statusObservedAtUtc": { "type": "string", "format": "date-time" },
            "capturedAtUtc": { "type": "string", "format": "date-time" },
            "mods": {
              "type": "array",
              "maxItems": 100,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["uniqueId", "sourceCategory", "expectedKind", "loadedKind", "expectedVersion", "loadedVersion", "loadStatus", "warnings", "errors"],
                "properties": {
                  "uniqueId": { "$ref": "#/$defs/uniqueId" },
                  "sourceCategory": { "type": "string", "enum": ["target", "companion", "contentPack", "sdvkitSupport"] },
                  "expectedKind": { "anyOf": [{ "$ref": "#/$defs/kind" }, { "type": "null" }] },
                  "loadedKind": { "anyOf": [{ "$ref": "#/$defs/kind" }, { "type": "null" }] },
                  "expectedVersion": { "anyOf": [{ "$ref": "#/$defs/version" }, { "type": "null" }] },
                  "loadedVersion": { "anyOf": [{ "$ref": "#/$defs/version" }, { "type": "null" }] },
                  "loadStatus": { "$ref": "#/$defs/loadStatus" },
                  "warnings": { "$ref": "#/$defs/diagnostics" },
                  "errors": { "$ref": "#/$defs/diagnostics" }
                }
              }
            },
            "page": {
              "type": "object",
              "additionalProperties": false,
              "required": ["offset", "limit", "returned", "total", "nextOffset"],
              "properties": {
                "offset": { "type": "integer", "minimum": 0 },
                "limit": { "type": "integer", "minimum": 1, "maximum": 100 },
                "returned": { "type": "integer", "minimum": 0, "maximum": 100 },
                "total": { "type": "integer", "minimum": 0, "maximum": 257 },
                "nextOffset": { "type": ["integer", "null"], "minimum": 0 }
              }
            }
          },
          "$defs": {
            "uniqueId": { "type": "string", "minLength": 1, "maxLength": 256, "pattern": "^[A-Za-z0-9_.-]+$" },
            "version": { "type": "string", "minLength": 1, "maxLength": 128 },
            "kind": { "type": "string", "enum": ["smapiMod", "contentPack"] },
            "loadStatus": { "type": "string", "enum": ["loaded", "notLoaded", "versionMismatch", "kindMismatch"] },
            "diagnostics": {
              "type": "array",
              "maxItems": 4,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["code", "message"],
                "properties": {
                  "code": { "type": "string", "pattern": "^[A-Za-z0-9]+$", "maxLength": 64 },
                  "message": { "type": "string", "maxLength": 256 }
                }
              }
            }
          }
        }
        """);

    public static IReadOnlyList<McpServerTool> Create(
        ProjectReviewMcpRuntimeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return [new ReviewMcpTool(reader), new ModsMcpTool(reader)];
    }

    private sealed class ReviewMcpTool(ProjectReviewMcpRuntimeReader reader)
        : McpServerTool
    {
        public override Tool ProtocolTool { get; } = Tool(
            ReviewToolName,
            "Read the exact owned review identity, selected role, process freshness, target state, test-save identity, and staged artifacts.",
            EmptyInputSchema,
            ReviewOutputSchema);

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Params?.Arguments is { Count: > 0 })
            {
                return ValueTask.FromResult(Error(
                    "Invalid arguments: stardew_review_get accepts an empty object only."));
            }

            return ValueTask.FromResult(ReadReview(reader));
        }
    }

    private sealed class ModsMcpTool(ProjectReviewMcpRuntimeReader reader)
        : McpServerTool
    {
        public override Tool ProtocolTool { get; } = Tool(
            ModsToolName,
            "List one bounded page of the selected review's staged mods and their role-local SMAPI load state.",
            ModsInputSchema,
            ModsOutputSchema);

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryPage(request.Params?.Arguments, out int offset, out int limit))
            {
                return ValueTask.FromResult(Error(
                    "Invalid arguments for stardew_mods_list."));
            }

            return ValueTask.FromResult(ReadMods(reader, offset, limit));
        }
    }

    private static CallToolResult ReadReview(ProjectReviewMcpRuntimeReader reader)
    {
        if (!TryReadInventory(
                reader,
                out ProjectReviewMcpVerifiedContext? context,
                out IReadOnlyList<ProjectReviewMcpStagedArtifact>? stagedArtifacts,
                out IReadOnlyList<ProjectReviewMcpMod>? mods,
                out _,
                out CallToolResult? error))
        {
            return error!;
        }

        ProjectReviewMcpStagedArtifact targetArtifact = stagedArtifacts!.Single(
            artifact => string.Equals(
                artifact.Role,
                ProjectReviewArtifactRole.Target,
                StringComparison.Ordinal));
        ProjectReviewMcpMod targetMod = mods!.Single(mod => string.Equals(
            mod.UniqueId,
            targetArtifact.UniqueId,
            StringComparison.OrdinalIgnoreCase));
        ProjectReviewMcpTestSaveStatus? testSave = context!.TestSave is null
            ? null
            : new ProjectReviewMcpTestSaveStatus(
                context.TestSave.FixtureId,
                context.TestSave.SaveId,
                IdentityVerified: true);
        var snapshot = new ProjectReviewMcpReviewSnapshot(
            1,
            "ready",
            context.State.LaunchId,
            context.State.Topology,
            context.Role,
            new ProjectReviewMcpProcessStatus(
                "running",
                IdentityVerified: true,
                StatusFresh: true,
                context.AlwaysOn.ObservedAtUtc!.Value),
            new ProjectReviewMcpReviewTarget(
                targetArtifact.UniqueId,
                targetArtifact.Version,
                targetArtifact.Kind,
                targetArtifact.BuildIdentity,
                targetMod.LoadStatus),
            testSave,
            stagedArtifacts!);
        return Success(snapshot);
    }

    private static CallToolResult ReadMods(
        ProjectReviewMcpRuntimeReader reader,
        int offset,
        int limit)
    {
        if (!TryReadInventory(
                reader,
                out ProjectReviewMcpVerifiedContext? context,
                out _,
                out IReadOnlyList<ProjectReviewMcpMod>? mods,
                out DateTimeOffset capturedAtUtc,
                out CallToolResult? error))
        {
            return error!;
        }

        int total = mods!.Count;
        IReadOnlyList<ProjectReviewMcpMod> pageItems = offset >= total
            ? []
            : mods.Skip(offset).Take(limit).ToArray();
        long endOffset = (long)offset + pageItems.Count;
        int? nextOffset = endOffset < total ? (int)endOffset : null;
        var snapshot = new ProjectReviewMcpModsSnapshot(
            1,
            context!.State.LaunchId,
            context.State.Topology,
            context.Role,
            context.AlwaysOn.ObservedAtUtc!.Value,
            capturedAtUtc,
            pageItems,
            new ProjectReviewMcpPage(
                offset,
                limit,
                pageItems.Count,
                total,
                nextOffset));
        return Success(snapshot);
    }

    private static bool TryReadInventory(
        ProjectReviewMcpRuntimeReader reader,
        out ProjectReviewMcpVerifiedContext? context,
        out IReadOnlyList<ProjectReviewMcpStagedArtifact>? stagedArtifacts,
        out IReadOnlyList<ProjectReviewMcpMod>? mods,
        out DateTimeOffset capturedAtUtc,
        out CallToolResult? error)
    {
        context = null;
        stagedArtifacts = null;
        mods = null;
        capturedAtUtc = default;
        error = null;

        ProjectReviewMcpContextResult result = reader.ReadContext();
        if (!result.Succeeded)
        {
            error = Error(
                $"SDVKit review unavailable [{result.ErrorCode}]: {result.ErrorMessage}");
            return false;
        }

        context = result.Context;
        if (!ValidFixedIdentity(context!.State.LaunchId)
            || (context.TestSave is not null
                && (!ValidFixedIdentity(context.TestSave.FixtureId)
                    || context.TestSave.SaveId is not { Length: > 0 and <= 256 }
                    || context.TestSave.SaveId.Any(char.IsControl))))
        {
            error = Error(
                "SDVKit review diagnostics unavailable [reviewIdentityInvalid].");
            return false;
        }

        LoadedModsStatusReport? loaded = context!.AlwaysOn.LoadedMods;
        if (loaded is null
            || string.Equals(loaded.State, "pending", StringComparison.Ordinal))
        {
            error = Error(
                "SDVKit review diagnostics unavailable [reviewLoadedModsUnavailable].");
            return false;
        }

        if (!string.Equals(loaded.State, "ready", StringComparison.Ordinal)
            || loaded.SchemaVersion != LoadedModsContract.SchemaVersion
            || loaded.CapturedAtUtc is null
            || loaded.Mods is null
            || loaded.ProblemCode is not null)
        {
            error = Error(
                "SDVKit review diagnostics unavailable [reviewLoadedModsInvalid].");
            return false;
        }

        if (!TryCreateStagedArtifacts(context.Staging, out stagedArtifacts)
            || !TryReconcile(stagedArtifacts!, loaded.Mods, out mods))
        {
            error = Error(
                "SDVKit review diagnostics unavailable [reviewLoadedModsMismatch].");
            return false;
        }

        capturedAtUtc = loaded.CapturedAtUtc.Value;
        return true;
    }

    private static bool TryCreateStagedArtifacts(
        ProjectReviewStaging staging,
        out IReadOnlyList<ProjectReviewMcpStagedArtifact>? artifacts)
    {
        artifacts = null;
        if (staging.Artifacts.Count is < 1 or > MaximumReviewArtifacts)
        {
            return false;
        }

        var projected = new List<ProjectReviewMcpStagedArtifact>(staging.Artifacts.Count);
        try
        {
            foreach (ProjectReviewOwnedArtifact artifact in staging.Artifacts)
            {
                string version = ProjectModLaunchState.NormalizeVersion(
                    artifact.Manifest.Version);
                if (!ValidUniqueId(artifact.Manifest.UniqueId)
                    || !ValidVersion(version)
                    || !ValidRoleAndKind(
                        artifact.Role,
                        artifact.Manifest.Kind,
                        artifact.Manifest.ContentPackFor)
                    || (artifact.Manifest.ContentPackFor is not null
                        && !ValidUniqueId(artifact.Manifest.ContentPackFor))
                    || !ModBuildIdentity.IsValid(artifact.BuildIdentity))
                {
                    return false;
                }

                projected.Add(new ProjectReviewMcpStagedArtifact(
                    artifact.Role,
                    artifact.Manifest.Kind,
                    artifact.Manifest.UniqueId,
                    version,
                    artifact.Manifest.ContentPackFor,
                    artifact.BuildIdentity));
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (projected.Select(artifact => artifact.UniqueId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != projected.Count
            || projected.Count(artifact => string.Equals(
                artifact.Role,
                ProjectReviewArtifactRole.Target,
                StringComparison.Ordinal)) != 1)
        {
            return false;
        }

        artifacts = projected
            .OrderBy(artifact => RoleRank(artifact.Role))
            .ThenBy(artifact => artifact.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artifact => artifact.UniqueId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static bool TryReconcile(
        IReadOnlyList<ProjectReviewMcpStagedArtifact> stagedArtifacts,
        IReadOnlyList<LoadedModEntry> loadedEntries,
        out IReadOnlyList<ProjectReviewMcpMod>? mods)
    {
        mods = null;
        var expectedById = stagedArtifacts.ToDictionary(
            artifact => artifact.UniqueId,
            StringComparer.OrdinalIgnoreCase);
        var loadedById = loadedEntries.ToDictionary(
            entry => entry.UniqueId,
            StringComparer.OrdinalIgnoreCase);
        if (expectedById.ContainsKey(LoadedModsContract.AlwaysOnUniqueId)
            || !loadedById.TryGetValue(
                LoadedModsContract.AlwaysOnUniqueId,
                out LoadedModEntry? alwaysOn)
            || alwaysOn.IsContentPack
            || loadedEntries.Any(entry =>
                !expectedById.ContainsKey(entry.UniqueId)
                && !string.Equals(
                    entry.UniqueId,
                    LoadedModsContract.AlwaysOnUniqueId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var projected = new List<ProjectReviewMcpMod>(stagedArtifacts.Count + 1)
        {
            new(
                alwaysOn.UniqueId,
                "sdvkitSupport",
                ExpectedKind: null,
                LoadedKind: ProjectInspectionReport.SmapiMod,
                ExpectedVersion: null,
                alwaysOn.Version,
                "loaded",
                [],
                []),
        };
        foreach (ProjectReviewMcpStagedArtifact expected in stagedArtifacts)
        {
            loadedById.TryGetValue(expected.UniqueId, out LoadedModEntry? actual);
            string status;
            ProjectReviewMcpModDiagnostic[] errors;
            if (actual is null)
            {
                status = "notLoaded";
                errors =
                [
                    new(
                        "modNotLoaded",
                        "The selected mod was not reported as loaded by the role-local SMAPI registry."),
                ];
            }
            else if (actual.IsContentPack != string.Equals(
                         expected.Kind,
                         ProjectInspectionReport.ContentPack,
                         StringComparison.Ordinal))
            {
                status = "kindMismatch";
                errors =
                [
                    new(
                        "modKindMismatch",
                        "The role-local SMAPI registry reported a different mod kind than the selected staging metadata."),
                ];
            }
            else if (!string.Equals(
                         actual.Version,
                         expected.Version,
                         StringComparison.Ordinal))
            {
                status = "versionMismatch";
                errors =
                [
                    new(
                        "modVersionMismatch",
                        "The role-local SMAPI registry reported a different version than the selected staging metadata."),
                ];
            }
            else
            {
                status = "loaded";
                errors = [];
            }

            projected.Add(new ProjectReviewMcpMod(
                expected.UniqueId,
                expected.Role,
                ExpectedKind: expected.Kind,
                LoadedKind: actual is null
                    ? null
                    : actual.IsContentPack
                        ? ProjectInspectionReport.ContentPack
                        : ProjectInspectionReport.SmapiMod,
                expected.Version,
                actual?.Version,
                status,
                [],
                errors));
        }

        mods = projected
            .OrderBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.UniqueId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static bool TryPage(
        IDictionary<string, JsonElement>? arguments,
        out int offset,
        out int limit)
    {
        offset = 0;
        limit = DefaultPageLimit;
        return (arguments is null
                || arguments.Keys.All(key => key is "offset" or "limit"))
            && TryOptionalInt(arguments, "offset", 0, int.MaxValue, ref offset)
            && TryOptionalInt(arguments, "limit", 1, MaximumPageLimit, ref limit);
    }

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

    private static bool ValidRoleAndKind(
        string role,
        string kind,
        string? contentPackFor) =>
        role switch
        {
            ProjectReviewArtifactRole.Target =>
                string.Equals(kind, ProjectInspectionReport.SmapiMod, StringComparison.Ordinal)
                    ? contentPackFor is null
                    : string.Equals(kind, ProjectInspectionReport.ContentPack, StringComparison.Ordinal)
                        && contentPackFor is not null,
            ProjectReviewArtifactRole.Companion =>
                string.Equals(kind, ProjectInspectionReport.SmapiMod, StringComparison.Ordinal)
                    && contentPackFor is null,
            ProjectReviewArtifactRole.ContentPack =>
                string.Equals(kind, ProjectInspectionReport.ContentPack, StringComparison.Ordinal)
                    && contentPackFor is not null,
            _ => false,
        };

    private static bool ValidUniqueId(string? value) =>
        value is { Length: > 0 and <= LoadedModsContract.MaximumUniqueIdLength }
        && value.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '.'
            or '-');

    private static bool ValidVersion(string? value) =>
        value is { Length: > 0 and <= LoadedModsContract.MaximumVersionLength }
        && !value.Any(char.IsControl);

    private static bool ValidFixedIdentity(string? value) =>
        value is { Length: 32 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static int RoleRank(string role) => role switch
    {
        ProjectReviewArtifactRole.Target => 0,
        ProjectReviewArtifactRole.Companion => 1,
        ProjectReviewArtifactRole.ContentPack => 2,
        _ => int.MaxValue,
    };

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

    private static CallToolResult Success(object snapshot)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(snapshot, JsonOptions);
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
        };
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

internal sealed record ProjectReviewMcpProcessStatus(
    string State,
    bool IdentityVerified,
    bool StatusFresh,
    DateTimeOffset ObservedAtUtc);

internal sealed record ProjectReviewMcpReviewTarget(
    string UniqueId,
    string Version,
    string Kind,
    string BuildIdentity,
    string LoadStatus);

internal sealed record ProjectReviewMcpTestSaveStatus(
    string FixtureId,
    string SaveId,
    bool IdentityVerified);

internal sealed record ProjectReviewMcpStagedArtifact(
    string Role,
    string Kind,
    string UniqueId,
    string Version,
    string? ContentPackFor,
    string BuildIdentity);

internal sealed record ProjectReviewMcpReviewSnapshot(
    int SchemaVersion,
    string State,
    string LaunchId,
    string Topology,
    string? Role,
    ProjectReviewMcpProcessStatus Process,
    ProjectReviewMcpReviewTarget Target,
    ProjectReviewMcpTestSaveStatus? TestSave,
    IReadOnlyList<ProjectReviewMcpStagedArtifact> StagedArtifacts);

internal sealed record ProjectReviewMcpModDiagnostic(
    string Code,
    string Message);

internal sealed record ProjectReviewMcpMod(
    string UniqueId,
    string SourceCategory,
    string? ExpectedKind,
    string? LoadedKind,
    string? ExpectedVersion,
    string? LoadedVersion,
    string LoadStatus,
    IReadOnlyList<ProjectReviewMcpModDiagnostic> Warnings,
    IReadOnlyList<ProjectReviewMcpModDiagnostic> Errors);

internal sealed record ProjectReviewMcpPage(
    int Offset,
    int Limit,
    int Returned,
    int Total,
    int? NextOffset);

internal sealed record ProjectReviewMcpModsSnapshot(
    int SchemaVersion,
    string LaunchId,
    string Topology,
    string? Role,
    DateTimeOffset StatusObservedAtUtc,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ProjectReviewMcpMod> Mods,
    ProjectReviewMcpPage Page);
