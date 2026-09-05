using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SdvKit.Tests;

internal sealed class McpTestClient : IAsyncDisposable
{
    private readonly Pipe _clientToServer;
    private readonly StreamServerTransport _transport;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _timeout;
    private readonly Task _serverTask;

    private McpTestClient(
        Pipe clientToServer,
        StreamServerTransport transport,
        McpServer server,
        CancellationTokenSource timeout,
        Task serverTask,
        McpClient client)
    {
        _clientToServer = clientToServer;
        _transport = transport;
        _server = server;
        _timeout = timeout;
        _serverTask = serverTask;
        Client = client;
    }

    public McpClient Client { get; }

    public CancellationToken Token => _timeout.Token;

    public static async Task<McpTestClient> StartAsync(
        McpServerOptions options)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var transport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "sdvkit-test");
        McpServer server = McpServer.Create(
            transport,
            options);
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = server.RunAsync(timeout.Token);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream());
        McpClient client = await McpClient.CreateAsync(
            clientTransport,
            cancellationToken: timeout.Token);
        return new McpTestClient(
            clientToServer,
            transport,
            server,
            timeout,
            serverTask,
            client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _clientToServer.Writer.CompleteAsync();
        await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        await _server.DisposeAsync();
        await _transport.DisposeAsync();
        _timeout.Dispose();
    }
}
