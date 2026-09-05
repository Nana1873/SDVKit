using System.Runtime.ExceptionServices;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using Xunit.Abstractions;

namespace SdvKit.Tests;

internal static class StatusConcurrencyFailure
{
    public static void ThrowIfAny(
        ITestOutputHelper output,
        string statusPath,
        object context,
        Exception? writerFailure,
        Exception? observerFailure)
    {
        if (writerFailure is null && observerFailure is null)
        {
            return;
        }

        try
        {
            string details = JsonSerializer.Serialize(context);
            output.WriteLine(details);
            output.WriteLine($"Writer failure: {writerFailure}");
            output.WriteLine($"Observer failure: {observerFailure}");
            string destination = CreateEvidenceDirectory();
            File.WriteAllText(Path.Combine(destination, "context.json"), details);
            File.WriteAllText(Path.Combine(destination, "failures.txt"),
                $"Writer: {writerFailure}{Environment.NewLine}Observer: {observerFailure}");
            File.WriteAllText(Path.Combine(destination, "observation.txt"),
                $"Captured at {DateTimeOffset.UtcNow:O}, after both workers finished. "
                + "These files may be later snapshots, not the bytes seen by the failing operation. "
                + "The publisher may already have removed its temporary file.");
            CaptureFile(statusPath, Path.Combine(destination, "status.json"));
            CaptureFile(statusPath + $".{Environment.ProcessId}.tmp", Path.Combine(destination, "temporary.json"));
            output.WriteLine($"Retained status failure evidence: {destination}");
        }
        catch (Exception exception)
        {
            try
            {
                output.WriteLine($"Could not retain failure evidence: {exception}");
            }
            catch (Exception)
            {
                // A failed diagnostic sink must not replace the original test failures.
            }
        }

        if (writerFailure is not null && observerFailure is not null)
        {
            throw new AggregateException("Status writer and observer both failed.", writerFailure, observerFailure);
        }

        ExceptionDispatchInfo.Capture(writerFailure ?? observerFailure!).Throw();
    }

    public static void CaptureNativeFailure(string temporaryPath, string statusPath, object context)
    {
        string destination = CreateEvidenceDirectory();
        File.WriteAllText(Path.Combine(destination, "native-error.json"), JsonSerializer.Serialize(context));
        File.WriteAllText(Path.Combine(destination, "observation.txt"),
            "Native status is a thread-local observation, not an independently traced syscall result. "
            + "Files and path attributes are captured after the rename failure, before publisher cleanup; "
            + "they do not identify a competing process.");
        CaptureFile(temporaryPath, Path.Combine(destination, "temporary.json"));
        CaptureFile(statusPath, Path.Combine(destination, "status.json"));
    }

    private static string CreateEvidenceDirectory()
    {
        string destination = Path.Combine(
            FindRepositoryRoot(), ".sdvkit", "test-failures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        return destination;
    }

    private static void CaptureFile(string source, string destination)
    {
        try
        {
            using FileStream stream = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (length is < 0 or > AlwaysOnStatusReader.MaximumStatusBytes)
            {
                File.WriteAllText(destination + ".txt", $"Snapshot exceeds the capture limit: {length} bytes.");
                return;
            }

            byte[] bytes = new byte[checked((int)length)];
            stream.ReadExactly(bytes);
            File.WriteAllBytes(destination, bytes);
            File.WriteAllText(destination + ".attributes.txt", File.GetAttributes(source).ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.WriteAllText(destination + ".txt", exception.ToString());
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SDVKit.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the SDVKit repository for test failure evidence.");
    }
}
