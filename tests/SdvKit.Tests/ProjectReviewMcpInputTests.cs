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
public sealed class ProjectReviewMcpInputTests
{
    [Fact]
    public void DefaultDiscoveryHasNoActionToolsAndOptInAddsExactlyFour()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        ProjectReviewMcpInputSession session = CreateSession(
            temporary,
            reader,
            (query, token) => SuccessfulResponse(
                LiveLabPaths.Resolve(temporary.Path).RuntimePath,
                query,
                token));

        McpServerOptions defaultOptions = ProjectReviewMcpServer.CreateOptions(reader);
        McpServerOptions optedInOptions = ProjectReviewMcpServer.CreateOptions(
            reader,
            runData: null,
            inputSession: session);

        string[] defaultNames = defaultOptions.ToolCollection!
            .Select(tool => tool.ProtocolTool.Name)
            .ToArray();
        string[] optedInNames = optedInOptions.ToolCollection!
            .Select(tool => tool.ProtocolTool.Name)
            .ToArray();
        Assert.DoesNotContain(defaultNames, name => name.StartsWith(
            "stardew_input_",
            StringComparison.Ordinal));
        Assert.Equal(
            [
                ProjectReviewMcpInputTools.CursorClearToolName,
                ProjectReviewMcpInputTools.CursorSetToolName,
                ProjectReviewMcpInputTools.PressToolName,
                ProjectReviewMcpInputTools.WheelToolName,
            ],
            optedInNames
                .Except(defaultNames, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task OfficialClientListsClosedRoleFixedToolsAndCallsEveryAction()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var queries = new List<ReviewInputQuery>();
        ProjectReviewMcpInputSession session = CreateSession(
            temporary,
            reader,
            (query, _) =>
            {
                queries.Add(query);
                return SuccessfulResponse(
                    LiveLabPaths.Resolve(temporary.Path).RuntimePath,
                    query,
                    CancellationToken.None);
            });
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        McpServerOptions options = ProjectReviewMcpServer.CreateOptions(
            reader,
            runData: null,
            inputSession: session);
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "sdvkit-input-test");
        await using McpServer server = McpServer.Create(serverTransport, options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = server.RunAsync(timeout.Token);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream());
        await using McpClient client = await McpClient.CreateAsync(
            clientTransport,
            cancellationToken: timeout.Token);

        ListToolsResult listed = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            timeout.Token);
        Tool[] inputTools = listed.Tools
            .Where(tool => tool.Name.StartsWith("stardew_input_", StringComparison.Ordinal))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(4, inputTools.Length);
        foreach (Tool tool in inputTools)
        {
            Assert.False(tool.Annotations?.ReadOnlyHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            if (tool.InputSchema.TryGetProperty("properties", out JsonElement properties))
            {
                Assert.False(properties.TryGetProperty("role", out _));
            }
            else
            {
                Assert.Equal(
                    ProjectReviewMcpInputTools.CursorClearToolName,
                    tool.Name);
            }
            JsonElement output = Assert.IsType<JsonElement>(tool.OutputSchema);
            Assert.False(output.GetProperty("additionalProperties").GetBoolean());
            Assert.False(output.GetProperty("properties")
                .GetProperty("problem")
                .GetProperty("additionalProperties")
                .GetBoolean());
        }

        CallToolResult press = await client.CallToolAsync(
            ProjectReviewMcpInputTools.PressToolName,
            new Dictionary<string, object?> { ["button"] = "MouseLeft" },
            cancellationToken: timeout.Token);
        CallToolResult set = await client.CallToolAsync(
            ProjectReviewMcpInputTools.CursorSetToolName,
            new Dictionary<string, object?> { ["x"] = 200, ["y"] = 100 },
            cancellationToken: timeout.Token);
        CallToolResult wheel = await client.CallToolAsync(
            ProjectReviewMcpInputTools.WheelToolName,
            new Dictionary<string, object?> { ["direction"] = "down" },
            cancellationToken: timeout.Token);
        CallToolResult clear = await client.CallToolAsync(
            new CallToolRequestParams
            {
                Name = ProjectReviewMcpInputTools.CursorClearToolName,
            },
            timeout.Token);

        Assert.All([press, set, wheel, clear], result => Assert.NotEqual(true, result.IsError));
        Assert.Equal(
            [
                ReviewInputContract.PressAction,
                ReviewInputContract.CursorSetAction,
                ReviewInputContract.WheelAction,
                ReviewInputContract.CursorClearAction,
            ],
            queries.Select(query => query.Action).ToArray());
        JsonElement pressAck = Assert.IsType<JsonElement>(press.StructuredContent);
        Assert.Equal(JsonValueKind.Null, pressAck.GetProperty("role").ValueKind);
        Assert.Equal("MouseLeft", pressAck.GetProperty("button").GetString());
        Assert.False(pressAck.GetProperty("cancellationRequested").GetBoolean());
        Assert.DoesNotContain("Path", pressAck.GetRawText(), StringComparison.OrdinalIgnoreCase);

        int beforeInvalid = queries.Count;
        CallToolResult invalid = await client.CallToolAsync(
            ProjectReviewMcpInputTools.PressToolName,
            new Dictionary<string, object?>
            {
                ["button"] = "MouseLeft",
                ["role"] = "host",
            },
            cancellationToken: timeout.Token);
        Assert.True(invalid.IsError);
        Assert.Equal(beforeInvalid, queries.Count);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole)]
    [InlineData(NetworkTwoContract.FarmhandRole)]
    public void AcknowledgementRemainsBoundToTheServerRole(string role)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, role);
        string runtimePath = LiveLabPaths.ResolveNetworkRole(
            LiveLabPaths.Resolve(temporary.Path),
            role).RuntimePath;
        var session = new ProjectReviewMcpInputSession(
            reader,
            runtimePath,
            (query, token) => SuccessfulResponse(runtimePath, query, token));

        ProjectReviewMcpInputInvocation result = session.Execute(
            new ReviewInputQuery(
                ReviewInputContract.PressAction,
                "F8",
                null,
                null,
                null),
            CancellationToken.None);

        Assert.Equal(role, result.Acknowledgement?.Role);
        Assert.Equal(NetworkTwoContract.Topology, result.Acknowledgement?.Topology);
    }

    [Fact]
    public void ParallelActionIsRejectedInsteadOfQueued()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        string runtimePath = LiveLabPaths.Resolve(temporary.Path).RuntimePath;
        var calls = 0;
        var session = new ProjectReviewMcpInputSession(
            reader,
            runtimePath,
            (query, token) =>
            {
                calls++;
                return SuccessfulResponse(runtimePath, query, token);
            });
        using ProjectReviewActionLock held = Assert.IsType<ProjectReviewActionLock>(
            ProjectReviewActionLock.TryAcquire(runtimePath));

        ProjectReviewMcpInputInvocation result = session.Execute(
            CursorSetQuery(),
            CancellationToken.None);

        Assert.Null(result.Acknowledgement);
        Assert.Equal("inputBusy", result.Problem?.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PreCanceledActionDoesNotDispatch()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var calls = 0;
        ProjectReviewMcpInputSession session = CreateSession(
            temporary,
            reader,
            (query, token) =>
            {
                calls++;
                return SuccessfulResponse(
                    LiveLabPaths.Resolve(temporary.Path).RuntimePath,
                    query,
                    token);
            });
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        ProjectReviewMcpInputInvocation result = session.Execute(
            CursorSetQuery(),
            canceled.Token);

        Assert.Equal("inputRequestCanceled", result.Problem?.Code);
        Assert.False(result.ActionMayHaveRun);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PostDispatchCancellationRetainsAcknowledgementAndEofCleanupClearsInput()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var queries = new List<ReviewInputQuery>();
        ProjectReviewMcpInputSession session = CreateSession(
            temporary,
            reader,
            (query, token) =>
            {
                queries.Add(query);
                return queries.Count == 1
                    ? new ProjectReviewInputExecutionResult(
                        SuccessfulResponse(
                            LiveLabPaths.Resolve(temporary.Path).RuntimePath,
                            query,
                            token).Response,
                        [new ReviewInputProblem(
                            "inputRequestCanceled",
                            "The request was canceled after dispatch.")],
                        ActionMayHaveRun: true,
                        CancellationRequested: true)
                    : SuccessfulResponse(
                        LiveLabPaths.Resolve(temporary.Path).RuntimePath,
                        query,
                        token);
            });

        ProjectReviewMcpInputInvocation action = session.Execute(
            CursorSetQuery(),
            CancellationToken.None);
        ReviewInputProblem? cleanup = session.Cleanup();

        Assert.True(action.ActionMayHaveRun);
        Assert.NotNull(action.Acknowledgement);
        Assert.True(action.Acknowledgement.CancellationRequested);
        Assert.True(action.Acknowledgement.Succeeded);
        Assert.Equal("inputRequestCanceled", action.Problem?.Code);
        Assert.Null(cleanup);
        Assert.Equal(2, queries.Count);
        Assert.Equal(ReviewInputContract.CursorSetAction, queries[0].Action);
        Assert.Equal(ReviewInputContract.CursorClearAction, queries[1].Action);
    }

    [Fact]
    public void PostActionBindingInvalidationFailsClosedWithoutRetry()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        var calls = 0;
        ProjectReviewMcpInputSession session = CreateSession(
            temporary,
            reader,
            (query, token) =>
            {
                calls++;
                File.Delete(paths.StatusPath);
                return ResponseWithoutStatusAdvance(query);
            },
            postActionTimeout: TimeSpan.Zero);

        ProjectReviewMcpInputInvocation result = session.Execute(
            CursorSetQuery(),
            CancellationToken.None);

        Assert.Null(result.Acknowledgement);
        Assert.True(result.ActionMayHaveRun);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ActionRequiresPublishedForegroundIdentityBeforeDispatch()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        AlwaysOnStatusMarker marker = ReadStatus(paths.StatusPath);
        WriteStatus(paths.StatusPath, marker with
        {
            ForegroundWindowHandle = null,
            ForegroundProcessId = null,
        });
        var calls = 0;
        ProjectReviewMcpInputSession session = CreateSession(
            temporary,
            reader,
            (query, token) =>
            {
                calls++;
                return ResponseWithoutStatusAdvance(query);
            });

        ProjectReviewMcpInputInvocation result = session.Execute(
            CursorSetQuery(),
            CancellationToken.None);

        Assert.Null(result.Acknowledgement);
        Assert.False(result.ActionMayHaveRun);
        Assert.Equal("inputForegroundUnavailable", result.Problem?.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AcknowledgementRequiresANewerAlwaysOnStatusAndTick()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        ProjectReviewMcpInputSession session = CreateSession(
            temporary,
            reader,
            (query, token) => ResponseWithoutStatusAdvance(query),
            postActionTimeout: TimeSpan.Zero);

        ProjectReviewMcpInputInvocation result = session.Execute(
            CursorSetQuery(),
            CancellationToken.None);

        Assert.Null(result.Acknowledgement);
        Assert.True(result.ActionMayHaveRun);
        Assert.Equal("inputPostStateTimedOut", result.Problem?.Code);
    }

    [Fact]
    public void NewerStatusWithChangedForegroundBindingFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        string runtimePath = LiveLabPaths.Resolve(temporary.Path).RuntimePath;
        ProjectReviewMcpInputSession session = CreateSession(
            temporary,
            reader,
            (query, token) =>
            {
                string statusPath = Path.Combine(runtimePath, "always-on-status.json");
                AlwaysOnStatusMarker marker = ReadStatus(statusPath);
                DateTimeOffset acknowledgedAt = marker.ObservedAtUtc.AddMilliseconds(1);
                DateTimeOffset confirmedAt = marker.ObservedAtUtc.AddMilliseconds(2);
                WriteStatus(statusPath, marker with
                {
                    Tick = marker.Tick + 1,
                    ObservedAtUtc = confirmedAt,
                    ForegroundWindowHandle = marker.ForegroundWindowHandle + 1,
                    Runtime = marker.Runtime! with { ObservedAtUtc = confirmedAt },
                });
                return AcknowledgedResponse(query, acknowledgedAt, marker.Tick);
            });

        ProjectReviewMcpInputInvocation result = session.Execute(
            CursorSetQuery(),
            CancellationToken.None);

        Assert.Null(result.Acknowledgement);
        Assert.True(result.ActionMayHaveRun);
        Assert.Equal("inputBindingChanged", result.Problem?.Code);
    }

    [Fact]
    public void FailedEofCleanupReturnsANonzeroExitCode()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        string runtimePath = LiveLabPaths.Resolve(temporary.Path).RuntimePath;
        var calls = 0;
        var session = new ProjectReviewMcpInputSession(
            reader,
            runtimePath,
            (query, token) => ++calls == 1
                ? SuccessfulResponse(runtimePath, query, token)
                : new ProjectReviewInputExecutionResult(
                    null,
                    [new ReviewInputProblem(
                        "inputCleanupTransportFailed",
                        "The cleanup acknowledgement was not produced.")],
                    ActionMayHaveRun: true,
                    CancellationRequested: false));
        Assert.NotNull(session.Execute(CursorSetQuery(), CancellationToken.None).Acknowledgement);
        using var error = new StringWriter();

        int exitCode = ProjectReviewMcpServer.CompleteInputCleanup(session, error);

        Assert.Equal(3, exitCode);
        Assert.Contains("inputCleanupTransportFailed", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, calls);
    }

    private static ProjectReviewMcpInputSession CreateSession(
        TemporaryDirectory temporary,
        ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpInputRunner runner,
        TimeSpan? postActionTimeout = null) =>
        new(
            reader,
            LiveLabPaths.Resolve(temporary.Path).RuntimePath,
            runner,
            delay: _ => { },
            postActionTimeout: postActionTimeout);

    private static ReviewInputQuery CursorSetQuery() => new(
        ReviewInputContract.CursorSetAction,
        null,
        null,
        20,
        30);

    private static ProjectReviewInputExecutionResult SuccessfulResponse(
        string runtimePath,
        ReviewInputQuery query,
        CancellationToken _)
    {
        string statusPath = Path.Combine(runtimePath, "always-on-status.json");
        AlwaysOnStatusMarker marker = ReadStatus(statusPath);
        DateTimeOffset acknowledgedAt = marker.ObservedAtUtc.AddMilliseconds(1);
        int acknowledgedTick = marker.Tick;
        DateTimeOffset confirmedAt = marker.ObservedAtUtc.AddMilliseconds(2);
        WriteStatus(statusPath, marker with
        {
            Tick = marker.Tick + 1,
            ObservedAtUtc = confirmedAt,
            Runtime = marker.Runtime! with { ObservedAtUtc = confirmedAt },
        });
        return AcknowledgedResponse(query, acknowledgedAt, acknowledgedTick);
    }

    private static ProjectReviewInputExecutionResult AcknowledgedResponse(
        ReviewInputQuery query,
        DateTimeOffset acknowledgedAt,
        int acknowledgedTick) =>
        new(
            new ReviewInputResponseEnvelope(
                ReviewInputContract.SchemaVersion,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                acknowledgedAt,
                acknowledgedTick,
                query.Action,
                true,
                query.Button,
                query.Direction,
                query.X,
                query.Y,
                !string.Equals(
                    query.Action,
                    ReviewInputContract.CursorClearAction,
                    StringComparison.Ordinal),
                true,
                null),
            [],
            ActionMayHaveRun: true,
            CancellationRequested: false);

    private static ProjectReviewInputExecutionResult ResponseWithoutStatusAdvance(
        ReviewInputQuery query) =>
        new(
            new ReviewInputResponseEnvelope(
                ReviewInputContract.SchemaVersion,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                DateTimeOffset.UtcNow,
                600,
                query.Action,
                true,
                query.Button,
                query.Direction,
                query.X,
                query.Y,
                !string.Equals(
                    query.Action,
                    ReviewInputContract.CursorClearAction,
                    StringComparison.Ordinal),
                true,
                null),
            [],
            ActionMayHaveRun: true,
            CancellationRequested: false);

    private static AlwaysOnStatusMarker ReadStatus(string statusPath) =>
        JsonSerializer.Deserialize<AlwaysOnStatusMarker>(
            File.ReadAllText(statusPath),
            LiveLabJsonOptions.CamelCase)
        ?? throw new InvalidDataException("The test status marker is invalid.");

    private static void WriteStatus(string statusPath, AlwaysOnStatusMarker marker) =>
        File.WriteAllText(
            statusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
}
