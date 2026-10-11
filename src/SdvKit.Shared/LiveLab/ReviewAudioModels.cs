namespace SdvKit.Cli.LiveLab;

internal static class ReviewAudioContract
{
    public const int SchemaVersion = 1;
    public const int DefaultPageLimit = 50;
    public const int MaximumPageLimit = 100;
    public const int MaximumCueIdLength = 256;
    public const int MaximumResponseBytes = 512 * 1024;
    public const int MaximumAudioChangeEntries = 4096;
    public const int MaximumJukeboxTrackEntries = 4096;
    public const int MaximumAlternativesPerTrack = 256;
    public const int MaximumAlternativeReferences = 16_384;
    public const int MaximumDiscoverableCueIds = 8192;
    public const int MaximumVariants = 4096;
    public const int MaximumCategoryLength = 128;
    public const string CuesOperation = "cues";
    public const string CueOperation = "cue";
    public const string AudioChangesSource = "audioChanges";
    public const string JukeboxTrackSource = "jukeboxTrack";
    public const string JukeboxAlternativeSource = "jukeboxAlternativeUnlock";
    public const string PrimaryJukeboxRelation = "trackCue";
    public const string AlternativeJukeboxRelation = "alternativeUnlock";
    public const string BuiltInInventoryStatus = "unavailableByPublicApi";

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-audio runtime path is required.",
                nameof(runtimePath));
        }
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-audio request ID is invalid.",
                nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-audio-{requestId}.json");
    }
}

internal static class ReviewAudioValidation
{
    public static bool IsSafeCueId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= ReviewAudioContract.MaximumCueIdLength
        && !value.Any(char.IsControl)
        && ReviewTransportText.IsWellFormedUtf16(value);
}

internal sealed record ReviewAudioQuery(
    string Operation,
    string? CueId,
    int Offset,
    int Limit);

internal sealed record ReviewAudioProblem(
    string Code,
    string Message);

internal sealed record ReviewAudioPage(
    int Offset,
    int Limit,
    int Returned,
    int Total,
    int? NextOffset);

internal sealed record ReviewAudioJukeboxReference(
    string TrackCueId,
    string Relation);

internal sealed record ReviewAudioCueReport(
    string CueId,
    IReadOnlyList<string> Sources,
    bool DataDefined,
    bool SessionResident,
    bool DefinitionAvailable,
    int? DefinitionVariantCount,
    int? DataVariantCount,
    string? Category,
    bool? StreamedVorbis,
    bool? Looped,
    bool? UseReverb,
    IReadOnlyList<ReviewAudioJukeboxReference> JukeboxReferences);

internal sealed record ReviewAudioCoverageReport(
    int AudioChangeEntries,
    int JukeboxTrackEntries,
    int JukeboxAlternativeReferences,
    int DiscoverableCueIds,
    int ProbedCueIds,
    int SessionResidentCueIds,
    int UnavailableCueIds,
    int IdentityCollisionGroups,
    bool DataDrivenPopulationComplete,
    int? BuiltInCueCount,
    string BuiltInCueInventoryStatus);

internal sealed record ReviewAudioReport(
    int SchemaVersion,
    string State,
    string Operation,
    string? GameVersion,
    string? GameFileVersion,
    string? CueId,
    IReadOnlyList<ReviewAudioCueReport>? Cues,
    ReviewAudioPage? Page,
    ReviewAudioCoverageReport? Coverage,
    IReadOnlyList<ReviewAudioProblem> Problems);

internal sealed record ReviewAudioResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    ReviewAudioReport Report);

internal static class ReviewAudioQueryValidation
{
    public static ReviewAudioProblem? Validate(ReviewAudioQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Operation == ReviewAudioContract.CuesOperation)
        {
            if (query.CueId is not null)
            {
                return new ReviewAudioProblem(
                    "audioRequestInvalid",
                    "The cue inventory request has an unexpected cue ID.");
            }
            if (query.Offset < 0
                || query.Limit < 1
                || query.Limit > ReviewAudioContract.MaximumPageLimit)
            {
                return new ReviewAudioProblem(
                    "audioPaginationInvalid",
                    $"Offset must be non-negative and limit must be between 1 and {ReviewAudioContract.MaximumPageLimit}.");
            }

            return null;
        }

        if (query.Operation != ReviewAudioContract.CueOperation)
        {
            return new ReviewAudioProblem(
                "audioOperationUnknown",
                "The review-audio operation is unknown.");
        }
        if (query.Offset != 0 || query.Limit != 1)
        {
            return new ReviewAudioProblem(
                "audioRequestInvalid",
                "An exact cue request does not accept pagination.");
        }
        if (!ReviewAudioValidation.IsSafeCueId(query.CueId))
        {
            return new ReviewAudioProblem(
                "audioCueIdInvalid",
                $"A cue ID must contain 1-{ReviewAudioContract.MaximumCueIdLength} non-control characters.");
        }

        return null;
    }
}
