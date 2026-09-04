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
    private const int MaximumJsonDepth = 16;
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
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
        "operation",
        "gameVersion",
        "gameFileVersion",
        "assetName",
        "sourceCategory",
        "available",
        "metadata",
        "provenance",
        "preview",
        "assets",
        "page",
        "coverage",
        "problems");
    private static readonly HashSet<string> ProblemProperties = PropertySet(
        "code",
        "message");
    private static readonly HashSet<string> AssetProperties = PropertySet(
        "assetName",
        "sourceCategory",
        "available");
    private static readonly HashSet<string> MetadataProperties = PropertySet(
        "width",
        "height",
        "runtimeFormat",
        "levelCount",
        "hasMipMaps");
    private static readonly HashSet<string> ProvenanceProperties = PropertySet(
        "pipelineStage",
        "detailedProviderAvailable",
        "detail");
    private static readonly HashSet<string> PreviewProperties = PropertySet(
        "relativePath",
        "width",
        "height",
        "encodedBytes",
        "sha256");
    private static readonly HashSet<string> PageProperties = PropertySet(
        "offset",
        "limit",
        "returned",
        "total",
        "nextOffset");
    private static readonly HashSet<string> CoverageProperties = PropertySet(
        "candidates",
        "classified",
        "textures",
        "nonTextures",
        "gaps",
        "complete");

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
                DeserializeResponse,
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
        if (Validate(query) is ReviewTextureProblem queryProblem)
        {
            throw new ArgumentException(queryProblem.Message, nameof(query));
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

    internal static ReviewTextureResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using (JsonDocument document = JsonDocument.Parse(bytes, ResponseDocumentOptions))
        {
            ValidateEnvelopeShape(document.RootElement);
        }

        return JsonSerializer.Deserialize<ReviewTextureResponseEnvelope>(
            bytes,
            ResponseJsonOptions);
    }

    internal static bool MatchesRequest(
        ReviewTextureResponseEnvelope? envelope,
        ReviewTextureQuery query,
        string requestId,
        string runtimePath)
    {
        try
        {
            return MatchesRequestCore(envelope, query, requestId, runtimePath);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return false;
        }
    }

    private static bool MatchesRequestCore(
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

        if (!string.Equals(metadata.RuntimeFormat, "Color", StringComparison.Ordinal)
            || metadata.Width > ReviewTextureContract.MaximumSourceDimension
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
        var normalizedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReviewTextureAssetReport? asset in report.Assets)
        {
            if (asset is null
                || !ReviewTextureContract.IsCanonicalAssetName(asset.AssetName)
                || !normalizedNames.Add(
                    StableIdentityNormalizer.Normalize(asset.AssetName))
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
            && !ReviewTextureContract.IsCanonicalAssetName(query.Asset))
        {
            return Problem(
                "textureAssetInvalid",
                "A canonical bounded texture asset name is required.");
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

    private static void ValidateEnvelopeShape(JsonElement root)
    {
        RequireExactObject(root, EnvelopeProperties);
        JsonElement report = root.GetProperty("report");
        RequireExactObject(report, ReportProperties);

        ValidateOptionalMetadata(report.GetProperty("metadata"));
        ValidateOptionalProvenance(report.GetProperty("provenance"));
        ValidateOptionalPreview(report.GetProperty("preview"));
        ValidateOptionalArray(
            report.GetProperty("assets"),
            ReviewTextureContract.MaximumPageLimit,
            asset =>
            {
                RequireExactObject(asset, AssetProperties);
                RequiredBoolean(asset, "available");
            });
        ValidateOptionalPage(report.GetProperty("page"));
        ValidateOptionalCoverage(report.GetProperty("coverage"));
        ValidateRequiredArray(
            report.GetProperty("problems"),
            ReviewTextureContract.MaximumProblemCount,
            problem => RequireExactObject(problem, ProblemProperties));
    }

    private static void ValidateOptionalMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireExactObject(metadata, MetadataProperties);
        RequiredInt32(metadata, "width");
        RequiredInt32(metadata, "height");
        RequiredInt32(metadata, "levelCount");
        RequiredBoolean(metadata, "hasMipMaps");
    }

    private static void ValidateOptionalProvenance(JsonElement provenance)
    {
        if (provenance.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireExactObject(provenance, ProvenanceProperties);
        RequiredBoolean(provenance, "detailedProviderAvailable");
    }

    private static void ValidateOptionalPreview(JsonElement preview)
    {
        if (preview.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireExactObject(preview, PreviewProperties);
        RequiredInt32(preview, "width");
        RequiredInt32(preview, "height");
        RequiredInt64(preview, "encodedBytes");
    }

    private static void ValidateOptionalPage(JsonElement page)
    {
        if (page.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireExactObject(page, PageProperties);
        RequiredInt32(page, "offset");
        RequiredInt32(page, "limit");
        RequiredInt32(page, "returned");
        RequiredInt32(page, "total");
        JsonElement nextOffset = page.GetProperty("nextOffset");
        if (nextOffset.ValueKind != JsonValueKind.Null
            && (nextOffset.ValueKind != JsonValueKind.Number
                || !nextOffset.TryGetInt32(out _)))
        {
            throw new InvalidDataException(
                "The review-texture response has an invalid bounded page member.");
        }
    }

    private static void ValidateOptionalCoverage(JsonElement coverage)
    {
        if (coverage.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireExactObject(coverage, CoverageProperties);
        int candidates = RequiredInt32(coverage, "candidates");
        int classified = RequiredInt32(coverage, "classified");
        int textures = RequiredInt32(coverage, "textures");
        int nonTextures = RequiredInt32(coverage, "nonTextures");
        int gaps = RequiredInt32(coverage, "gaps");
        bool complete = RequiredBoolean(coverage, "complete");
        bool calculatedComplete = (long)candidates == (long)classified + gaps
            && (long)classified == (long)textures + nonTextures
            && gaps == 0;
        if (complete != calculatedComplete)
        {
            throw new InvalidDataException(
                "The review-texture coverage completion flag is inconsistent.");
        }
    }

    private static int RequiredInt32(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int result))
        {
            throw new InvalidDataException(
                "The review-texture response has an invalid bounded integer member.");
        }

        return result;
    }

    private static long RequiredInt64(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out long result))
        {
            throw new InvalidDataException(
                "The review-texture response has an invalid bounded integer member.");
        }

        return result;
    }

    private static bool RequiredBoolean(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                "The review-texture response has an invalid Boolean member.");
        }

        return property.GetBoolean();
    }

    private static void ValidateOptionalArray(
        JsonElement value,
        int maximumCount,
        Action<JsonElement> validateItem)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ValidateRequiredArray(value, maximumCount, validateItem);
    }

    private static void ValidateRequiredArray(
        JsonElement value,
        int maximumCount,
        Action<JsonElement> validateItem)
    {
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > maximumCount)
        {
            throw new InvalidDataException(
                "The review-texture response has an invalid bounded array shape.");
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            validateItem(item);
        }
    }

    private static void RequireExactObject(
        JsonElement value,
        HashSet<string> requiredProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The review-texture response has an invalid JSON object shape.");
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!requiredProperties.Contains(property.Name)
                || !observed.Add(property.Name))
            {
                throw new InvalidDataException(
                    "The review-texture response has an unknown or duplicate JSON member.");
            }
        }

        if (observed.Count != requiredProperties.Count)
        {
            throw new InvalidDataException(
                "The review-texture response is missing a required JSON member.");
        }
    }

    private static HashSet<string> PropertySet(params string[] names) =>
        new(names, StringComparer.Ordinal);

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
            or OverflowException
            or PathTooLongException
            or SecurityException
            or UnauthorizedAccessException;
}
