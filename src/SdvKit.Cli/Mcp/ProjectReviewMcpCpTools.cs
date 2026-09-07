using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate CpDiagnosisResult ProjectReviewMcpCpDiagnosisRunner(string packId, string providerId, string? asset, string? parse);
internal delegate CpRefreshResult ProjectReviewMcpCpRefreshRunner(string packId, string providerId, IReadOnlyList<string> files,
    string asset, string key, ProjectReviewMcpVerifiedContext permission);

internal static class ProjectReviewMcpCpTools
{
    internal const string DiagnoseToolName = "stardew_cp_diagnose";
    internal const string RefreshToolName = "stardew_cp_refresh";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private const string Definitions = """
        {
          "hash": {"type":["string","null"],"pattern":"^sha256:[0-9a-f]{64}$"},
          "id": {"type":["string","null"],"pattern":"^[0-9a-f]{32}$"},
          "code": {"type":["string","null"],"maxLength":64,"pattern":"^[A-Za-z0-9]+$"},
          "response": {
            "type":["object","null"],"additionalProperties":false,
            "required":["state","errorCode","commandWritten","commandMayHaveBeenWritten","startedAtUtc","completedAtUtc","logTime","messages","withheldLines","truncated","patches"],
            "properties":{
              "state":{"enum":["ready","incomplete"]},"errorCode":{"$ref":"#/$defs/code"},
              "commandWritten":{"type":"boolean"},"commandMayHaveBeenWritten":{"type":"boolean"},
              "startedAtUtc":{"type":"string","format":"date-time"},"completedAtUtc":{"type":"string","format":"date-time"},
              "logTime":{"type":["string","null"],"maxLength":32},
              "messages":{"type":"array","maxItems":256,"items":{"type":"string","maxLength":1024}},
              "withheldLines":{"type":"integer","minimum":0},"truncated":{"type":"boolean"},
              "patches":{"type":"array","maxItems":256,"items":{
                "type":"object","additionalProperties":false,"required":["loadedAndEnabled","conditionsMatch","applied","details"],
                "properties":{"loadedAndEnabled":{"type":"boolean"},"conditionsMatch":{"type":"boolean"},"applied":{"type":"boolean"},"details":{"type":"string","maxLength":1024}}
              }}
            }
          },
          "diagnosis": {
            "type":["object","null"],"additionalProperties":false,
            "required":["state","errorCode","launchId","packId","providerId","providerVersion","packBuildIdentity","providerBuildIdentity","packLoaded","providerLoaded","summary","parse","assetObservation","reload"],
            "properties":{
              "state":{"enum":["ready","incomplete","unavailable","unsupported"]},"errorCode":{"$ref":"#/$defs/code"},
              "launchId":{"$ref":"#/$defs/id"},"packId":{"type":"string","maxLength":256},
              "providerId":{"type":"string","maxLength":256},"providerVersion":{"type":["string","null"],"maxLength":128},
              "packBuildIdentity":{"$ref":"#/$defs/hash"},"providerBuildIdentity":{"$ref":"#/$defs/hash"},
              "packLoaded":{"type":"boolean"},"providerLoaded":{"type":"boolean"},
              "summary":{"$ref":"#/$defs/response"},"parse":{"$ref":"#/$defs/response"},"reload":{"$ref":"#/$defs/response"},
              "assetObservation":{"type":"string","maxLength":256}
            }
          },
          "receipt": {
            "type":["object","null"],"additionalProperties":false,
            "required":["refreshId","launchId","previousBuildIdentity","stagedBuildIdentity","files","commandWritten","requiresRestart"],
            "properties":{
              "refreshId":{"$ref":"#/$defs/id"},"launchId":{"$ref":"#/$defs/id"},
              "previousBuildIdentity":{"$ref":"#/$defs/hash"},"stagedBuildIdentity":{"$ref":"#/$defs/hash"},
              "files":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"string","maxLength":240}},
              "commandWritten":{"type":["boolean","null"]},"requiresRestart":{"type":"boolean"}
            }
          },
          "observation": {
            "type":["object","null"],"additionalProperties":false,
            "required":["schemaVersion","gameVersion","gameFileVersion","assetName","dataType","shape","keyKind","key","record"],
            "properties":{
              "schemaVersion":{"const":1},"gameVersion":{"type":"string"},"gameFileVersion":{"type":"string"},
              "assetName":{"type":"string","maxLength":256},"dataType":{"type":"string"},
              "shape":{"enum":["dictionary","list","singleton"]},"keyKind":{"enum":["string","integer","index","singleton"]},
              "key":{"type":"string","maxLength":2048},"record":{}
            }
          }
        }
        """;
    private static readonly JsonElement DiagnoseInput = Schema("""
        {"type":"object","additionalProperties":false,"required":["packId","providerId"],"properties":{
          "packId":{"type":"string","minLength":1,"maxLength":256},
          "providerId":{"type":"string","minLength":1,"maxLength":256},
          "asset":{"type":"string","minLength":1,"maxLength":256},
          "parse":{"type":"string","minLength":1,"maxLength":512}
        }}
        """);
    private static readonly JsonElement RefreshInput = Schema("""
        {"type":"object","additionalProperties":false,"required":["packId","providerId","files","asset","key"],"properties":{
          "packId":{"type":"string","minLength":1,"maxLength":256},
          "providerId":{"type":"string","minLength":1,"maxLength":256},
          "files":{"type":"array","minItems":1,"maxItems":16,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":240}},
          "asset":{"type":"string","minLength":1,"maxLength":256},
          "key":{"type":"string","minLength":1,"maxLength":2048}
        }}
        """);
    private static readonly JsonElement DiagnoseOutput = OutputSchema(diagnosis: true);
    private static readonly JsonElement RefreshOutput = OutputSchema(diagnosis: false);

    internal static string? RefreshPermissionError(ProjectReviewMcpVerifiedContext context)
    {
        if (context.Staging.Topology != LiveLabState.SingleTopology || context.Role is not null)
            return "cpRefreshSingleRequired";
        if (!string.Equals(context.Staging.Target.Manifest.ContentPackFor, ProjectReviewCpDiagnosis.ProviderId, StringComparison.OrdinalIgnoreCase))
            return "cpRefreshRootPackRequired";
        var provider = context.Staging.Artifacts.SingleOrDefault(a => a.Manifest.UniqueId.Equals(ProjectReviewCpDiagnosis.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider?.Manifest.Version != "2.9.1") return "cpVersionUnsupported";
        if (!context.AllTargetsReady) return "cpSelectedModsNotLoaded";
        return context.Staging.Target.CpRefresh?.RequiresRestart == true ? "cpRefreshRestartRequired" : null;
    }

    internal static IReadOnlyList<McpServerTool> Create(ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpCpDiagnosisRunner runDiagnosis, ProjectReviewMcpCpRefreshRunner? runRefresh = null,
        ProjectReviewMcpVerifiedContext? permission = null)
    {
        if (reader.Topology != LiveLabState.SingleTopology || reader.Role is not null) return [];
        var tools = new List<McpServerTool> { new DiagnoseTool(reader, runDiagnosis) };
        if (runRefresh is not null)
        {
            if (permission is null || RefreshPermissionError(permission) is not null)
                throw new ArgumentException("CP refresh requires a verified explicit startup permission.");
            tools.Add(new RefreshTool(reader, runRefresh, permission));
        }
        return tools;
    }

    private sealed class DiagnoseTool(ProjectReviewMcpRuntimeReader reader, ProjectReviewMcpCpDiagnosisRunner run) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = Tool(DiagnoseToolName,
            "Diagnose one explicitly selected staged CP 2.9.1 pack through bounded summary and optional parse replies. Read-only; does not reload or inspect an asset. Ready means correlated diagnosis, not patch success.",
            DiagnoseInput, DiagnoseOutput, readOnly: true);
        public override IReadOnlyList<object> Metadata => [];
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = request.Params?.Arguments;
            if (!Only(args, ["packId", "providerId", "asset", "parse"])
                || !String(args, "packId", out var pack) || !String(args, "providerId", out var provider)
                || !OptionalString(args, "asset", out var asset) || !OptionalString(args, "parse", out var parse)
                || !ProjectReviewCpDiagnosis.ValidArguments(pack!, provider!, asset, parse))
                return Done(Error("cpArgumentsInvalid"));
            var before = reader.ReadContext();
            if (!before.Succeeded) return Done(Error(before.ErrorCode!));
            var result = run(pack!, provider!, asset, parse);
            var after = reader.ReadContext();
            if (!after.Succeeded || before.Context!.State != after.Context!.State
                || !OwnedReviewLogReader.SameStagedContent(before.Context.Staging, after.Context.Staging))
                return Done(Error("cpDiagnosisBindingChanged"));
            if (!ValidDiagnosis(result, before.Context!, pack!, provider!, parse, reload: false))
                return Done(Error("cpDiagnosisResponseInvalid"));
            return Done(Json(result, result.State != "ready"));
        }
    }

    private sealed class RefreshTool(ProjectReviewMcpRuntimeReader reader, ProjectReviewMcpCpRefreshRunner run,
        ProjectReviewMcpVerifiedContext permission) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = Tool(RefreshToolName,
            "Refresh only explicitly selected existing patch JSON in the startup-bound root CP pack. Uses the separately granted CP refresh permission, replaces owned staged files, reloads once, diagnoses and observes one Data record. Incomplete delivery may have run: retain receipt and recovery, never retry blindly.",
            RefreshInput, RefreshOutput, readOnly: false);
        public override IReadOnlyList<object> Metadata => [];
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = request.Params?.Arguments;
            if (!Only(args, ["packId", "providerId", "files", "asset", "key"])
                || !String(args, "packId", out var pack) || !String(args, "providerId", out var provider)
                || !String(args, "asset", out var asset) || !String(args, "key", out var key)
                || !Files(args, out var files) || !ProjectReviewCpDiagnosis.ValidArguments(pack!, provider!, asset, null)
                || !asset!.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)
                || !ReviewTransportText.IsWellFormedUtf16(key!) || string.IsNullOrWhiteSpace(key)
                || key.Length > ReviewDataContract.MaximumKeyLength || key.Any(char.IsControl))
                return Done(Error("cpRefreshArgumentsInvalid"));
            if (!permission.Staging.Target.Manifest.UniqueId.Equals(pack, StringComparison.OrdinalIgnoreCase))
                return Done(Error("cpRefreshSelectionMismatch"));
            var before = reader.ReadContext();
            if (!before.Succeeded) return Done(Error(before.ErrorCode!));
            if (!ProjectReviewCpRefresh.SamePermissionBinding(permission, before.Context!))
                return Done(Error("cpRefreshBindingChanged"));
            // Cancellation after dispatch must not erase a possibly executed operation's receipt.
            // The existing service holds both locks and completes or retains restart recovery; no retry.
            var result = run(pack!, provider!, files!, asset!, key!, permission);
            var after = reader.ReadContext();
            bool bindingMatches = after.Succeeded && ProjectReviewCpRefresh.SamePermissionBinding(permission, after.Context!);
            bool valid = ValidRefresh(result, before.Context!, pack!, provider!, files!, asset!, key!, out var observation);
            bool success = valid && bindingMatches && result.State == "observed"
                && after.Context!.Staging.Target.CpRefresh?.RefreshId == result.Refresh!.RefreshId
                && after.Context.Staging.Target.StagedBuildIdentity == result.Refresh.StagedBuildIdentity;
            if (!valid) return Done(Error("cpRefreshResponseInvalid"));
            // A lost final binding is an error, but the valid receipt remains useful recovery evidence.
            var snapshot = new
            {
                state = success ? "observed" : result.State == "observed" ? "incomplete" : result.State,
                errorCode = result.State == "observed" && !success ? "cpRefreshBindingChanged" : Code(result.ErrorCode),
                recovery = success ? "none" : result.Recovery == "none" && result.Refresh is not null
                    ? "Inspect project review status, then stop, reset and start the exact selection. Do not retry this refresh." : result.Recovery,
                result.LaunchId,
                process = result.Process is null ? null : new { result.Process.ProcessId, result.Process.StartTimeUtc },
                result.LaunchBuildIdentity,
                result.Refresh,
                result.FilesReplaced,
                result.StagingRestored,
                result.Diagnosis,
                observation,
                result.ElapsedSeconds,
            };
            return Done(Json(snapshot, !success));
        }
    }

    private static bool ValidDiagnosis(CpDiagnosisResult result, ProjectReviewMcpVerifiedContext context,
        string pack, string provider, string? parse, bool reload)
    {
        var selected = context.Staging.Artifacts.SingleOrDefault(a => a.Manifest.UniqueId.Equals(pack, StringComparison.OrdinalIgnoreCase));
        var selectedProvider = context.Staging.Artifacts.SingleOrDefault(a => a.Manifest.UniqueId.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (result is null || result.State is not ("ready" or "incomplete" or "unavailable" or "unsupported")
            || !string.Equals(result.PackId, pack, StringComparison.OrdinalIgnoreCase) || !string.Equals(result.ProviderId, provider, StringComparison.OrdinalIgnoreCase)
            || result.LaunchId is not null && result.LaunchId != context.State.LaunchId
            || result.PackBuildIdentity is not null && result.PackBuildIdentity != selected?.StagedBuildIdentity
            || result.ProviderBuildIdentity is not null && result.ProviderBuildIdentity != selectedProvider?.StagedBuildIdentity
            || result.ProviderVersion is not null && result.ProviderVersion != selectedProvider?.Manifest.Version
            || !ValidResponse(result.Summary) || !ValidResponse(result.Parse) || !ValidResponse(result.Reload)
            || !reload && result.Reload is not null || parse is null && result.Parse is not null
            || result.AssetObservation is not { Length: <= 256 } || result.ErrorCode != Code(result.ErrorCode)) return false;
        return result.State != "ready" || result.ErrorCode is null && result.LaunchId == context.State.LaunchId
            && result.PackLoaded && result.ProviderLoaded && result.ProviderVersion == "2.9.1"
            && result.PackBuildIdentity is not null && result.ProviderBuildIdentity is not null
            && result.Summary?.State == "ready" && (parse is null || result.Parse?.State == "ready")
            && (!reload || result.Reload?.State == "ready");
    }

    private static bool ValidResponse(CpResponse? response) => response is null
        || response.State is "ready" or "incomplete" && response.CompletedAtUtc >= response.StartedAtUtc
        && response.ErrorCode == Code(response.ErrorCode) && response.LogTime is not { Length: > 32 }
        && response.WithheldLines >= 0 && response.Messages is { Count: <= 256 }
        && response.Messages.All(m => m is { Length: <= 1024 }) && response.Patches is { Count: <= 256 }
        && response.Patches.All(p => p is not null && p.Details is { Length: <= 1024 })
        && (!response.CommandWritten || response.CommandMayHaveBeenWritten)
        && (response.State != "ready" || response.ErrorCode is null && response.CommandWritten && response.CommandMayHaveBeenWritten);

    private static bool ValidRefresh(CpRefreshResult result, ProjectReviewMcpVerifiedContext before, string pack,
        string provider, IReadOnlyList<string> files, string asset, string key, out object? observation)
    {
        observation = null;
        if (result is null || result.State is not ("observed" or "incomplete" or "rejected")
            || result.LaunchId is not null && result.LaunchId != before.State.LaunchId
            || result.Process is not null && result.Process != before.State.OwnedProcessIdentity
            || result.LaunchBuildIdentity is not null && result.LaunchBuildIdentity != before.Staging.Target.BuildIdentity
            || result.FilesReplaced < 0 || result.FilesReplaced > files.Count
            || !double.IsFinite(result.ElapsedSeconds) || result.ElapsedSeconds < 0 || result.Recovery is not { Length: <= 1024 }) return false;
        var receipt = result.Refresh;
        if (receipt is not null && (!ReviewTransportToken.IsRequestId(receipt.RefreshId) || receipt.LaunchId != before.State.LaunchId
            || !ModBuildIdentity.IsValid(receipt.PreviousBuildIdentity) || !ModBuildIdentity.IsValid(receipt.StagedBuildIdentity)
            || !ProjectReviewCpRefresh.ValidFiles(receipt.Files))) return false;
        // A pre-existing recovery receipt describes the previous interrupted selection.
        bool pendingRecovery = result.State == "rejected" && result.ErrorCode == "cpRefreshRestartRequired";
        if (receipt is not null && !pendingRecovery && (!receipt.Files.SequenceEqual(files)
            || receipt.PreviousBuildIdentity != before.Staging.Target.StagedBuildIdentity)) return false;
        if (result.Diagnosis is not null)
        {
            var diagnosticContext = before with
            {
                Staging = before.Staging with
                {
                    Artifacts = before.Staging.Artifacts.Select(a => a == before.Staging.Target ? a with { CpRefresh = receipt } : a).ToArray(),
                }
            };
            if (!ValidDiagnosis(result.Diagnosis, diagnosticContext, pack, provider, null, reload: true)) return false;
        }
        if (result.Observation is ReviewDataReport data && ProjectReviewCpRefresh.MatchesObservation(data, asset, key))
            observation = new ProjectReviewMcpDataRecordSnapshot(data.SchemaVersion, data.GameVersion!, data.GameFileVersion!,
                data.AssetName!, data.DataType!, data.Shape!, data.KeyKind!, data.Key!, data.Record!.Value);
        bool exactExecutionBinding = result.LaunchId == before.State.LaunchId
            && result.Process == before.State.OwnedProcessIdentity
            && result.LaunchBuildIdentity == before.Staging.Target.BuildIdentity;
        bool hasError = !string.IsNullOrWhiteSpace(result.ErrorCode);
        if (result.State == "rejected")
        {
            if (!hasError || result.FilesReplaced != 0 || result.StagingRestored
                || result.Diagnosis is not null || result.Observation is not null)
                return false;
            return pendingRecovery
                ? exactExecutionBinding && receipt is { RequiresRestart: true }
                    && SameReceipt(receipt, before.Staging.Target.CpRefresh)
                    && result.Recovery != "none"
                : receipt is null && result.Recovery == "none";
        }
        if (result.State == "incomplete")
        {
            return hasError && exactExecutionBinding
                && receipt is { RequiresRestart: true }
                && result.Recovery != "none"
                && (!result.StagingRestored
                    || receipt.CommandWritten == false
                    && receipt.StagedBuildIdentity == receipt.PreviousBuildIdentity);
        }
        return result.ErrorCode is null && result.Recovery == "none"
            && receipt is { RequiresRestart: false, CommandWritten: true }
            && result.Diagnosis?.State == "ready" && exactExecutionBinding
            && result.FilesReplaced == files.Count && observation is not null;
    }

    private static bool SameReceipt(CpRefreshReceipt? left, CpRefreshReceipt? right) =>
        left is not null && right is not null
        && left.RefreshId == right.RefreshId && left.LaunchId == right.LaunchId
        && left.PreviousBuildIdentity == right.PreviousBuildIdentity
        && left.StagedBuildIdentity == right.StagedBuildIdentity
        && left.Files.SequenceEqual(right.Files)
        && left.CommandWritten == right.CommandWritten
        && left.RequiresRestart == right.RequiresRestart;

    private static bool Only(IDictionary<string, JsonElement>? args, string[] names) => args is not null && args.Keys.All(names.Contains);
    private static bool String(IDictionary<string, JsonElement>? args, string name, out string? value)
    {
        value = null;
        if (args is null || !args.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value is not null && ReviewTransportText.IsWellFormedUtf16(value);
    }
    private static bool OptionalString(IDictionary<string, JsonElement>? args, string name, out string? value)
    {
        value = null;
        return args is not null && (!args.ContainsKey(name) || String(args, name, out value));
    }
    private static bool Files(IDictionary<string, JsonElement>? args, out string[]? files)
    {
        files = null;
        if (args is null || !args.TryGetValue("files", out var element) || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() is < 1 or > ProjectReviewCpRefresh.MaximumFiles
            || element.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) return false;
        files = element.EnumerateArray().Select(v => v.GetString()!).ToArray();
        return ProjectReviewCpRefresh.ValidFiles(files);
    }
    private static string? Code(string? value) => value is null ? null
        : value.Split(':')[0] is { Length: > 0 and <= 64 } code && code.All(char.IsAsciiLetterOrDigit) ? code : "cpOperationFailed";
    private static Tool Tool(string name, string description, JsonElement input, JsonElement output, bool readOnly) => new()
    {
        Name = name,
        Description = description,
        InputSchema = input,
        OutputSchema = output,
        Annotations = new ToolAnnotations { ReadOnlyHint = readOnly, DestructiveHint = !readOnly, IdempotentHint = readOnly, OpenWorldHint = false },
    };
    private static ValueTask<CallToolResult> Done(CallToolResult result) => ValueTask.FromResult(result);
    private static CallToolResult Error(string code) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"SDVKit CP unavailable [{Code(code)}]. No automatic retry." }],
    };
    private static CallToolResult Json(object value, bool error)
    {
        var json = JsonSerializer.SerializeToElement(value, JsonOptions);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
    private static JsonElement Schema(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static JsonElement OutputSchema(bool diagnosis)
    {
        var definitions = JsonNode.Parse(Definitions)!.AsObject();
        JsonObject schema = diagnosis ? definitions["diagnosis"]!.DeepClone().AsObject() : JsonNode.Parse("""
            {"type":"object","additionalProperties":false,
             "required":["state","errorCode","recovery","launchId","process","launchBuildIdentity","refresh","filesReplaced","stagingRestored","diagnosis","observation","elapsedSeconds"],
             "properties":{
               "state":{"enum":["observed","incomplete","rejected"]},"errorCode":{"$ref":"#/$defs/code"},
               "recovery":{"type":"string","maxLength":1024},"launchId":{"$ref":"#/$defs/id"},
               "process":{"type":["object","null"],"additionalProperties":false,"required":["processId","startTimeUtc"],
                 "properties":{"processId":{"type":"integer","minimum":1},"startTimeUtc":{"type":"string","format":"date-time"}}},
               "launchBuildIdentity":{"$ref":"#/$defs/hash"},"refresh":{"$ref":"#/$defs/receipt"},
               "filesReplaced":{"type":"integer","minimum":0,"maximum":16},"stagingRestored":{"type":"boolean"},
               "diagnosis":{"$ref":"#/$defs/diagnosis"},"observation":{"$ref":"#/$defs/observation"},
               "elapsedSeconds":{"type":"number","minimum":0}
             }}
            """)!.AsObject();
        schema["type"] = "object";
        schema["$defs"] = definitions;
        return JsonSerializer.SerializeToElement(schema, JsonOptions);
    }
}
