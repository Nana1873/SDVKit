using System.Globalization;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static class ProjectReviewDataService
{
    private const int OperationFailed = 3;
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static LiveLabCommandResult Execute(
        ReviewDataQuery query,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);

        ReviewDataProblem? queryProblem = Validate(query);
        if (queryProblem is not null)
        {
            return Failure(query.Operation, queryProblem);
        }

        LiveLabPaths paths;
        try
        {
            paths = LiveLabPaths.Resolve(labRoot);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                query.Operation,
                Problem("labPathInvalid", exception.Message));
        }

        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewDataContract.ResponsePath(
            paths.RuntimePath,
            requestId);
        string command = BuildCommand(requestId, query);
        ProjectReviewResponseTransportResult<ReviewDataResponseEnvelope> transported =
            ProjectReviewResponseTransport.Execute(
                command,
                responsePath,
                ReviewDataContract.MaximumResponseBytes,
                "data",
                "review-data",
                labRoot,
                bytes => JsonSerializer.Deserialize<ReviewDataResponseEnvelope>(
                    bytes,
                    ResponseJsonOptions),
                envelope => envelope.Report is not null
                    && envelope.Report.Problems is not null
                    && envelope.SchemaVersion == ReviewDataContract.SchemaVersion
                    && string.Equals(
                        envelope.RequestId,
                        requestId,
                        StringComparison.Ordinal)
                    && envelope.Report.SchemaVersion == ReviewDataContract.SchemaVersion
                    && string.Equals(
                        envelope.Report.Operation,
                        query.Operation,
                        StringComparison.Ordinal),
                inputSender,
                delay,
                responseTimeout);
        if (transported.Response is null)
        {
            return Failure(
                query.Operation,
                transported.Problems
                    .Select(problem => Problem(problem.Code, problem.Message))
                    .ToArray());
        }

        ReviewDataReport report = transported.Response.Report;
        return new LiveLabCommandResult(
            report.Problems.Count == 0
                && string.Equals(report.State, "ready", StringComparison.Ordinal)
                    ? 0
                    : OperationFailed,
            report);
    }

    internal static string BuildCommand(
        string requestId,
        ReviewDataQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ReviewDataContract.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-data request ID is invalid.",
                nameof(requestId));
        }

        var tokens = new List<string>
        {
            "sdvkit",
            "data",
            requestId,
            query.Operation,
            query.Offset.ToString(CultureInfo.InvariantCulture),
            query.Limit.ToString(CultureInfo.InvariantCulture),
        };
        if (query.Asset is not null)
        {
            tokens.Add(ReviewDataContract.Encode(query.Asset));
        }

        if (query.Key is not null)
        {
            tokens.Add(ReviewDataContract.Encode(query.Key));
        }

        string command = string.Join(" ", tokens);
        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        return command;
    }

    private static ReviewDataProblem? Validate(ReviewDataQuery query)
    {
        if (query.Operation is not (
                ReviewDataContract.AssetsOperation
                or ReviewDataContract.KeysOperation
                or ReviewDataContract.GetOperation))
        {
            return Problem("dataOperationUnknown", "The review-data operation is unknown.");
        }

        if (query.Offset < 0
            || query.Limit < 1
            || query.Limit > ReviewDataContract.MaximumPageLimit)
        {
            return Problem(
                "dataPaginationInvalid",
                $"Offset must be non-negative and limit must be between 1 and {ReviewDataContract.MaximumPageLimit}.");
        }

        bool needsAsset = query.Operation is ReviewDataContract.KeysOperation
            or ReviewDataContract.GetOperation;
        bool needsKey = query.Operation == ReviewDataContract.GetOperation;
        if (needsAsset
            && (string.IsNullOrWhiteSpace(query.Asset)
                || query.Asset.Length > ReviewDataContract.MaximumAssetLength
                || query.Asset.Any(char.IsControl)))
        {
            return Problem("dataAssetInvalid", "A bounded non-empty Data asset name is required.");
        }

        if (needsKey
            && (string.IsNullOrWhiteSpace(query.Key)
                || query.Key.Length > ReviewDataContract.MaximumKeyLength
                || query.Key.Any(char.IsControl)))
        {
            return Problem("dataKeyInvalid", "A bounded non-empty stable record key is required.");
        }

        if ((!needsAsset && query.Asset is not null)
            || (!needsKey && query.Key is not null))
        {
            return Problem("dataRequestInvalid", "The review-data request has unexpected operands.");
        }

        return null;
    }

    private static LiveLabCommandResult Failure(
        string operation,
        params ReviewDataProblem[] problems) =>
        new(
            OperationFailed,
            new ReviewDataReport(
                ReviewDataContract.SchemaVersion,
                "blocked",
                operation,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                problems));

    private static ReviewDataProblem Problem(string code, string message) =>
        new(code, message);

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
