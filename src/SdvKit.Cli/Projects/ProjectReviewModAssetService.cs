using System.Globalization;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static class ProjectReviewModAssetService
{
    private const int Success = 0;
    private const int OperationFailed = 3;
    private const int MaximumJsonDepth = 12;
    private const string MissingToken = "-";

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
        "coverageScope",
        "asset",
        "key",
        "assets",
        "keys",
        "page",
        "coverage",
        "record",
        "problems");

    private static readonly HashSet<string> AssetProperties = PropertySet(
        "assetName",
        "namespaceOwnerId",
        "namespaceOwnerStatus",
        "providerModId",
        "providerStatus",
        "dataType",
        "shape",
        "lifecycle",
        "generation",
        "requestCount",
        "readyCount",
        "available",
        "adapterSupported",
        "nameCollision",
        "typeCollision",
        "problemCode");

    private static readonly HashSet<string> PageProperties = PropertySet(
        "offset",
        "limit",
        "returned",
        "total",
        "nextOffset");

    private static readonly HashSet<string> CoverageProperties = PropertySet(
        "scope",
        "observationStartedAtUtc",
        "observed",
        "catalogued",
        "adapterSupported",
        "adapterUnavailable",
        "ready",
        "invalidated",
        "unavailable",
        "nameCollisions",
        "typeCollisions",
        "dropped",
        "complete");

    private static readonly HashSet<string> ProblemProperties = PropertySet(
        "code",
        "message");

    private static readonly Dictionary<string, string> SupportedShapesByDataType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["System.Collections.Generic.Dictionary<System.String,System.String>"] =
                "stringDictionary",
            ["System.Collections.Generic.Dictionary<System.String,System.Int32>"] =
                "integerDictionary",
            ["System.Collections.Generic.Dictionary<System.Int32,System.String>"] =
                "integerKeyStringDictionary",
            ["System.Collections.Generic.Dictionary<System.Int32,System.Int32>"] =
                "integerKeyIntegerDictionary",
            ["System.Collections.Generic.List<System.String>"] = "stringList",
            ["System.String"] = "stringSingleton",
        };

    public static LiveLabCommandResult Execute(
        ReviewModAssetQuery query,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);

        ReviewModAssetProblem? queryProblem = Validate(query);
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
        string responsePath = ReviewModAssetContract.ResponsePath(
            paths.RuntimePath,
            requestId);
        string command = BuildCommand(requestId, query);
        ProjectReviewResponseTransportResult<ReviewModAssetResponseEnvelope> transported =
            ProjectReviewResponseTransport.Execute(
                command,
                responsePath,
                ReviewModAssetContract.MaximumResponseBytes,
                "modAsset",
                "review-mod-assets",
                labRoot,
                DeserializeResponse,
                envelope => MatchesRequest(envelope, query, requestId),
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

        ReviewModAssetReport report = transported.Response.Report;
        return new LiveLabCommandResult(
            report.Problems.Count == 0
                && string.Equals(report.State, "ready", StringComparison.Ordinal)
                    ? Success
                    : OperationFailed,
            report);
    }

    internal static string BuildCommand(
        string requestId,
        ReviewModAssetQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ReviewModAssetContract.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-mod-assets request ID is invalid.",
                nameof(requestId));
        }
        if (Validate(query) is ReviewModAssetProblem queryProblem)
        {
            throw new ArgumentException(queryProblem.Message, nameof(query));
        }

        string command = string.Join(
            ' ',
            "sdvkit",
            "mod-assets",
            requestId,
            query.Operation,
            query.Offset.ToString(CultureInfo.InvariantCulture),
            query.Limit.ToString(CultureInfo.InvariantCulture),
            EncodeOptional(query.Asset),
            EncodeOptional(query.Key));
        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        return command;
    }

    internal static ReviewModAssetResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using (JsonDocument document = JsonDocument.Parse(bytes, ResponseDocumentOptions))
        {
            ValidateEnvelopeShape(document.RootElement);
        }

        return JsonSerializer.Deserialize<ReviewModAssetResponseEnvelope>(
            bytes,
            ResponseJsonOptions);
    }

    internal static bool MatchesRequest(
        ReviewModAssetResponseEnvelope? envelope,
        ReviewModAssetQuery query,
        string requestId)
    {
        try
        {
            return MatchesRequestCore(envelope, query, requestId);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return false;
        }
    }

    private static bool MatchesRequestCore(
        ReviewModAssetResponseEnvelope? envelope,
        ReviewModAssetQuery query,
        string requestId)
    {
        if (envelope is null
            || query is null
            || Validate(query) is not null
            || !ReviewModAssetContract.IsRequestId(requestId)
            || envelope.SchemaVersion != ReviewModAssetContract.SchemaVersion
            || !string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal)
            || envelope.Report is null
            || envelope.Report.SchemaVersion != ReviewModAssetContract.SchemaVersion
            || !string.Equals(
                envelope.Report.Operation,
                query.Operation,
                StringComparison.Ordinal)
            || !ReviewModAssetContract.IsBoundedText(
                envelope.Report.GameVersion,
                ReviewModAssetContract.MaximumVersionLength)
            || !ReviewModAssetContract.IsBoundedText(
                envelope.Report.GameFileVersion,
                ReviewModAssetContract.MaximumVersionLength)
            || !string.Equals(
                envelope.Report.CoverageScope,
                ReviewModAssetContract.CoverageScope,
                StringComparison.Ordinal)
            || !ProblemsAreSafe(envelope.Report.Problems))
        {
            return false;
        }

        ReviewModAssetReport report = envelope.Report;
        bool ready = string.Equals(report.State, "ready", StringComparison.Ordinal);
        bool blocked = string.Equals(report.State, "blocked", StringComparison.Ordinal);
        if ((!ready && !blocked)
            || (ready && report.Problems.Count != 0)
            || (blocked && report.Problems.Count == 0))
        {
            return false;
        }

        return query.Operation switch
        {
            ReviewModAssetContract.AssetsOperation =>
                MatchesAssets(report, query, ready),
            ReviewModAssetContract.KeysOperation =>
                MatchesKeys(report, query, ready),
            ReviewModAssetContract.GetOperation =>
                MatchesGet(report, query, ready),
            _ => false,
        };
    }

    private static bool MatchesAssets(
        ReviewModAssetReport report,
        ReviewModAssetQuery query,
        bool ready)
    {
        if (report.Asset is not null
            || report.Key is not null
            || report.Keys is not null
            || report.Record is not null)
        {
            return false;
        }

        if (report.Assets is null || report.Page is null || report.Coverage is null)
        {
            return !ready && MatchesEmptyFailure(report);
        }

        ReviewModAssetCoverageReport coverage = report.Coverage;
        if (!MatchesCoverage(coverage)
            || ready != coverage.Complete
            || report.Page.Offset != query.Offset
            || report.Page.Limit != query.Limit
            || report.Page.Total != coverage.Catalogued
            || !MatchesPage(report.Page, query, report.Assets.Count))
        {
            return false;
        }

        string? previousAsset = null;
        string? previousType = null;
        var exactIdentities = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ReviewModAssetAssetReport? asset in report.Assets)
        {
            if (!MatchesAsset(asset))
            {
                return false;
            }

            if (!exactIdentities.TryGetValue(
                    asset!.AssetName,
                    out HashSet<string>? dataTypes))
            {
                dataTypes = new HashSet<string>(StringComparer.Ordinal);
                exactIdentities.Add(asset.AssetName, dataTypes);
            }
            if (!dataTypes.Add(asset.DataType)
                || (previousAsset is not null
                    && (string.CompareOrdinal(previousAsset, asset.AssetName) > 0
                        || (string.Equals(
                                previousAsset,
                                asset.AssetName,
                                StringComparison.Ordinal)
                            && string.CompareOrdinal(previousType, asset.DataType) >= 0))))
            {
                return false;
            }

            previousAsset = asset.AssetName;
            previousType = asset.DataType;
        }

        return true;
    }

    private static bool MatchesKeys(
        ReviewModAssetReport report,
        ReviewModAssetQuery query,
        bool ready)
    {
        if (!ready)
        {
            return MatchesAssetFailure(report, query);
        }
        if (!MatchesExactAsset(report.Asset, query.Asset!)
            || report.Key is not null
            || report.Assets is not null
            || report.Keys is null
            || report.Page is null
            || report.Coverage is not null
            || report.Record is not null
            || !MatchesPage(report.Page, query, report.Keys.Count))
        {
            return false;
        }

        return MatchesKeysForShape(
            report.Asset!.Shape!,
            report.Keys,
            report.Page,
            query);
    }

    private static bool MatchesGet(
        ReviewModAssetReport report,
        ReviewModAssetQuery query,
        bool ready)
    {
        if (!ready)
        {
            return MatchesAssetFailure(report, query);
        }
        if (!MatchesExactAsset(report.Asset, query.Asset!)
            || !MatchesGetKey(report.Asset!.Shape!, report.Key, query.Key!)
            || report.Assets is not null
            || report.Keys is not null
            || report.Page is not null
            || report.Coverage is not null
            || report.Record is null)
        {
            return false;
        }

        JsonElement record = report.Record.Value;
        return report.Asset!.Shape switch
        {
            "stringDictionary"
                or "integerKeyStringDictionary"
                or "stringList"
                or "stringSingleton" =>
                    record.ValueKind == JsonValueKind.String
                    && record.GetString() is string text
                    && text.Length <= ReviewModAssetContract.MaximumStringValueLength
                    && ReviewTransportText.IsWellFormedUtf16(text),
            "integerDictionary"
                or "integerKeyIntegerDictionary" =>
                    record.ValueKind == JsonValueKind.Number
                    && record.TryGetInt32(out _),
            _ => false,
        };
    }

    private static bool MatchesAssetFailure(
        ReviewModAssetReport report,
        ReviewModAssetQuery query)
    {
        if (report.Key is not null
            || report.Assets is not null
            || report.Keys is not null
            || report.Page is not null
            || report.Coverage is not null
            || report.Record is not null)
        {
            return false;
        }

        return report.Asset is null
            || MatchesAsset(report.Asset)
                && AssetMatchesQuery(report.Asset.AssetName, query.Asset!);
    }

    private static bool MatchesEmptyFailure(ReviewModAssetReport report) =>
        report.Asset is null
        && report.Key is null
        && report.Assets is null
        && report.Keys is null
        && report.Page is null
        && report.Coverage is null
        && report.Record is null;

    private static bool MatchesExactAsset(
        ReviewModAssetAssetReport? asset,
        string queryAsset) =>
        MatchesAsset(asset)
        && asset!.Available
        && string.Equals(asset.Lifecycle, "ready", StringComparison.Ordinal)
        && asset.AdapterSupported
        && !asset.NameCollision
        && !asset.TypeCollision
        && asset.ProblemCode is null
        && AssetMatchesQuery(asset.AssetName, queryAsset);

    private static bool MatchesAsset(ReviewModAssetAssetReport? asset)
    {
        if (asset is null
            || !ReviewModAssetContract.IsCanonicalAssetName(asset.AssetName)
            || !ReviewModAssetContract.IsBoundedText(
                asset.NamespaceOwnerStatus,
                ReviewModAssetContract.MaximumIdentityStatusLength)
            || !string.Equals(
                asset.ProviderStatus,
                "unavailableThroughPublicSmapiApi",
                StringComparison.Ordinal)
            || asset.ProviderModId is not null
            || !ReviewModAssetContract.IsBoundedText(
                asset.DataType,
                ReviewModAssetContract.MaximumDataTypeLength)
            || !ReviewModAssetContract.IsBoundedText(
                asset.Lifecycle,
                ReviewModAssetContract.MaximumLifecycleLength)
            || asset.Generation < 0
            || asset.RequestCount < 1
            || asset.ReadyCount < 0)
        {
            return false;
        }

        string ownerSegment = asset.AssetName.Split('/')[1];
        bool ownerMatches = asset.NamespaceOwnerStatus switch
        {
            "resolved" => ReviewModAssetContract.IsBoundedText(
                asset.NamespaceOwnerId,
                ReviewModAssetContract.MaximumAssetLength)
                && string.Equals(
                    asset.NamespaceOwnerId,
                    ownerSegment,
                    StringComparison.OrdinalIgnoreCase),
            "unknown" or "ambiguous" => asset.NamespaceOwnerId is null,
            _ => false,
        };
        bool knownType = SupportedShapesByDataType.TryGetValue(
            asset.DataType,
            out string? expectedShape);
        bool shapeMatches = asset.AdapterSupported == knownType
            && (knownType
                ? string.Equals(asset.Shape, expectedShape, StringComparison.Ordinal)
                : asset.Shape is null);
        bool lifecycleMatches = asset.Lifecycle is
            "requested" or "ready" or "invalidated" or "unavailable"
            && (!asset.Available
                || string.Equals(asset.Lifecycle, "ready", StringComparison.Ordinal));
        string? expectedProblemCode = asset.TypeCollision
            ? "modAssetTypeAmbiguous"
            : asset.NameCollision
                ? "modAssetNameAmbiguous"
                : asset.AdapterSupported
                    ? null
                    : "modAssetAdapterUnavailable";
        return ownerMatches
            && shapeMatches
            && lifecycleMatches
            && string.Equals(
                asset.ProblemCode,
                expectedProblemCode,
                StringComparison.Ordinal);
    }

    private static bool MatchesCoverage(ReviewModAssetCoverageReport coverage)
    {
        if (!string.Equals(
                coverage.Scope,
                ReviewModAssetContract.CoverageScope,
                StringComparison.Ordinal)
            || coverage.ObservationStartedAtUtc == default)
        {
            return false;
        }

        int[] cataloguedValues =
        [
            coverage.Catalogued,
            coverage.AdapterSupported,
            coverage.AdapterUnavailable,
            coverage.Ready,
            coverage.Invalidated,
            coverage.Unavailable,
            coverage.NameCollisions,
            coverage.TypeCollisions,
        ];
        return coverage.Observed >= 0
            && coverage.Dropped >= 0
            && cataloguedValues.All(value =>
                value is >= 0 and <= ReviewModAssetContract.MaximumObservedAssets)
            && (long)coverage.Catalogued + coverage.Dropped == coverage.Observed
            && (long)coverage.AdapterSupported + coverage.AdapterUnavailable
                == coverage.Catalogued
            && (long)coverage.Ready + coverage.Invalidated + coverage.Unavailable
                <= coverage.Catalogued;
    }

    private static bool MatchesPage(
        ReviewModAssetPage page,
        ReviewModAssetQuery query,
        int actualReturned)
    {
        if (page.Offset != query.Offset
            || page.Limit != query.Limit
            || page.Total < 0
            || page.Total > ReviewModAssetContract.MaximumRecordsPerAsset
                && query.Operation != ReviewModAssetContract.AssetsOperation
            || page.Total > ReviewModAssetContract.MaximumObservedAssets
                && query.Operation == ReviewModAssetContract.AssetsOperation
            || page.Returned != actualReturned
            || page.Returned < 0
            || page.Returned > query.Limit)
        {
            return false;
        }

        int expectedReturned = query.Offset >= page.Total
            ? 0
            : Math.Min(query.Limit, page.Total - query.Offset);
        int consumed = Math.Min(
            page.Total,
            checked(query.Offset + expectedReturned));
        int? expectedNextOffset = consumed < page.Total ? consumed : null;
        return page.Returned == expectedReturned
            && page.NextOffset == expectedNextOffset;
    }

    private static bool ProblemsAreSafe(
        IReadOnlyList<ReviewModAssetProblem>? problems)
    {
        if (problems is null
            || problems.Count > ReviewModAssetContract.MaximumProblemCount)
        {
            return false;
        }

        foreach (ReviewModAssetProblem? problem in problems)
        {
            if (problem is null
                || !ReviewModAssetContract.IsBoundedText(
                    problem.Code,
                    ReviewModAssetContract.MaximumProblemCodeLength)
                || !ReviewModAssetContract.IsBoundedText(
                    problem.Message,
                    ReviewModAssetContract.MaximumProblemMessageLength))
            {
                return false;
            }
        }

        return true;
    }

    internal static ReviewModAssetProblem? Validate(ReviewModAssetQuery query)
    {
        if (query.Operation is not (
                ReviewModAssetContract.AssetsOperation
                or ReviewModAssetContract.KeysOperation
                or ReviewModAssetContract.GetOperation))
        {
            return Problem(
                "modAssetOperationUnknown",
                "The review-mod-assets operation is unknown.");
        }

        bool listOperation = query.Operation is ReviewModAssetContract.AssetsOperation
            or ReviewModAssetContract.KeysOperation;
        if (query.Offset < 0
            || query.Limit < 1
            || query.Limit > ReviewModAssetContract.MaximumPageLimit
            || (!listOperation && (query.Offset != 0 || query.Limit != 1)))
        {
            return Problem(
                "modAssetPaginationInvalid",
                $"List offsets must be non-negative with limits from 1 through {ReviewModAssetContract.MaximumPageLimit}; exact reads do not accept pagination.");
        }

        bool needsAsset = query.Operation is ReviewModAssetContract.KeysOperation
            or ReviewModAssetContract.GetOperation;
        bool needsKey = query.Operation == ReviewModAssetContract.GetOperation;
        if (needsAsset && !ReviewModAssetContract.IsCanonicalAssetName(query.Asset))
        {
            return Problem(
                "modAssetNameInvalid",
                "A canonical bounded Mods/<owner>/... asset name is required.");
        }
        if (needsKey
            && (!ReviewModAssetContract.IsBoundedText(
                    query.Key,
                    ReviewModAssetContract.MaximumKeyLength)
                || string.IsNullOrWhiteSpace(query.Key)))
        {
            return Problem(
                "modAssetKeyInvalid",
                "A bounded non-empty adapted record key is required.");
        }
        if ((!needsAsset && query.Asset is not null)
            || (!needsKey && query.Key is not null))
        {
            return Problem(
                "modAssetRequestInvalid",
                "The review-mod-assets request has unexpected operands.");
        }

        return null;
    }

    private static string EncodeOptional(string? value) =>
        value is null ? MissingToken : ReviewModAssetContract.Encode(value);

    private static bool IsSafeKey(string? value) =>
        ReviewModAssetContract.IsBoundedText(
            value,
            ReviewModAssetContract.MaximumKeyLength)
        && !string.IsNullOrWhiteSpace(value);

    private static bool MatchesKeysForShape(
        string shape,
        IReadOnlyList<string> keys,
        ReviewModAssetPage page,
        ReviewModAssetQuery query)
    {
        if (string.Equals(shape, "stringList", StringComparison.Ordinal))
        {
            for (var index = 0; index < keys.Count; index++)
            {
                string expected = checked(query.Offset + index)
                    .ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(keys[index], expected, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        if (string.Equals(shape, "stringSingleton", StringComparison.Ordinal))
        {
            return page.Total == 1
                && keys.All(key => string.Equals(
                    key,
                    ReviewModAssetContract.SingletonKey,
                    StringComparison.Ordinal));
        }

        bool integerKeys = shape is
            "integerKeyStringDictionary" or "integerKeyIntegerDictionary";
        if (!integerKeys
            && shape is not "stringDictionary" and not "integerDictionary")
        {
            return false;
        }

        string? previous = null;
        foreach (string? key in keys)
        {
            if (!IsSafeKey(key)
                || (integerKeys && !IsCanonicalIntegerKey(key!))
                || (previous is not null && string.CompareOrdinal(previous, key) >= 0))
            {
                return false;
            }

            previous = key;
        }

        return true;
    }

    private static bool IsCanonicalIntegerKey(string value) =>
        int.TryParse(
            value,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out int parsed)
        && string.Equals(
            value,
            parsed.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private static bool MatchesGetKey(
        string shape,
        string? returnedKey,
        string queryKey)
    {
        if (!IsSafeKey(returnedKey))
        {
            return false;
        }

        return shape switch
        {
            "stringDictionary" or "integerDictionary" =>
                StableOrExactEquals(returnedKey!, queryKey),
            "integerKeyStringDictionary" or "integerKeyIntegerDictionary" =>
                IsCanonicalIntegerKey(returnedKey!)
                && string.Equals(returnedKey, queryKey, StringComparison.Ordinal),
            "stringList" =>
                IsCanonicalListIndex(returnedKey!)
                && string.Equals(returnedKey, queryKey, StringComparison.Ordinal),
            "stringSingleton" =>
                string.Equals(
                    returnedKey,
                    ReviewModAssetContract.SingletonKey,
                    StringComparison.Ordinal)
                && string.Equals(returnedKey, queryKey, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsCanonicalListIndex(string value) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int parsed)
        && parsed < ReviewModAssetContract.MaximumRecordsPerAsset
        && string.Equals(
            value,
            parsed.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private static bool AssetMatchesQuery(string asset, string query) =>
        ReviewModAssetContract.AssetIdentityEquals(asset, query)
        || ReviewModAssetContract.StableAssetIdentityEquals(asset, query);

    private static bool StableOrExactEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal)
        || string.Equals(
            StableIdentityNormalizer.Normalize(left),
            StableIdentityNormalizer.Normalize(right),
            StringComparison.Ordinal);

    private static void ValidateEnvelopeShape(JsonElement root)
    {
        RequireExactObject(root, EnvelopeProperties);
        RequiredInt32(root, "schemaVersion");
        RequiredString(root, "requestId");

        JsonElement report = root.GetProperty("report");
        RequireExactObject(report, ReportProperties);
        RequiredInt32(report, "schemaVersion");
        RequiredString(report, "state");
        RequiredString(report, "operation");
        OptionalString(report, "gameVersion");
        OptionalString(report, "gameFileVersion");
        RequiredString(report, "coverageScope");
        ValidateOptionalAsset(report.GetProperty("asset"));
        OptionalString(report, "key");
        ValidateOptionalArray(
            report.GetProperty("assets"),
            ReviewModAssetContract.MaximumPageLimit,
            ValidateAsset);
        ValidateOptionalArray(
            report.GetProperty("keys"),
            ReviewModAssetContract.MaximumPageLimit,
            value => RequireKind(value, JsonValueKind.String));
        ValidateOptionalPage(report.GetProperty("page"));
        ValidateOptionalCoverage(report.GetProperty("coverage"));
        ValidateOptionalRecord(report.GetProperty("record"));
        ValidateRequiredArray(
            report.GetProperty("problems"),
            ReviewModAssetContract.MaximumProblemCount,
            problem =>
            {
                RequireExactObject(problem, ProblemProperties);
                RequiredString(problem, "code");
                RequiredString(problem, "message");
            });
    }

    private static void ValidateOptionalAsset(JsonElement asset)
    {
        if (asset.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ValidateAsset(asset);
    }

    private static void ValidateAsset(JsonElement asset)
    {
        RequireExactObject(asset, AssetProperties);
        RequiredString(asset, "assetName");
        OptionalString(asset, "namespaceOwnerId");
        RequiredString(asset, "namespaceOwnerStatus");
        OptionalString(asset, "providerModId");
        RequiredString(asset, "providerStatus");
        RequiredString(asset, "dataType");
        OptionalString(asset, "shape");
        RequiredString(asset, "lifecycle");
        RequiredInt32(asset, "generation");
        RequiredInt32(asset, "requestCount");
        RequiredInt32(asset, "readyCount");
        RequiredBoolean(asset, "available");
        RequiredBoolean(asset, "adapterSupported");
        RequiredBoolean(asset, "nameCollision");
        RequiredBoolean(asset, "typeCollision");
        OptionalString(asset, "problemCode");
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
        OptionalInt32(page, "nextOffset");
    }

    private static void ValidateOptionalCoverage(JsonElement coverage)
    {
        if (coverage.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireExactObject(coverage, CoverageProperties);
        RequiredString(coverage, "scope");
        JsonElement startedAt = coverage.GetProperty("observationStartedAtUtc");
        if (startedAt.ValueKind != JsonValueKind.String
            || !startedAt.TryGetDateTimeOffset(out _))
        {
            throw new InvalidDataException(
                "The review-mod-assets response has an invalid observation timestamp.");
        }

        int observed = RequiredInt32(coverage, "observed");
        int catalogued = RequiredInt32(coverage, "catalogued");
        RequiredInt32(coverage, "adapterSupported");
        RequiredInt32(coverage, "adapterUnavailable");
        RequiredInt32(coverage, "ready");
        RequiredInt32(coverage, "invalidated");
        RequiredInt32(coverage, "unavailable");
        RequiredInt32(coverage, "nameCollisions");
        RequiredInt32(coverage, "typeCollisions");
        int dropped = RequiredInt32(coverage, "dropped");
        bool complete = RequiredBoolean(coverage, "complete");
        if (complete != (observed == catalogued && dropped == 0))
        {
            throw new InvalidDataException(
                "The review-mod-assets coverage completion flag is inconsistent.");
        }
    }

    private static void ValidateOptionalRecord(JsonElement record)
    {
        if (record.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (record.ValueKind == JsonValueKind.String)
        {
            return;
        }
        if (record.ValueKind == JsonValueKind.Number && record.TryGetInt32(out _))
        {
            return;
        }

        throw new InvalidDataException(
            "The review-mod-assets response has an unsupported record value shape.");
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "The review-mod-assets response has an invalid string member.");
        }

        return property.GetString()!;
    }

    private static string? OptionalString(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequiredString(value, propertyName);
    }

    private static int RequiredInt32(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int result))
        {
            throw new InvalidDataException(
                "The review-mod-assets response has an invalid bounded integer member.");
        }

        return result;
    }

    private static int? OptionalInt32(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequiredInt32(value, propertyName);
    }

    private static bool RequiredBoolean(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                "The review-mod-assets response has an invalid Boolean member.");
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
                "The review-mod-assets response has an invalid bounded array shape.");
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            validateItem(item);
        }
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind)
    {
        if (value.ValueKind != kind)
        {
            throw new InvalidDataException(
                "The review-mod-assets response has an invalid JSON value kind.");
        }
    }

    private static void RequireExactObject(
        JsonElement value,
        HashSet<string> requiredProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The review-mod-assets response has an invalid JSON object shape.");
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!requiredProperties.Contains(property.Name)
                || !observed.Add(property.Name))
            {
                throw new InvalidDataException(
                    "The review-mod-assets response has an unknown or duplicate JSON member.");
            }
        }

        if (observed.Count != requiredProperties.Count)
        {
            throw new InvalidDataException(
                "The review-mod-assets response is missing a required JSON member.");
        }
    }

    private static HashSet<string> PropertySet(params string[] names) =>
        new(names, StringComparer.Ordinal);

    private static LiveLabCommandResult Failure(
        string operation,
        params ReviewModAssetProblem[] problems) =>
        new(
            OperationFailed,
            new ReviewModAssetReport(
                ReviewModAssetContract.SchemaVersion,
                "blocked",
                operation,
                null,
                null,
                ReviewModAssetContract.CoverageScope,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                problems));

    private static ReviewModAssetProblem Problem(string code, string message) =>
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
