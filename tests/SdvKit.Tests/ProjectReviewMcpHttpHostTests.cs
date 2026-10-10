using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli.Mcp;
using SdvKit.GameSide;

namespace SdvKit.Tests;

public sealed class ProjectReviewMcpHttpHostTests
{
    [Fact]
    public async Task AuthorizedClientListsAndCallsToolsOverHttp()
    {
        string token = ReviewHttpContract.CreateToken();
        await using ProjectReviewMcpHttpHost host = await ProjectReviewMcpHttpHost.StartAsync(Options(), token, 0);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(host.Url),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        });
        await using McpClient client = await McpClient.CreateAsync(transport);

        IList<McpClientTool> tools = await client.ListToolsAsync();
        CallToolResult result = await client.CallToolAsync("echo", new Dictionary<string, object?>());

        Assert.Equal("echo", Assert.Single(tools).Name);
        Assert.Equal("pong", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.StartsWith("http://127.0.0.1:", host.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, null, HttpStatusCode.Unauthorized)]
    [InlineData(true, "Origin", "http://example.test", HttpStatusCode.Forbidden)]
    [InlineData(true, "Host", "localhost", HttpStatusCode.Forbidden)]
    public async Task RequestsWithoutTokenOrFromBrowsersAreRejected(
        bool withToken,
        string? header,
        string? value,
        HttpStatusCode expected)
    {
        string token = ReviewHttpContract.CreateToken();
        await using ProjectReviewMcpHttpHost host = await ProjectReviewMcpHttpHost.StartAsync(Options(), token, 0);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, host.Url)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (withToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (header == "Host")
        {
            request.Headers.Host = value;
        }
        else if (header is not null)
        {
            request.Headers.Add(header, value);
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public void ConnectionFileIsWrittenBesideTheSelectedRoleRuntime()
    {
        using TemporaryDirectory temporary = new();
        string single = ProjectReviewMcpHttpHost.ConnectionPath(temporary.Path, "single", null, null);
        string screen = ProjectReviewMcpHttpHost.ConnectionPath(temporary.Path, "single", null, 1);
        string host = ProjectReviewMcpHttpHost.ConnectionPath(temporary.Path, "network-2", "host", null);

        Assert.EndsWith("mcp-http.json", single, StringComparison.Ordinal);
        Assert.EndsWith("mcp-http-screen-1.json", screen, StringComparison.Ordinal);
        Assert.NotEqual(Path.GetDirectoryName(single), Path.GetDirectoryName(host));

        Directory.CreateDirectory(Path.GetDirectoryName(single)!);
        Assert.True(ProjectReviewMcpHttpHost.TryClaimConnection(single, new ProjectReviewMcpHttpConnection(
            1, "http://127.0.0.1:1/mcp", "t", "l", "single", null, null, 7), out _));
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(single)))
        {
            Assert.Equal("http://127.0.0.1:1/mcp", document.RootElement.GetProperty("url").GetString());
        }

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(single)!, "*.tmp"));
        ProjectReviewMcpHttpHost.ReleaseConnection(single, processId: 8);
        Assert.True(File.Exists(single));
        ProjectReviewMcpHttpHost.ReleaseConnection(single, processId: 7);
        Assert.False(File.Exists(single));
    }

    [Fact]
    public void SecondLiveServerForTheSameLaunchCannotClaimTheConnection()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "mcp-http.json");
        int live = Environment.ProcessId;
        Assert.True(ProjectReviewMcpHttpHost.TryClaimConnection(path, new ProjectReviewMcpHttpConnection(
            1, "http://127.0.0.1:1/mcp", "t", "launch", "single", null, null, live), out _));

        Assert.False(ProjectReviewMcpHttpHost.TryClaimConnection(path, new ProjectReviewMcpHttpConnection(
            1, "http://127.0.0.1:2/mcp", "u", "launch", "single", null, null, live + 1), out string? owner));
        Assert.Equal("http://127.0.0.1:1/mcp", owner);
        Assert.True(ProjectReviewMcpHttpHost.TryClaimConnection(path, new ProjectReviewMcpHttpConnection(
            1, "http://127.0.0.1:3/mcp", "v", "other-launch", "single", null, null, live + 1), out _));
    }

    [Fact]
    public void ServerIsUnboundWithoutAnOwnedLabState()
    {
        using TemporaryDirectory temporary = new();
        Assert.Equal(
            ProjectReviewMcpBinding.Ended,
            ProjectReviewMcpHttpHost.CheckReview(temporary.Path, "single", null, Guid.NewGuid().ToString("N")));
    }

    private static McpServerOptions Options() => new()
    {
        ServerInfo = new Implementation { Name = "test", Version = "1.0.0" },
        ToolCollection = [new EchoTool()],
    };

    private sealed class EchoTool : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = "echo",
            InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
        };

        public override IReadOnlyList<object> Metadata { get; } = [];

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "pong" }] });
    }
}
