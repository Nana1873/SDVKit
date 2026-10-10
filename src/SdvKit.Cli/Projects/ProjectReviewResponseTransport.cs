using System.Diagnostics;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal sealed record ProjectReviewResponseTransportProblem(
    string Code,
    string Message);

internal sealed record ProjectReviewResponseTransportResult<TResponse>(
    TResponse? Response,
    IReadOnlyList<ProjectReviewResponseTransportProblem> Problems,
    bool CommandWritten = false,
    bool CommandMayHaveBeenWritten = false,
    bool CancellationRequested = false)
    where TResponse : class;

internal static class ProjectReviewResponseTransport
{
    private const int Success = 0;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

    internal static bool SameBinding(ProjectReviewMcpRuntimeSnapshot before, ProjectReviewMcpRuntimeSnapshot after) =>
        before.LaunchId == after.LaunchId && before.Topology == after.Topology && before.Role == after.Role
        && before.Target == after.Target && before.TestSave == after.TestSave
        && before.Screen == after.Screen && before.SessionId == after.SessionId;

    internal static bool MatchesCaptureBinding(string? launchId, string topology, string? role,
        DateTimeOffset capturedAtUtc, ProjectReviewMcpRuntimeSnapshot expected,
        DateTimeOffset started, DateTimeOffset now) =>
        launchId == expected.LaunchId && topology == expected.Topology && role == expected.Role
        && capturedAtUtc.Offset == TimeSpan.Zero
        && capturedAtUtc >= started && capturedAtUtc <= now.AddSeconds(5)
        && now - capturedAtUtc <= TimeSpan.FromSeconds(5);

    public static ProjectReviewResponseTransportResult<TResponse> Execute<TResponse>(
        string command,
        string responsePath,
        int maximumResponseBytes,
        string problemPrefix,
        string displayName,
        string labRoot,
        Func<byte[], TResponse?> deserialize,
        Func<TResponse, bool> matchesRequest,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? responseTimeout = null,
        string topology = LiveLabState.SingleTopology,
        string? role = null,
        bool drainAfterDispatchOnCancellation = false,
        Func<string, LiveLabCommandResult>? send = null,
        Action? onCancellation = null,
        IReviewResponseReader? responses = null,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(responsePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(problemPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentNullException.ThrowIfNull(matchesRequest);
        TimeSpan timeout = responseTimeout ?? ResponseTimeout;
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

        if (!drainAfterDispatchOnCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        else if (cancellationToken.IsCancellationRequested)
        {
            return Failure<TResponse>(
                commandWritten: false,
                commandMayHaveBeenWritten: false,
                cancellationRequested: true,
                new ProjectReviewResponseTransportProblem(
                    $"{problemPrefix}RequestCanceled",
                    $"The bounded {displayName} request was canceled before it was written."));
        }

        // An injected send stands in for the whole game side and publishes response files.
        IReviewResponseReader? responseReader = responses
            ?? (send is not null ? FileReviewResponseReader.Instance : null);
        LiveLabCommandResult sent = send is not null ? send(command) : ProjectReviewService.ExecuteCommand(
            command,
            topology,
            role,
            labRoot,
            inputSender,
            useSender: sender => responseReader ??=
                sender as IReviewResponseReader ?? FileReviewResponseReader.Instance);
        if (!drainAfterDispatchOnCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        var cancellationRequested = drainAfterDispatchOnCancellation
            && cancellationToken.IsCancellationRequested;
        (bool? commandWritten, IReadOnlyList<ProjectReviewProblem> commandProblems) =
            sent.Report switch
            {
                ProjectReviewCommandReport single =>
                    (single.CommandWritten, single.Problems),
                ProjectNetworkReviewCommandReport network =>
                    (network.CommandWritten, network.Problems),
                _ => throw new InvalidDataException(
                    $"The {displayName} transport returned an unexpected report type."),
            };
        if (sent.ExitCode != Success || commandWritten != true)
        {
            return Failure<TResponse>(
                commandWritten: commandWritten == true,
                commandMayHaveBeenWritten: commandWritten != false,
                cancellationRequested: cancellationRequested,
                commandProblems.Count > 0
                    ? commandProblems
                        .Select(problem => new ProjectReviewResponseTransportProblem(
                            problem.Code,
                            problem.Message))
                        .ToArray()
                    : [new ProjectReviewResponseTransportProblem(
                        $"{problemPrefix}TransportFailed",
                        $"The bounded {displayName} request was not written to the exact owned review.")]);
        }

        IReviewResponseReader reader = responseReader ?? FileReviewResponseReader.Instance;
        bool cancellationSignaled = false;
        ProjectReviewResponseTransportProblem? cancellationSignalFailure = null;
        var stopwatch = Stopwatch.StartNew();
        Action<TimeSpan> wait = delay ?? Thread.Sleep;
        ReviewResponseRead read;
        while (true)
        {
            ObserveCancellation();
            read = reader.TryTake(responsePath, maximumResponseBytes);
            if (read.Status != ReviewResponseReadStatus.Pending || stopwatch.Elapsed >= timeout)
            {
                break;
            }

            wait(TimeSpan.FromMilliseconds(50));
        }

        ObserveCancellation();

        if (read.Status == ReviewResponseReadStatus.Pending)
        {
            return Failure<TResponse>(
                commandWritten: true,
                commandMayHaveBeenWritten: true,
                cancellationRequested: cancellationRequested,
                new ProjectReviewResponseTransportProblem(
                    cancellationRequested
                        ? $"{problemPrefix}RequestCanceled"
                        : $"{problemPrefix}ResponseTimedOut",
                    cancellationRequested
                        ? $"The bounded {displayName} request was canceled after it was written; it was not retried."
                        : $"The exact owned review did not publish the bounded {displayName} response in time; the request was not retried."));
        }

        if (read.Status == ReviewResponseReadStatus.Invalid)
        {
            return Failure<TResponse>(
                commandWritten: true,
                commandMayHaveBeenWritten: true,
                cancellationRequested: cancellationRequested,
                new ProjectReviewResponseTransportProblem(
                    $"{problemPrefix}ResponseInvalid",
                    $"The {displayName} response could not be taken: {read.Error}"));
        }

        string responseStage = "validation";
        try
        {
            TResponse? response = deserialize(read.Bytes!);
            if (response is null || !matchesRequest(response))
            {
                throw new InvalidDataException(
                    $"The {displayName} response does not match the exact request.");
            }

            ObserveCancellation();
            return new ProjectReviewResponseTransportResult<TResponse>(
                response,
                cancellationSignalFailure is not null ? [cancellationSignalFailure]
                    : cancellationRequested
                    ? [new ProjectReviewResponseTransportProblem(
                        $"{problemPrefix}RequestCanceled",
                        $"The bounded {displayName} request was canceled after it was written; its validated response was retained and the request was not retried.")]
                    : [],
                CommandWritten: true,
                CommandMayHaveBeenWritten: true,
                CancellationRequested: cancellationRequested);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure<TResponse>(
                commandWritten: true,
                commandMayHaveBeenWritten: true,
                cancellationRequested: cancellationRequested,
                new ProjectReviewResponseTransportProblem(
                    $"{problemPrefix}ResponseInvalid",
                    FormattableString.Invariant($"The {displayName} response could not be validated ({exception.GetType().Name}; stage={responseStage}; HRESULT=0x{exception.HResult:X8}).")));
        }

        void ObserveCancellation()
        {
            if (drainAfterDispatchOnCancellation)
            {
                cancellationRequested |= cancellationToken.IsCancellationRequested;
                if (cancellationRequested && !cancellationSignaled)
                {
                    cancellationSignaled = true;
                    try { onCancellation?.Invoke(); }
                    catch (Exception exception) when (IsControlledFailure(exception))
                    {
                        cancellationSignalFailure = new($"{problemPrefix}CancellationNotConfirmed",
                            $"Cancellation delivery failed after dispatch ({exception.GetType().Name}); the original action was drained without retry.");
                    }
                }
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static ProjectReviewResponseTransportResult<TResponse> Failure<TResponse>(
        params ProjectReviewResponseTransportProblem[] problems)
        where TResponse : class =>
        new(null, problems);

    private static ProjectReviewResponseTransportResult<TResponse> Failure<TResponse>(
        bool commandWritten,
        bool commandMayHaveBeenWritten,
        bool cancellationRequested,
        params ProjectReviewResponseTransportProblem[] problems)
        where TResponse : class =>
        new(
            null,
            problems,
            commandWritten,
            commandMayHaveBeenWritten,
            cancellationRequested);

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or JsonException
            or NotSupportedException
            or PathTooLongException
            or SecurityException
            or UnauthorizedAccessException;
}

/// <summary>
/// Reads response files published by a test double that stands in for the game side. Production
/// responses come from the exact launch's command endpoint.
/// </summary>
internal sealed class FileReviewResponseReader : IReviewResponseReader
{
    public static readonly FileReviewResponseReader Instance = new();

    public ReviewResponseRead TryTake(string responsePath, int maximumBytes)
    {
        if (!File.Exists(responsePath))
        {
            return new(ReviewResponseReadStatus.Pending);
        }

        string responseStage = "attributes";
        try
        {
            FileAttributes attributes = File.GetAttributes(responsePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) != 0)
            {
                return new(ReviewResponseReadStatus.Invalid, Error: "The response is not a regular file.");
            }

            byte[] bytes = ReadBoundedResponse(responsePath, maximumBytes, "review", ref responseStage);
            responseStage = "delete";
            File.Delete(responsePath);
            return new(ReviewResponseReadStatus.Available, bytes);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(responsePath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // The request still fails closed; a unique response name is never reused.
            }

            return new(
                ReviewResponseReadStatus.Invalid,
                Error: FormattableString.Invariant($"{exception.GetType().Name}; stage={responseStage}; HRESULT=0x{exception.HResult:X8}"));
        }
    }

    private static byte[] ReadBoundedResponse(
        string responsePath,
        int maximumResponseBytes,
        string displayName,
        ref string responseStage)
    {
        responseStage = "open";
        using var stream = new FileStream(
            responsePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        responseStage = "read";
        long length = stream.Length;
        if (length <= 0 || length > maximumResponseBytes)
        {
            throw new InvalidDataException(
                $"The {displayName} response is empty or exceeds its bounded maximum.");
        }

        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"The {displayName} response changed while it was read.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1 || stream.Length != length)
        {
            throw new InvalidDataException(
                $"The {displayName} response changed while it was read.");
        }

        return bytes;
    }

}
