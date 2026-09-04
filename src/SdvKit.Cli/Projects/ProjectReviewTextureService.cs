using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static class ProjectReviewTextureService
{
    private const int Success = 0;
    private const int OperationFailed = 3;
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static LiveLabCommandResult Execute(
        ReviewTextureQuery query,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);

        ReviewTextureProblem? queryProblem = Validate(query);
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
        string responsePath = ReviewTextureContract.ResponsePath(
            paths.RuntimePath,
            requestId);
        if (query.Operation == ReviewTextureContract.PreviewOperation)
        {
            string previewPath = ReviewTextureContract.PreviewPath(
                paths.RuntimePath,
                requestId);
            if (File.Exists(previewPath) || Directory.Exists(previewPath))
            {
                return Failure(
                    query.Operation,
                    Problem(
                        "texturePreviewTargetExists",
                        "The unique bounded texture preview target already exists; the request was not sent."));
            }
        }

        string command = BuildCommand(requestId, query);
        ProjectReviewResponseTransportResult<ReviewTextureResponseEnvelope> transported =
            ProjectReviewResponseTransport.Execute(
                command,
                responsePath,
                ReviewTextureContract.MaximumResponseBytes,
                "texture",
                "review-texture",
                labRoot,
                bytes => JsonSerializer.Deserialize<ReviewTextureResponseEnvelope>(
                    bytes,
                    ResponseJsonOptions),
                envelope => MatchesRequest(
                    envelope,
                    query,
                    requestId,
                    paths.RuntimePath),
                inputSender,
                delay);
        if (transported.Response is null)
        {
            return Failure(
                query.Operation,
                transported.Problems
                    .Select(problem => Problem(problem.Code, problem.Message))
                    .ToArray());
        }

        ReviewTextureReport report = transported.Response.Report;
        return new LiveLabCommandResult(
            report.Problems.Count == 0
                && string.Equals(report.State, "ready", StringComparison.Ordinal)
                    ? Success
                    : OperationFailed,
            report);
    }

    internal static string BuildCommand(
        string requestId,
        ReviewTextureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-texture request ID is invalid.",
                nameof(requestId));
        }

        var tokens = new List<string>
        {
            "sdvkit",
            "texture",
            requestId,
            query.Operation,
            query.Offset.ToString(CultureInfo.InvariantCulture),
            query.Limit.ToString(CultureInfo.InvariantCulture),
        };
        if (query.Asset is not null)
        {
            tokens.Add(ReviewTransportToken.Encode(query.Asset));
        }

        string command = string.Join(" ", tokens);
        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        return command;
    }

    internal static bool MatchesRequest(
        ReviewTextureResponseEnvelope? envelope,
        ReviewTextureQuery query,
        string requestId,
        string runtimePath)
    {
        if (envelope is null
            || query is null
            || Validate(query) is not null
            || !ReviewTransportToken.IsRequestId(requestId)
            || string.IsNullOrWhiteSpace(runtimePath)
            || envelope.SchemaVersion != ReviewTextureContract.SchemaVersion
            || !string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal)
            || envelope.Report is null
            || envelope.Report.SchemaVersion != ReviewTextureContract.SchemaVersion
            || !string.Equals(
                envelope.Report.Operation,
                query.Operation,
                StringComparison.Ordinal)
            || !ReviewTextureContract.IsBoundedText(
                envelope.Report.GameVersion,
                ReviewTextureContract.MaximumVersionLength)
            || !ReviewTextureContract.IsBoundedText(
                envelope.Report.GameFileVersion,
                ReviewTextureContract.MaximumVersionLength)
            || !ProblemsAreSafe(envelope.Report.Problems))
        {
            return false;
        }

        ReviewTextureReport report = envelope.Report;
        bool ready = string.Equals(
            report.State,
            "ready",
            StringComparison.Ordinal);
        bool blocked = string.Equals(
            report.State,
            "blocked",
            StringComparison.Ordinal);
        if ((!ready && !blocked)
            || (ready && report.Problems.Count != 0)
            || (blocked && report.Problems.Count == 0))
        {
            return false;
        }

        if (query.Operation == ReviewTextureContract.AssetsOperation)
        {
            return MatchesAssetsReport(report, query, ready);
        }

        if (blocked && MatchesEmptyFailure(report))
        {
            return true;
        }

        if (!MatchesExactIdentity(report, query)
            || (blocked
                && (report.Preview is not null
                    || (report.Available == false && report.Metadata is not null)
                    || (query.Operation == ReviewTextureContract.GetOperation
                        && report.Metadata is not null))))
        {
            return false;
        }

        if (blocked)
        {
            return true;
        }

        ReviewTextureMetadataReport? metadata = report.Metadata;
        if (report.Available != true || metadata is null)
        {
            return false;
        }

        if (query.Operation == ReviewTextureContract.GetOperation)
        {
            return report.Preview is null;
        }

        if (metadata.Width > ReviewTextureContract.MaximumSourceDimension
            || metadata.Height > ReviewTextureContract.MaximumSourceDimension
            || (long)metadata.Width * metadata.Height
                > ReviewTextureContract.MaximumSourcePixels)
        {
            return false;
        }

        (int expectedWidth, int expectedHeight) =
            ReviewTextureContract.PreviewDimensions(
                metadata.Width,
                metadata.Height);
        ReviewTexturePreviewReport? preview = report.Preview;
        if (preview is null
            || !string.Equals(
                preview.RelativePath,
                ReviewTextureContract.PreviewFileName(requestId),
                StringComparison.Ordinal)
            || preview.Width != expectedWidth
            || preview.Height != expectedHeight
            || (long)preview.Width * preview.Height
                > ReviewTextureContract.MaximumPreviewPixels
            || preview.EncodedBytes is < 57 or > ReviewTextureContract.MaximumPreviewBytes
            || preview.Sha256 is null
            || preview.Sha256.Length != 64
            || preview.Sha256.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            return false;
        }

        try
        {
            string absoluteRuntimePath = Path.GetFullPath(runtimePath);
            FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
            if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
                || (runtimeAttributes & FileAttributes.Directory) == 0)
            {
                return false;
            }

            string path = ReviewTextureContract.PreviewPath(
                absoluteRuntimePath,
                requestId);
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) != 0)
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length != preview.EncodedBytes)
            {
                return false;
            }

            if (!ReviewTexturePngValidator.TryValidateRgba8(
                    stream,
                    ReviewTextureContract.MaximumPreviewBytes,
                    ReviewTextureContract.MaximumPreviewDimension,
                    ReviewTextureContract.MaximumPreviewPixels,
                    out ReviewTexturePngInfo? png)
                || png is null
                || png.Width != preview.Width
                || png.Height != preview.Height)
            {
                return false;
            }

            stream.Position = 0;
            string actualHash = Convert.ToHexString(SHA256.HashData(stream))
                .ToLowerInvariant();
            return stream.Length == preview.EncodedBytes
                && string.Equals(actualHash, preview.Sha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return false;
        }
    }

    private static bool MatchesAssetsReport(
        ReviewTextureReport report,
        ReviewTextureQuery query,
        bool ready)
    {
        if (!ready && MatchesEmptyFailure(report))
        {
            return true;
        }

        if (report.AssetName is not null
            || report.Available is not null
            || report.Metadata is not null
            || report.Preview is not null
            || !string.Equals(
                report.SourceCategory,
                ReviewTextureContract.CanonicalGameContentSource,
                StringComparison.Ordinal)
            || !MatchesProvenance(report.Provenance)
            || report.Assets is null
            || report.Page is null
            || report.Coverage is null
            || !MatchesCoverage(report.Coverage, ready)
            || report.Page.Offset != query.Offset
            || report.Page.Limit != query.Limit
            || report.Page.Total != report.Coverage.Textures
            || report.Page.Returned != report.Assets.Count
            || report.Page.Returned < 0
            || report.Page.Returned > query.Limit)
        {
            return false;
        }

        int expectedReturned = query.Offset >= report.Page.Total
            ? 0
            : Math.Min(query.Limit, report.Page.Total - query.Offset);
        int consumed = Math.Min(
            report.Page.Total,
            checked(query.Offset + expectedReturned));
        int? expectedNextOffset = consumed < report.Page.Total
            ? consumed
            : null;
        if (report.Page.Returned != expectedReturned
            || report.Page.NextOffset != expectedNextOffset)
        {
            return false;
        }

        string? previous = null;
        foreach (ReviewTextureAssetReport? asset in report.Assets)
        {
            if (asset is null
                || !ReviewTextureContract.IsCanonicalAssetName(asset.AssetName)
                || !asset.Available
                || !string.Equals(
                    asset.SourceCategory,
                    ReviewTextureContract.CanonicalGameContentSource,
                    StringComparison.Ordinal)
                || (previous is not null
                    && string.CompareOrdinal(previous, asset.AssetName) >= 0))
            {
                return false;
            }

            previous = asset.AssetName;
        }

        return true;
    }

    private static bool MatchesExactIdentity(
        ReviewTextureReport report,
        ReviewTextureQuery query)
    {
        if (query.Asset is null
            || !ReviewTextureContract.IsCanonicalAssetName(report.AssetName)
            || !string.Equals(
                StableIdentityNormalizer.Normalize(report.AssetName!),
                StableIdentityNormalizer.Normalize(query.Asset),
                StringComparison.Ordinal)
            || !string.Equals(
                    report.SourceCategory,
                    ReviewTextureContract.CanonicalGameContentSource,
                    StringComparison.Ordinal))
        {
            return false;
        }

        return report.Available is not null
            && (report.Metadata is null || MatchesMetadata(report.Metadata))
            && MatchesProvenance(report.Provenance)
            && report.Assets is null
            && report.Page is null
            && report.Coverage is null;
    }

    private static bool MatchesMetadata(ReviewTextureMetadataReport metadata) =>
        metadata.Width > 0
        && metadata.Height > 0
        && metadata.LevelCount > 0
        && metadata.HasMipMaps == (metadata.LevelCount > 1)
        && ReviewTextureContract.IsBoundedText(
            metadata.RuntimeFormat,
            ReviewTextureContract.MaximumRuntimeFormatLength);

    private static bool MatchesProvenance(ReviewTextureProvenanceReport? provenance) =>
        provenance is not null
        && string.Equals(
            provenance.PipelineStage,
            ReviewTextureContract.FinalPipelineStage,
            StringComparison.Ordinal)
        && !provenance.DetailedProviderAvailable
        && string.Equals(
            provenance.Detail,
            ReviewTextureContract.ProvenanceUnavailableDetail,
            StringComparison.Ordinal);

    private static bool MatchesCoverage(
        ReviewTextureCoverageReport coverage,
        bool ready) =>
        coverage.Candidates is >= 0 and <= ReviewTextureContract.MaximumDiscoveredAssets
        && coverage.Classified is >= 0 and <= ReviewTextureContract.MaximumDiscoveredAssets
        && coverage.Textures is >= 0 and <= ReviewTextureContract.MaximumDiscoveredAssets
        && coverage.NonTextures is >= 0 and <= ReviewTextureContract.MaximumDiscoveredAssets
        && coverage.Gaps is >= 0 and <= ReviewTextureContract.MaximumDiscoveredAssets
        && (long)coverage.Classified + coverage.Gaps == coverage.Candidates
        && (long)coverage.Textures + coverage.NonTextures == coverage.Classified
        && coverage.Complete == ready;

    private static bool MatchesEmptyFailure(ReviewTextureReport report) =>
        report.AssetName is null
        && report.SourceCategory is null
        && report.Available is null
        && report.Metadata is null
        && report.Provenance is null
        && report.Preview is null
        && report.Assets is null
        && report.Page is null
        && report.Coverage is null;

    private static bool ProblemsAreSafe(
        IReadOnlyList<ReviewTextureProblem>? problems)
    {
        if (problems is null
            || problems.Count > ReviewTextureContract.MaximumProblemCount)
        {
            return false;
        }

        foreach (ReviewTextureProblem? problem in problems)
        {
            if (problem is null
                || !ReviewTextureContract.IsBoundedText(
                    problem.Code,
                    ReviewTextureContract.MaximumProblemCodeLength)
                || !ReviewTextureContract.IsBoundedText(
                    problem.Message,
                    ReviewTextureContract.MaximumProblemMessageLength))
            {
                return false;
            }
        }

        return true;
    }

    private static ReviewTextureProblem? Validate(ReviewTextureQuery query)
    {
        if (query.Operation is not (
                ReviewTextureContract.AssetsOperation
                or ReviewTextureContract.GetOperation
                or ReviewTextureContract.PreviewOperation))
        {
            return Problem(
                "textureOperationUnknown",
                "The review-texture operation is unknown.");
        }

        if (query.Offset < 0
            || query.Limit < 1
            || query.Limit > ReviewTextureContract.MaximumPageLimit)
        {
            return Problem(
                "texturePaginationInvalid",
                $"Offset must be non-negative and limit must be between 1 and {ReviewTextureContract.MaximumPageLimit}.");
        }

        bool needsAsset = query.Operation is ReviewTextureContract.GetOperation
            or ReviewTextureContract.PreviewOperation;
        if (needsAsset
            && (string.IsNullOrWhiteSpace(query.Asset)
                || query.Asset.Length > ReviewTextureContract.MaximumAssetLength
                || query.Asset.Any(char.IsControl)))
        {
            return Problem(
                "textureAssetInvalid",
                "A bounded non-empty texture asset name is required.");
        }

        if ((!needsAsset && query.Asset is not null)
            || (needsAsset && (query.Offset != 0 || query.Limit != 1)))
        {
            return Problem(
                "textureRequestInvalid",
                "The review-texture request has unexpected operands or pagination.");
        }

        return null;
    }

    private static LiveLabCommandResult Failure(
        string operation,
        params ReviewTextureProblem[] problems) =>
        new(
            OperationFailed,
            new ReviewTextureReport(
                ReviewTextureContract.SchemaVersion,
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
                problems));

    private static ReviewTextureProblem Problem(string code, string message) =>
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
