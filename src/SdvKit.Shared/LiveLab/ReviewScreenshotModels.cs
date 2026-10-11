namespace SdvKit.Cli.LiveLab;

internal static class ReviewScreenshotContract
{
    public const int SchemaVersion = 1;
    public const int MaximumLabelLength = 64;
    public const int MaximumResponseBytes = 8 * 1024;
    public const int MaximumPngBytes = 16 * 1024 * 1024;
    public const int MaximumDimension = 8192;
    public const int MaximumPixels = 64 * 1024 * 1024;
    public const int MaximumProblemCount = 1;
    public const int MaximumProblemCodeLength = 64;
    public const int MaximumProblemMessageLength = 256;
    public const string MapMode = "map";
    public const string ViewportMode = "viewport";
    public const string MimeType = "image/png";

    public static bool IsMode(string? value) =>
        string.Equals(value, MapMode, StringComparison.Ordinal)
        || string.Equals(value, ViewportMode, StringComparison.Ordinal);

    public static bool IsLabel(string? value) =>
        value is not null
        && value.Length is >= 1 and <= MaximumLabelLength
        && value.All(character =>
            character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-'
                or '_');

    public static string FileName(string label)
    {
        if (!IsLabel(label))
        {
            throw new ArgumentException(
                "The screenshot label is invalid.",
                nameof(label));
        }

        return $"SDVKit-{label}.png";
    }

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-screenshot runtime path is required.",
                nameof(runtimePath));
        }
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-screenshot request ID is invalid.",
                nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-screenshot-{requestId}.json");
    }
}

internal sealed record ReviewScreenshotCaptureQuery(
    string Mode,
    string Label);

internal sealed record ReviewScreenshotProblem(
    string Code,
    string Message);

internal sealed record ReviewScreenshotReport(
    int SchemaVersion,
    string State,
    string Mode,
    string Label,
    string? FileName,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ReviewScreenshotProblem> Problems);

internal sealed record ReviewScreenshotResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    ReviewScreenshotReport Report);
