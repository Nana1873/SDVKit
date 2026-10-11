using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.GameSide;

internal static class ReviewScreenshotArguments
{
    internal const string Usage =
        "Usage: sdvkit screenshot <label> | sdvkit screenshot viewport <label>";
    internal const string LabelError =
        "A screenshot label must contain 1-64 ASCII letters, digits, '-' or '_' only.";

    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out ReviewScreenshotRequest? request,
        out string error)
    {
        request = null;
        if (arguments is null
            || arguments.Count < 2
            || !string.Equals(arguments[0], "screenshot", StringComparison.Ordinal))
        {
            error = Usage;
            return false;
        }

        ReviewScreenshotKind kind;
        string label;
        if (arguments.Count == 2)
        {
            kind = ReviewScreenshotKind.Map;
            label = arguments[1];
        }
        else if (arguments.Count == 3
            && string.Equals(arguments[1], "viewport", StringComparison.Ordinal))
        {
            kind = ReviewScreenshotKind.Viewport;
            label = arguments[2];
        }
        else
        {
            error = Usage;
            return false;
        }

        if (!IsValidLabel(label))
        {
            error = LabelError;
            return false;
        }

        request = new ReviewScreenshotRequest(kind, label);
        error = string.Empty;
        return true;
    }

    public static bool IsValidLabel(string? label)
        => ReviewScreenshotContract.IsLabel(label);
}

internal static class ReviewScreenshotTransportArguments
{
    internal const string Usage =
        "Usage: sdvkit screenshot capture <request-id> <map|viewport> <label>";

    public static bool IsTransport(IReadOnlyList<string>? arguments) =>
        arguments is { Count: >= 2 }
        && string.Equals(arguments[0], "screenshot", StringComparison.Ordinal)
        && string.Equals(arguments[1], "capture", StringComparison.Ordinal);

    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out string requestId,
        out ReviewScreenshotCaptureQuery? query,
        out string error)
    {
        requestId = string.Empty;
        query = null;
        if (!IsTransport(arguments)
            || arguments!.Count != 5
            || !ReviewTransportToken.IsRequestId(arguments[2])
            || !ReviewScreenshotContract.IsMode(arguments[3])
            || !ReviewScreenshotContract.IsLabel(arguments[4]))
        {
            error = Usage;
            return false;
        }

        requestId = arguments[2];
        query = new ReviewScreenshotCaptureQuery(arguments[3], arguments[4]);
        error = string.Empty;
        return true;
    }
}

internal enum ReviewScreenshotKind
{
    Map,
    Viewport,
}

internal sealed record ReviewScreenshotRequest(
    ReviewScreenshotKind Kind,
    string Label);

internal interface IReviewScreenshotRuntime
{
    bool IsWorldReady { get; }

    bool CanTakeScreenshots { get; }

    bool ScreenshotBusy { get; }

    string GetScreenshotFolder();

    bool FileExists(string path);

    string? TakeMapScreenshot(string screenshotName);

    bool TryTakeViewportScreenshot(string path, out string error);
}

internal sealed record ReviewScreenshotResult(
    bool Succeeded,
    string Message,
    string? ProblemCode = null,
    string? FileName = null);

internal static class ReviewScreenshotOperation
{
    public static ReviewScreenshotResult Execute(
        ReviewScreenshotRequest request,
        IReviewScreenshotRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        if (!Enum.IsDefined(request.Kind)
            || !ReviewScreenshotArguments.IsValidLabel(request.Label))
        {
            return Failure(
                "screenshotRequestInvalid",
                ReviewScreenshotArguments.LabelError);
        }

        if (request.Kind == ReviewScreenshotKind.Map
            && !runtime.IsWorldReady)
        {
            return Failure(
                "screenshotWorldNotReady",
                "A world must be loaded before taking a map review screenshot.");
        }

        if (request.Kind == ReviewScreenshotKind.Map
            && (!runtime.CanTakeScreenshots || runtime.ScreenshotBusy))
        {
            return Failure(
                "screenshotUnavailable",
                "Stardew cannot take a map screenshot right now.");
        }

        string screenshotName = $"SDVKit-{request.Label}";
        string screenshotFileName = ReviewScreenshotContract.FileName(request.Label);
        string screenshotFolder = runtime.GetScreenshotFolder();
        if (string.IsNullOrWhiteSpace(screenshotFolder)
            || !Path.IsPathFullyQualified(screenshotFolder))
        {
            return Failure(
                "screenshotPathInvalid",
                "Stardew returned an invalid screenshot folder.");
        }

        screenshotFolder = Path.GetFullPath(screenshotFolder);
        string expectedPath = Path.Combine(screenshotFolder, screenshotFileName);
        if (runtime.FileExists(expectedPath))
        {
            return Failure(
                "screenshotAlreadyExists",
                $"Refusing to overwrite existing isolated screenshot '{expectedPath}'.");
        }

        if (request.Kind == ReviewScreenshotKind.Viewport)
        {
            if (!runtime.TryTakeViewportScreenshot(expectedPath, out string viewportError)
                || !runtime.FileExists(expectedPath))
            {
                return Failure(
                    "screenshotCaptureFailed",
                    $"Stardew failed to create the requested viewport screenshot '{expectedPath}': "
                    + viewportError);
            }

            return new ReviewScreenshotResult(
                true,
                $"Created isolated viewport screenshot '{expectedPath}'.",
                FileName: screenshotFileName);
        }

        string? writtenFileName = runtime.TakeMapScreenshot(screenshotName);
        if (!string.Equals(writtenFileName, screenshotFileName, StringComparison.Ordinal)
            || !runtime.FileExists(expectedPath))
        {
            return Failure(
                "screenshotCaptureFailed",
                $"Stardew failed to create the requested map screenshot '{expectedPath}'.");
        }

        return new ReviewScreenshotResult(
            true,
            $"Created isolated map screenshot '{expectedPath}'.",
            FileName: screenshotFileName);
    }

    private static ReviewScreenshotResult Failure(string code, string message) =>
        new(false, message, code);
}

internal static class ReviewScreenshotResponse
{
    public static ReviewScreenshotResponseEnvelope Create(
        string requestId,
        ReviewScreenshotCaptureQuery query,
        ReviewScreenshotResult result,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(result);
        if (!ReviewTransportToken.IsRequestId(requestId)
            || !ReviewScreenshotContract.IsMode(query.Mode)
            || !ReviewScreenshotContract.IsLabel(query.Label))
        {
            throw new ArgumentException("The review-screenshot response identity is invalid.");
        }

        IReadOnlyList<ReviewScreenshotProblem> problems = result.Succeeded
            ? []
            : [Problem(result.ProblemCode)];
        return new ReviewScreenshotResponseEnvelope(
            ReviewScreenshotContract.SchemaVersion,
            requestId,
            new ReviewScreenshotReport(
                ReviewScreenshotContract.SchemaVersion,
                result.Succeeded ? "ready" : "blocked",
                query.Mode,
                query.Label,
                result.Succeeded ? ReviewScreenshotContract.FileName(query.Label) : null,
                capturedAtUtc,
                problems));
    }

    private static ReviewScreenshotProblem Problem(string? code) => code switch
    {
        "screenshotWorldNotReady" => new(
            code,
            "A world must be loaded before taking a map screenshot."),
        "screenshotUnavailable" => new(
            code,
            "Stardew cannot take a map screenshot right now."),
        "screenshotPathInvalid" => new(
            code,
            "The isolated screenshot destination is invalid."),
        "screenshotAlreadyExists" => new(
            code,
            "The exact isolated screenshot target already exists; it was not overwritten."),
        "screenshotRequestInvalid" => new(
            code,
            "The screenshot request is invalid."),
        _ => new(
            "screenshotCaptureFailed",
            "Stardew did not create a confirmed screenshot for the request."),
    };
}

internal static class ReviewScreenshotResponsePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Write(
        string runtimePath,
        ReviewScreenshotResponseEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException("The review runtime path is required.", nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        string responsePath = ReviewScreenshotContract.ResponsePath(
            Path.GetFullPath(runtimePath),
            envelope.RequestId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length is < 1 or > ReviewScreenshotContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The review-screenshot response exceeds its bounded maximum.");
        }
        ReviewResponseMailbox.Publish(responsePath, bytes);
    }

}
