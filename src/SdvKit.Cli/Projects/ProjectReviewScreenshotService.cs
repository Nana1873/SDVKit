using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal sealed record ProjectReviewScreenshotProblem(
    string Code,
    string Message);

internal sealed record ProjectReviewScreenshotCapture(
    string Mode,
    string Label,
    string FileName,
    DateTimeOffset CapturedAtUtc,
    int Width,
    int Height,
    int EncodedBytes,
    string Sha256,
    byte[] PngBytes);

internal sealed record ProjectReviewScreenshotResult(
    ProjectReviewScreenshotCapture? Capture,
    IReadOnlyList<ProjectReviewScreenshotProblem> Problems)
{
    public bool Succeeded => Capture is not null && Problems.Count == 0;
}

internal static class ProjectReviewScreenshotService
{
    private const int MaximumJsonDepth = 8;
    private static readonly TimeSpan PngSettleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FileTimestampTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FutureTimestampTolerance = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = MaximumJsonDepth,
    };
    private static readonly JsonDocumentOptions ResponseDocumentOptions = new()
    {
        MaxDepth = MaximumJsonDepth,
    };
    private static readonly HashSet<string> EnvelopeProperties = PropertySet(
        "schemaVersion",
        "requestId",
        "report");
    private static readonly HashSet<string> ReportProperties = PropertySet(
        "schemaVersion",
        "state",
        "mode",
        "label",
        "fileName",
        "capturedAtUtc",
        "problems");
    private static readonly HashSet<string> ProblemProperties = PropertySet(
        "code",
        "message");

    public static ProjectReviewScreenshotResult Execute(
        ReviewScreenshotCaptureQuery query,
        string topology,
        string? role,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? responseTimeout = null,
        Func<DateTimeOffset>? utcNow = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);
        cancellationToken.ThrowIfCancellationRequested();

        ProjectReviewScreenshotProblem? queryProblem = ValidateQuery(query);
        if (queryProblem is not null)
        {
            return Failure(queryProblem);
        }

        Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        LiveLabPaths paths;
        string expectedFileName = ReviewScreenshotContract.FileName(query.Label);
        try
        {
            paths = ResolveRolePaths(labRoot, topology, role);
            string expectedPath = ExpectedPngPath(paths, expectedFileName);
            ValidateScreenshotPath(paths, expectedPath);
            if (EntryExists(expectedPath))
            {
                return Failure(Problem(
                    "screenshotAlreadyExists",
                    "The exact isolated screenshot target already exists; it was not overwritten."));
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(Problem(
                "screenshotPathInvalid",
                $"The selected role's isolated screenshot path is invalid ({exception.GetType().Name})."));
        }

        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewScreenshotContract.ResponsePath(
            paths.RuntimePath,
            requestId);
        string command = BuildCommand(requestId, query);
        DateTimeOffset requestedAtUtc = clock().ToUniversalTime();
        ProjectReviewResponseTransportResult<ReviewScreenshotResponseEnvelope> transported =
            ProjectReviewResponseTransport.Execute(
                command,
                responsePath,
                ReviewScreenshotContract.MaximumResponseBytes,
                "screenshot",
                "review-screenshot",
                labRoot,
                DeserializeResponse,
                envelope => MatchesResponse(
                    envelope,
                    requestId,
                    query,
                    requestedAtUtc),
                inputSender,
                delay,
                responseTimeout,
                topology,
                role,
                cancellationToken);

        if (transported.Response is null)
        {
            return Failure(
                transported.Problems
                    .Select(problem => Problem(problem.Code, problem.Message))
                    .ToArray());
        }

        ReviewScreenshotReport report = transported.Response.Report;
        if (!string.Equals(report.State, "ready", StringComparison.Ordinal)
            || report.Problems.Count != 0
            || report.FileName is null)
        {
            return Failure(
                report.Problems.Count == 0
                    ? [Problem(
                        "screenshotCaptureFailed",
                        "Stardew did not create a confirmed screenshot for the request.")]
                    : report.Problems
                        .Select(problem => Problem(problem.Code, problem.Message))
                        .ToArray());
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            LiveLabPaths verifiedPaths = ResolveRolePaths(labRoot, topology, role);
            string expectedPath = ExpectedPngPath(verifiedPaths, expectedFileName);
            ValidateScreenshotPath(verifiedPaths, expectedPath);
            var settle = Stopwatch.StartNew();
            Action<TimeSpan> wait = delay ?? Thread.Sleep;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateScreenshotPath(verifiedPaths, expectedPath);
                DateTimeOffset observedAtUtc = clock().ToUniversalTime();
                if (report.CapturedAtUtc > observedAtUtc + FutureTimestampTolerance)
                {
                    return Failure(Problem(
                        "screenshotResultStale",
                        "The screenshot response timestamp is not fresh for this request."));
                }

                ProjectReviewScreenshotCapture? capture = null;
                try
                {
                    capture = ReadAndValidatePng(
                        expectedPath,
                        query,
                        expectedFileName,
                        report.CapturedAtUtc,
                        requestedAtUtc,
                        observedAtUtc,
                        cancellationToken);
                }
                catch (IOException) when (settle.Elapsed < PngSettleTimeout)
                {
                    // Stardew's map capture can publish its response while the PNG
                    // encoder still owns the exact create-new file briefly.
                }

                if (capture is not null)
                {
                    return new ProjectReviewScreenshotResult(capture, []);
                }
                if (settle.Elapsed >= PngSettleTimeout)
                {
                    return Failure(Problem(
                        "screenshotPngInvalid",
                        "The exact isolated screenshot is not a fresh bounded 8-bit RGB or RGBA PNG."));
                }

                wait(TimeSpan.FromMilliseconds(50));
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(Problem(
                "screenshotPngInvalid",
                $"The exact isolated screenshot could not be validated ({exception.GetType().Name})."));
        }
    }

    internal static string BuildCommand(
        string requestId,
        ReviewScreenshotCaptureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-screenshot request ID is invalid.",
                nameof(requestId));
        }
        if (ValidateQuery(query) is ProjectReviewScreenshotProblem problem)
        {
            throw new ArgumentException(problem.Message, nameof(query));
        }

        string command = string.Join(
            " ",
            "sdvkit",
            "screenshot",
            "capture",
            requestId,
            query.Mode,
            query.Label);
        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        return command;
    }

    internal static ReviewScreenshotResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using (JsonDocument document = JsonDocument.Parse(bytes, ResponseDocumentOptions))
        {
            ValidateEnvelopeShape(document.RootElement);
        }

        return JsonSerializer.Deserialize<ReviewScreenshotResponseEnvelope>(
            bytes,
            ResponseJsonOptions);
    }

    internal static bool MatchesResponse(
        ReviewScreenshotResponseEnvelope? envelope,
        string requestId,
        ReviewScreenshotCaptureQuery query,
        DateTimeOffset requestedAtUtc)
    {
        if (envelope is null
            || ValidateQuery(query) is not null
            || !ReviewTransportToken.IsRequestId(requestId)
            || envelope.SchemaVersion != ReviewScreenshotContract.SchemaVersion
            || !string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal))
        {
            return false;
        }

        ReviewScreenshotReport? report = envelope.Report;
        if (report is null
            || report.Problems is null
            || report.SchemaVersion != ReviewScreenshotContract.SchemaVersion
            || !string.Equals(report.Mode, query.Mode, StringComparison.Ordinal)
            || !string.Equals(report.Label, query.Label, StringComparison.Ordinal)
            || report.CapturedAtUtc.Offset != TimeSpan.Zero
            || report.CapturedAtUtc < requestedAtUtc)
        {
            return false;
        }

        bool ready = string.Equals(report.State, "ready", StringComparison.Ordinal)
            && string.Equals(
                report.FileName,
                ReviewScreenshotContract.FileName(query.Label),
                StringComparison.Ordinal)
            && report.Problems.Count == 0;
        bool blocked = string.Equals(report.State, "blocked", StringComparison.Ordinal)
            && report.FileName is null
            && report.Problems.Count == 1
            && IsProblem(report.Problems[0]);
        return ready || blocked;
    }

    internal static ProjectReviewScreenshotProblem? ValidateQuery(
        ReviewScreenshotCaptureQuery? query)
    {
        if (query is null || !ReviewScreenshotContract.IsMode(query.Mode))
        {
            return Problem(
                "screenshotModeInvalid",
                "Screenshot mode must be exactly 'map' or 'viewport'.");
        }
        if (!ReviewScreenshotContract.IsLabel(query.Label))
        {
            return Problem(
                "screenshotLabelInvalid",
                "A screenshot label must contain 1-64 ASCII letters, digits, '-' or '_' only.");
        }

        return null;
    }

    internal static ProjectReviewScreenshotCapture? ReadAndValidatePng(
        string expectedPath,
        ReviewScreenshotCaptureQuery query,
        string expectedFileName,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes attributes = File.GetAttributes(expectedPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) != 0)
        {
            return null;
        }

        DateTimeOffset lastWriteUtc = File.GetLastWriteTimeUtc(expectedPath);
        if (lastWriteUtc < requestedAtUtc - FileTimestampTolerance
            || lastWriteUtc > observedAtUtc + FutureTimestampTolerance
            || lastWriteUtc > capturedAtUtc + FutureTimestampTolerance)
        {
            return null;
        }

        byte[] bytes;
        using (var stream = new FileStream(
            expectedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan))
        {
            long length = stream.Length;
            if (length is < 1 or > ReviewScreenshotContract.MaximumPngBytes)
            {
                return null;
            }

            bytes = new byte[checked((int)length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    return null;
                }

                offset += read;
            }
            if (stream.ReadByte() != -1 || stream.Length != length)
            {
                return null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes finalAttributes = File.GetAttributes(expectedPath);
        if ((finalAttributes & FileAttributes.ReparsePoint) != 0
            || (finalAttributes & FileAttributes.Directory) != 0
            || File.GetLastWriteTimeUtc(expectedPath) != lastWriteUtc)
        {
            return null;
        }

        using var png = new MemoryStream(bytes, writable: false);
        if (!ReviewTexturePngValidator.TryValidateRgbOrRgba8(
                png,
                ReviewScreenshotContract.MaximumPngBytes,
                ReviewScreenshotContract.MaximumDimension,
                ReviewScreenshotContract.MaximumPixels,
                out ReviewTexturePngInfo? info)
            || info is null)
        {
            return null;
        }

        string sha256 = "sha256:"
            + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ProjectReviewScreenshotCapture(
            query.Mode,
            query.Label,
            expectedFileName,
            capturedAtUtc,
            info.Width,
            info.Height,
            bytes.Length,
            sha256,
            bytes);
    }

    internal static LiveLabPaths ResolveRolePaths(
        string labRoot,
        string topology,
        string? role)
    {
        LiveLabPaths paths = LiveLabPaths.Resolve(labRoot);
        if (string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            && role is null)
        {
            return paths;
        }
        if (string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
            && role is not null
            && NetworkTwoContract.IsRole(role))
        {
            return LiveLabPaths.ResolveNetworkRole(paths, role);
        }

        throw new ArgumentException(
            "The screenshot topology and role selection is invalid.");
    }

    internal static string ExpectedPngPath(
        LiveLabPaths paths,
        string expectedFileName)
    {
        string screenshotDirectory = Path.GetFullPath(
            Path.Combine(paths.StardewDataPath, "Screenshots"));
        string expectedPath = Path.GetFullPath(
            Path.Combine(screenshotDirectory, expectedFileName));
        if (!string.Equals(
                Path.GetDirectoryName(expectedPath),
                screenshotDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The screenshot target escaped the selected role's isolated directory.");
        }

        return expectedPath;
    }

    internal static void ValidateScreenshotPath(
        LiveLabPaths paths,
        string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);

        string screenshotDirectory = Path.GetDirectoryName(expectedPath)
            ?? throw new InvalidDataException(
                "The screenshot target has no parent directory.");
        string[] ancestors =
        [
            paths.UserProfilePath,
            Path.Combine(paths.UserProfilePath, "AppData"),
            paths.RoamingAppDataPath,
            paths.StardewDataPath,
            screenshotDirectory,
        ];
        foreach (string ancestor in ancestors)
        {
            if (!EntryExists(ancestor))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(ancestor);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException(
                    "The isolated screenshot path contains a non-regular directory.");
            }
        }

        if (Directory.Exists(screenshotDirectory))
        {
            LiveLabPaths.RejectReparsePointsBelow(screenshotDirectory);
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void ValidateEnvelopeShape(JsonElement root)
    {
        RequireExactObject(root, EnvelopeProperties);
        RequiredInt32(root, "schemaVersion");
        RequiredString(root, "requestId");

        JsonElement report = root.GetProperty("report");
        RequireExactObject(report, ReportProperties);
        RequiredInt32(report, "schemaVersion");
        RequiredString(report, "state");
        RequiredString(report, "mode");
        RequiredString(report, "label");
        JsonElement fileName = report.GetProperty("fileName");
        if (fileName.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
        {
            throw new InvalidDataException(
                "The review-screenshot response has an invalid file name.");
        }
        if (fileName.ValueKind == JsonValueKind.String)
        {
            _ = RequiredString(fileName);
        }
        _ = RequiredString(report, "capturedAtUtc");

        JsonElement problems = report.GetProperty("problems");
        if (problems.ValueKind != JsonValueKind.Array
            || problems.GetArrayLength() > ReviewScreenshotContract.MaximumProblemCount)
        {
            throw new InvalidDataException(
                "The review-screenshot response has an invalid problem list.");
        }
        foreach (JsonElement problem in problems.EnumerateArray())
        {
            RequireExactObject(problem, ProblemProperties);
            RequiredString(problem, "code");
            RequiredString(problem, "message");
        }
    }

    private static void RequireExactObject(
        JsonElement value,
        HashSet<string> requiredProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The review-screenshot response has an invalid JSON object shape.");
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!requiredProperties.Contains(property.Name)
                || !observed.Add(property.Name))
            {
                throw new InvalidDataException(
                    "The review-screenshot response has an unknown or duplicate JSON member.");
            }
        }
        if (observed.Count != requiredProperties.Count)
        {
            throw new InvalidDataException(
                "The review-screenshot response is missing a required JSON member.");
        }
    }

    private static int RequiredInt32(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int result))
        {
            throw new InvalidDataException(
                "The review-screenshot response has an invalid integer member.");
        }

        return result;
    }

    private static string RequiredString(JsonElement value, string propertyName) =>
        RequiredString(value.GetProperty(propertyName));

    private static string RequiredString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "The review-screenshot response has an invalid string member.");
        }

        string result = value.GetString()
            ?? throw new InvalidDataException(
                "The review-screenshot response has a null string member.");
        if (!ReviewTransportText.IsWellFormedUtf16(result))
        {
            throw new InvalidDataException(
                "The review-screenshot response has malformed Unicode text.");
        }

        return result;
    }

    private static bool IsProblem(ReviewScreenshotProblem? problem) =>
        problem is not null
        && problem.Code.Length is >= 1 and <= ReviewScreenshotContract.MaximumProblemCodeLength
        && problem.Code.All(character =>
            character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9')
        && problem.Message.Length is >= 1 and <= ReviewScreenshotContract.MaximumProblemMessageLength
        && !problem.Message.Any(char.IsControl)
        && ReviewTransportText.IsWellFormedUtf16(problem.Message);

    private static HashSet<string> PropertySet(params string[] names) =>
        new(names, StringComparer.Ordinal);

    private static ProjectReviewScreenshotProblem Problem(
        string code,
        string message) => new(code, message);

    private static ProjectReviewScreenshotResult Failure(
        params ProjectReviewScreenshotProblem[] problems) => new(null, problems);

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
