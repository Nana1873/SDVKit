using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SdvKit.Cli.LiveLab;
using SdvKit.GameSide;

namespace SdvKit.Cli.Mcp;

internal sealed record ProjectReviewMcpHttpConnection(
    int SchemaVersion,
    string Url,
    string Token,
    string LaunchId,
    string Topology,
    string? Role,
    int? ScreenId,
    int ProcessId);

/// <summary>
/// Serves the same review MCP tools over Streamable HTTP on loopback. Every request needs the
/// bearer token from the connection file, the exact <c>127.0.0.1:port</c> host and no browser
/// <c>Origin</c> header.
/// </summary>
internal sealed class ProjectReviewMcpHttpHost : IAsyncDisposable
{
    public const string EndpointPath = "/mcp";
    private const long MaximumRequestBodyBytes = 1024 * 1024;

    private readonly WebApplication _application;

    private ProjectReviewMcpHttpHost(WebApplication application, int port)
    {
        _application = application;
        Port = port;
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}{EndpointPath}";

    public static async Task<ProjectReviewMcpHttpHost> StartAsync(
        McpServerOptions options,
        string token,
        int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ReviewHttpContract.IsToken(token))
        {
            throw new ArgumentException("The MCP bearer token is invalid.", nameof(token));
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
            kestrel.Listen(IPAddress.Loopback, port);
        });
        builder.Services
            .AddMcpServer(server =>
            {
                server.ServerInfo = options.ServerInfo;
                server.ServerInstructions = options.ServerInstructions;
                server.ToolCollection = options.ToolCollection;
            })
            // Stateless Streamable HTTP, the SDK default: every request carries its own context, and
            // the tools keep no per-session state beyond this process's startup binding.
            .WithHttpTransport();

        WebApplication application = builder.Build();
        byte[] expectedAuthorization = Encoding.ASCII.GetBytes($"Bearer {token}");
        application.Use(async (context, next) =>
        {
            string authority = $"127.0.0.1:{context.Connection.LocalPort}";
            if (!string.Equals(context.Request.Host.Value, authority, StringComparison.Ordinal)
                || context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            byte[] presented = Encoding.ASCII.GetBytes(context.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(presented, expectedAuthorization))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"SDVKit\"";
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        application.MapMcp(EndpointPath);

        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        string address = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new ProjectReviewMcpHttpHost(application, new Uri(address).Port);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _application.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Connection file in the selected role's owned runtime directory.</summary>
    public static string ConnectionPath(string projectRoot, string topology, string? role, int? screenId) =>
        Path.Combine(
            ProjectReviewInputService.RuntimePath(projectRoot, topology, role),
            screenId is int screen ? $"mcp-http-screen-{screen}.json" : "mcp-http.json");

    public static void WriteConnection(string path, ProjectReviewMcpHttpConnection connection)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(connection, LiveLabJsonOptions.CamelCase));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// True while the owned lab state still names the startup launch and its exact process. The
    /// HTTP server never outlives the review it is bound to.
    /// </summary>
    public static bool ReviewStillBound(string projectRoot, string topology, string? role, string launchId)
    {
        try
        {
            LiveLabPaths single = LiveLabPaths.Resolve(projectRoot);
            LiveLabPaths paths = role is null ? single : LiveLabPaths.ResolveNetworkRole(single, role);
            LiveLabState? state = new JsonLiveLabStateStore(paths.StatePath).Read();
            if (state is null || !string.Equals(state.LaunchId, launchId, StringComparison.Ordinal))
            {
                return false;
            }

            using var process = System.Diagnostics.Process.GetProcessById(state.OwnedProcessIdentity.ProcessId);
            return !process.HasExited
                && process.StartTime.ToUniversalTime() == state.OwnedProcessIdentity.StartTimeUtc.UtcDateTime;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or JsonException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
