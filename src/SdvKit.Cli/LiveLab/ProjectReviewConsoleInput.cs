using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using SdvKit.GameSide;

namespace SdvKit.Cli.LiveLab;

internal static class ProjectReviewConsoleLine
{
    public const int MaximumLength = 4096;

    public static bool CanRunBeforeScenarioReady(string line)
    {
        if (ValidationError(line) is not null)
        {
            return false;
        }

        string[] tokens = line.Split(' ');
        if (tokens.Any(string.IsNullOrEmpty)
            || !string.Equals(tokens[0], "sdvkit", StringComparison.Ordinal))
        {
            return false;
        }

        return IsInputCommand(tokens)
            || IsViewportScreenshotCommand(tokens)
            || IsNetworkLifecycleCommand(tokens);
    }

    /// <summary>
    /// Input and viewport-screenshot lines. From 2.0 the CLI accepts them only while an owned test-save
    /// fixture is not loaded, such as at the title screen during a native language change, because the
    /// input and screenshot tools require a ready fixture.
    /// </summary>
    public static bool IsTitleInputOrViewport(string line)
    {
        if (ValidationError(line) is not null)
        {
            return false;
        }

        string[] tokens = line.Split(' ');
        return !tokens.Any(string.IsNullOrEmpty)
            && string.Equals(tokens[0], "sdvkit", StringComparison.Ordinal)
            && (IsInputCommand(tokens) || IsViewportScreenshotCommand(tokens));
    }

    public static string? ValidationError(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "A project-review console command must contain one non-empty line.";
        }

        if (line.Length > MaximumLength)
        {
            return $"A project-review console command cannot exceed {MaximumLength} UTF-16 code units.";
        }

        for (var index = 0; index < line.Length; index++)
        {
            if (char.IsHighSurrogate(line[index]))
            {
                if (index + 1 >= line.Length || !char.IsLowSurrogate(line[index + 1]))
                {
                    return "A project-review console command must contain well-formed UTF-16 text.";
                }

                index++;
            }
            else if (char.IsLowSurrogate(line[index]))
            {
                return "A project-review console command must contain well-formed UTF-16 text.";
            }
        }

        if (line.Any(char.IsControl))
        {
            return "A project-review console command cannot contain control characters.";
        }

        return null;
    }

    private static bool IsInputCommand(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 4
            || !string.Equals(tokens[1], "input", StringComparison.Ordinal))
        {
            return false;
        }

        var actionIndex = 2;
        var transportRequest = false;
        if (tokens.Count >= 6
            && string.Equals(tokens[2], "request", StringComparison.Ordinal)
            && Guid.TryParseExact(tokens[3], "N", out _))
        {
            actionIndex = 4;
            transportRequest = true;
        }

        if (ReviewInputContract.IsGesture(tokens[actionIndex]))
            return ReviewInputContract.ParseGesture(tokens[actionIndex], tokens.Skip(actionIndex + 1).ToArray()) is not null;
        if (!transportRequest && tokens.Count == 4 && tokens[2] == "cancel")
            return ReviewTransportToken.IsRequestId(tokens[3]);
        if (tokens.Count >= actionIndex + 4 && tokens[actionIndex] == "chord"
            && int.TryParse(tokens[actionIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int duration))
            return ProjectReviewInputService.Validate(new(ReviewInputContract.ChordAction,
                null, null, null, null, tokens.Skip(actionIndex + 3).ToArray(), duration,
                tokens[actionIndex + 2])) is null;

        if (tokens.Count == actionIndex + 2
            && string.Equals(tokens[actionIndex], "press", StringComparison.Ordinal))
        {
            return IsAsciiAlphaNumeric(tokens[actionIndex + 1], maximumLength: 64)
                && (!transportRequest
                    || tokens[actionIndex + 1] is not (
                        "MouseWheelUp" or "MouseWheelDown"));
        }

        if (transportRequest
            && tokens.Count == actionIndex + 2
            && string.Equals(tokens[actionIndex], "wheel", StringComparison.Ordinal))
        {
            return tokens[actionIndex + 1] is "up" or "down";
        }

        if (!string.Equals(tokens[actionIndex], "cursor", StringComparison.Ordinal))
        {
            return false;
        }

        return tokens.Count == actionIndex + 2
                && string.Equals(tokens[actionIndex + 1], "clear", StringComparison.Ordinal)
            || tokens.Count == actionIndex + 3
                && TryParseUnsignedCoordinate(tokens[actionIndex + 1])
                && TryParseUnsignedCoordinate(tokens[actionIndex + 2]);
    }

    private static bool IsViewportScreenshotCommand(IReadOnlyList<string> tokens) =>
        tokens.Count == 4
            && string.Equals(tokens[1], "screenshot", StringComparison.Ordinal)
            && string.Equals(tokens[2], "viewport", StringComparison.Ordinal)
            && IsScreenshotLabel(tokens[3])
        || tokens.Count == 6
            && string.Equals(tokens[1], "screenshot", StringComparison.Ordinal)
            && string.Equals(tokens[2], "capture", StringComparison.Ordinal)
            && ReviewTransportToken.IsRequestId(tokens[3])
            && string.Equals(
                tokens[4],
                ReviewScreenshotContract.ViewportMode,
                StringComparison.Ordinal)
                && IsScreenshotLabel(tokens[5]);

    private static bool IsNetworkLifecycleCommand(IReadOnlyList<string> tokens) =>
        tokens.Count == 3
        && string.Equals(tokens[1], "network", StringComparison.Ordinal)
        && tokens[2] is "leave" or "join" or "status";

    private static bool TryParseUnsignedCoordinate(string value) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _);

    private static bool IsScreenshotLabel(string value) =>
        value.Length is >= 1 and <= 64
        && value.All(character =>
            IsAsciiAlphaNumeric(character)
            || character is '-' or '_');

    private static bool IsAsciiAlphaNumeric(string value, int maximumLength) =>
        value.Length >= 1
        && value.Length <= maximumLength
        && value.All(IsAsciiAlphaNumeric);

    private static bool IsAsciiAlphaNumeric(char character) =>
        (character >= 'a' && character <= 'z')
        || (character >= 'A' && character <= 'Z')
        || (character >= '0' && character <= '9');
}

internal enum ProjectReviewConsoleInputStatus
{
    /// <summary>The exact review ran the command on its game thread.</summary>
    Written = 0,
    InvalidRequest = 10,

    /// <summary>No command endpoint was published or reachable; nothing was sent.</summary>
    EndpointUnavailable = 11,

    /// <summary>The endpoint or the game-side dispatcher refused the command before running it.</summary>
    Rejected = 12,

    /// <summary>The command was withdrawn before the game thread started it.</summary>
    NotExecuted = 13,

    /// <summary>The selected local screen does not exist; the command did not run.</summary>
    ScreenUnavailable = 14,

    /// <summary>The command ran but its dispatcher failed.</summary>
    ExecutedWithFailure = 20,

    /// <summary>The request may have run; it must not be retried automatically.</summary>
    DeliveryUnknown = 30,
}

internal sealed record ProjectReviewConsoleInputResult(
    ProjectReviewConsoleInputStatus Status,
    string? Error = null)
{
    public bool? CommandWritten => Status switch
    {
        ProjectReviewConsoleInputStatus.Written
            or ProjectReviewConsoleInputStatus.ExecutedWithFailure => true,
        ProjectReviewConsoleInputStatus.DeliveryUnknown => null,
        _ => false,
    };
}

internal enum ReviewResponseReadStatus
{
    Pending,
    Available,
    Invalid,
}

internal sealed record ReviewResponseRead(
    ReviewResponseReadStatus Status,
    byte[]? Bytes = null,
    string? Error = null);

/// <summary>Takes one published review response, at most once.</summary>
internal interface IReviewResponseReader
{
    ReviewResponseRead TryTake(string responsePath, int maximumBytes);
}

internal interface IProjectReviewConsoleInputSender
{
    ProjectReviewConsoleInputResult SendLine(
        OwnedProcessIdentity expected,
        string line);
}

/// <summary>
/// Sends one review command line to the exact launch's loopback command endpoint. The port comes
/// from the launch-bound status marker and the bearer token from the owned lab state; the reply
/// must name the same launch.
/// </summary>
internal sealed class HttpProjectReviewCommandSender(
    int? port,
    string? token,
    string launchId) : IProjectReviewConsoleInputSender, IReviewResponseReader
{
    private const int MaximumReplyBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HttpProjectReviewCommandSender For(LiveLabState state, GameSideStatusReport? gameSide) =>
        new(gameSide?.HttpPort, state.HttpToken, state.LaunchId);

    public ProjectReviewConsoleInputResult SendLine(OwnedProcessIdentity expected, string line)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string? validationError = ProjectReviewConsoleLine.ValidationError(line);
        if (validationError is not null)
        {
            return new(ProjectReviewConsoleInputStatus.InvalidRequest, validationError);
        }

        if (port is not (>= 1 and <= 65535) || !ReviewHttpContract.IsToken(token))
        {
            return new(
                ProjectReviewConsoleInputStatus.EndpointUnavailable,
                "The exact review has not published its command endpoint; no command was sent.");
        }

        // Only a failure before the TCP connection exists proves that nothing was sent.
        bool connected = false;
        using var client = new HttpClient(
            new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                ConnectCallback = async (context, cancellation) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, cancellation).ConfigureAwait(false);
                        connected = true;
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            },
            disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
        string endpoint = string.Create(
            CultureInfo.InvariantCulture,
            $"http://127.0.0.1:{port.Value}{ReviewHttpContract.CommandPath}");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint))
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(new ReviewHttpCommandBody(line), JsonOptions)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = client.Send(request);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return connected
                ? Unknown($"The command endpoint did not answer: {exception.Message}")
                : new(
                    ProjectReviewConsoleInputStatus.EndpointUnavailable,
                    $"The exact review's command endpoint could not be reached; no command was sent. {exception.Message}");
        }

        using (response)
        {
            ReviewHttpCommandReply? reply = ReadReply(response);
            int status = (int)response.StatusCode;
            if (status is 200 or 409 or 504 && reply is null)
            {
                return Unknown("The command endpoint returned an invalid reply.");
            }

            if (reply is not null && !string.Equals(reply.LaunchId, launchId, StringComparison.Ordinal))
            {
                return Unknown("The command endpoint answered for a different launch.");
            }

            return (status, reply?.Outcome) switch
            {
                (200, ReviewHttpContract.Executed) => new(ProjectReviewConsoleInputStatus.Written),
                (200, ReviewHttpContract.Rejected) => new(
                    ProjectReviewConsoleInputStatus.Rejected,
                    reply!.Message ?? "The game-side dispatcher rejected the command; it did not run."),
                (200, ReviewHttpContract.Failed) => new(
                    ProjectReviewConsoleInputStatus.ExecutedWithFailure,
                    $"The command ran but its dispatcher failed: {reply!.Message}"),
                (409, ReviewHttpContract.ScreenUnavailable) => new(
                    ProjectReviewConsoleInputStatus.ScreenUnavailable,
                    reply!.Message ?? "The selected local screen is not active; the command did not run."),
                (504, ReviewHttpContract.NotExecuted) => new(
                    ProjectReviewConsoleInputStatus.NotExecuted,
                    reply!.Message ?? "The game thread did not run the command in time; it was withdrawn."),
                (400 or 401 or 403 or 404 or 405 or 411 or 413 or 415 or 429 or 431 or 503, _) => new(
                    ProjectReviewConsoleInputStatus.Rejected,
                    reply?.Message
                        ?? $"The command endpoint rejected the request with HTTP {status}; the command did not run."),
                _ => Unknown(reply?.Message ?? $"The command endpoint returned HTTP {status}."),
            };
        }
    }

    /// <summary>Takes the response with the path's file name from the exact launch's endpoint.</summary>
    public ReviewResponseRead TryTake(string responsePath, int maximumBytes)
    {
        string name = Path.GetFileName(responsePath);
        if (!ReviewHttpContract.IsResponseName(name))
        {
            return new(ReviewResponseReadStatus.Invalid, Error: "The response name is invalid.");
        }

        if (port is not (>= 1 and <= 65535) || !ReviewHttpContract.IsToken(token))
        {
            return new(ReviewResponseReadStatus.Invalid, Error: "The exact review has not published its command endpoint.");
        }

        try
        {
            using var client = new HttpClient(
                new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(3),
                },
                disposeHandler: true)
            {
                Timeout = RequestTimeout,
            };
            string endpoint = string.Create(
                CultureInfo.InvariantCulture,
                $"http://127.0.0.1:{port.Value}{ReviewHttpContract.ResponsePathPrefix}{name}");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = client.Send(request);
            // Not yet published, or the endpoint is busy: keep waiting within the caller's bound.
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.ServiceUnavailable
                or System.Net.HttpStatusCode.TooManyRequests)
            {
                return new(ReviewResponseReadStatus.Pending);
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return new(
                    ReviewResponseReadStatus.Invalid,
                    Error: $"The command endpoint returned HTTP {(int)response.StatusCode} for the response.");
            }

            byte[]? bytes = ReadBounded(response, maximumBytes);
            return bytes is { Length: > 0 }
                ? new(ReviewResponseReadStatus.Available, bytes)
                : new(ReviewResponseReadStatus.Invalid, Error: "The response is empty or exceeds its bounded maximum.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            // A transient transport failure leaves the response in the mailbox; retry within the bound.
            return new(ReviewResponseReadStatus.Pending, Error: exception.Message);
        }
    }

    private static byte[]? ReadBounded(HttpResponseMessage response, int maximumBytes)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > maximumBytes)
        {
            return null;
        }

        using Stream stream = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ProjectReviewConsoleInputResult Unknown(string message) =>
        new(
            ProjectReviewConsoleInputStatus.DeliveryUnknown,
            $"{message} The command may have run; do not retry it automatically.");

    private static ReviewHttpCommandReply? ReadReply(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength is > MaximumReplyBytes)
        {
            return null;
        }

        try
        {
            using Stream stream = response.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaximumReplyBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0)
            {
                return null;
            }

            ReviewHttpCommandReply? reply =
                JsonSerializer.Deserialize<ReviewHttpCommandReply>(buffer.ToArray(), JsonOptions);
            return reply is { SchemaVersion: ReviewHttpContract.SchemaVersion, LaunchId: not null, Outcome: not null }
                ? reply
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or HttpRequestException)
        {
            return null;
        }
    }
}
