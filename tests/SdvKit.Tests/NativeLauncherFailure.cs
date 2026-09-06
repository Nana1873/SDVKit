using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SdvKit.Cli.LiveLab;
using Xunit.Abstractions;

namespace SdvKit.Tests;

internal sealed class NativeLauncherFailure(
    ITestOutputHelper output, string stdout, string stderr, string? evidenceDirectory = null)
{
    internal const int MaximumOutputBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public LabProcessStartResult? Start { get; set; }
    public LabProcessWaitResult? Wait { get; set; }
    public string ChildAcquisition { get; set; } = "Unavailable: not reached.";
    public string Cleanup { get; set; } = "Unavailable: not reached.";
    public Dictionary<string, double?> ElapsedMilliseconds { get; } = new()
    {
        ["startAndIdentityVerification"] = null,
        ["childAcquisition"] = null,
        ["wait"] = null,
        ["outputAssertions"] = null,
        ["cleanup"] = null,
    };

    public void Run(Action assertions, Action cleanup, Action? dispose = null)
    {
        Exception? failure = null;
        Exception? cleanupFailure = null;
        try
        {
            assertions();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            finally
            {
                ElapsedMilliseconds["cleanup"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }

        if (failure is null && cleanupFailure is null)
        {
            dispose?.Invoke();
            return;
        }

        try
        {
            string destination = evidenceDirectory ?? StatusConcurrencyFailure.CreateEvidenceDirectory();
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "native-launcher.json"), JsonSerializer.Serialize(new
            {
                Test = "NativeLauncherPreservesEnvironmentAndArgumentWithSpaces",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Observation = "Post-cleanup snapshots before temporary-file disposal; not necessarily bytes at the failed wait. Null results/timings mean unavailable or not reached. Start includes native launch and identity verification; these are not separately timed.",
                Start,
                IdentityAvailability = Start?.Identity is null ? "Unavailable: no captured identity." : "Captured by start.",
                ChildAcquisition,
                Wait,
                WaitBoundMilliseconds = 15000,
                Cleanup,
                CleanupBoundMilliseconds = 5000,
                ElapsedMilliseconds,
                Failure = failure?.ToString(),
                CleanupFailure = cleanupFailure?.ToString(),
            }, SerializerOptions));
            CaptureOutput(stdout, Path.Combine(destination, "stdout.log"));
            CaptureOutput(stderr, Path.Combine(destination, "stderr.log"));
            WriteOutput($"Retained native launcher failure evidence: {destination}");
        }
        catch (Exception exception)
        {
            WriteOutput($"Could not retain native launcher failure evidence: {exception}");
        }

        try
        {
            dispose?.Invoke();
        }
        catch (Exception exception)
        {
            WriteOutput($"Temporary-file disposal also failed: {exception}");
        }

        ExceptionDispatchInfo.Capture(failure ?? cleanupFailure!).Throw();
    }

    private void WriteOutput(string message)
    {
        try
        {
            output.WriteLine(message);
        }
        catch (Exception)
        {
            // Diagnostic sinks must never replace the original failure.
        }
    }

    internal static void CaptureOutput(string source, string destination)
    {
        try
        {
            using FileStream stream = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            byte[] bytes = new byte[(int)Math.Min(length, MaximumOutputBytes)];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            File.WriteAllBytes(destination, bytes.AsSpan(0, read).ToArray());
            File.WriteAllText(destination + ".txt",
                $"Observed length: {length} bytes. Captured prefix: {read} bytes. Limit: {MaximumOutputBytes} bytes. Truncated: {length > read}. The file may change during capture.");
        }
        catch (Exception exception)
        {
            try
            {
                File.WriteAllText(destination + ".txt", $"Missing or unavailable: {exception}");
            }
            catch (Exception)
            {
                // Retaining one output must not prevent capture of the other or mask the test failure.
            }
        }
    }
}
