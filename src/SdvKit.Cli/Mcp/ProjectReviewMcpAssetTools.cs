using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli.Mcp;

internal delegate LiveLabCommandResult ProjectReviewMcpAudioQueryRunner(
    ReviewAudioQuery query);

internal delegate LiveLabCommandResult ProjectReviewMcpModAssetQueryRunner(
    ReviewModAssetQuery query);

internal static class ProjectReviewMcpAssetTools
{
    internal const string AudioCuesToolName = "stardew_audio_cues_list";
    internal const string AudioCueToolName = "stardew_audio_cue_get";
    internal const string ModAssetsToolName = "stardew_mod_assets_list";
    internal const string ModAssetKeysToolName = "stardew_mod_asset_keys_list";
    internal const string ModAssetRecordToolName = "stardew_mod_asset_record_get";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonElement PageInputSchema = Schema(
        """
        {"type":"object","additionalProperties":false,"properties":{"offset":{"type":"integer","minimum":0,"maximum":2147483647},"limit":{"type":"integer","minimum":1,"maximum":100}}}
        """);
    private static readonly JsonElement CueInputSchema = Schema(
        """
        {"type":"object","additionalProperties":false,"required":["cueId"],"properties":{"cueId":{"type":"string","minLength":1,"maxLength":256}}}
        """);
    private static readonly JsonElement ModAssetKeysInputSchema = Schema(
        """
        {"type":"object","additionalProperties":false,"required":["asset"],"properties":{"asset":{"type":"string","minLength":1,"maxLength":512,"pattern":"^Mods/[^/]+/.+"},"offset":{"type":"integer","minimum":0,"maximum":2147483647},"limit":{"type":"integer","minimum":1,"maximum":100}}}
        """);
    private static readonly JsonElement ModAssetRecordInputSchema = Schema(
        """
        {"type":"object","additionalProperties":false,"required":["asset","key"],"properties":{"asset":{"type":"string","minLength":1,"maxLength":512,"pattern":"^Mods/[^/]+/.+"},"key":{"type":"string","minLength":1,"maxLength":480}}}
        """);
    private static readonly JsonElement AudioOutputSchema = Schema(
        """
        {
          "type":"object","additionalProperties":false,
          "required":["schemaVersion","gameVersion","gameFileVersion","cueId","cues","page","coverage"],
          "properties":{
            "schemaVersion":{"const":1},"gameVersion":{"type":"string","maxLength":128},"gameFileVersion":{"type":"string","maxLength":128},
            "cueId":{"type":["string","null"],"maxLength":256},
            "cues":{"type":"array","maxItems":100,"items":{"$ref":"#/$defs/cue"}},
            "page":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/page"}]},"coverage":{"$ref":"#/$defs/coverage"}
          },
          "$defs":{
            "cue":{"type":"object","additionalProperties":false,"required":["cueId","sources","dataDefined","sessionResident","definitionAvailable","definitionVariantCount","dataVariantCount","category","streamedVorbis","looped","useReverb","jukeboxReferences"],"properties":{"cueId":{"type":"string","maxLength":256},"sources":{"type":"array","maxItems":3,"items":{"enum":["audioChanges","jukeboxTrack","jukeboxAlternativeUnlock"]}},"dataDefined":{"type":"boolean"},"sessionResident":{"type":"boolean"},"definitionAvailable":{"type":"boolean"},"definitionVariantCount":{"type":["integer","null"],"minimum":0,"maximum":4096},"dataVariantCount":{"type":["integer","null"],"minimum":0,"maximum":4096},"category":{"type":["string","null"],"maxLength":128},"streamedVorbis":{"type":["boolean","null"]},"looped":{"type":["boolean","null"]},"useReverb":{"type":["boolean","null"]},"jukeboxReferences":{"type":"array","maxItems":2,"items":{"type":"object","additionalProperties":false,"required":["trackCueId","relation"],"properties":{"trackCueId":{"type":"string","maxLength":256},"relation":{"enum":["trackCue","alternativeUnlock"]}}}}}},
            "page":{"type":"object","additionalProperties":false,"required":["offset","limit","returned","total","nextOffset"],"properties":{"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":100},"returned":{"type":"integer","minimum":0,"maximum":100},"total":{"type":"integer","minimum":0},"nextOffset":{"type":["integer","null"],"minimum":0}}},
            "coverage":{"type":"object","additionalProperties":false,"required":["audioChangeEntries","jukeboxTrackEntries","jukeboxAlternativeReferences","discoverableCueIds","probedCueIds","sessionResidentCueIds","unavailableCueIds","identityCollisionGroups","dataDrivenPopulationComplete","builtInCueCount","builtInCueInventoryStatus"],"properties":{"audioChangeEntries":{"type":"integer","minimum":0,"maximum":4096},"jukeboxTrackEntries":{"type":"integer","minimum":0,"maximum":4096},"jukeboxAlternativeReferences":{"type":"integer","minimum":0,"maximum":16384},"discoverableCueIds":{"type":"integer","minimum":0,"maximum":8192},"probedCueIds":{"type":"integer","minimum":0,"maximum":100},"sessionResidentCueIds":{"type":"integer","minimum":0,"maximum":100},"unavailableCueIds":{"type":"integer","minimum":0,"maximum":100},"identityCollisionGroups":{"type":"integer","minimum":0},"dataDrivenPopulationComplete":{"type":"boolean"},"builtInCueCount":{"type":"null"},"builtInCueInventoryStatus":{"const":"unavailableByPublicApi"}}}
          }
        }
        """);
    private static readonly JsonElement ModAssetsOutputSchema = Schema(
        """
        {"type":"object","additionalProperties":false,"required":["schemaVersion","gameVersion","gameFileVersion","coverageScope","assets","page","coverage"],"properties":{"schemaVersion":{"const":1},"gameVersion":{"type":"string","maxLength":128},"gameFileVersion":{"type":"string","maxLength":128},"coverageScope":{"const":"observedRequestsSinceAlwaysOnSubscribed"},"assets":{"type":"array","maxItems":100,"items":{"$ref":"#/$defs/asset"}},"page":{"$ref":"#/$defs/page"},"coverage":{"$ref":"#/$defs/coverage"}},"$defs":{"asset":{"type":"object","additionalProperties":false,"required":["assetName","namespaceOwnerId","namespaceOwnerStatus","providerModId","providerStatus","dataType","shape","lifecycle","generation","requestCount","readyCount","available","adapterSupported","nameCollision","typeCollision","problemCode"],"properties":{"assetName":{"type":"string","maxLength":512,"pattern":"^Mods/"},"namespaceOwnerId":{"type":["string","null"]},"namespaceOwnerStatus":{"type":"string","maxLength":64},"providerModId":{"type":["string","null"]},"providerStatus":{"type":"string","maxLength":64},"dataType":{"type":"string","maxLength":512},"shape":{"type":["string","null"],"maxLength":64},"lifecycle":{"type":"string","maxLength":32},"generation":{"type":"integer","minimum":0},"requestCount":{"type":"integer","minimum":0},"readyCount":{"type":"integer","minimum":0},"available":{"type":"boolean"},"adapterSupported":{"type":"boolean"},"nameCollision":{"type":"boolean"},"typeCollision":{"type":"boolean"},"problemCode":{"type":["string","null"],"maxLength":64}}},"page":{"type":"object","additionalProperties":false,"required":["offset","limit","returned","total","nextOffset"],"properties":{"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":100},"returned":{"type":"integer","minimum":0,"maximum":100},"total":{"type":"integer","minimum":0},"nextOffset":{"type":["integer","null"],"minimum":0}}},"coverage":{"type":"object","additionalProperties":false,"required":["scope","observationStartedAtUtc","observed","catalogued","adapterSupported","adapterUnavailable","ready","invalidated","unavailable","nameCollisions","typeCollisions","dropped","complete"],"properties":{"scope":{"const":"observedRequestsSinceAlwaysOnSubscribed"},"observationStartedAtUtc":{"type":"string","format":"date-time"},"observed":{"type":"integer","minimum":0,"maximum":2048},"catalogued":{"type":"integer","minimum":0,"maximum":2048},"adapterSupported":{"type":"integer","minimum":0,"maximum":2048},"adapterUnavailable":{"type":"integer","minimum":0,"maximum":2048},"ready":{"type":"integer","minimum":0,"maximum":2048},"invalidated":{"type":"integer","minimum":0,"maximum":2048},"unavailable":{"type":"integer","minimum":0,"maximum":2048},"nameCollisions":{"type":"integer","minimum":0,"maximum":2048},"typeCollisions":{"type":"integer","minimum":0,"maximum":2048},"dropped":{"type":"integer","minimum":0},"complete":{"const":true}}}}}
        """);
    private static readonly JsonElement ModAssetKeysOutputSchema = Schema(
        """
        {"type":"object","additionalProperties":false,"required":["schemaVersion","gameVersion","gameFileVersion","coverageScope","asset","keys","page"],"properties":{"schemaVersion":{"const":1},"gameVersion":{"type":"string","maxLength":128},"gameFileVersion":{"type":"string","maxLength":128},"coverageScope":{"const":"observedRequestsSinceAlwaysOnSubscribed"},"asset":{"$ref":"#/$defs/asset"},"keys":{"type":"array","maxItems":100,"items":{"type":"string","maxLength":480}},"page":{"$ref":"#/$defs/page"}},"$defs":{"asset":{"type":"object","additionalProperties":false,"required":["assetName","namespaceOwnerId","namespaceOwnerStatus","providerModId","providerStatus","dataType","shape","lifecycle","generation","requestCount","readyCount","available","adapterSupported","nameCollision","typeCollision","problemCode"],"properties":{"assetName":{"type":"string","maxLength":512,"pattern":"^Mods/"},"namespaceOwnerId":{"type":["string","null"]},"namespaceOwnerStatus":{"type":"string","maxLength":64},"providerModId":{"type":["string","null"]},"providerStatus":{"type":"string","maxLength":64},"dataType":{"type":"string","maxLength":512},"shape":{"type":"string","enum":["stringDictionary","integerDictionary","integerKeyStringDictionary","integerKeyIntegerDictionary","stringList","stringSingleton"]},"lifecycle":{"const":"ready"},"generation":{"type":"integer","minimum":0},"requestCount":{"type":"integer","minimum":1},"readyCount":{"type":"integer","minimum":0},"available":{"const":true},"adapterSupported":{"const":true},"nameCollision":{"const":false},"typeCollision":{"const":false},"problemCode":{"type":"null"}}},"page":{"type":"object","additionalProperties":false,"required":["offset","limit","returned","total","nextOffset"],"properties":{"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":100},"returned":{"type":"integer","minimum":0,"maximum":100},"total":{"type":"integer","minimum":0,"maximum":10000},"nextOffset":{"type":["integer","null"],"minimum":0}}}}}
        """);
    private static readonly JsonElement ModAssetRecordOutputSchema = Schema(
        """
        {"type":"object","additionalProperties":false,"required":["schemaVersion","gameVersion","gameFileVersion","coverageScope","asset","key","record"],"properties":{"schemaVersion":{"const":1},"gameVersion":{"type":"string","maxLength":128},"gameFileVersion":{"type":"string","maxLength":128},"coverageScope":{"const":"observedRequestsSinceAlwaysOnSubscribed"},"asset":{"$ref":"#/$defs/asset"},"key":{"type":"string","maxLength":480},"record":{"oneOf":[{"type":"string","maxLength":65536,"pattern":"^[^\\u0000-\\u001F\\u007F]*$"},{"type":"integer","minimum":-2147483648,"maximum":2147483647}]}},"$defs":{"asset":{"type":"object","additionalProperties":false,"required":["assetName","namespaceOwnerId","namespaceOwnerStatus","providerModId","providerStatus","dataType","shape","lifecycle","generation","requestCount","readyCount","available","adapterSupported","nameCollision","typeCollision","problemCode"],"properties":{"assetName":{"type":"string","maxLength":512,"pattern":"^Mods/"},"namespaceOwnerId":{"type":["string","null"]},"namespaceOwnerStatus":{"type":"string","maxLength":64},"providerModId":{"type":["string","null"]},"providerStatus":{"type":"string","maxLength":64},"dataType":{"type":"string","maxLength":512},"shape":{"type":"string","enum":["stringDictionary","integerDictionary","integerKeyStringDictionary","integerKeyIntegerDictionary","stringList","stringSingleton"]},"lifecycle":{"const":"ready"},"generation":{"type":"integer","minimum":0},"requestCount":{"type":"integer","minimum":1},"readyCount":{"type":"integer","minimum":0},"available":{"const":true},"adapterSupported":{"const":true},"nameCollision":{"const":false},"typeCollision":{"const":false},"problemCode":{"type":"null"}}}}}
        """);

    public static IReadOnlyList<McpServerTool> Create(
        ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpAudioQueryRunner runAudio,
        ProjectReviewMcpModAssetQueryRunner runModAsset)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(runAudio);
        ArgumentNullException.ThrowIfNull(runModAsset);
        return
        [
            new AudioTool(reader, runAudio, inventory: true),
            new AudioTool(reader, runAudio, inventory: false),
            new ModAssetTool(reader, runModAsset, ReviewModAssetContract.AssetsOperation),
            new ModAssetTool(reader, runModAsset, ReviewModAssetContract.KeysOperation),
            new ModAssetTool(reader, runModAsset, ReviewModAssetContract.GetOperation),
        ];
    }

    private sealed class AudioTool(
        ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpAudioQueryRunner run,
        bool inventory) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = Tool(
            inventory ? AudioCuesToolName : AudioCueToolName,
            inventory
                ? "List one bounded page of data-defined, jukebox, and exact alternative audio cue identities without playback."
                : "Read bounded metadata for one exact active audio cue without playback or file access.",
            inventory ? PageInputSchema : CueInputSchema,
            AudioOutputSchema);

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryAudioQuery(request.Params?.Arguments, inventory, out ReviewAudioQuery? query))
            {
                return ValueTask.FromResult(Error($"Invalid arguments for {ProtocolTool.Name}."));
            }

            if (!TryPreflight(reader, out ProjectReviewMcpContextResult? before, out CallToolResult? error))
            {
                return ValueTask.FromResult(error!);
            }

            LiveLabCommandResult result = run(query!);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0
                || result.Report is not ReviewAudioReport report
                || !ValidAudioReport(query!, report))
            {
                return ValueTask.FromResult(QueryError("audio", AudioProblem(result.Report as ReviewAudioReport)));
            }
            if (!PostflightMatches(reader, before!))
            {
                return ValueTask.FromResult(QueryError("audio", "reviewBindingChanged"));
            }

            return ValueTask.FromResult(Success(new ProjectReviewMcpAudioSnapshot(
                report.SchemaVersion,
                report.GameVersion!,
                report.GameFileVersion!,
                report.CueId,
                report.Cues!,
                report.Page,
                report.Coverage!)));
        }
    }

    private sealed class ModAssetTool(
        ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpModAssetQueryRunner run,
        string operation) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = operation switch
        {
            ReviewModAssetContract.AssetsOperation => Tool(ModAssetsToolName,
                "List one bounded page of conventional mod-owned asset requests observed in this review.",
                PageInputSchema, ModAssetsOutputSchema),
            ReviewModAssetContract.KeysOperation => Tool(ModAssetKeysToolName,
                "List one bounded page of stable primitive keys for one already-observed mod-owned asset.",
                ModAssetKeysInputSchema, ModAssetKeysOutputSchema),
            _ => Tool(ModAssetRecordToolName,
                "Read one primitive string or integer value from one already-observed mod-owned asset.",
                ModAssetRecordInputSchema, ModAssetRecordOutputSchema),
        };

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryModAssetQuery(request.Params?.Arguments, operation, out ReviewModAssetQuery? query))
            {
                return ValueTask.FromResult(Error($"Invalid arguments for {ProtocolTool.Name}."));
            }
            if (!TryPreflight(reader, out ProjectReviewMcpContextResult? before, out CallToolResult? error))
            {
                return ValueTask.FromResult(error!);
            }

            LiveLabCommandResult result = run(query!);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0
                || result.Report is not ReviewModAssetReport report
                || !ValidModAssetReport(query!, report))
            {
                return ValueTask.FromResult(QueryError("mod asset", ModAssetProblem(result.Report as ReviewModAssetReport)));
            }
            if (!PostflightMatches(reader, before!))
            {
                return ValueTask.FromResult(QueryError("mod asset", "reviewBindingChanged"));
            }

            object snapshot = operation switch
            {
                ReviewModAssetContract.AssetsOperation => new ProjectReviewMcpModAssetsSnapshot(
                    report.SchemaVersion, report.GameVersion!, report.GameFileVersion!, report.CoverageScope,
                    report.Assets!, report.Page!, report.Coverage!),
                ReviewModAssetContract.KeysOperation => new ProjectReviewMcpModAssetKeysSnapshot(
                    report.SchemaVersion, report.GameVersion!, report.GameFileVersion!, report.CoverageScope,
                    report.Asset!, report.Keys!, report.Page!),
                _ => new ProjectReviewMcpModAssetRecordSnapshot(
                    report.SchemaVersion, report.GameVersion!, report.GameFileVersion!, report.CoverageScope,
                    report.Asset!, report.Key!, report.Record!.Value),
            };
            return ValueTask.FromResult(Success(snapshot));
        }
    }

    private static bool TryAudioQuery(
        IDictionary<string, JsonElement>? arguments,
        bool inventory,
        out ReviewAudioQuery? query)
    {
        query = null;
        if (inventory)
        {
            if (!TryPage(arguments, ["offset", "limit"], ReviewAudioContract.DefaultPageLimit, out int offset, out int limit))
            {
                return false;
            }
            query = new ReviewAudioQuery(ReviewAudioContract.CuesOperation, null, offset, limit);
            return ProjectReviewAudioService.Validate(query) is null;
        }

        if (!HasOnly(arguments, ["cueId"])
            || !TryString(arguments, "cueId", ReviewAudioContract.MaximumCueIdLength, out string? cueId))
        {
            return false;
        }
        query = new ReviewAudioQuery(ReviewAudioContract.CueOperation, cueId, 0, 1);
        return ProjectReviewAudioService.Validate(query) is null;
    }

    private static bool TryModAssetQuery(
        IDictionary<string, JsonElement>? arguments,
        string operation,
        out ReviewModAssetQuery? query)
    {
        query = null;
        if (operation == ReviewModAssetContract.AssetsOperation)
        {
            if (!TryPage(arguments, ["offset", "limit"], ReviewModAssetContract.DefaultPageLimit, out int offset, out int limit))
            {
                return false;
            }
            query = new ReviewModAssetQuery(operation, null, null, offset, limit);
        }
        else if (operation == ReviewModAssetContract.KeysOperation)
        {
            if (!TryPage(arguments, ["asset", "offset", "limit"], ReviewModAssetContract.DefaultPageLimit, out int offset, out int limit)
                || !TryString(arguments, "asset", ReviewModAssetContract.MaximumAssetLength, out string? asset))
            {
                return false;
            }
            query = new ReviewModAssetQuery(operation, asset, null, offset, limit);
        }
        else if (HasOnly(arguments, ["asset", "key"])
            && TryString(arguments, "asset", ReviewModAssetContract.MaximumAssetLength, out string? asset)
            && TryString(arguments, "key", ReviewModAssetContract.MaximumKeyLength, out string? key))
        {
            query = new ReviewModAssetQuery(operation, asset, key, 0, 1);
        }
        return query is not null && ProjectReviewModAssetService.Validate(query) is null;
    }

    private static bool ValidAudioReport(ReviewAudioQuery query, ReviewAudioReport report)
    {
        string requestId = Guid.NewGuid().ToString("N");
        return ProjectReviewAudioService.MatchesResponse(
            new ReviewAudioResponseEnvelope(ReviewAudioContract.SchemaVersion, requestId, report),
            requestId,
            query)
            && string.Equals(report.State, "ready", StringComparison.Ordinal)
            && report.Problems.Count == 0;
    }

    private static bool ValidModAssetReport(ReviewModAssetQuery query, ReviewModAssetReport report)
    {
        string requestId = Guid.NewGuid().ToString("N");
        return ProjectReviewModAssetService.MatchesRequest(
            new ReviewModAssetResponseEnvelope(ReviewModAssetContract.SchemaVersion, requestId, report),
            query,
            requestId)
            && string.Equals(report.State, "ready", StringComparison.Ordinal)
            && report.Problems.Count == 0
            && (report.Record is not JsonElement record
                || record.ValueKind != JsonValueKind.String
                || record.GetString() is string text && !text.Any(char.IsControl));
    }

    private static bool TryPreflight(
        ProjectReviewMcpRuntimeReader reader,
        out ProjectReviewMcpContextResult? context,
        out CallToolResult? error)
    {
        ProjectReviewMcpContextResult result = reader.ReadContext();
        context = result;
        error = result.Succeeded ? null : Error($"SDVKit review unavailable [{result.ErrorCode}]: {result.ErrorMessage}");
        return result.Succeeded;
    }

    private static bool PostflightMatches(
        ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpContextResult before)
    {
        ProjectReviewMcpContextResult result = reader.ReadContext();
        return before.Succeeded
            && result.Succeeded
            && before.Context!.State == result.Context!.State
            && string.Equals(before.Context.Role, result.Context.Role, StringComparison.Ordinal)
            && before.Context.TestSave == result.Context.TestSave
            && before.Context.AllTargetsReady == result.Context.AllTargetsReady
            && OwnedReviewLogReader.SameStagedContent(before.Context.Staging, result.Context.Staging);
    }

    private static bool TryPage(
        IDictionary<string, JsonElement>? arguments,
        IReadOnlyList<string> allowed,
        int defaultLimit,
        out int offset,
        out int limit)
    {
        offset = 0;
        limit = defaultLimit;
        return HasOnly(arguments, allowed)
            && TryOptionalInt(arguments, "offset", 0, int.MaxValue, ref offset)
            && TryOptionalInt(arguments, "limit", 1, 100, ref limit);
    }

    private static bool HasOnly(IDictionary<string, JsonElement>? arguments, IReadOnlyList<string> allowed) =>
        arguments is null || arguments.Keys.All(allowed.Contains);

    private static bool TryOptionalInt(
        IDictionary<string, JsonElement>? arguments,
        string name,
        int minimum,
        int maximum,
        ref int value)
    {
        if (arguments is null || !arguments.TryGetValue(name, out JsonElement element)) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int parsed) || parsed < minimum || parsed > maximum) return false;
        value = parsed;
        return true;
    }

    private static bool TryString(
        IDictionary<string, JsonElement>? arguments,
        string name,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (arguments is null || !arguments.TryGetValue(name, out JsonElement element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);
    }

    private static Tool Tool(string name, string description, JsonElement input, JsonElement output) => new()
    {
        Name = name,
        Description = description,
        InputSchema = input,
        OutputSchema = output,
        Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
    };

    private static CallToolResult Success(object snapshot)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(snapshot, JsonOptions);
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static string AudioProblem(ReviewAudioReport? report) => SafeCode(
        report is { Problems.Count: > 0 } ? report.Problems[0].Code : null,
        "audioQueryFailed");
    private static string ModAssetProblem(ReviewModAssetReport? report) => SafeCode(
        report is { Problems.Count: > 0 } ? report.Problems[0].Code : null,
        "modAssetQueryFailed");
    private static string SafeCode(string? candidate, string fallback) =>
        candidate is { Length: > 0 and <= 64 } && candidate.All(char.IsAsciiLetterOrDigit) ? candidate : fallback;
    private static CallToolResult QueryError(string family, string code) => Error($"SDVKit review {family} unavailable [{code}].");
    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

internal sealed record ProjectReviewMcpAudioSnapshot(
    int SchemaVersion,
    string GameVersion,
    string GameFileVersion,
    string? CueId,
    IReadOnlyList<ReviewAudioCueReport> Cues,
    ReviewAudioPage? Page,
    ReviewAudioCoverageReport Coverage);

internal sealed record ProjectReviewMcpModAssetsSnapshot(
    int SchemaVersion,
    string GameVersion,
    string GameFileVersion,
    string CoverageScope,
    IReadOnlyList<ReviewModAssetAssetReport> Assets,
    ReviewModAssetPage Page,
    ReviewModAssetCoverageReport Coverage);

internal sealed record ProjectReviewMcpModAssetKeysSnapshot(
    int SchemaVersion,
    string GameVersion,
    string GameFileVersion,
    string CoverageScope,
    ReviewModAssetAssetReport Asset,
    IReadOnlyList<string> Keys,
    ReviewModAssetPage Page);

internal sealed record ProjectReviewMcpModAssetRecordSnapshot(
    int SchemaVersion,
    string GameVersion,
    string GameFileVersion,
    string CoverageScope,
    ReviewModAssetAssetReport Asset,
    string Key,
    JsonElement Record);
