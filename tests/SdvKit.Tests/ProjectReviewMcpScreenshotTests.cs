using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ProjectReviewMcpScreenshotTests
{
    [Fact]
    public async Task ToolHasClosedSchemaHonestAnnotationsAndRealImageContent()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        byte[] png = PngTestData.CreateRgba8(2, 1);
        ProjectReviewScreenshotCapture capture = Capture("viewport", "menu", png);
        (McpClient client, McpServer server, Pipe clientToServer, CancellationTokenSource timeout, Task serverTask) =
            await StartAsync(reader, (_, _) => new ProjectReviewScreenshotResult(capture, []));
        await using (client)
        await using (server)
        using (timeout)
        {
            try
            {
                ListToolsResult listed = await client.ListToolsAsync(
                    new ListToolsRequestParams(),
                    timeout.Token);
                Tool tool = Assert.Single(
                    listed.Tools,
                    candidate => string.Equals(
                        candidate.Name,
                        ProjectReviewMcpScreenshotTools.CaptureToolName,
                        StringComparison.Ordinal));
                Assert.False(tool.Annotations?.ReadOnlyHint);
                Assert.False(tool.Annotations?.DestructiveHint);
                Assert.False(tool.Annotations?.IdempotentHint);
                Assert.False(tool.Annotations?.OpenWorldHint);
                Assert.False(tool.InputSchema
                    .GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(
                    ["label", "mode"],
                    tool.InputSchema.GetProperty("required")
                        .EnumerateArray()
                        .Select(value => value.GetString()!)
                        .Order(StringComparer.Ordinal)
                        .ToArray());
                Assert.Equal(
                    ["map", "viewport"],
                    tool.InputSchema.GetProperty("properties")
                        .GetProperty("mode")
                        .GetProperty("enum")
                        .EnumerateArray()
                        .Select(value => value.GetString()!)
                        .ToArray());
                JsonElement outputSchema = Assert.IsType<JsonElement>(tool.OutputSchema);
                Assert.False(outputSchema
                    .GetProperty("additionalProperties").GetBoolean());

                CallToolResult result = await client.CallToolAsync(
                    ProjectReviewMcpScreenshotTools.CaptureToolName,
                    new Dictionary<string, object?>
                    {
                        ["mode"] = "viewport",
                        ["label"] = "menu",
                    },
                    cancellationToken: timeout.Token);

                Assert.NotEqual(true, result.IsError);
                JsonElement metadata = Assert.IsType<JsonElement>(result.StructuredContent);
                Assert.Equal("single", metadata.GetProperty("topology").GetString());
                Assert.Equal(JsonValueKind.Null, metadata.GetProperty("role").ValueKind);
                Assert.Equal("viewport", metadata.GetProperty("mode").GetString());
                Assert.Equal("SDVKit-menu.png", metadata.GetProperty("fileName").GetString());
                Assert.Equal(2, metadata.GetProperty("width").GetInt32());
                Assert.Equal(ReviewScreenshotContract.MimeType, metadata
                    .GetProperty("mimeType").GetString());
                Assert.DoesNotContain(
                    temporary.Path,
                    metadata.GetRawText(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Collection(
                    result.Content,
                    block =>
                    {
                        TextContentBlock text = Assert.IsType<TextContentBlock>(block);
                        Assert.True(JsonElement.DeepEquals(
                            metadata,
                            JsonDocument.Parse(text.Text).RootElement));
                    },
                    block =>
                    {
                        ImageContentBlock image = Assert.IsType<ImageContentBlock>(block);
                        Assert.Equal(ReviewScreenshotContract.MimeType, image.MimeType);
                        Assert.Equal(png, image.DecodedData.ToArray());
                    });

                CallToolResult invalid = await client.CallToolAsync(
                    ProjectReviewMcpScreenshotTools.CaptureToolName,
                    new Dictionary<string, object?>
                    {
                        ["mode"] = "desktop",
                        ["label"] = "menu",
                    },
                    cancellationToken: timeout.Token);
                Assert.True(invalid.IsError);
            }
            finally
            {
                await StopAsync(client, clientToServer, serverTask);
            }
        }
    }

    [Theory]
    [InlineData("host")]
    [InlineData("farmhand")]
    public async Task ToolReturnsOnlyTheRoleFixedAtNetworkServerStartup(string role)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, role);
        byte[] png = PngTestData.CreateRgba8(1, 1);
        ReviewScreenshotCaptureQuery? observedQuery = null;
        (McpClient client, McpServer server, Pipe clientToServer, CancellationTokenSource timeout, Task serverTask) =
            await StartAsync(
                reader,
                (query, _) =>
                {
                    observedQuery = query;
                    return new ProjectReviewScreenshotResult(
                        Capture(query.Mode, query.Label, png),
                        []);
                });
        await using (client)
        await using (server)
        using (timeout)
        {
            try
            {
                CallToolResult result = await client.CallToolAsync(
                    ProjectReviewMcpScreenshotTools.CaptureToolName,
                    new Dictionary<string, object?>
                    {
                        ["mode"] = "map",
                        ["label"] = $"{role}_proof",
                    },
                    cancellationToken: timeout.Token);

                Assert.NotEqual(true, result.IsError);
                Assert.Equal("map", observedQuery?.Mode);
                Assert.Equal($"{role}_proof", observedQuery?.Label);
                JsonElement metadata = Assert.IsType<JsonElement>(result.StructuredContent);
                Assert.Equal("network-2", metadata.GetProperty("topology").GetString());
                Assert.Equal(role, metadata.GetProperty("role").GetString());
                Assert.DoesNotContain(
                    role == "host" ? "farmhand" : "host",
                    metadata.GetRawText(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.IsType<ImageContentBlock>(result.Content[1]);
            }
            finally
            {
                await StopAsync(client, clientToServer, serverTask);
            }
        }
    }

    [Fact]
    public async Task ToolFailsClosedWhenLaunchBindingChangesDuringCapture()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        (McpClient client, McpServer server, Pipe clientToServer, CancellationTokenSource timeout, Task serverTask) =
            await StartAsync(
                reader,
                (query, _) =>
                {
                    LiveLabState state = Assert.IsType<LiveLabState>(
                        new JsonLiveLabStateStore(paths.StatePath).Read());
                    new JsonLiveLabStateStore(paths.StatePath).Write(
                        state with { LaunchId = Guid.NewGuid().ToString("N") });
                    return new ProjectReviewScreenshotResult(
                        Capture(
                            query.Mode,
                            query.Label,
                            PngTestData.CreateRgba8(1, 1)),
                        []);
                });
        await using (client)
        await using (server)
        using (timeout)
        {
            try
            {
                CallToolResult result = await client.CallToolAsync(
                    ProjectReviewMcpScreenshotTools.CaptureToolName,
                    new Dictionary<string, object?>
                    {
                        ["mode"] = "viewport",
                        ["label"] = "binding",
                    },
                    cancellationToken: timeout.Token);

                Assert.True(result.IsError);
                TextContentBlock error = Assert.IsType<TextContentBlock>(
                    Assert.Single(result.Content));
                Assert.Contains("reviewBindingChanged", error.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(temporary.Path, error.Text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await StopAsync(client, clientToServer, serverTask);
            }
        }
    }

    private static ProjectReviewScreenshotCapture Capture(
        string mode,
        string label,
        byte[] png) => new(
            mode,
            label,
            ReviewScreenshotContract.FileName(label),
            new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
            2,
            1,
            png.Length,
            "sha256:" + new string('a', 64),
            png);

    private static async Task<(
        McpClient Client,
        McpServer Server,
        Pipe ClientToServer,
        CancellationTokenSource Timeout,
        Task ServerTask)> StartAsync(
        ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpScreenshotRunner runCapture)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "sdvkit-screenshot-test");
        McpServer server = McpServer.Create(
            serverTransport,
            ProjectReviewMcpServer.CreateOptions(
                reader,
                runData: null,
                runCapture));
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = server.RunAsync(timeout.Token);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream());
        McpClient client = await McpClient.CreateAsync(
            clientTransport,
            cancellationToken: timeout.Token);
        return (client, server, clientToServer, timeout, serverTask);
    }

    private static async Task StopAsync(
        McpClient client,
        Pipe clientToServer,
        Task serverTask)
    {
        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
