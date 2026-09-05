using System.Globalization;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal static class ProjectReviewAudioService
{
    private static readonly ReviewResponseJson ResponseJson = new("review-audio");

    private const int Success = 0;
    private const int OperationFailed = 3;
    private const int MaximumJsonDepth = 16;
    private const int MaximumProblemCount = 8;
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
        "cueId",
        "cues",
        "page",
        "coverage",
        "problems");
    private static readonly HashSet<string> ProblemProperties = PropertySet(
        "code",
        "message");
    private static readonly HashSet<string> CueProperties = PropertySet(
        "cueId",
        "sources",
        "dataDefined",
        "sessionResident",
        "definitionAvailable",
        "definitionVariantCount",
        "dataVariantCount",
        "category",
        "streamedVorbis",
        "looped",
        "useReverb",
        "jukeboxReferences");
    private static readonly HashSet<string> JukeboxReferenceProperties = PropertySet(
        "trackCueId",
        "relation");
    private static readonly HashSet<string> PageProperties = PropertySet(
        "offset",
        "limit",
        "returned",
        "total",
        "nextOffset");
    private static readonly HashSet<string> CoverageProperties = PropertySet(
        "audioChangeEntries",
        "jukeboxTrackEntries",
        "jukeboxAlternativeReferences",
        "discoverableCueIds",
        "probedCueIds",
        "sessionResidentCueIds",
        "unavailableCueIds",
        "identityCollisionGroups",
        "dataDrivenPopulationComplete",
        "builtInCueCount",
        "builtInCueInventoryStatus");

    public static LiveLabCommandResult Execute(
        ReviewAudioQuery query,
        string labRoot,
        IProjectReviewConsoleInputSender? inputSender = null,
        Action<TimeSpan>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(labRoot);

        ReviewAudioProblem? queryProblem = Validate(query);
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
        string responsePath = ReviewAudioContract.ResponsePath(
            paths.RuntimePath,
            requestId);
        string command = BuildCommand(requestId, query);
        ProjectReviewResponseTransportResult<ReviewAudioResponseEnvelope> transported =
            ProjectReviewResponseTransport.Execute(
                command,
                responsePath,
                ReviewAudioContract.MaximumResponseBytes,
                "audio",
                "review-audio",
                labRoot,
                DeserializeResponse,
                envelope => MatchesResponse(envelope, requestId, query),
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

        ReviewAudioReport report = transported.Response.Report;
        return new LiveLabCommandResult(
            report.Problems.Count == 0
                && string.Equals(report.State, "ready", StringComparison.Ordinal)
                    ? Success
                    : OperationFailed,
            report);
    }

    internal static string BuildCommand(
        string requestId,
        ReviewAudioQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-audio request ID is invalid.",
                nameof(requestId));
        }
        if (Validate(query) is ReviewAudioProblem queryProblem)
        {
            throw new ArgumentException(queryProblem.Message, nameof(query));
        }

        var tokens = new List<string>
        {
            "sdvkit",
            "audio",
            requestId,
            query.Operation,
            query.Offset.ToString(CultureInfo.InvariantCulture),
            query.Limit.ToString(CultureInfo.InvariantCulture),
        };
        if (query.CueId is not null)
        {
            tokens.Add(ReviewTransportToken.Encode(query.CueId));
        }

        string command = string.Join(" ", tokens);
        string? validationError = ProjectReviewConsoleLine.ValidationError(command);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        return command;
    }

    internal static ReviewAudioResponseEnvelope? DeserializeResponse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using (JsonDocument document = JsonDocument.Parse(bytes, ResponseDocumentOptions))
        {
            ValidateEnvelopeShape(document.RootElement);
        }

        return JsonSerializer.Deserialize<ReviewAudioResponseEnvelope>(
            bytes,
            ResponseJsonOptions);
    }

    private static void ValidateEnvelopeShape(JsonElement root)
    {
        ResponseJson.RequireExactObject(root, EnvelopeProperties);
        ResponseJson.RequiredInt32(root, "schemaVersion");
        RequiredString(root, "requestId");

        JsonElement report = root.GetProperty("report");
        ResponseJson.RequireExactObject(report, ReportProperties);
        ResponseJson.RequiredInt32(report, "schemaVersion");
        RequiredString(report, "state");
        RequiredString(report, "operation");
        ValidateOptionalString(report.GetProperty("gameVersion"));
        ValidateOptionalString(report.GetProperty("gameFileVersion"));
        ValidateOptionalString(report.GetProperty("cueId"));
        ResponseJson.ValidateOptionalArray(
            report.GetProperty("cues"),
            ReviewAudioContract.MaximumPageLimit,
            ValidateCueShape);
        ValidateOptionalPageShape(report.GetProperty("page"));
        ValidateOptionalCoverageShape(report.GetProperty("coverage"));
        ResponseJson.ValidateRequiredArray(
            report.GetProperty("problems"),
            MaximumProblemCount,
            problem =>
            {
                ResponseJson.RequireExactObject(problem, ProblemProperties);
                RequiredString(problem, "code");
                RequiredString(problem, "message");
            });
    }

    private static void ValidateCueShape(JsonElement cue)
    {
        ResponseJson.RequireExactObject(cue, CueProperties);
        RequiredString(cue, "cueId");
        ResponseJson.ValidateRequiredArray(
            cue.GetProperty("sources"),
            3,
            source => _ = RequiredString(source));
        ResponseJson.RequiredBoolean(cue, "dataDefined");
        ResponseJson.RequiredBoolean(cue, "sessionResident");
        ResponseJson.RequiredBoolean(cue, "definitionAvailable");
        ValidateOptionalInt32(cue.GetProperty("definitionVariantCount"));
        ValidateOptionalInt32(cue.GetProperty("dataVariantCount"));
        ValidateOptionalString(cue.GetProperty("category"));
        ValidateOptionalBoolean(cue.GetProperty("streamedVorbis"));
        ValidateOptionalBoolean(cue.GetProperty("looped"));
        ValidateOptionalBoolean(cue.GetProperty("useReverb"));
        ResponseJson.ValidateRequiredArray(
            cue.GetProperty("jukeboxReferences"),
            2,
            reference =>
            {
                ResponseJson.RequireExactObject(reference, JukeboxReferenceProperties);
                RequiredString(reference, "trackCueId");
                RequiredString(reference, "relation");
            });
    }

    private static void ValidateOptionalPageShape(JsonElement page)
    {
        if (page.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ResponseJson.RequireExactObject(page, PageProperties);
        ResponseJson.RequiredInt32(page, "offset");
        ResponseJson.RequiredInt32(page, "limit");
        ResponseJson.RequiredInt32(page, "returned");
        ResponseJson.RequiredInt32(page, "total");
        ValidateOptionalInt32(page.GetProperty("nextOffset"));
    }

    private static void ValidateOptionalCoverageShape(JsonElement coverage)
    {
        if (coverage.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ResponseJson.RequireExactObject(coverage, CoverageProperties);
        ResponseJson.RequiredInt32(coverage, "audioChangeEntries");
        ResponseJson.RequiredInt32(coverage, "jukeboxTrackEntries");
        ResponseJson.RequiredInt32(coverage, "jukeboxAlternativeReferences");
        ResponseJson.RequiredInt32(coverage, "discoverableCueIds");
        ResponseJson.RequiredInt32(coverage, "probedCueIds");
        ResponseJson.RequiredInt32(coverage, "sessionResidentCueIds");
        ResponseJson.RequiredInt32(coverage, "unavailableCueIds");
        ResponseJson.RequiredInt32(coverage, "identityCollisionGroups");
        ResponseJson.RequiredBoolean(coverage, "dataDrivenPopulationComplete");
        ValidateOptionalInt32(coverage.GetProperty("builtInCueCount"));
        RequiredString(coverage, "builtInCueInventoryStatus");
    }

    private static void ValidateOptionalInt32(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Null
            && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
        {
            throw new InvalidDataException(
                "The review-audio response has an invalid optional integer member.");
        }
    }

    private static void ValidateOptionalBoolean(JsonElement value)
    {
        if (value.ValueKind is not (
                JsonValueKind.Null
                or JsonValueKind.True
                or JsonValueKind.False))
        {
            throw new InvalidDataException(
                "The review-audio response has an invalid optional Boolean member.");
        }
    }

    private static string RequiredString(JsonElement value, string propertyName) =>
        RequiredString(value.GetProperty(propertyName));

    private static string RequiredString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "The review-audio response has an invalid string member.");
        }

        string result;
        try
        {
            result = value.GetString()
                ?? throw new InvalidDataException(
                    "The review-audio response has a null string member.");
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                "The review-audio response has a malformed Unicode string member.",
                exception);
        }
        if (!ReviewTransportText.IsWellFormedUtf16(result))
        {
            throw new InvalidDataException(
                "The review-audio response has a malformed Unicode string member.");
        }

        return result;
    }

    private static void ValidateOptionalString(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Null and not JsonValueKind.String)
        {
            throw new InvalidDataException(
                "The review-audio response has an invalid optional string member.");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            _ = RequiredString(value);
        }
    }

    private static HashSet<string> PropertySet(params string[] names) =>
        new(names, StringComparer.Ordinal);

    internal static bool MatchesResponse(
        ReviewAudioResponseEnvelope? envelope,
        string requestId,
        ReviewAudioQuery? query)
    {
        if (envelope is null
            || query is null
            || Validate(query) is not null
            || !ReviewTransportToken.IsRequestId(requestId))
        {
            return false;
        }

        ReviewAudioReport? report = envelope.Report;
        if (report is null
            || report.Problems is null
            || envelope.SchemaVersion != ReviewAudioContract.SchemaVersion
            || !string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal)
            || report.SchemaVersion != ReviewAudioContract.SchemaVersion
            || !string.Equals(report.Operation, query.Operation, StringComparison.Ordinal)
            || !OptionalCueIdIsSafe(report.CueId))
        {
            return false;
        }

        if (string.Equals(report.State, "blocked", StringComparison.Ordinal))
        {
            return BlockedResponseMatches(report, query);
        }
        if (!string.Equals(report.State, "ready", StringComparison.Ordinal)
            || report.Problems.Count != 0
            || !IsSafeText(report.GameVersion, 128)
            || !IsSafeText(report.GameFileVersion, 128)
            || report.Coverage is not ReviewAudioCoverageReport coverage
            || !CoverageIsValid(coverage)
            || report.Cues is not IReadOnlyList<ReviewAudioCueReport> cues
            || !CueGraphIsValid(cues))
        {
            return false;
        }

        return query.Operation switch
        {
            ReviewAudioContract.CuesOperation => InventoryResponseMatches(
                report,
                query,
                cues,
                coverage),
            ReviewAudioContract.CueOperation => ExactResponseMatches(
                report,
                query,
                cues,
                coverage),
            _ => false,
        };
    }

    private static bool BlockedResponseMatches(
        ReviewAudioReport report,
        ReviewAudioQuery query)
    {
        if (report.Problems.Count is < 1 or > 8
            || report.Problems.Any(problem => problem is null
                || !IsSafeText(problem.Code, 128)
                || !IsSafeText(problem.Message, 1024))
            || report.Cues is not null
            || report.Page is not null
            || (report.Coverage is not null && !CoverageIsValid(report.Coverage))
            || !OptionalVersionPairIsSafe(report.GameVersion, report.GameFileVersion))
        {
            return false;
        }

        return query.Operation switch
        {
            ReviewAudioContract.CuesOperation => report.CueId is null,
            ReviewAudioContract.CueOperation => report.CueId is null
                || string.Equals(report.CueId, query.CueId, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool InventoryResponseMatches(
        ReviewAudioReport report,
        ReviewAudioQuery query,
        IReadOnlyList<ReviewAudioCueReport> cues,
        ReviewAudioCoverageReport coverage)
    {
        if (report.CueId is not null
            || report.Page is not ReviewAudioPage page
            || !PageMatches(page, query, cues.Count)
            || coverage.DiscoverableCueIds != page.Total
            || coverage.ProbedCueIds != cues.Count
            || coverage.SessionResidentCueIds != cues.Count(cue => cue.SessionResident)
            || coverage.UnavailableCueIds != cues.Count(cue => !cue.SessionResident)
            || !coverage.DataDrivenPopulationComplete
            || !CoverageIncludesReturnedSources(cues, coverage))
        {
            return false;
        }

        string? previousCueId = null;
        foreach (ReviewAudioCueReport cue in cues)
        {
            if (previousCueId is not null
                && string.Compare(previousCueId, cue.CueId, StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            previousCueId = cue.CueId;
        }

        return true;
    }

    private static bool ExactResponseMatches(
        ReviewAudioReport report,
        ReviewAudioQuery query,
        IReadOnlyList<ReviewAudioCueReport> cues,
        ReviewAudioCoverageReport coverage) =>
        report.Page is null
        && string.Equals(report.CueId, query.CueId, StringComparison.Ordinal)
        && cues.Count == 1
        && string.Equals(cues[0].CueId, query.CueId, StringComparison.Ordinal)
        && coverage.ProbedCueIds == 1
        && coverage.SessionResidentCueIds == (cues[0].SessionResident ? 1 : 0)
        && coverage.UnavailableCueIds == (cues[0].SessionResident ? 0 : 1)
        && CoverageIncludesReturnedSources(cues, coverage)
        && (cues[0].Sources.Count == 0 || coverage.DiscoverableCueIds >= 1);

    private static bool CoverageIncludesReturnedSources(
        IReadOnlyList<ReviewAudioCueReport> cues,
        ReviewAudioCoverageReport coverage)
    {
        int audioChangeCues = cues.Count(cue => cue.Sources.Contains(
            ReviewAudioContract.AudioChangesSource,
            StringComparer.Ordinal));
        int primaryJukeboxCues = cues.Count(cue => cue.Sources.Contains(
            ReviewAudioContract.JukeboxTrackSource,
            StringComparer.Ordinal));
        int alternativeJukeboxCues = cues.Count(cue => cue.Sources.Contains(
            ReviewAudioContract.JukeboxAlternativeSource,
            StringComparer.Ordinal));

        return audioChangeCues <= coverage.AudioChangeEntries
            && primaryJukeboxCues <= coverage.JukeboxTrackEntries
            && alternativeJukeboxCues <= coverage.JukeboxAlternativeReferences;
    }

    private static bool PageMatches(
        ReviewAudioPage page,
        ReviewAudioQuery query,
        int returned)
    {
        if (page.Offset != query.Offset
            || page.Limit != query.Limit
            || page.Returned != returned
            || page.Total < 0
            || page.Total > ReviewAudioContract.MaximumDiscoverableCueIds)
        {
            return false;
        }

        long remaining = Math.Max(0L, (long)page.Total - query.Offset);
        int expectedReturned = (int)Math.Min(query.Limit, remaining);
        long end = (long)query.Offset + returned;
        int? expectedNext = end < page.Total ? checked((int)end) : null;
        return returned == expectedReturned && page.NextOffset == expectedNext;
    }

    private static bool CoverageIsValid(ReviewAudioCoverageReport coverage)
    {
        if (coverage.AudioChangeEntries is < 0 or > ReviewAudioContract.MaximumAudioChangeEntries
            || coverage.JukeboxTrackEntries is < 0 or > ReviewAudioContract.MaximumJukeboxTrackEntries
            || coverage.JukeboxAlternativeReferences is < 0 or > ReviewAudioContract.MaximumAlternativeReferences
            || coverage.DiscoverableCueIds is < 0 or > ReviewAudioContract.MaximumDiscoverableCueIds
            || coverage.ProbedCueIds is < 0 or > ReviewAudioContract.MaximumPageLimit
            || coverage.SessionResidentCueIds < 0
            || coverage.UnavailableCueIds < 0
            || (long)coverage.SessionResidentCueIds + coverage.UnavailableCueIds
                != coverage.ProbedCueIds
            || coverage.IdentityCollisionGroups < 0
            || coverage.IdentityCollisionGroups > coverage.DiscoverableCueIds
            || !coverage.DataDrivenPopulationComplete
            || coverage.BuiltInCueCount is not null
            || !string.Equals(
                coverage.BuiltInCueInventoryStatus,
                ReviewAudioContract.BuiltInInventoryStatus,
                StringComparison.Ordinal)
            || coverage.DiscoverableCueIds >
                (long)coverage.AudioChangeEntries
                    + coverage.JukeboxTrackEntries
                    + coverage.JukeboxAlternativeReferences)
        {
            return false;
        }

        return coverage.JukeboxAlternativeReferences <=
            (long)coverage.JukeboxTrackEntries
                * ReviewAudioContract.MaximumAlternativesPerTrack;
    }

    private static bool CueGraphIsValid(IReadOnlyList<ReviewAudioCueReport> cues)
    {
        if (cues.Count > ReviewAudioContract.MaximumPageLimit)
        {
            return false;
        }

        foreach (ReviewAudioCueReport? cue in cues)
        {
            if (cue is null || !CueIsValid(cue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CueIsValid(ReviewAudioCueReport cue)
    {
        if (!ReviewAudioValidation.IsSafeCueId(cue.CueId)
            || cue.Sources is null
            || cue.JukeboxReferences is null
            || cue.DefinitionVariantCount is < 0 or > ReviewAudioContract.MaximumVariants
            || cue.DataVariantCount is < 0 or > ReviewAudioContract.MaximumVariants
            || (!cue.SessionResident && cue.DefinitionAvailable)
            || (!cue.DefinitionAvailable && cue.DefinitionVariantCount is not null))
        {
            return false;
        }

        string[] validSources =
        [
            ReviewAudioContract.AudioChangesSource,
            ReviewAudioContract.JukeboxTrackSource,
            ReviewAudioContract.JukeboxAlternativeSource,
        ];
        var previousSourceIndex = -1;
        foreach (string? source in cue.Sources)
        {
            int sourceIndex = Array.IndexOf(validSources, source);
            if (sourceIndex <= previousSourceIndex)
            {
                return false;
            }

            previousSourceIndex = sourceIndex;
        }

        bool hasAudioChange = cue.Sources.Contains(
            ReviewAudioContract.AudioChangesSource,
            StringComparer.Ordinal);
        if (cue.DataDefined != hasAudioChange
            || (cue.DataDefined
                && (!IsSafeOptionalCategory(cue.Category)
                    || cue.StreamedVorbis is null
                    || cue.Looped is null
                    || cue.UseReverb is null))
            || (!cue.DataDefined
                && (cue.DataVariantCount is not null
                    || cue.Category is not null
                    || cue.StreamedVorbis is not null
                    || cue.Looped is not null
                    || cue.UseReverb is not null)))
        {
            return false;
        }

        if (cue.JukeboxReferences.Count > 2)
        {
            return false;
        }

        string? previousTrackCueId = null;
        string? previousRelation = null;
        var hasPrimaryReference = false;
        var hasAlternativeReference = false;
        foreach (ReviewAudioJukeboxReference? reference in cue.JukeboxReferences)
        {
            if (reference is null
                || !ReviewAudioValidation.IsSafeCueId(reference.TrackCueId)
                || reference.Relation is not (
                    ReviewAudioContract.PrimaryJukeboxRelation
                    or ReviewAudioContract.AlternativeJukeboxRelation))
            {
                return false;
            }

            if (previousTrackCueId is not null)
            {
                int trackComparison = string.Compare(
                    previousTrackCueId,
                    reference.TrackCueId,
                    StringComparison.Ordinal);
                if (trackComparison > 0
                    || (trackComparison == 0
                        && string.Compare(
                            previousRelation,
                            reference.Relation,
                            StringComparison.Ordinal) >= 0))
                {
                    return false;
                }
            }

            if (reference.Relation == ReviewAudioContract.PrimaryJukeboxRelation)
            {
                if (hasPrimaryReference
                    || !string.Equals(
                        reference.TrackCueId,
                        cue.CueId,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                hasPrimaryReference = true;
            }
            else
            {
                if (hasAlternativeReference)
                {
                    return false;
                }

                hasAlternativeReference = true;
            }

            previousTrackCueId = reference.TrackCueId;
            previousRelation = reference.Relation;
        }

        return hasPrimaryReference == cue.Sources.Contains(
                ReviewAudioContract.JukeboxTrackSource,
                StringComparer.Ordinal)
            && hasAlternativeReference == cue.Sources.Contains(
                ReviewAudioContract.JukeboxAlternativeSource,
                StringComparer.Ordinal);
    }

    private static bool IsSafeOptionalCategory(string? value) =>
        value is not null
        && value.Length <= ReviewAudioContract.MaximumCategoryLength
        && IsSafeText(value, ReviewAudioContract.MaximumCategoryLength);

    private static bool OptionalCueIdIsSafe(string? value) =>
        value is null || ReviewAudioValidation.IsSafeCueId(value);

    private static bool OptionalVersionPairIsSafe(string? gameVersion, string? fileVersion) =>
        gameVersion is null && fileVersion is null
        || IsSafeText(gameVersion, 128) && IsSafeText(fileVersion, 128);

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl)
        && ReviewTransportText.IsWellFormedUtf16(value);

    internal static ReviewAudioProblem? Validate(ReviewAudioQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Operation is not (
                ReviewAudioContract.CuesOperation
                or ReviewAudioContract.CueOperation))
        {
            return Problem(
                "audioOperationUnknown",
                "The review-audio operation is unknown.");
        }

        if (query.Operation == ReviewAudioContract.CuesOperation)
        {
            if (query.CueId is not null)
            {
                return Problem(
                    "audioRequestInvalid",
                    "The cue inventory request has an unexpected cue ID.");
            }
            if (query.Offset < 0
                || query.Limit < 1
                || query.Limit > ReviewAudioContract.MaximumPageLimit)
            {
                return Problem(
                    "audioPaginationInvalid",
                    $"Offset must be non-negative and limit must be between 1 and {ReviewAudioContract.MaximumPageLimit}.");
            }

            return null;
        }

        if (query.Offset != 0 || query.Limit != 1)
        {
            return Problem(
                "audioRequestInvalid",
                "An exact cue request does not accept pagination.");
        }
        if (!ReviewAudioValidation.IsSafeCueId(query.CueId))
        {
            return Problem(
                "audioCueIdInvalid",
                $"A cue ID must contain 1-{ReviewAudioContract.MaximumCueIdLength} well-formed non-control characters.");
        }

        return null;
    }

    private static LiveLabCommandResult Failure(
        string operation,
        params ReviewAudioProblem[] problems) =>
        new(
            OperationFailed,
            new ReviewAudioReport(
                ReviewAudioContract.SchemaVersion,
                "blocked",
                operation,
                null,
                null,
                null,
                null,
                null,
                null,
                problems));

    private static ReviewAudioProblem Problem(string code, string message) =>
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
