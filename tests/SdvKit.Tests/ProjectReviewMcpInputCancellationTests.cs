using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ProjectReviewMcpInputCancellationTests
{
    [Fact]
    public async Task CleanupCancelsPendingInputAndWaitsForReleaseBeforeClearing()
    {
        using TemporaryDirectory temporary = new();
        var entered = Signal();
        var canceled = Signal();
        using var release = new ManualResetEventSlim();
        var calls = new List<string>();
        ProjectReviewMcpInputSession session = CreateSession(temporary, (query, token) =>
        {
            calls.Add(query.Action);
            if (query.Action != ReviewInputContract.CursorClearAction)
            {
                using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None));
                calls.Add("released");
            }
            return Acknowledge(temporary, query);
        });

        Task<ProjectReviewMcpInputInvocation> action = Task.Run(() => session.Execute(Query(), CancellationToken.None));
        Task<ReviewInputProblem?>? cleanup = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cleanup = Task.Run(session.Cleanup);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(cleanup.IsCompleted);
            Assert.Equal("inputSessionStopping", session.Execute(Query(), CancellationToken.None).Problem?.Code);
            release.Set();

            ProjectReviewMcpInputInvocation result = await action.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Acknowledgement!.CancellationRequested);
            Assert.Null(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal([ReviewInputContract.CursorSetAction, "released", ReviewInputContract.CursorClearAction], calls);
        }
        finally
        {
            release.Set();
            await action.WaitAsync(TimeSpan.FromSeconds(10));
            if (cleanup is not null)
            {
                await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public void CancellationDeliveryFailureSurvivesTheMcpAcknowledgement()
    {
        using TemporaryDirectory temporary = new();
        using var cancellation = new CancellationTokenSource();
        ProjectReviewMcpInputSession session = CreateSession(temporary, (query, _) =>
        {
            cancellation.Cancel();
            return Acknowledge(temporary, query) with
            {
                CancellationRequested = true,
                Problems = [new("inputCancellationNotConfirmed", "Cancellation delivery failed after dispatch.")],
            };
        });

        ProjectReviewMcpInputInvocation result = session.Execute(Query(), cancellation.Token);
        Assert.Equal("inputCancellationNotConfirmed", result.Problem?.Code);
        Assert.True(result.ActionMayHaveRun);
        Assert.True(result.Acknowledgement!.CancellationRequested);
    }

    [Fact]
    public void ExceptionAfterDispatchStillRequiresCleanup()
    {
        using TemporaryDirectory temporary = new();
        int calls = 0;
        ProjectReviewMcpInputSession session = CreateSession(temporary, (query, _) =>
        {
            calls++;
            if (query.Action != ReviewInputContract.CursorClearAction)
            {
                throw new InvalidOperationException("Failure after possible delivery.");
            }
            return Acknowledge(temporary, query);
        });

        Assert.Throws<InvalidOperationException>(() => session.Execute(Query(), CancellationToken.None));
        Assert.Null(session.Cleanup());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnfinishedCancellationFailsCleanupWithoutDispatchingAnotherAction()
    {
        using TemporaryDirectory temporary = new();
        var entered = Signal();
        var canceled = Signal();
        using var release = new ManualResetEventSlim();
        int clearCalls = 0;
        ProjectReviewMcpInputSession session = CreateSession(temporary, (query, token) =>
        {
            if (query.Action == ReviewInputContract.CursorClearAction)
            {
                clearCalls++;
            }
            else
            {
                using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None));
            }
            return Acknowledge(temporary, query);
        }, cleanupTimeout: TimeSpan.Zero);

        Task<ProjectReviewMcpInputInvocation> action = Task.Run(() => session.Execute(Query(), CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("inputCleanupTimedOut", session.Cleanup()?.Code);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, clearCalls);
            Assert.Equal("inputSessionStopping", session.Execute(Query(), CancellationToken.None).Problem?.Code);
        }
        finally
        {
            release.Set();
            await action.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Null(session.Cleanup());
        Assert.Equal(1, clearCalls);
    }

    [Fact]
    public async Task TransportEofCancelsAnInFlightHandlerBeforeServerDisposal()
    {
        using TemporaryDirectory temporary = new();
        var entered = Signal();
        var canceled = Signal();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        var session = new ProjectReviewMcpInputSession(reader,
            LiveLabPaths.Resolve(temporary.Path).RuntimePath, (query, token) =>
            {
                if (query.Action != ReviewInputContract.CursorClearAction)
                {
                    entered.TrySetResult();
                    Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
                    canceled.TrySetResult();
                }
                return Acknowledge(temporary, query);
            });
        var input = new Pipe();
        var output = new Pipe();
        await using var writer = new StreamWriter(input.Writer.AsStream()) { AutoFlush = true };
        using var responses = new StreamReader(output.Reader.AsStream());
        await using var transport = new StreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream(), "input-eof");
        await using McpServer server = McpServer.Create(transport,
            ProjectReviewMcpServer.CreateOptions(reader, inputSession: session));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task running = ProjectReviewMcpServer.RunUntilDisconnectAsync(server, transport, session, timeout.Token);

        try
        {
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"input-eof-test","version":"1"}}}""");
            string? initialized = await responses.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(initialized);
            using (JsonDocument response = JsonDocument.Parse(initialized))
            {
                Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
                Assert.True(response.RootElement.TryGetProperty("result", out _));
            }
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"stardew_input_cursor_set","arguments":{"x":20,"y":30}}}""");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await writer.DisposeAsync();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(session.Cleanup());
        }
        finally
        {
            timeout.Cancel();
            session.CancelPending();
        }
    }

    [Fact]
    public async Task RequestNotificationCancelsAnInFlightHandlerWithoutEof()
    {
        using TemporaryDirectory temporary = new();
        var entered = Signal();
        var canceled = Signal();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        var session = new ProjectReviewMcpInputSession(reader,
            LiveLabPaths.Resolve(temporary.Path).RuntimePath, (query, token) =>
            {
                if (query.Action != ReviewInputContract.CursorClearAction)
                {
                    entered.TrySetResult();
                    Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));
                    canceled.TrySetResult();
                }
                return Acknowledge(temporary, query);
            });
        var input = new Pipe();
        var output = new Pipe();
        await using var writer = new StreamWriter(input.Writer.AsStream()) { AutoFlush = true };
        using var responses = new StreamReader(output.Reader.AsStream());
        await using var transport = new StreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream(), "input-eof");
        await using McpServer server = McpServer.Create(transport,
            ProjectReviewMcpServer.CreateOptions(reader, inputSession: session));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task running = ProjectReviewMcpServer.RunUntilDisconnectAsync(server, transport, session, timeout.Token);

        try
        {
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"input-eof-test","version":"1"}}}""");
            string? initialized = await responses.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(initialized);
            using (JsonDocument response = JsonDocument.Parse(initialized))
            {
                Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
                Assert.True(response.RootElement.TryGetProperty("result", out _));
            }
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"stardew_input_cursor_set","arguments":{"x":20,"y":30}}}""");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2,"reason":"Bounded cancellation regression"}}""");
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await writer.DisposeAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(session.Cleanup());
        }
        finally
        {
            timeout.Cancel();
            session.CancelPending();
        }
    }

    [Fact]
    public void ConcurrentLabOperationPreventsTheExactCancellationCommandFromBeingWritten()
    {
        using TemporaryDirectory temporary = new();
        using LiveLabOperationLock held = LiveLabOperationLock.TryAcquire(temporary.Path)!;
        LiveLabCommandResult result = ProjectReviewService.ExecuteCommand(
            "sdvkit input cancel aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", LiveLabState.SingleTopology, null, temporary.Path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(Assert.IsType<ProjectReviewCommandReport>(result.Report).Problems, problem => problem.Code == "labBusy");
    }
    [Fact]
    public void CancellationDispatchPolicyWaitsOnlyAfterKnownUnwrittenContention()
    {
        using TemporaryDirectory temporary = new();
        using LiveLabOperationLock held = LiveLabOperationLock.TryAcquire(temporary.Path)!;
        int calls = 0;
        int waits = 0;
        ProjectReviewInputService.DispatchCancellation(() =>
        {
            calls++;
            return calls == 1
                ? ProjectReviewService.ExecuteCommand("sdvkit input cancel aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", LiveLabState.SingleTopology, null, temporary.Path)
                : new LiveLabCommandResult(0, new object());
        }, _ => waits++);
        Assert.Equal(2, calls);
        Assert.Equal(1, waits);
    }

    [Theory]
    [InlineData(null, "deliveryFailed")]
    [InlineData(null, "labBusy")]
    [InlineData(true, "deliveryFailed")]
    [InlineData(true, "labBusy")]
    [InlineData(false, "deliveryFailed")]
    public void CancellationNeverRepeatsUncertainOrNonContentionFailures(bool? written, string code)
    {
        int calls = 0;
        Assert.Throws<IOException>(() => ProjectReviewInputService.DispatchCancellation(() =>
        {
            calls++;
            return new LiveLabCommandResult(1, new ProjectReviewCommandReport(1, null, "lab", "blocked", null,
                written, [new(code, null, "Unconfirmed delivery")], []));
        }, _ => Assert.Fail("No wait is allowed after unconfirmed delivery.")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void PersistentContentionHasABoundedCancellationWait()
    {
        using TemporaryDirectory temporary = new();
        using LiveLabOperationLock held = LiveLabOperationLock.TryAcquire(temporary.Path)!;
        int waits = 0;
        LiveLabCommandResult busy = ProjectReviewService.ExecuteCommand(
            "sdvkit input cancel aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", LiveLabState.SingleTopology, null, temporary.Path);
        Assert.Throws<IOException>(() => ProjectReviewInputService.DispatchCancellation(() => busy, duration =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(50), duration);
            waits++;
        }));
        Assert.Equal(20, waits);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ReviewInputQuery Query() => new(ReviewInputContract.CursorSetAction, null, null, 20, 30);

    private static ProjectReviewMcpInputSession CreateSession(TemporaryDirectory temporary,
        ProjectReviewMcpInputRunner run, TimeSpan? cleanupTimeout = null) =>
        new(ProjectReviewMcpTests.CreateReadyReview(temporary), LiveLabPaths.Resolve(temporary.Path).RuntimePath,
            run, cleanupTimeout: cleanupTimeout);

    private static ProjectReviewInputExecutionResult Acknowledge(TemporaryDirectory temporary, ReviewInputQuery query)
    {
        string path = Path.Combine(LiveLabPaths.Resolve(temporary.Path).RuntimePath, "always-on-status.json");
        AlwaysOnStatusMarker before = JsonSerializer.Deserialize<AlwaysOnStatusMarker>(
            File.ReadAllText(path), LiveLabJsonOptions.CamelCase)!;
        DateTimeOffset observed = before.ObservedAtUtc.AddMilliseconds(1);
        DateTimeOffset after = before.ObservedAtUtc.AddMilliseconds(2);
        File.WriteAllText(path, JsonSerializer.Serialize(before with
        {
            Tick = before.Tick + 1,
            ObservedAtUtc = after,
            Runtime = before.Runtime! with { ObservedAtUtc = after },
        }, LiveLabJsonOptions.CamelCase));
        return new(new(ReviewInputContract.SchemaVersion, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", observed,
            before.Tick, query.Action, true, query.Button, query.Direction, query.X, query.Y,
            query.Action != ReviewInputContract.CursorClearAction, true, null), [], true, false);
    }
}
