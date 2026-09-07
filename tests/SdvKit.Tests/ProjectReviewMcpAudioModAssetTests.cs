using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ProjectReviewMcpAudioModAssetTests
{
    [Fact]
    public async Task OfficialClientListsAndCallsAudioAndModAssetTools()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        var audioQueries = new List<ReviewAudioQuery>();
        var modAssetQueries = new List<ReviewModAssetQuery>();
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runAudio: query => { audioQueries.Add(query); return ReadyAudio(query); },
                runModAsset: query => { modAssetQueries.Add(query); return ReadyModAsset(query); }));

        ListToolsResult listed = await harness.Client.ListToolsAsync(new ListToolsRequestParams(), harness.Token);
        Tool[] tools = listed.Tools.Where(tool => tool.Name.StartsWith("stardew_audio_", StringComparison.Ordinal)
                || tool.Name.StartsWith("stardew_mod_asset", StringComparison.Ordinal))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                ProjectReviewMcpAssetTools.AudioCueToolName,
                ProjectReviewMcpAssetTools.AudioCuesToolName,
                ProjectReviewMcpAssetTools.ModAssetKeysToolName,
                ProjectReviewMcpAssetTools.ModAssetRecordToolName,
                ProjectReviewMcpAssetTools.ModAssetsToolName,
            ],
            tools.Select(tool => tool.Name));
        foreach (Tool tool in tools)
        {
            Assert.True(tool.Annotations?.ReadOnlyHint);
            Assert.False(tool.Annotations?.DestructiveHint);
            Assert.True(tool.Annotations?.IdempotentHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            JsonElement output = Assert.IsType<JsonElement>(tool.OutputSchema);
            Assert.False(output.GetProperty("additionalProperties").GetBoolean());
            Assert.All(
                output.TryGetProperty("$defs", out JsonElement definitions)
                    ? definitions.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("type", out JsonElement type)
                        && type.GetString() == "object")
                    : [],
                definition => Assert.False(definition.Value.GetProperty("additionalProperties").GetBoolean()));
        }

        JsonElement audioList = Successful(await Call(harness, ProjectReviewMcpAssetTools.AudioCuesToolName,
            new() { ["offset"] = 0, ["limit"] = 10 }));
        Assert.Equal("FixtureCue", audioList.GetProperty("cues")[0].GetProperty("cueId").GetString());
        JsonElement audioCue = Successful(await Call(harness, ProjectReviewMcpAssetTools.AudioCueToolName,
            new() { ["cueId"] = "FixtureCue" }));
        Assert.Equal("FixtureCue", audioCue.GetProperty("cueId").GetString());

        JsonElement assets = Successful(await Call(harness, ProjectReviewMcpAssetTools.ModAssetsToolName,
            new() { ["offset"] = 0, ["limit"] = 10 }));
        Assert.True(assets.GetProperty("coverage").GetProperty("complete").GetBoolean());
        JsonElement keys = Successful(await Call(harness, ProjectReviewMcpAssetTools.ModAssetKeysToolName,
            new() { ["asset"] = "Mods/Example.Mod/Words", ["offset"] = 0, ["limit"] = 10 }));
        Assert.Equal("Alpha", keys.GetProperty("keys")[0].GetString());
        JsonElement record = Successful(await Call(harness, ProjectReviewMcpAssetTools.ModAssetRecordToolName,
            new() { ["asset"] = "Mods/Example.Mod/Words", ["key"] = "Alpha" }));
        Assert.Equal("one", record.GetProperty("record").GetString());

        Assert.Equal(new ReviewAudioQuery(ReviewAudioContract.CuesOperation, null, 0, 10), audioQueries[0]);
        Assert.Equal(new ReviewAudioQuery(ReviewAudioContract.CueOperation, "FixtureCue", 0, 1), audioQueries[1]);
        Assert.Equal(new ReviewModAssetQuery(ReviewModAssetContract.GetOperation, "Mods/Example.Mod/Words", "Alpha", 0, 1), modAssetQueries[2]);
    }

    [Fact]
    public async Task InjectedBlockedAndTypeChangingResponsesFailClosed()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runAudio: query => new LiveLabCommandResult(0, ReadyAudioReport(query) with
                {
                    State = "blocked",
                    Cues = null,
                    Page = null,
                    Coverage = null,
                    Problems = [new ReviewAudioProblem("audioUnavailable", "Unavailable.")],
                }),
                runModAsset: query =>
                {
                    ReviewModAssetReport report = Assert.IsType<ReviewModAssetReport>(ReadyModAsset(query).Report);
                    return new LiveLabCommandResult(0, report with
                    {
                        Asset = report.Asset is null ? null : report.Asset with
                        {
                            AssetName = "Mods/Example.Mod/Typed",
                            DataType = "System.String",
                            Shape = "stringSingleton",
                            TypeCollision = true,
                            ProblemCode = "modAssetTypeAmbiguous",
                        },
                    });
                }));

        CallToolResult audio = await Call(harness, ProjectReviewMcpAssetTools.AudioCueToolName,
            new() { ["cueId"] = "FixtureCue" });
        Assert.True(audio.IsError);

        CallToolResult record = await Call(harness, ProjectReviewMcpAssetTools.ModAssetRecordToolName,
            new() { ["asset"] = "Mods/Example.Mod/Words", ["key"] = "Alpha" });
        Assert.True(record.IsError);
        Assert.DoesNotContain("Typed", Assert.IsType<TextContentBlock>(Assert.Single(record.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactModAssetPreservesZeroReadyCountFromCliContract()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runAudio: ReadyAudio,
                runModAsset: query =>
                {
                    ReviewModAssetReport report = Assert.IsType<ReviewModAssetReport>(ReadyModAsset(query).Report);
                    return new LiveLabCommandResult(0, report with
                    {
                        Asset = report.Asset! with { ReadyCount = 0 },
                    });
                }));

        ListToolsResult listed = await harness.Client.ListToolsAsync(
            new ListToolsRequestParams(), harness.Token);
        Tool tool = Assert.Single(listed.Tools, candidate =>
            candidate.Name == ProjectReviewMcpAssetTools.ModAssetRecordToolName);
        JsonElement schema = Assert.IsType<JsonElement>(tool.OutputSchema);
        Assert.Equal(0, schema.GetProperty("$defs").GetProperty("asset")
            .GetProperty("properties").GetProperty("readyCount")
            .GetProperty("minimum").GetInt32());

        JsonElement result = Successful(await Call(harness, ProjectReviewMcpAssetTools.ModAssetRecordToolName,
            new() { ["asset"] = "Mods/Example.Mod/Words", ["key"] = "Alpha" }));
        Assert.Equal(0, result.GetProperty("asset").GetProperty("readyCount").GetInt32());
    }

    [Fact]
    public async Task AudioExactRejectsEchoMismatchButAcceptsUnknownCueNullMetadata()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runAudio: query => new LiveLabCommandResult(0,
                    query.CueId == "Mismatch"
                        ? ReadyAudioReport(query) with { CueId = "Other" }
                        : UnknownAudioReport(query)),
                runModAsset: ReadyModAsset));

        CallToolResult mismatch = await Call(harness, ProjectReviewMcpAssetTools.AudioCueToolName,
            new() { ["cueId"] = "Mismatch" });
        Assert.True(mismatch.IsError);

        JsonElement unknown = Successful(await Call(harness, ProjectReviewMcpAssetTools.AudioCueToolName,
            new() { ["cueId"] = "MissingCue" }));
        JsonElement cue = unknown.GetProperty("cues")[0];
        Assert.False(cue.GetProperty("sessionResident").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cue.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, cue.GetProperty("streamedVorbis").ValueKind);
    }

    [Fact]
    public async Task UnsupportedCatalogueEntryIsVisibleButExactReadFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runAudio: ReadyAudio,
                runModAsset: query => UnsupportedModAsset(query)));

        JsonElement catalogue = Successful(await Call(harness, ProjectReviewMcpAssetTools.ModAssetsToolName,
            new() { ["offset"] = 0, ["limit"] = 10 }));
        Assert.Equal(JsonValueKind.Null, catalogue.GetProperty("assets")[0].GetProperty("shape").ValueKind);

        CallToolResult exact = await Call(harness, ProjectReviewMcpAssetTools.ModAssetRecordToolName,
            new() { ["asset"] = "Mods/Example.Mod/Words", ["key"] = "Alpha" });
        Assert.True(exact.IsError);
    }

    [Fact]
    public async Task PrimitiveRecordBoundsAreEnforced()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runAudio: ReadyAudio,
                runModAsset: query => query.Key switch
                {
                    "Min" => ExactModAsset(query, int.MinValue),
                    "Max" => ExactModAsset(query, int.MaxValue),
                    "Control" => ExactModAsset(query, "unsafe\nvalue"),
                    _ => ExactModAsset(query, new string('x', 65_537)),
                }));

        foreach ((string key, int expected) in new[] { ("Min", int.MinValue), ("Max", int.MaxValue) })
        {
            JsonElement result = Successful(await Call(harness, ProjectReviewMcpAssetTools.ModAssetRecordToolName,
                new() { ["asset"] = "Mods/Example.Mod/Words", ["key"] = key }));
            Assert.Equal(expected, result.GetProperty("record").GetInt32());
        }
        foreach (string key in new[] { "Control", "Oversized" })
        {
            CallToolResult result = await Call(harness, ProjectReviewMcpAssetTools.ModAssetRecordToolName,
                new() { ["asset"] = "Mods/Example.Mod/Words", ["key"] = key });
            Assert.True(result.IsError);
        }
    }

    [Fact]
    public async Task ResultIsRejectedWhenReviewBecomesStaleDuringDispatch()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runAudio: query =>
                {
                    File.Delete(LiveLabPaths.Resolve(temporary.Path).StatusPath);
                    return ReadyAudio(query);
                },
                runModAsset: ReadyModAsset));

        CallToolResult result = await Call(harness, ProjectReviewMcpAssetTools.AudioCueToolName,
            new() { ["cueId"] = "FixtureCue" });
        Assert.True(result.IsError);
        Assert.Contains("reviewBindingChanged",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshReceiptIdentityParticipatesInReviewBinding()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ProjectReviewMcpVerifiedContext context = Assert.IsType<ProjectReviewMcpVerifiedContext>(
            reader.ReadContext().Context);
        ProjectReviewOwnedArtifact artifact = Assert.Single(context.Staging.Artifacts);
        var receipt = new CpRefreshReceipt(
            new string('a', 32), context.State.LaunchId,
            artifact.BuildIdentity, artifact.BuildIdentity,
            ["content.json"], true, false);
        ProjectReviewStaging before = context.Staging with
        {
            Artifacts = [artifact with { CpRefresh = receipt }],
        };
        ProjectReviewStaging after = before with
        {
            Artifacts = [artifact with
            {
                CpRefresh = receipt with { RefreshId = new string('b', 32) },
            }],
        };

        Assert.False(OwnedReviewLogReader.SameStagedContent(before, after));
    }

    [Fact]
    public async Task AssetToolsAreNotRegisteredForNetworkReaders()
    {
        using TemporaryDirectory temporary = new();
        var reader = new ProjectReviewMcpRuntimeReader(temporary.Path, "network-2", "host");
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runAudio: ReadyAudio,
                runModAsset: ReadyModAsset));

        ListToolsResult listed = await harness.Client.ListToolsAsync(new ListToolsRequestParams(), harness.Token);
        Assert.DoesNotContain(listed.Tools, tool => tool.Name.StartsWith("stardew_audio_", StringComparison.Ordinal));
        Assert.DoesNotContain(listed.Tools, tool => tool.Name.StartsWith("stardew_mod_asset", StringComparison.Ordinal));
    }

    private static async Task<CallToolResult> Call(McpTestClient harness, string tool, Dictionary<string, object?> arguments) =>
        await harness.Client.CallToolAsync(tool, arguments, cancellationToken: harness.Token);

    private static JsonElement Successful(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
        using JsonDocument text = JsonDocument.Parse(
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.True(JsonElement.DeepEquals(structured, text.RootElement));
        return structured;
    }

    private static LiveLabCommandResult ReadyAudio(ReviewAudioQuery query) => new(0, ReadyAudioReport(query));

    private static ReviewAudioReport ReadyAudioReport(ReviewAudioQuery query)
    {
        bool inventory = query.Operation == ReviewAudioContract.CuesOperation;
        var cue = inventory
            ? new ReviewAudioCueReport(
                "FixtureCue", [ReviewAudioContract.AudioChangesSource], true, true, true,
                1, 1, "Sound", false, false, false, [])
            : new ReviewAudioCueReport(
                "FixtureCue", [], false, true, true,
                3, null, null, null, null, null, []);
        return new ReviewAudioReport(
            ReviewAudioContract.SchemaVersion, "ready", query.Operation,
            "1.6.15", "1.6.15.24356", inventory ? null : query.CueId,
            [cue], inventory ? new ReviewAudioPage(query.Offset, query.Limit, 1, 1, null) : null,
            new ReviewAudioCoverageReport(inventory ? 1 : 0, 0, 0, inventory ? 1 : 0, 1, 1, 0, 0, true, null, ReviewAudioContract.BuiltInInventoryStatus),
            []);
    }

    private static ReviewAudioReport UnknownAudioReport(ReviewAudioQuery query)
    {
        var cue = new ReviewAudioCueReport(
            query.CueId!, [], false, false, false,
            null, null, null, null, null, null, []);
        return new ReviewAudioReport(
            ReviewAudioContract.SchemaVersion, "ready", query.Operation,
            "1.6.15", "1.6.15.24356", query.CueId,
            [cue], null,
            new ReviewAudioCoverageReport(0, 0, 0, 0, 1, 0, 1, 0, true, null,
                ReviewAudioContract.BuiltInInventoryStatus),
            []);
    }

    private static LiveLabCommandResult ReadyModAsset(ReviewModAssetQuery query)
    {
        ReviewModAssetAssetReport asset = Asset();
        ReviewModAssetReport report = new(
            ReviewModAssetContract.SchemaVersion, "ready", query.Operation,
            "1.6.15", "1.6.15.24356", ReviewModAssetContract.CoverageScope,
            query.Operation == ReviewModAssetContract.AssetsOperation ? null : asset,
            query.Operation == ReviewModAssetContract.GetOperation ? "Alpha" : null,
            query.Operation == ReviewModAssetContract.AssetsOperation ? [asset] : null,
            query.Operation == ReviewModAssetContract.KeysOperation ? ["Alpha"] : null,
            query.Operation == ReviewModAssetContract.GetOperation ? null : new ReviewModAssetPage(query.Offset, query.Limit, 1, 1, null),
            query.Operation == ReviewModAssetContract.AssetsOperation
                ? new ReviewModAssetCoverageReport(ReviewModAssetContract.CoverageScope, new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero), 1, 1, 1, 0, 1, 0, 0, 0, 0, 0)
                : null,
            query.Operation == ReviewModAssetContract.GetOperation ? JsonSerializer.SerializeToElement("one") : null,
            []);
        return new LiveLabCommandResult(0, report);
    }

    private static ReviewModAssetAssetReport Asset() => new(
        "Mods/Example.Mod/Words", "Example.Mod", "resolved", null,
        "unavailableThroughPublicSmapiApi",
        "System.Collections.Generic.Dictionary<System.String,System.String>",
        "stringDictionary", "ready", 0, 1, 1, true, true, false, false, null);

    private static LiveLabCommandResult UnsupportedModAsset(ReviewModAssetQuery query)
    {
        ReviewModAssetAssetReport unsupported = Asset() with
        {
            DataType = "Example.Unsupported",
            Shape = null,
            Lifecycle = "requested",
            ReadyCount = 0,
            Available = false,
            AdapterSupported = false,
            ProblemCode = "modAssetAdapterUnavailable",
        };
        ReviewModAssetReport report = Assert.IsType<ReviewModAssetReport>(ReadyModAsset(query).Report);
        return new LiveLabCommandResult(0, report with
        {
            Asset = query.Operation == ReviewModAssetContract.AssetsOperation ? null : unsupported,
            Assets = query.Operation == ReviewModAssetContract.AssetsOperation ? [unsupported] : null,
            Coverage = query.Operation == ReviewModAssetContract.AssetsOperation
                ? new ReviewModAssetCoverageReport(
                    ReviewModAssetContract.CoverageScope,
                    new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
                    1, 1, 0, 1, 0, 0, 0, 0, 0, 0)
                : null,
        });
    }

    private static LiveLabCommandResult ExactModAsset(ReviewModAssetQuery query, object value)
    {
        ReviewModAssetReport report = Assert.IsType<ReviewModAssetReport>(ReadyModAsset(query).Report);
        bool integer = value is int;
        return new LiveLabCommandResult(0, report with
        {
            Asset = report.Asset! with
            {
                DataType = integer
                    ? "System.Collections.Generic.Dictionary<System.String,System.Int32>"
                    : "System.Collections.Generic.Dictionary<System.String,System.String>",
                Shape = integer ? "integerDictionary" : "stringDictionary",
            },
            Key = query.Key,
            Record = JsonSerializer.SerializeToElement(value),
        });
    }
}
