using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SdvKit.GameSide;

/// <summary>Shared contract between the CLI sender and the in-process command endpoint.</summary>
internal static class ReviewHttpContract
{
    public const int SchemaVersion = 1;
    public const string CommandPath = "/v1/command";
    public const string TokenVariable = "SDVKIT_LAB_HTTP_TOKEN";
    public const int TokenLength = 43;
    public const int MaximumBodyBytes = 16 * 1024;
    public const int MaximumLineLength = 4096;
    public const string Executed = "executed";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string NotExecuted = "notExecuted";
    public const string ScreenUnavailable = "screenUnavailable";
    public const string Unknown = "unknown";

    public static bool IsToken(string? value) =>
        value is { Length: TokenLength }
        && value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    public static string CreateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

internal sealed record ReviewHttpCommandBody(string? Line);

internal sealed record ReviewHttpCommandReply(int SchemaVersion, string LaunchId, string Outcome, string? Message = null);

/// <summary>Parses a console line exactly like SMAPI's command manager does.</summary>
internal static class ReviewCommandLine
{
    public const string RootCommand = "sdvkit";
    private const string ScreenPrefix = "screen=";

    /// <summary>SMAPI splits on whitespace; double quotes group text and are removed.</summary>
    public static string[] Split(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        bool inQuotes = false;
        var arguments = new List<string>();
        var current = new StringBuilder();
        foreach (char character in line)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && char.IsWhiteSpace(character))
            {
                arguments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        arguments.Add(current.ToString());
        return arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)).ToArray();
    }

    /// <summary>
    /// Returns the arguments after the root command with SMAPI's first <c>screen=N</c> argument removed.
    /// </summary>
    public static bool TryParse(string line, out string[] arguments, out int screenId, out string error)
    {
        arguments = [];
        screenId = 0;
        string[] all = Split(line);
        if (all.Length == 0 || !string.Equals(all[0], RootCommand, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Only '{RootCommand}' review commands are accepted.";
            return false;
        }

        string[] rest = all.Skip(1).ToArray();
        for (int index = 0; index < rest.Length; index++)
        {
            if (!rest[index].StartsWith(ScreenPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string raw = rest[index][ScreenPrefix.Length..];
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out screenId))
            {
                error = $"Invalid screen ID format: {raw}";
                return false;
            }

            rest = rest.Take(index).Concat(rest.Skip(index + 1)).ToArray();
            break;
        }

        arguments = rest;
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Commands received over loopback wait here until the game thread runs them on their screen.
/// A command that has not started can be withdrawn, so a timeout before execution is a guaranteed
/// non-execution rather than an unknown outcome.
/// </summary>
internal sealed class ReviewCommandQueue
{
    public const int MaximumPending = 8;

    private readonly object _gate = new();
    private readonly List<PendingReviewCommand> _pending = [];

    public bool TryEnqueue(string[] arguments, int screenId, out PendingReviewCommand? command)
    {
        lock (_gate)
        {
            if (_pending.Count >= MaximumPending)
            {
                command = null;
                return false;
            }

            command = new PendingReviewCommand(arguments, screenId);
            _pending.Add(command);
            return true;
        }
    }

    /// <summary>Withdraws a command that has not started yet.</summary>
    public bool TryWithdraw(PendingReviewCommand command)
    {
        lock (_gate)
        {
            return _pending.Remove(command);
        }
    }

    /// <summary>Runs on the game thread once per screen update.</summary>
    /// <param name="dispatch">Returns false when the dispatcher rejected the command before running it.</param>
    public void Drain(int currentScreenId, Func<int, bool> screenExists, Func<string[], bool> dispatch)
    {
        ArgumentNullException.ThrowIfNull(screenExists);
        ArgumentNullException.ThrowIfNull(dispatch);
        List<PendingReviewCommand> ready;
        List<PendingReviewCommand> unavailable;
        lock (_gate)
        {
            ready = _pending.Where(command => command.ScreenId == currentScreenId).ToList();
            unavailable = currentScreenId == 0
                ? _pending.Where(command => command.ScreenId != 0 && !screenExists(command.ScreenId)).ToList()
                : [];
            foreach (PendingReviewCommand command in ready.Concat(unavailable))
            {
                _pending.Remove(command);
            }
        }

        foreach (PendingReviewCommand command in unavailable)
        {
            command.Complete(ReviewHttpContract.ScreenUnavailable, $"There is no active screen with ID {command.ScreenId}.");
        }

        foreach (PendingReviewCommand command in ready)
        {
            try
            {
                if (dispatch(command.Arguments))
                {
                    command.Complete(ReviewHttpContract.Executed, null);
                }
                else
                {
                    command.Complete(
                        ReviewHttpContract.Rejected,
                        "The game-side dispatcher rejected the command before running it; see the isolated SMAPI log.");
                }
            }
            catch (Exception exception)
            {
                command.Complete(ReviewHttpContract.Failed, exception.GetBaseException().Message);
            }
        }
    }
}

internal sealed class PendingReviewCommand(string[] arguments, int screenId)
{
    private readonly TaskCompletionSource<(string Outcome, string? Message)> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string[] Arguments { get; } = arguments;

    public int ScreenId { get; } = screenId;

    public Task<(string Outcome, string? Message)> Completion => _completion.Task;

    public void Complete(string outcome, string? message) => _completion.TrySetResult((outcome, message));
}

/// <summary>
/// Loopback-only HTTP endpoint for review commands. It accepts one bounded JSON request per
/// connection from the owning CLI, authenticated by the per-launch bearer token.
/// </summary>
internal sealed class ReviewHttpEndpoint : IDisposable
{
    private const int HeaderLimit = 8192;
    private const int MaximumConnections = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _launchId;
    private readonly byte[] _token;
    private readonly ReviewCommandQueue _queue;
    private readonly TimeSpan _executionTimeout;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TcpListener _listener;
    private int _connections;

    private ReviewHttpEndpoint(
        string launchId,
        string token,
        ReviewCommandQueue queue,
        TimeSpan executionTimeout,
        Action<string> log)
    {
        _launchId = launchId;
        _token = Encoding.ASCII.GetBytes(token);
        _queue = queue;
        _executionTimeout = executionTimeout;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(MaximumConnections);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public static ReviewHttpEndpoint Start(
        string launchId,
        string token,
        ReviewCommandQueue queue,
        Action<string> log,
        TimeSpan? executionTimeout = null)
    {
        if (!Guid.TryParseExact(launchId, "N", out _))
        {
            throw new ArgumentException("The review launch ID is invalid.", nameof(launchId));
        }

        if (!ReviewHttpContract.IsToken(token))
        {
            throw new ArgumentException("The review command token is invalid.", nameof(token));
        }

        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(log);
        return new ReviewHttpEndpoint(launchId, token, queue, executionTimeout ?? TimeSpan.FromSeconds(5), log);
    }

    public void Dispose()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _stopping.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (!_stopping.IsCancellationRequested)
                {
                    _log($"SDVKit review command endpoint stopped accepting connections: {exception.Message}");
                }

                return;
            }

            if (Interlocked.Increment(ref _connections) > MaximumConnections)
            {
                Interlocked.Decrement(ref _connections);
                _ = RefuseBusy(client);
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Handle(client).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _connections);
                }
            });
        }
    }

    /// <summary>Answers an over-limit connection with 503 so the client knows nothing ran.</summary>
    private async Task RefuseBusy(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var output = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                output.CancelAfter(TimeSpan.FromSeconds(1));
                await Write(client.GetStream(), 503, null, output.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that cannot receive the refusal reports an unknown outcome; nothing ran.
            }
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            int status;
            object? body;
            try
            {
                using var input = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                input.CancelAfter(TimeSpan.FromSeconds(5));
                (status, body) = await Process(stream, input.Token).ConfigureAwait(false);
            }
            catch (EndpointRejection rejection)
            {
                (status, body) = (rejection.Status, null);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                using var output = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                output.CancelAfter(TimeSpan.FromSeconds(5));
                await Write(stream, status, body, output.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client disconnected; the command outcome is already final on the game side.
            }
        }
    }

    private async Task<(int Status, object? Body)> Process(NetworkStream stream, CancellationToken cancellation)
    {
        (string method, string target, Dictionary<string, string> headers) =
            await ReadHeaders(stream, cancellation).ConfigureAwait(false);
        string authority = $"127.0.0.1:{Port}";
        if (!headers.TryGetValue("Host", out string? host) || host != authority || headers.ContainsKey("Origin"))
        {
            throw new EndpointRejection(403);
        }

        if (!Authenticated(headers.GetValueOrDefault("Authorization")))
        {
            throw new EndpointRejection(401);
        }

        if (target != ReviewHttpContract.CommandPath)
        {
            throw new EndpointRejection(404);
        }

        if (method != "POST")
        {
            throw new EndpointRejection(405);
        }

        if (!headers.TryGetValue("Content-Type", out string? contentType)
            || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new EndpointRejection(415);
        }

        if (headers.ContainsKey("Transfer-Encoding")
            || !headers.TryGetValue("Content-Length", out string? lengthText)
            || !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out int length))
        {
            throw new EndpointRejection(411);
        }

        if (length > ReviewHttpContract.MaximumBodyBytes)
        {
            throw new EndpointRejection(413);
        }

        byte[] bytes = new byte[length];
        int read = 0;
        while (read < length)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(read), cancellation).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndpointRejection(400);
            }

            read += count;
        }

        ReviewHttpCommandBody? request;
        try
        {
            request = JsonSerializer.Deserialize<ReviewHttpCommandBody>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            throw new EndpointRejection(400);
        }

        if (request?.Line is not { Length: > 0 and <= ReviewHttpContract.MaximumLineLength } line)
        {
            return (400, Reply(ReviewHttpContract.NotExecuted, "The request needs one non-empty command line."));
        }

        if (!ReviewCommandLine.TryParse(line, out string[] arguments, out int screenId, out string error))
        {
            return (400, Reply(ReviewHttpContract.NotExecuted, error));
        }

        if (!_queue.TryEnqueue(arguments, screenId, out PendingReviewCommand? command))
        {
            return (429, Reply(ReviewHttpContract.NotExecuted, "Too many review commands are waiting for the game thread."));
        }

        Task finished = await Task.WhenAny(command!.Completion, Task.Delay(_executionTimeout, _stopping.Token)).ConfigureAwait(false);
        if (finished != command.Completion && _queue.TryWithdraw(command))
        {
            return (504, Reply(ReviewHttpContract.NotExecuted, "The game thread did not run the command in time; it was withdrawn and will not run."));
        }

        finished = await Task.WhenAny(command.Completion, Task.Delay(_executionTimeout, _stopping.Token)).ConfigureAwait(false);
        if (finished != command.Completion)
        {
            return (504, Reply(ReviewHttpContract.Unknown, "The command started on the game thread but did not finish in time."));
        }

        (string outcome, string? message) = command.Completion.Result;
        int status = outcome switch
        {
            ReviewHttpContract.Executed or ReviewHttpContract.Failed or ReviewHttpContract.Rejected => 200,
            ReviewHttpContract.ScreenUnavailable => 409,
            _ => 500,
        };
        return (status, Reply(outcome, message));
    }

    private ReviewHttpCommandReply Reply(string outcome, string? message) =>
        new(ReviewHttpContract.SchemaVersion, _launchId, outcome, message);

    private bool Authenticated(string? header)
    {
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] presented = Encoding.ASCII.GetBytes(header[prefix.Length..]);
        return CryptographicOperations.FixedTimeEquals(presented, _token);
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers)> ReadHeaders(
        NetworkStream stream,
        CancellationToken cancellation)
    {
        byte[] buffer = new byte[HeaderLimit];
        int count = 0;
        while (true)
        {
            if (count == HeaderLimit)
            {
                throw new EndpointRejection(431);
            }

            if (await stream.ReadAsync(buffer.AsMemory(count, 1), cancellation).ConfigureAwait(false) == 0)
            {
                throw new EndpointRejection(400);
            }

            count++;
            if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n' && buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
            {
                break;
            }
        }

        if (buffer.AsSpan(0, count).ToArray().Any(value => value > 126 || (value < 32 && value is not 9 and not 10 and not 13)))
        {
            throw new EndpointRejection(400);
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, count - 4).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[2] != "HTTP/1.1" || lines.Length > 33)
        {
            throw new EndpointRejection(400);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim(' ', '\t')))
            {
                throw new EndpointRejection(400);
            }
        }

        return (requestLine[0], requestLine[1], headers);
    }

    private static async Task Write(NetworkStream stream, int status, object? body, CancellationToken cancellation)
    {
        byte[] content = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        string head =
            $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Length: {content.Length}\r\n"
            + (body is null ? string.Empty : "Content-Type: application/json\r\n")
            + (status == 401 ? "WWW-Authenticate: Bearer realm=\"SDVKit\"\r\n" : string.Empty)
            + "Connection: close\r\nCache-Control: no-store\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellation).ConfigureAwait(false);
        await stream.WriteAsync(content, cancellation).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Internal Server Error",
    };

    private sealed class EndpointRejection(int status) : Exception
    {
        public int Status { get; } = status;
    }
}
