using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using SdvKit.Cli.LiveLab;
using SdvKit.GameSide;

namespace SdvKit.Tests;

public sealed class ReviewHttpEndpointTests
{
    private static readonly OwnedProcessIdentity AnyProcess =
        new(1, DateTimeOffset.UnixEpoch, "StardewModdingAPI.exe");

    [Theory]
    [InlineData("sdvkit input press K", "input|press|K", 0)]
    [InlineData("SDVKIT  input   press K", "input|press|K", 0)]
    [InlineData("sdvkit input press K screen=1", "input|press|K", 1)]
    [InlineData("sdvkit screen=2 split-screen status", "split-screen|status", 2)]
    [InlineData("sdvkit screenshot viewport \"two words\"", "screenshot|viewport|two words", 0)]
    public void CommandLineMatchesSmapiParsing(string line, string expected, int expectedScreen)
    {
        Assert.True(ReviewCommandLine.TryParse(line, out string[] arguments, out int screen, out _));
        Assert.Equal(expected.Split('|'), arguments);
        Assert.Equal(expectedScreen, screen);
    }

    [Theory]
    [InlineData("other input press K")]
    [InlineData("sdvkit input press K screen=one")]
    [InlineData("   ")]
    public void CommandLineRejectsForeignRootsAndInvalidScreens(string line) =>
        Assert.False(ReviewCommandLine.TryParse(line, out _, out _, out _));

    [Fact]
    public async Task QueueRunsCommandsOnlyOnTheirScreenAndReportsMissingScreens()
    {
        var queue = new ReviewCommandQueue();
        Assert.True(queue.TryEnqueue(["a"], 0, out PendingReviewCommand? first));
        Assert.True(queue.TryEnqueue(["b"], 1, out PendingReviewCommand? second));
        Assert.True(queue.TryEnqueue(["c"], 3, out PendingReviewCommand? missing));
        var ran = new List<string>();

        queue.Drain(1, id => id is 0 or 1, arguments => Record(ran, arguments[0]));
        Assert.Equal(["b"], ran);
        Assert.False(first!.Completion.IsCompleted);
        Assert.False(missing!.Completion.IsCompleted);

        queue.Drain(0, id => id is 0 or 1, arguments => Record(ran, arguments[0]));
        Assert.Equal(["b", "a"], ran);
        Assert.Equal(ReviewHttpContract.Executed, (await second!.Completion).Outcome);
        Assert.Equal(ReviewHttpContract.ScreenUnavailable, (await missing.Completion).Outcome);
    }

    [Fact]
    public void QueueIsBoundedAndWithdrawnCommandsNeverRun()
    {
        var queue = new ReviewCommandQueue();
        PendingReviewCommand? withdrawn = null;
        for (int index = 0; index < ReviewCommandQueue.MaximumPending; index++)
        {
            Assert.True(queue.TryEnqueue([index.ToString(System.Globalization.CultureInfo.InvariantCulture)], 0, out withdrawn));
        }

        Assert.False(queue.TryEnqueue(["overflow"], 0, out _));
        Assert.True(queue.TryWithdraw(withdrawn!));
        Assert.False(queue.TryWithdraw(withdrawn!));
        var ran = new List<string>();
        queue.Drain(0, _ => true, arguments => Record(ran, arguments[0]));

        Assert.Equal(ReviewCommandQueue.MaximumPending - 1, ran.Count);
        Assert.DoesNotContain((ReviewCommandQueue.MaximumPending - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ran);
    }

    [Fact]
    public void SenderReceivesExecutedOnlyAfterTheGameThreadRanTheCommand()
    {
        using var lab = new FakeLab();
        ProjectReviewConsoleInputResult result = lab.Sender().SendLine(AnyProcess, "sdvkit input press K");

        Assert.Equal(ProjectReviewConsoleInputStatus.Written, result.Status);
        Assert.True(result.CommandWritten);
        Assert.Equal(["input", "press", "K"], Assert.Single(lab.Executed));
    }

    [Fact]
    public void DispatcherFailureIsReportedAsExecutedWithFailure()
    {
        using var lab = new FakeLab(_ => throw new InvalidOperationException("boom"));
        ProjectReviewConsoleInputResult result = lab.Sender().SendLine(AnyProcess, "sdvkit input press K");

        Assert.Equal(ProjectReviewConsoleInputStatus.ExecutedWithFailure, result.Status);
        Assert.True(result.CommandWritten);
        Assert.Contains("boom", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingScreenIsReportedWithoutRunning()
    {
        using var lab = new FakeLab();
        ProjectReviewConsoleInputResult result = lab.Sender().SendLine(AnyProcess, "sdvkit input press K screen=4");

        Assert.Equal(ProjectReviewConsoleInputStatus.ScreenUnavailable, result.Status);
        Assert.False(result.CommandWritten);
        Assert.Empty(lab.Executed);
    }

    [Fact]
    public void StalledGameThreadWithdrawsTheCommandAsNotExecuted()
    {
        using var lab = new FakeLab(executionTimeout: TimeSpan.FromMilliseconds(200), pump: false);
        ProjectReviewConsoleInputResult result = lab.Sender().SendLine(AnyProcess, "sdvkit input press K");

        Assert.Equal(ProjectReviewConsoleInputStatus.NotExecuted, result.Status);
        Assert.False(result.CommandWritten);
        lab.Queue.Drain(0, _ => true, arguments => Record(lab.Executed, arguments));
        Assert.Empty(lab.Executed);
    }

    [Fact]
    public void WrongTokenIsRejectedBeforeQueueing()
    {
        using var lab = new FakeLab();
        var sender = new HttpProjectReviewCommandSender(lab.Endpoint.Port, ReviewHttpContract.CreateToken(), lab.LaunchId);

        ProjectReviewConsoleInputResult result = sender.SendLine(AnyProcess, "sdvkit input press K");

        Assert.Equal(ProjectReviewConsoleInputStatus.Rejected, result.Status);
        Assert.False(result.CommandWritten);
        Assert.Empty(lab.Executed);
    }

    [Fact]
    public void ReplyForAnotherLaunchIsTreatedAsUnknownDelivery()
    {
        using var lab = new FakeLab();
        var sender = new HttpProjectReviewCommandSender(lab.Endpoint.Port, lab.Token, Guid.NewGuid().ToString("N"));

        ProjectReviewConsoleInputResult result = sender.SendLine(AnyProcess, "sdvkit input press K");

        Assert.Equal(ProjectReviewConsoleInputStatus.DeliveryUnknown, result.Status);
        Assert.Null(result.CommandWritten);
    }

    [Fact]
    public void UnpublishedOrClosedEndpointSendsNothing()
    {
        Assert.Equal(
            ProjectReviewConsoleInputStatus.EndpointUnavailable,
            new HttpProjectReviewCommandSender(null, ReviewHttpContract.CreateToken(), Guid.NewGuid().ToString("N"))
                .SendLine(AnyProcess, "sdvkit input press K").Status);

        int closedPort;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }

        Assert.Equal(
            ProjectReviewConsoleInputStatus.EndpointUnavailable,
            new HttpProjectReviewCommandSender(closedPort, ReviewHttpContract.CreateToken(), Guid.NewGuid().ToString("N"))
                .SendLine(AnyProcess, "sdvkit input press K").Status);
    }

    [Theory]
    [InlineData("Origin", "http://example.test")]
    [InlineData("Host", "localhost")]
    public void BrowserOriginsAndForeignHostsAreForbidden(string header, string value)
    {
        using var lab = new FakeLab();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{lab.Endpoint.Port}{ReviewHttpContract.CommandPath}")
        {
            Content = new StringContent("{\"line\":\"sdvkit input press K\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lab.Token);
        if (header == "Host")
        {
            request.Headers.Host = value;
        }
        else
        {
            request.Headers.Add(header, value);
        }

        using HttpResponseMessage response = client.Send(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(lab.Executed);
    }

    private static bool Record<T>(List<T> target, T value)
    {
        lock (target)
        {
            target.Add(value);
        }

        return true;
    }

    [Fact]
    public void DispatcherRejectionIsReportedAsNotRun()
    {
        using var lab = new FakeLab(_ => false);
        ProjectReviewConsoleInputResult result = lab.Sender().SendLine(AnyProcess, "sdvkit unknown");

        Assert.Equal(ProjectReviewConsoleInputStatus.Rejected, result.Status);
        Assert.False(result.CommandWritten);
    }

    [Fact]
    public void ConnectionsBeyondTheLimitAreRefusedAsNotRun()
    {
        using var lab = new FakeLab();
        var idle = new List<TcpClient>();
        try
        {
            for (int index = 0; index < 8; index++)
            {
                var client = new TcpClient();
                client.Connect(IPAddress.Loopback, lab.Endpoint.Port);
                idle.Add(client);
            }

            Thread.Sleep(200);
            ProjectReviewConsoleInputResult result = lab.Sender().SendLine(AnyProcess, "sdvkit input press K");

            Assert.Equal(ProjectReviewConsoleInputStatus.Rejected, result.Status);
            Assert.False(result.CommandWritten);
            Assert.Empty(lab.Executed);
        }
        finally
        {
            foreach (TcpClient client in idle)
            {
                client.Dispose();
            }
        }
    }

    private sealed class FakeLab : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task? _pump;

        public FakeLab(Func<string[], bool>? dispatch = null, TimeSpan? executionTimeout = null, bool pump = true)
        {
            Endpoint = ReviewHttpEndpoint.Start(LaunchId, Token, Queue, _ => { }, executionTimeout);
            Func<string[], bool> run = dispatch ?? (arguments => Record(Executed, arguments));
            if (pump)
            {
                _pump = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        Queue.Drain(0, id => id == 0, run);
                        await Task.Delay(10).ConfigureAwait(false);
                    }
                });
            }
        }

        public string LaunchId { get; } = Guid.NewGuid().ToString("N");

        public string Token { get; } = ReviewHttpContract.CreateToken();

        public ReviewCommandQueue Queue { get; } = new();

        public ReviewHttpEndpoint Endpoint { get; }

        public List<string[]> Executed { get; } = [];

        public HttpProjectReviewCommandSender Sender() => new(Endpoint.Port, Token, LaunchId);

        public void Dispose()
        {
            _stop.Cancel();
            _pump?.Wait(TimeSpan.FromSeconds(5));
            Endpoint.Dispose();
        }
    }
}
