using System.Diagnostics;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal sealed record ProjectReviewResponseTransportProblem(
    string Code,
    string Message);

internal sealed record ProjectReviewResponseTransportResult<TResponse>(
    TResponse? Response,
    IReadOnlyList<ProjectReviewResponseTransportProblem> Problems)
    where TResponse : class;

internal static class ProjectReviewResponseTransport
{
    private const int Success = 0;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

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

        cancellationToken.ThrowIfCancellationRequested();
        LiveLabCommandResult sent = ProjectReviewService.ExecuteCommand(
            command,
            topology,
            role,
            labRoot,
            inputSender);
        cancellationToken.ThrowIfCancellationRequested();
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

        var stopwatch = Stopwatch.StartNew();
        Action<TimeSpan> wait = delay ?? Thread.Sleep;
        while (!File.Exists(responsePath) && stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            wait(TimeSpan.FromMilliseconds(50));
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(responsePath))
        {
            return Failure<TResponse>(
                new ProjectReviewResponseTransportProblem(
                    $"{problemPrefix}ResponseTimedOut",
                    $"The exact owned review did not publish the bounded {displayName} response in time; the request was not retried."));
        }

        bool regularResponse = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(responsePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) != 0)
            {
                return Failure<TResponse>(
                    new ProjectReviewResponseTransportProblem(
                        $"{problemPrefix}ResponseInvalid",
                        $"The {displayName} response is not a regular file."));
            }
            regularResponse = true;

            byte[] bytes = ReadBoundedResponse(responsePath, maximumResponseBytes, displayName);
            cancellationToken.ThrowIfCancellationRequested();
            TResponse? response = deserialize(bytes);
            if (response is null || !matchesRequest(response))
            {
                throw new InvalidDataException(
                    $"The {displayName} response does not match the exact request.");
            }

            File.Delete(responsePath);
            regularResponse = false;
            cancellationToken.ThrowIfCancellationRequested();
            return new ProjectReviewResponseTransportResult<TResponse>(response, []);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            if (regularResponse)
            {
                try
                {
                    File.Delete(responsePath);
                }
                catch (Exception cleanupException) when (IsControlledFailure(cleanupException))
                {
                    // The request still fails closed; a unique response name is never reused.
                }
            }

            return Failure<TResponse>(
                new ProjectReviewResponseTransportProblem(
                    $"{problemPrefix}ResponseInvalid",
                    $"The {displayName} response could not be validated ({exception.GetType().Name})."));
        }
    }

    private static ProjectReviewResponseTransportResult<TResponse> Failure<TResponse>(
        params ProjectReviewResponseTransportProblem[] problems)
        where TResponse : class =>
        new(null, problems);

    private static byte[] ReadBoundedResponse(
        string responsePath,
        int maximumResponseBytes,
        string displayName)
    {
        using var stream = new FileStream(
            responsePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
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
