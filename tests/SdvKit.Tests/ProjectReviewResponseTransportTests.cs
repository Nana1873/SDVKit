using System.Text;
using SdvKit.Cli;

namespace SdvKit.Tests;

public sealed class ProjectReviewResponseTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationSignalsOnceAfterDispatchAndDrainsTheReleaseResponse(bool deliveryFails)
    {
        using TemporaryDirectory temporary = new();
        using var cancellation = new CancellationTokenSource();
        string path = Path.Combine(temporary.Path, "response.json");
        int signals = 0, waits = 0;
        bool dispatched = false;
        var response = ProjectReviewResponseTransport.Execute("sdvkit input request owned chord", path,
            100, "input", "review-input", temporary.Path,
            bytes => Encoding.UTF8.GetString(bytes), text => text == "released",
            delay: _ =>
            {
                waits++;
                if (waits == 3) File.WriteAllText(path, "released");
            },
            responseTimeout: TimeSpan.FromSeconds(2), drainAfterDispatchOnCancellation: true,
            send: _ =>
            {
                dispatched = true;
                cancellation.Cancel();
                return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
            },
            onCancellation: () =>
            {
                Assert.True(dispatched);
                signals++;
                if (deliveryFails) throw new IOException("Cancellation channel failed.");
            },
            cancellationToken: cancellation.Token);
        Assert.Equal(1, signals);
        Assert.Equal("released", response.Response);
        Assert.True(response.CancellationRequested);
        Assert.True(response.CommandWritten);
        Assert.True(response.CommandMayHaveBeenWritten);
        Assert.Contains(response.Problems, p => p.Code == (deliveryFails
            ? "inputCancellationNotConfirmed" : "inputRequestCanceled"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CancellationBeforeDispatchWritesNothingAndDoesNotSignalGame()
    {
        using TemporaryDirectory temporary = new();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var response = ProjectReviewResponseTransport.Execute("input", Path.Combine(temporary.Path, "response"),
            100, "input", "review-input", temporary.Path,
            bytes => Encoding.UTF8.GetString(bytes), _ => true,
            drainAfterDispatchOnCancellation: true,
            send: _ => throw new InvalidOperationException("No dispatch allowed."),
            onCancellation: () => Assert.Fail("No game request exists."), cancellationToken: cancellation.Token);
        Assert.False(response.CommandWritten);
        Assert.True(response.CancellationRequested);
    }
}
