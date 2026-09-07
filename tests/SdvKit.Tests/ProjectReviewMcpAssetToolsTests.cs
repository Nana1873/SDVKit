using System.Security.Cryptography;
using System.Text.Json;
using Json.Schema;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ProjectReviewMcpAssetToolsTests
{
    [Fact]
    public async Task MapToolsExposeClosedSchemasAndExactTypedReads()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var dispatched = new List<ReviewMapQuery>();
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runMap: query =>
                {
                    dispatched.Add(query);
                    return ReadyMap(query);
                }));

        ListToolsResult listed = await harness.Client.ListToolsAsync(
            new ListToolsRequestParams(), harness.Token);
        string[] names = listed.Tools.Select(tool => tool.Name).ToArray();
        Assert.Contains(ProjectReviewMcpMapTools.AssetsToolName, names);
        Assert.Contains(ProjectReviewMcpMapTools.GetToolName, names);
        Assert.Contains(ProjectReviewMcpMapTools.LayersToolName, names);
        Assert.Contains(ProjectReviewMcpMapTools.LayerToolName, names);
        Assert.Contains(ProjectReviewMcpMapTools.TileSheetsToolName, names);
        Assert.Contains(ProjectReviewMcpMapTools.WarpsToolName, names);
        Assert.Contains(ProjectReviewMcpMapTools.TileToolName, names);
        Assert.Contains(ProjectReviewMcpMapTools.PropertyToolName, names);
        foreach (Tool tool in listed.Tools.Where(tool => tool.Name.StartsWith(
                     "stardew_map_", StringComparison.Ordinal)))
        {
            Assert.True(tool.Annotations?.ReadOnlyHint);
            Assert.True(tool.Annotations?.IdempotentHint);
            Assert.False(tool.Annotations?.DestructiveHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            JsonElement outputSchema = Assert.IsType<JsonElement>(tool.OutputSchema);
            Assert.False(outputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.True(outputSchema.TryGetProperty("$defs", out _));
        }

        CallToolResult result = await harness.Client.CallToolAsync(
            ProjectReviewMcpMapTools.GetToolName,
            new Dictionary<string, object?> { ["asset"] = "Maps/Farm" },
            cancellationToken: harness.Token);

        Assert.NotEqual(true, result.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("Maps/Farm", structured.GetProperty("assetName").GetString());
        Assert.Equal(1280, structured.GetProperty("map").GetProperty("displayWidth").GetInt32());
        Assert.True(JsonSchema.FromText(Assert.IsType<JsonElement>(listed.Tools.Single(
                tool => tool.Name == ProjectReviewMcpMapTools.GetToolName).OutputSchema).GetRawText())
            .Evaluate(System.Text.Json.Nodes.JsonNode.Parse(structured.GetRawText())).IsValid);
        Assert.True(JsonElement.DeepEquals(structured,
            JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text)
                .RootElement));
        Assert.Equal(new ReviewMapQuery(ReviewMapContract.GetOperation, "Maps/Farm",
            null, null, null, null, null, null, null, 0, 1), Assert.Single(dispatched));
    }

    [Fact]
    public async Task TexturePreviewReturnsOnlyCheckedBytesAsImageContent()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        byte[] png = PngTestData.CreateRgba8(2, 1);
        string requestId = Guid.NewGuid().ToString("N");
        string path = ReviewTextureContract.PreviewPath(
            LiveLabPaths.Resolve(temporary.Path).RuntimePath, requestId);
        File.WriteAllBytes(path, png);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runTexture: query => ReadyTexturePreview(query, requestId, png)));

        CallToolResult result = await harness.Client.CallToolAsync(
            ProjectReviewMcpTextureTools.PreviewToolName,
            new Dictionary<string, object?> { ["asset"] = "LooseSprites/Cursors" },
            cancellationToken: harness.Token);

        Assert.NotEqual(true, result.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.False(structured.GetRawText().Contains("relativePath", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("image/png", structured.GetProperty("preview").GetProperty("mimeType").GetString());
        Assert.Collection(result.Content,
            block => Assert.True(JsonElement.DeepEquals(structured,
                JsonDocument.Parse(Assert.IsType<TextContentBlock>(block).Text).RootElement)),
            block => Assert.Equal(png, Assert.IsType<ImageContentBlock>(block).DecodedData.ToArray()));
        Tool tool = (await harness.Client.ListToolsAsync(
            new ListToolsRequestParams(), harness.Token)).Tools.Single(
                candidate => candidate.Name == ProjectReviewMcpTextureTools.PreviewToolName);
        Assert.False(tool.Annotations?.ReadOnlyHint);
        Assert.False(tool.Annotations?.IdempotentHint);
        Assert.False(tool.Annotations?.DestructiveHint);
        Assert.False(tool.Annotations?.OpenWorldHint);
        Assert.True(JsonSchema.FromText(Assert.IsType<JsonElement>(tool.OutputSchema).GetRawText())
            .Evaluate(System.Text.Json.Nodes.JsonNode.Parse(structured.GetRawText())).IsValid);
    }

    [Fact]
    public async Task MetadataSchemaAllowsLargeTexturesButPreviewRetainsItsSourceBound()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        byte[] preview = PngTestData.CreateRgba8(512, 1);
        string requestId = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(ReviewTextureContract.PreviewPath(
            LiveLabPaths.Resolve(temporary.Path).RuntimePath, requestId), preview);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runTexture: query => query.Operation == ReviewTextureContract.GetOperation
                    ? ReadyLargeTexture(query)
                    : ReadyLargeTexturePreview(query, requestId, preview)));

        CallToolResult get = await harness.Client.CallToolAsync(
            ProjectReviewMcpTextureTools.GetToolName,
            new Dictionary<string, object?> { ["asset"] = "LooseSprites/Cursors" },
            cancellationToken: harness.Token);
        Assert.NotEqual(true, get.IsError);
        Tool getTool = (await harness.Client.ListToolsAsync(
            new ListToolsRequestParams(), harness.Token)).Tools.Single(
                candidate => candidate.Name == ProjectReviewMcpTextureTools.GetToolName);
        Assert.True(getTool.Annotations?.ReadOnlyHint);
        Assert.True(getTool.Annotations?.IdempotentHint);
        Assert.True(JsonSchema.FromText(Assert.IsType<JsonElement>(getTool.OutputSchema).GetRawText())
            .Evaluate(System.Text.Json.Nodes.JsonNode.Parse(
                Assert.IsType<JsonElement>(get.StructuredContent).GetRawText())).IsValid);

        CallToolResult rejectedPreview = await harness.Client.CallToolAsync(
            ProjectReviewMcpTextureTools.PreviewToolName,
            new Dictionary<string, object?> { ["asset"] = "LooseSprites/Cursors" },
            cancellationToken: harness.Token);
        Assert.True(rejectedPreview.IsError);
        Assert.IsType<TextContentBlock>(Assert.Single(rejectedPreview.Content));
    }

    [Fact]
    public async Task EveryMapAndTextureOperationMapsToTheExistingCliQuery()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        byte[] png = PngTestData.CreateRgba8(2, 1);
        string requestId = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(ReviewTextureContract.PreviewPath(
            LiveLabPaths.Resolve(temporary.Path).RuntimePath, requestId), png);
        var maps = new List<ReviewMapQuery>();
        var textures = new List<ReviewTextureQuery>();
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runMap: query =>
                {
                    maps.Add(query);
                    return ReadyMap(query);
                },
                runTexture: query =>
                {
                    textures.Add(query);
                    return query.Operation == ReviewTextureContract.PreviewOperation
                        ? ReadyTexturePreview(query, requestId, png)
                        : ReadyTexture(query);
                }));

        (string Name, Dictionary<string, object?> Arguments)[] calls =
        [
            (ProjectReviewMcpMapTools.AssetsToolName, new() { ["offset"] = 2, ["limit"] = 3 }),
            (ProjectReviewMcpMapTools.GetToolName, new() { ["asset"] = "Maps/Farm" }),
            (ProjectReviewMcpMapTools.LayersToolName, new() { ["asset"] = "Maps/Farm", ["limit"] = 4 }),
            (ProjectReviewMcpMapTools.LayerToolName, new() { ["asset"] = "Maps/Farm", ["layer"] = "Back" }),
            (ProjectReviewMcpMapTools.TileSheetsToolName, new() { ["asset"] = "Maps/Farm" }),
            (ProjectReviewMcpMapTools.WarpsToolName, new() { ["asset"] = "Maps/Farm", ["offset"] = 1 }),
            (ProjectReviewMcpMapTools.TileToolName, new() { ["asset"] = "Maps/Farm", ["layer"] = "Back", ["x"] = 3, ["y"] = 4 }),
            (ProjectReviewMcpMapTools.PropertyToolName, new() { ["asset"] = "Maps/Farm", ["scope"] = "map", ["source"] = "direct", ["property"] = "Outdoors" }),
            (ProjectReviewMcpTextureTools.AssetsToolName, new() { ["limit"] = 2 }),
            (ProjectReviewMcpTextureTools.GetToolName, new() { ["asset"] = "LooseSprites/Cursors" }),
            (ProjectReviewMcpTextureTools.PreviewToolName, new() { ["asset"] = "LooseSprites/Cursors" }),
        ];
        foreach ((string name, Dictionary<string, object?> arguments) in calls)
        {
            CallToolResult result = await harness.Client.CallToolAsync(
                name, arguments, cancellationToken: harness.Token);
            Assert.NotEqual(true, result.IsError);
        }

        Assert.Equal(8, maps.Count);
        Assert.Equal(new ReviewMapQuery(ReviewMapContract.AssetsOperation, null,
            null, null, null, null, null, null, null, 2, 3), maps[0]);
        Assert.Equal(new ReviewMapQuery(ReviewMapContract.LayersOperation, "Maps/Farm",
            null, null, null, null, null, null, null, 0, 4), maps[2]);
        Assert.Equal(new ReviewMapQuery(ReviewMapContract.TileOperation, "Maps/Farm",
            "Back", 3, 4, null, null, null, null, 0, 1), maps[6]);
        Assert.Equal(new ReviewMapQuery(ReviewMapContract.PropertyOperation, "Maps/Farm",
            null, null, null, "map", "direct", null, "Outdoors", 0, 1), maps[7]);
        Assert.Equal(
            [
                new ReviewTextureQuery(ReviewTextureContract.AssetsOperation, null, 0, 2),
                new ReviewTextureQuery(ReviewTextureContract.GetOperation, "LooseSprites/Cursors", 0, 1),
                new ReviewTextureQuery(ReviewTextureContract.PreviewOperation, "LooseSprites/Cursors", 0, 1),
            ],
            textures);
    }

    [Fact]
    public async Task InvalidAndBlockedResultsFailClosed()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var dispatchCount = 0;
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runMap: query =>
                {
                    dispatchCount++;
                    return BlockedMap(query);
                },
                runTexture: query =>
                {
                    dispatchCount++;
                    return BlockedTexture(query);
                }));

        CallToolResult invalid = await harness.Client.CallToolAsync(
            ProjectReviewMcpMapTools.PropertyToolName,
            new Dictionary<string, object?>
            {
                ["asset"] = "Maps/Farm",
                ["scope"] = "map",
                ["source"] = "tile-index",
                ["property"] = "Warp",
            }, cancellationToken: harness.Token);
        Assert.True(invalid.IsError);
        Assert.Equal(0, dispatchCount);

        CallToolResult blockedMap = await harness.Client.CallToolAsync(
            ProjectReviewMcpMapTools.GetToolName,
            new Dictionary<string, object?> { ["asset"] = "Maps/Farm" },
            cancellationToken: harness.Token);
        CallToolResult blockedTexture = await harness.Client.CallToolAsync(
            ProjectReviewMcpTextureTools.GetToolName,
            new Dictionary<string, object?> { ["asset"] = "LooseSprites/Cursors" },
            cancellationToken: harness.Token);
        Assert.True(blockedMap.IsError);
        Assert.True(blockedTexture.IsError);
        Assert.Equal(2, dispatchCount);
    }

    [Fact]
    public async Task PreviewRejectsBytesThatDoNotMatchTheValidatedReport()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        byte[] reported = PngTestData.CreateRgba8(2, 1);
        byte[] replaced = PngTestData.CreateRgba8(2, 1,
            [255, 0, 0, 255, 0, 255, 0, 255]);
        string requestId = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(ReviewTextureContract.PreviewPath(
            LiveLabPaths.Resolve(temporary.Path).RuntimePath, requestId), replaced);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runTexture: query => ReadyTexturePreview(
                    query, requestId, reported)));

        CallToolResult result = await harness.Client.CallToolAsync(
            ProjectReviewMcpTextureTools.PreviewToolName,
            new Dictionary<string, object?> { ["asset"] = "LooseSprites/Cursors" },
            cancellationToken: harness.Token);

        Assert.True(result.IsError);
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public async Task ResultIsRejectedWhenTheReviewBindingChangesDuringDispatch()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runMap: query =>
                {
                    File.Delete(LiveLabPaths.Resolve(temporary.Path).StatusPath);
                    return ReadyMap(query);
                }));

        CallToolResult result = await harness.Client.CallToolAsync(
            ProjectReviewMcpMapTools.GetToolName,
            new Dictionary<string, object?> { ["asset"] = "Maps/Farm" },
            cancellationToken: harness.Token);

        Assert.True(result.IsError);
        Assert.Contains("reviewBindingChanged",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InjectedAssetRunnersRemainAbsentFromNetworkServer()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyNetworkReview(
                temporary, NetworkTwoContract.HostRole);
        await using McpTestClient harness = await McpTestClient.StartAsync(
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runMap: ReadyMap,
                runTexture: ReadyTexture,
                topology: NetworkTwoContract.Topology,
                role: NetworkTwoContract.HostRole));

        ListToolsResult listed = await harness.Client.ListToolsAsync(
            new ListToolsRequestParams(), harness.Token);

        Assert.DoesNotContain(listed.Tools,
            tool => tool.Name.StartsWith("stardew_map_", StringComparison.Ordinal));
        Assert.DoesNotContain(listed.Tools,
            tool => tool.Name.StartsWith("stardew_texture_", StringComparison.Ordinal));
    }

    private static LiveLabCommandResult ReadyMap(ReviewMapQuery query)
    {
        ReviewMapPage page = new(query.Offset, query.Limit, 0, 0, null);
        ReviewMapLayerReport layer = new(0, query.Layer ?? "Back", 10, 10,
            16, 16, true, query.Operation == ReviewMapContract.PropertyOperation ? 1 : 0);
        ReviewMapTileReport tile = new(query.Layer ?? "Back", query.X ?? 0,
            query.Y ?? 0, false, null, null, null, null, null, null, 0, 0);
        ReviewMapReport report = query.Operation switch
        {
            ReviewMapContract.AssetsOperation => BaseMap(query) with
            {
                AssetName = null,
                DataType = null,
                Assets = [],
                Page = page,
                Coverage = new ReviewMapCoverageReport(0, 0, 0, 0, 0, 0, 0, 0),
            },
            ReviewMapContract.GetOperation => BaseMap(query) with
            {
                Map = new ReviewMapSummary(1280, 720, 1, 1, 0, 0),
            },
            ReviewMapContract.LayersOperation => BaseMap(query) with
            {
                Layers = [],
                Page = page,
            },
            ReviewMapContract.LayerOperation => BaseMap(query) with { Layer = layer },
            ReviewMapContract.TileSheetsOperation => BaseMap(query) with
            {
                TileSheets = [],
                Page = page,
            },
            ReviewMapContract.WarpsOperation => BaseMap(query) with
            {
                Warps = [],
                Page = page,
            },
            ReviewMapContract.TileOperation => BaseMap(query) with { Tile = tile },
            _ => BaseMap(query) with
            {
                Property = new ReviewMapPropertyReport("map", "direct", null,
                    query.Property!, "string", JsonSerializer.SerializeToElement("true")),
            },
        };
        return new LiveLabCommandResult(0, report);
    }

    private static ReviewMapReport BaseMap(ReviewMapQuery query) => new(
        ReviewMapContract.SchemaVersion, "ready", query.Operation, "1.6.15",
        "1.6.15.24356", query.Asset, "xTile.Map", null, null, null, null,
        null, null, null, null, null, null, []);

    private static LiveLabCommandResult ReadyTexture(ReviewTextureQuery query)
    {
        ReviewTextureReport report = query.Operation == ReviewTextureContract.AssetsOperation
            ? new ReviewTextureReport(ReviewTextureContract.SchemaVersion, "ready",
                query.Operation, "1.6.15", "1.6.15.24356", null,
                ReviewTextureContract.CanonicalGameContentSource, null, null,
                Provenance(), null, [],
                new ReviewTexturePage(query.Offset, query.Limit, 0, 0, null),
                new ReviewTextureCoverageReport(0, 0, 0, 0, 0), [])
            : new ReviewTextureReport(ReviewTextureContract.SchemaVersion, "ready",
                query.Operation, "1.6.15", "1.6.15.24356", query.Asset,
                ReviewTextureContract.CanonicalGameContentSource, true,
                new ReviewTextureMetadataReport(2, 1, "Color", 1, false),
                Provenance(), null, null, null, null, []);
        return new LiveLabCommandResult(0, report);
    }

    private static LiveLabCommandResult ReadyTexturePreview(
        ReviewTextureQuery query, string requestId, byte[] png) => new(0,
        new ReviewTextureReport(ReviewTextureContract.SchemaVersion, "ready",
            query.Operation, "1.6.15", "1.6.15.24356", query.Asset,
            ReviewTextureContract.CanonicalGameContentSource, true,
            new ReviewTextureMetadataReport(2, 1, "Color", 1, false),
            Provenance(),
            new ReviewTexturePreviewReport(
                ReviewTextureContract.PreviewFileName(requestId), 2, 1, png.Length,
                Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant()),
            null, null, null, []));

    private static LiveLabCommandResult ReadyLargeTexture(ReviewTextureQuery query) => new(0,
        new ReviewTextureReport(ReviewTextureContract.SchemaVersion, "ready",
            query.Operation, "1.6.15", "1.6.15.24356", query.Asset,
            ReviewTextureContract.CanonicalGameContentSource, true,
            new ReviewTextureMetadataReport(8193, 1, "Color", 1, false),
            Provenance(), null, null, null, null, []));

    private static LiveLabCommandResult ReadyLargeTexturePreview(
        ReviewTextureQuery query, string requestId, byte[] png) => new(0,
        new ReviewTextureReport(ReviewTextureContract.SchemaVersion, "ready",
            query.Operation, "1.6.15", "1.6.15.24356", query.Asset,
            ReviewTextureContract.CanonicalGameContentSource, true,
            new ReviewTextureMetadataReport(8193, 1, "Color", 1, false),
            Provenance(),
            new ReviewTexturePreviewReport(
                ReviewTextureContract.PreviewFileName(requestId), 512, 1, png.Length,
                Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant()),
            null, null, null, []));

    private static LiveLabCommandResult BlockedMap(ReviewMapQuery query) => new(0,
        new ReviewMapReport(ReviewMapContract.SchemaVersion, "blocked", query.Operation,
            "1.6.15", "1.6.15.24356", null, null, null, null, null, null,
            null, null, null, null, null, null,
            [new ReviewMapProblem("mapLoadFailed", "Unavailable.")]));

    private static LiveLabCommandResult BlockedTexture(ReviewTextureQuery query) => new(0,
        new ReviewTextureReport(ReviewTextureContract.SchemaVersion, "blocked",
            query.Operation, "1.6.15", "1.6.15.24356", null, null, null, null,
            null, null, null, null, null,
            [new ReviewTextureProblem("textureLoadFailed", "Unavailable.")]));

    private static ReviewTextureProvenanceReport Provenance() => new(
        ReviewTextureContract.FinalPipelineStage,
        false,
        ReviewTextureContract.ProvenanceUnavailableDetail);
}
