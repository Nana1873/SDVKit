using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using Xunit.Abstractions;

namespace SdvKit.Tests;

public sealed class NativeLauncherFailureTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("identity")]
    [InlineData("wait")]
    [InlineData("output")]
    public void RetainsFailureBeforeSourceDisposalIncludingCleanupOutcome(string phase)
    {
        using SimulationDirectory simulation = new();
        string source = Path.Combine(simulation.Path, "source");
        Directory.CreateDirectory(source);
        string stdout = Path.Combine(source, "child.stdout");
        string stderr = Path.Combine(source, "child.stderr");
        File.WriteAllText(stdout, "partial child output");
        File.WriteAllText(stderr, "child error");
        var output = new RecordingOutput();
        var diagnostics = new NativeLauncherFailure(output, stdout, stderr, simulation.Evidence);
        var original = new InvalidOperationException($"original {phase} failure");
        bool cleaned = false;
        bool capturedBeforeDisposal = false;
        try
        {
            Exception actual = Assert.Throws<InvalidOperationException>(() => diagnostics.Run(() =>
            {
                if (phase != "start")
                {
                    diagnostics.Start = new LabProcessStartResult(LabProcessStartStatus.Started,
                        phase == "identity" ? null : new OwnedProcessIdentity(42,
                            new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero), "test-child.exe"));
                }

                if (phase is "wait" or "output")
                {
                    diagnostics.Wait = new LabProcessWaitResult(phase == "wait"
                        ? LabProcessWaitStatus.TimedOut : LabProcessWaitStatus.Exited);
                    diagnostics.ElapsedMilliseconds["wait"] = 15001.25;
                }

                throw original;
            }, () =>
            {
                cleaned = true;
                diagnostics.Cleanup = "Kill returned; WaitForExit(5000): True.";
                File.AppendAllText(stdout, " after cleanup");
            }, () =>
            {
                capturedBeforeDisposal = File.Exists(Path.Combine(output.Destination, "native-launcher.json"))
                    && File.Exists(Path.Combine(output.Destination, "stdout.log"))
                    && File.Exists(Path.Combine(output.Destination, "stderr.log"));
                Directory.Delete(source, recursive: true);
            }));

            Assert.Same(original, actual);
            Assert.True(cleaned);
            Assert.True(capturedBeforeDisposal);
            string retained = output.Destination;
            Assert.Equal("partial child output after cleanup", File.ReadAllText(Path.Combine(retained, "stdout.log")));
            Assert.Equal("child error", File.ReadAllText(Path.Combine(retained, "stderr.log")));
            using JsonDocument context = JsonDocument.Parse(File.ReadAllText(Path.Combine(retained, "native-launcher.json")));
            JsonElement root = context.RootElement;
            Assert.Contains(original.Message, root.GetProperty("Failure").GetString());
            Assert.Equal(diagnostics.Cleanup, root.GetProperty("Cleanup").GetString());
            Assert.True(root.GetProperty("ElapsedMilliseconds").GetProperty("cleanup").GetDouble() >= 0);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("ElapsedMilliseconds").GetProperty("outputAssertions").ValueKind);
            if (phase is "wait" or "output")
            {
                Assert.Equal(42, root.GetProperty("Start").GetProperty("Identity").GetProperty("ProcessId").GetInt32());
                Assert.Equal(diagnostics.Wait!.Status.ToString(), root.GetProperty("Wait").GetProperty("Status").GetString());
                Assert.Equal(15001.25, root.GetProperty("ElapsedMilliseconds").GetProperty("wait").GetDouble());
            }
            else
            {
                Assert.Contains("Unavailable", root.GetProperty("IdentityAvailability").GetString());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("Wait").ValueKind);
            }
        }
        finally
        {
            if (Directory.Exists(source))
            {
                Directory.Delete(source, recursive: true);
            }
        }
    }

    [Fact]
    public void PassingTestCleansUpWithoutRetainingFailureEvidence()
    {
        using SimulationDirectory simulation = new();
        var diagnostics = new NativeLauncherFailure(new RecordingOutput(), "missing-stdout", "missing-stderr", simulation.Evidence);
        bool cleaned = false;
        bool disposed = false;
        diagnostics.Run(() => { }, () => cleaned = true, () => disposed = true);
        Assert.True(cleaned);
        Assert.True(disposed);
        Assert.False(Directory.Exists(simulation.Evidence));
    }

    [Fact]
    public void BoundsOutputAndReportsMissingAndUnreadableFiles()
    {
        using SimulationDirectory simulation = new();
        string directory = simulation.Path;
        string source = Path.Combine(directory, "large.log");
        File.WriteAllBytes(source, new byte[NativeLauncherFailure.MaximumOutputBytes + 17]);
        string destination = Path.Combine(directory, "captured.log");
        NativeLauncherFailure.CaptureOutput(source, destination);
        Assert.Equal(NativeLauncherFailure.MaximumOutputBytes, new FileInfo(destination).Length);
        Assert.Contains("Truncated: True", File.ReadAllText(destination + ".txt"));

        NativeLauncherFailure.CaptureOutput(Path.Combine(directory, "missing"), destination + ".missing");
        Assert.Contains("Missing or unavailable", File.ReadAllText(destination + ".missing.txt"));
        using FileStream locked = new(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        NativeLauncherFailure.CaptureOutput(source, destination + ".locked");
        Assert.Contains("Missing or unavailable", File.ReadAllText(destination + ".locked.txt"));
    }

    [Fact]
    public void CleanupAndDiagnosticSinkFailuresDoNotReplaceOriginalFailure()
    {
        using SimulationDirectory simulation = new();
        var original = new InvalidOperationException("original assertion");
        var diagnostics = new NativeLauncherFailure(new UnavailableOutput(), "missing-stdout", "missing-stderr", simulation.Evidence);
        Exception actual = Assert.Throws<InvalidOperationException>(() => diagnostics.Run(
            () => throw original, () => throw new IOException("cleanup failed"),
            () => throw new IOException("disposal failed")));
        Assert.Same(original, actual);
    }

    [Fact]
    public void CaptureFailureDoesNotReplaceOriginalFailure()
    {
        using SimulationDirectory simulation = new();
        var original = new InvalidOperationException("original assertion");
        var diagnostics = new NativeLauncherFailure(new UnavailableOutput(), "missing-stdout", "missing-stderr", simulation.Evidence);
        diagnostics.ElapsedMilliseconds["wait"] = double.NaN;
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => diagnostics.Run(
            () => throw original, () => { })));
    }

    [Fact]
    public void CleanupFailureOnOtherwisePassingTestRemainsVisible()
    {
        using SimulationDirectory simulation = new();
        var original = new IOException("cleanup failed");
        var output = new RecordingOutput();
        var diagnostics = new NativeLauncherFailure(output, "missing-stdout", "missing-stderr", simulation.Evidence);
        Assert.Same(original, Assert.Throws<IOException>(() => diagnostics.Run(() => { }, () => throw original)));
        Assert.Contains(original.Message, File.ReadAllText(Path.Combine(output.Destination, "native-launcher.json")));
    }

    private sealed class SimulationDirectory : IDisposable
    {
        public string Path { get; } = StatusConcurrencyFailure.CreateEvidenceDirectory();
        public string Evidence => System.IO.Path.Combine(Path, "simulated-capture");

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class RecordingOutput : ITestOutputHelper
    {
        public string Destination { get; private set; } = string.Empty;

        public void WriteLine(string message)
        {
            const string prefix = "Retained native launcher failure evidence: ";
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                Destination = message[prefix.Length..];
            }
        }

        public void WriteLine(string format, params object[] args) => WriteLine(string.Format(CultureInfo.InvariantCulture, format, args));
    }

    private sealed class UnavailableOutput : ITestOutputHelper
    {
        public void WriteLine(string message) => throw new IOException("Diagnostic output unavailable.");

        public void WriteLine(string format, params object[] args) => WriteLine(format);
    }
}
