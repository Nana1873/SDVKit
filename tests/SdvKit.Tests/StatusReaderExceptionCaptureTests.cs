using System.Diagnostics;
using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class StatusReaderExceptionCaptureTests
{
    [Fact]
    public void ThrowingExceptionDescriptionCannotMaskOriginalFailure()
    {
        var capture = new StatusReaderExceptionCapture();
        var original = new UndescribableIOException();
        Assert.Same(original, Assert.Throws<UndescribableIOException>(() => capture.Read(() => throw original)));

        using JsonDocument description = JsonSerializer.SerializeToDocument(capture.Describe());
        JsonElement item = Assert.Single(description.RootElement.EnumerateArray());
        Assert.Equal("<unavailable>", item.GetProperty("Message").GetString());
        Assert.Equal("<unavailable>", item.GetProperty("StackTrace").GetString());
        Assert.Same(original, Assert.Single(capture.Exceptions));
    }

    [Fact]
    public void CapturesSharingFailureWithoutChangingTheReaderReportAndRecoversAfterRelease()
    {
        if (!OperatingSystem.IsWindows()) return;

        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "status.json");
        const string launchId = "11111111111111111111111111111111";
        var writer = new StatusWriter(launchId, path);
        writer.Write("active", 17, false, false);
        using Process process = Process.GetCurrentProcess();
        var identity = new OwnedProcessIdentity(process.Id, process.StartTime.ToUniversalTime(), Environment.ProcessPath!);
        var capture = new StatusReaderExceptionCapture();

        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AlwaysOnStatusReport report = capture.Read(() =>
                AlwaysOnStatusReader.Read(path, launchId, identity, DateTimeOffset.UtcNow));

            Assert.Equal("invalid", report.State);
            Assert.Null(report.ObservedAtUtc);
            Assert.Contains(capture.Exceptions, exception => exception is IOException
                && exception.HResult == unchecked((int)0x80070020));
        }

        AlwaysOnStatusReport recovered = capture.Read(() =>
            AlwaysOnStatusReader.Read(path, launchId, identity, DateTimeOffset.UtcNow));
        Assert.Equal("active", recovered.State);
        Assert.Equal(17, recovered.Tick);
        Assert.NotNull(recovered.ObservedAtUtc);
        Assert.Empty(capture.Exceptions);
    }

    [Fact]
    public void PreservesOriginalExceptionAndUnsubscribesBeforeLaterCalls()
    {
        var capture = new StatusReaderExceptionCapture();
        var original = new IOException("original read failure");

        IOException actual = Assert.Throws<IOException>(() => capture.Read(() => throw original));
        Assert.Same(original, actual);
        Assert.Same(original, Assert.Single(capture.Exceptions));

        try { throw new IOException("outside the read scope"); }
        catch (IOException) { }
        Assert.Same(original, Assert.Single(capture.Exceptions));

        capture.Read(() => new AlwaysOnStatusReport("pending", null, null, null, null));
        Assert.Empty(capture.Exceptions);
    }

    [Fact]
    public void IgnoresOtherThreadsAndBoundsCapturedExceptions()
    {
        var capture = new StatusReaderExceptionCapture();
        var first = new IOException("first read failure");

        capture.Read(() =>
        {
            var unrelated = new Thread(() =>
            {
                try { throw new IOException("other test thread"); }
                catch (IOException) { }
            });
            unrelated.Start();
            unrelated.Join();
            try { throw first; }
            catch (IOException) { }
            for (int index = 0; index < 8; index++)
            {
                try { throw new IOException("bounded read failure"); }
                catch (IOException) { }
            }
            return new AlwaysOnStatusReport("invalid", null, null, null, null);
        });

        Assert.Equal(4, capture.Exceptions.Length);
        Assert.Same(first, capture.Exceptions[0]);
        Assert.DoesNotContain(capture.Exceptions, exception => exception.Message == "other test thread");
    }

    private sealed class UndescribableIOException : IOException
    {
        public override string Message => throw new InvalidOperationException("Message unavailable");
        public override string StackTrace => throw new InvalidOperationException("Stack unavailable");
    }
}
