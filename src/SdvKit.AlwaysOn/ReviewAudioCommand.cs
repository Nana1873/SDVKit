using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
#endif

namespace SdvKit.AlwaysOn;

internal static class ReviewAudioException
{
    public static bool IsFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        while (true)
        {
            if (exception is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException)
            {
                return true;
            }

            if (exception is not TargetInvocationException { InnerException: not null } invocation)
            {
                return false;
            }

            exception = invocation.InnerException!;
        }
    }
}

internal enum ReviewAudioSoundBankStatus
{
    Ready,
    Unavailable,
    Dummy,
    Disposed,
}

internal sealed record ReviewAudioChangeDefinition(
    string ModificationKey,
    string CueId,
    int? VariantCount,
    string? Category,
    bool StreamedVorbis,
    bool Looped,
    bool UseReverb);

internal sealed record ReviewAudioJukeboxDefinition(
    string CueId,
    IReadOnlyList<string?>? AlternativeCueIds);

internal sealed record ReviewAudioCueProbe(
    string CueId,
    bool Exists,
    bool DefinitionAvailable,
    string? DefinitionCueId,
    int? DefinitionVariantCount);

internal interface IReviewAudioSource
{
    string GameVersion { get; }

    string GameFileVersion { get; }

    IReadOnlyList<ReviewAudioChangeDefinition> LoadAudioChanges();

    IReadOnlyList<ReviewAudioJukeboxDefinition> LoadJukeboxTracks();

    ReviewAudioSoundBankStatus GetSoundBankStatus();

    ReviewAudioCueProbe ProbeCue(string cueId);
}

internal static class ReviewAudioOperation
{
    public static ReviewAudioReport Execute(
        ReviewAudioQuery query,
        IReviewAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(source);

        ReviewAudioProblem? queryProblem = Validate(query);
        if (queryProblem is not null)
        {
            return Failure(query.Operation, source, queryProblem);
        }

        AudioInventory inventory;
        try
        {
            inventory = BuildInventory(source);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                query.Operation,
                source,
                Problem(
                    "audioInventoryFailed",
                    $"The active audio data inventory could not be read safely ({exception.GetType().Name})."));
        }

        if (inventory.Problem is not null)
        {
            return Failure(query.Operation, source, inventory.Problem);
        }

        ReviewAudioCoverageReport baseCoverage = Coverage(
            inventory,
            probed: 0,
            resident: 0,
            unavailable: 0);
        ReviewAudioProblem? soundBankProblem = SoundBankProblem(source);
        if (soundBankProblem is not null)
        {
            return Blocked(
                query.Operation,
                source,
                cueId: query.CueId,
                cues: null,
                page: null,
                baseCoverage,
                soundBankProblem);
        }

        return query.Operation switch
        {
            ReviewAudioContract.CuesOperation => ListCues(query, source, inventory),
            ReviewAudioContract.CueOperation => GetCue(query, source, inventory),
            _ => Failure(
                query.Operation,
                source,
                Problem("audioOperationUnknown", "The review-audio operation is unknown.")),
        };
    }

    public static ReviewAudioReport Failure(
        string operation,
        IReviewAudioSource source,
        ReviewAudioProblem problem) =>
        Blocked(
            operation,
            source,
            cueId: null,
            cues: null,
            page: null,
            coverage: null,
            problem);

    internal static ReviewAudioProblem? Validate(ReviewAudioQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
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

        if (query.Operation != ReviewAudioContract.CueOperation)
        {
            return Problem(
                "audioOperationUnknown",
                "The review-audio operation is unknown.");
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
                $"A cue ID must contain 1-{ReviewAudioContract.MaximumCueIdLength} non-control characters.");
        }

        return null;
    }

    private static ReviewAudioReport ListCues(
        ReviewAudioQuery query,
        IReviewAudioSource source,
        AudioInventory inventory)
    {
        int start = Math.Min(query.Offset, inventory.CueIds.Count);
        int returned = Math.Min(query.Limit, inventory.CueIds.Count - start);
        string[] selected = inventory.CueIds
            .Skip(start)
            .Take(returned)
            .ToArray();
        var reports = new List<ReviewAudioCueReport>(selected.Length);
        foreach (string cueId in selected)
        {
            ReviewAudioCueProbe? probe;
            ReviewAudioProblem? probeProblem = TryProbe(source, cueId, out probe);
            if (probeProblem is not null)
            {
                return Blocked(
                    query.Operation,
                    source,
                    cueId,
                    cues: null,
                    page: null,
                    Coverage(inventory, reports.Count, reports.Count(report => report.SessionResident), reports.Count(report => !report.SessionResident)),
                    probeProblem);
            }

            reports.Add(BuildCueReport(cueId, inventory, probe!));
        }

        int resident = reports.Count(report => report.SessionResident);
        var page = new ReviewAudioPage(
            query.Offset,
            query.Limit,
            reports.Count,
            inventory.CueIds.Count,
            start + returned < inventory.CueIds.Count
                ? start + returned
                : null);
        return Ready(
            query.Operation,
            source,
            cueId: null,
            reports,
            page,
            Coverage(
                inventory,
                reports.Count,
                resident,
                reports.Count - resident));
    }

    private static ReviewAudioReport GetCue(
        ReviewAudioQuery query,
        IReviewAudioSource source,
        AudioInventory inventory)
    {
        string requested = query.CueId!;
        string? canonical = inventory.CueIds.FirstOrDefault(
            cueId => string.Equals(cueId, requested, StringComparison.Ordinal));
        if (canonical is null)
        {
            string[] caseMatches = inventory.CueIds
                .Where(cueId => string.Equals(
                    cueId,
                    requested,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (caseMatches.Length > 1)
            {
                return Blocked(
                    query.Operation,
                    source,
                    requested,
                    cues: null,
                    page: null,
                    Coverage(inventory, 0, 0, 0),
                    Problem(
                        "audioCueAmbiguous",
                        "The requested cue ID matches multiple data-driven identities case-insensitively; use an exact canonical cue ID."));
            }
            if (caseMatches.Length == 1)
            {
                return Blocked(
                    query.Operation,
                    source,
                    requested,
                    cues: null,
                    page: null,
                    Coverage(inventory, 0, 0, 0),
                    Problem(
                        "audioCueCaseMismatch",
                        "Audio cue IDs are case-sensitive; use the exact canonical cue ID."));
            }

            canonical = requested;
        }

        ReviewAudioProblem? probeProblem = TryProbe(source, canonical, out ReviewAudioCueProbe? probe);
        if (probeProblem is not null)
        {
            return Blocked(
                query.Operation,
                source,
                canonical,
                cues: null,
                page: null,
                Coverage(inventory, 0, 0, 0),
                probeProblem);
        }

        bool discovered = inventory.CueIdSet.Contains(canonical);
        if (!discovered && !probe!.Exists)
        {
            return Blocked(
                query.Operation,
                source,
                canonical,
                cues: null,
                page: null,
                Coverage(inventory, 1, 0, 1),
                Problem(
                    "audioCueUnknown",
                    "The exact cue is neither in the supported data-driven population nor available in the active soundbank."));
        }

        ReviewAudioCueReport cue = BuildCueReport(canonical, inventory, probe!);
        return Ready(
            query.Operation,
            source,
            canonical,
            [cue],
            page: null,
            Coverage(
                inventory,
                1,
                cue.SessionResident ? 1 : 0,
                cue.SessionResident ? 0 : 1));
    }

    private static ReviewAudioProblem? TryProbe(
        IReviewAudioSource source,
        string cueId,
        out ReviewAudioCueProbe? probe)
    {
        probe = null;
        try
        {
            probe = source.ProbeCue(cueId);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Problem(
                "audioCueProbeFailed",
                $"The exact cue could not be probed safely ({exception.GetType().Name}).");
        }

        if (probe is null
            || !string.Equals(probe.CueId, cueId, StringComparison.Ordinal)
            || (probe.DefinitionAvailable && !probe.Exists)
            || (probe.DefinitionAvailable
                && !string.Equals(
                    probe.DefinitionCueId,
                    cueId,
                    StringComparison.Ordinal))
            || probe.DefinitionVariantCount is < 0 or > ReviewAudioContract.MaximumVariants)
        {
            probe = null;
            return Problem(
                "audioCueProbeInvalid",
                "The soundbank returned inconsistent or mismatched metadata for the exact cue ID.");
        }

        return null;
    }

    private static ReviewAudioProblem? SoundBankProblem(IReviewAudioSource source)
    {
        ReviewAudioSoundBankStatus status;
        try
        {
            status = source.GetSoundBankStatus();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Problem(
                "audioSoundBankUnavailable",
                $"The active soundbank could not be inspected safely ({exception.GetType().Name}).");
        }

        return status switch
        {
            ReviewAudioSoundBankStatus.Ready => null,
            ReviewAudioSoundBankStatus.Unavailable => Problem(
                "audioSoundBankUnavailable",
                "The active soundbank is unavailable."),
            ReviewAudioSoundBankStatus.Dummy => Problem(
                "audioSoundBankUnsupported",
                "The active runtime exposes only Stardew's dummy soundbank; cue metadata is unavailable."),
            ReviewAudioSoundBankStatus.Disposed => Problem(
                "audioSoundBankDisposed",
                "The active soundbank is already disposed."),
            _ => Problem(
                "audioSoundBankUnsupported",
                "The active soundbank has an unsupported state."),
        };
    }

    private static AudioInventory BuildInventory(IReviewAudioSource source)
    {
        IReadOnlyList<ReviewAudioChangeDefinition> audioChanges =
            source.LoadAudioChanges();
        IReadOnlyList<ReviewAudioJukeboxDefinition> jukeboxTracks =
            source.LoadJukeboxTracks();
        if (audioChanges is null || jukeboxTracks is null)
        {
            return AudioInventory.Failed(
                Problem(
                    "audioInventoryInvalid",
                    "An active audio data asset returned no collection."));
        }
        if (audioChanges.Count > ReviewAudioContract.MaximumAudioChangeEntries
            || jukeboxTracks.Count > ReviewAudioContract.MaximumJukeboxTrackEntries)
        {
            return AudioInventory.Failed(
                Problem(
                    "audioInventoryTooLarge",
                    "The active audio data population exceeds its bounded maximum."));
        }

        var changes = new Dictionary<string, ReviewAudioChangeDefinition>(
            StringComparer.Ordinal);
        var references = new Dictionary<string, List<ReviewAudioJukeboxReference>>(
            StringComparer.Ordinal);
        var sources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var cueIds = new SortedSet<string>(StringComparer.Ordinal);
        var playableCueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReviewAudioChangeDefinition? change in audioChanges)
        {
            if (change is null
                || !ReviewAudioValidation.IsSafeCueId(change.CueId)
                || change.VariantCount is < 0 or > ReviewAudioContract.MaximumVariants
                || !IsSafeCategory(change.Category))
            {
                return AudioInventory.Failed(
                    Problem(
                        "audioChangeInvalid",
                        "Data/AudioChanges contains an unsafe or oversized entry."));
            }

            changes[change.CueId] = change;
            cueIds.Add(change.CueId);
            playableCueIds.Add(change.CueId);
            AddSource(sources, change.CueId, ReviewAudioContract.AudioChangesSource);
        }

        var trackCueIds = new HashSet<string>(StringComparer.Ordinal);
        var effectiveAlternatives = new Dictionary<string, EffectiveJukeboxAlternative>(
            StringComparer.OrdinalIgnoreCase);
        var alternativeReferences = 0;
        foreach (ReviewAudioJukeboxDefinition? track in jukeboxTracks)
        {
            IReadOnlyList<string?> alternatives = track?.AlternativeCueIds ?? [];
            if (track is null
                || !ReviewAudioValidation.IsSafeCueId(track.CueId)
                || alternatives.Count > ReviewAudioContract.MaximumAlternativesPerTrack
                || !trackCueIds.Add(track.CueId))
            {
                return AudioInventory.Failed(
                    Problem(
                        "audioJukeboxEntryInvalid",
                        "Data/JukeboxTracks contains an unsafe, duplicate, or oversized entry."));
            }

            cueIds.Add(track.CueId);
            playableCueIds.Add(track.CueId);
            AddSource(sources, track.CueId, ReviewAudioContract.JukeboxTrackSource);
            AddReference(
                references,
                track.CueId,
                new ReviewAudioJukeboxReference(
                    track.CueId,
                    ReviewAudioContract.PrimaryJukeboxRelation));

            foreach (string? alternativeCueId in alternatives)
            {
                alternativeReferences++;
                if (alternativeReferences > ReviewAudioContract.MaximumAlternativeReferences
                    || !ReviewAudioValidation.IsSafeCueId(alternativeCueId))
                {
                    return AudioInventory.Failed(
                        Problem(
                            "audioJukeboxAlternativeInvalid",
                            "Data/JukeboxTracks contains an unsafe or oversized alternative-unlock reference."));
                }

                effectiveAlternatives[alternativeCueId!] = new(
                    alternativeCueId!,
                    track.CueId);
            }
        }

        foreach (EffectiveJukeboxAlternative alternative in effectiveAlternatives.Values)
        {
            string[] playableMatches = playableCueIds
                .Where(cueId => string.Equals(
                    cueId,
                    alternative.CueId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (playableMatches.Length > 1)
            {
                return AudioInventory.Failed(
                    Problem(
                        "audioJukeboxAlternativeAmbiguous",
                        "A jukebox alternative matches multiple playable cue identities case-insensitively."));
            }

            string effectiveCueId = playableMatches.Length == 1
                ? playableMatches[0]
                : alternative.CueId;
            cueIds.Add(effectiveCueId);
            AddSource(
                sources,
                effectiveCueId,
                ReviewAudioContract.JukeboxAlternativeSource);
            AddReference(
                references,
                effectiveCueId,
                new ReviewAudioJukeboxReference(
                    alternative.TrackCueId,
                    ReviewAudioContract.AlternativeJukeboxRelation));
        }

        if (cueIds.Count > ReviewAudioContract.MaximumDiscoverableCueIds)
        {
            return AudioInventory.Failed(
                Problem(
                    "audioInventoryTooLarge",
                    $"The data-driven cue inventory exceeds the bounded maximum of {ReviewAudioContract.MaximumDiscoverableCueIds} identities."));
        }

        int collisionGroups = cueIds
            .GroupBy(cueId => cueId, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        return new AudioInventory(
            changes,
            references.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ReviewAudioJukeboxReference>)pair.Value
                    .OrderBy(reference => reference.TrackCueId, StringComparer.Ordinal)
                    .ThenBy(reference => reference.Relation, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal),
            sources.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<string>)pair.Value,
                StringComparer.Ordinal),
            cueIds.ToArray(),
            new HashSet<string>(cueIds, StringComparer.Ordinal),
            audioChanges.Count,
            jukeboxTracks.Count,
            alternativeReferences,
            collisionGroups,
            null);
    }

    private static ReviewAudioCueReport BuildCueReport(
        string cueId,
        AudioInventory inventory,
        ReviewAudioCueProbe probe)
    {
        inventory.Changes.TryGetValue(
            cueId,
            out ReviewAudioChangeDefinition? change);
        inventory.Sources.TryGetValue(cueId, out IReadOnlySet<string>? sourceSet);
        string[] orderedSources =
        [
            .. new[]
            {
                ReviewAudioContract.AudioChangesSource,
                ReviewAudioContract.JukeboxTrackSource,
                ReviewAudioContract.JukeboxAlternativeSource,
            }.Where(source => sourceSet?.Contains(source) == true),
        ];
        return new ReviewAudioCueReport(
            cueId,
            orderedSources,
            change is not null,
            probe.Exists,
            probe.DefinitionAvailable,
            probe.DefinitionVariantCount,
            change?.VariantCount,
            change is null ? null : EffectiveCategory(change.Category),
            change?.StreamedVorbis,
            change?.Looped,
            change?.UseReverb,
            inventory.References.TryGetValue(
                cueId,
                out IReadOnlyList<ReviewAudioJukeboxReference>? cueReferences)
                    ? cueReferences
                    : []);
    }

    private static void AddSource(
        Dictionary<string, HashSet<string>> sources,
        string cueId,
        string source)
    {
        if (!sources.TryGetValue(cueId, out HashSet<string>? values))
        {
            values = new HashSet<string>(StringComparer.Ordinal);
            sources.Add(cueId, values);
        }

        values.Add(source);
    }

    private static void AddReference(
        Dictionary<string, List<ReviewAudioJukeboxReference>> references,
        string cueId,
        ReviewAudioJukeboxReference reference)
    {
        if (!references.TryGetValue(
                cueId,
                out List<ReviewAudioJukeboxReference>? values))
        {
            values = [];
            references.Add(cueId, values);
        }

        values.Add(reference);
    }

    private static bool IsSafeCategory(string? value) =>
        string.IsNullOrEmpty(value)
        || (!string.IsNullOrWhiteSpace(value)
            && value.Length <= ReviewAudioContract.MaximumCategoryLength
            && !value.Any(char.IsControl)
            && ReviewTransportText.IsWellFormedUtf16(value));

    private static string EffectiveCategory(string? value) =>
        string.IsNullOrEmpty(value) ? "Default" : value;

    private static ReviewAudioCoverageReport Coverage(
        AudioInventory inventory,
        int probed,
        int resident,
        int unavailable) =>
        new(
            inventory.AudioChangeEntries,
            inventory.JukeboxTrackEntries,
            inventory.AlternativeReferences,
            inventory.CueIds.Count,
            probed,
            resident,
            unavailable,
            inventory.IdentityCollisionGroups,
            inventory.Problem is null,
            null,
            ReviewAudioContract.BuiltInInventoryStatus);

    private static ReviewAudioReport Ready(
        string operation,
        IReviewAudioSource source,
        string? cueId,
        IReadOnlyList<ReviewAudioCueReport> cues,
        ReviewAudioPage? page,
        ReviewAudioCoverageReport coverage) =>
        new(
            ReviewAudioContract.SchemaVersion,
            "ready",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            cueId,
            cues,
            page,
            coverage,
            []);

    private static ReviewAudioReport Blocked(
        string operation,
        IReviewAudioSource source,
        string? cueId,
        IReadOnlyList<ReviewAudioCueReport>? cues,
        ReviewAudioPage? page,
        ReviewAudioCoverageReport? coverage,
        ReviewAudioProblem problem) =>
        new(
            ReviewAudioContract.SchemaVersion,
            "blocked",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            cueId,
            cues,
            page,
            coverage,
            [problem]);

    private static ReviewAudioProblem Problem(string code, string message) =>
        new(code, message);

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException;

    private sealed record AudioInventory(
        IReadOnlyDictionary<string, ReviewAudioChangeDefinition> Changes,
        IReadOnlyDictionary<string, IReadOnlyList<ReviewAudioJukeboxReference>> References,
        IReadOnlyDictionary<string, IReadOnlySet<string>> Sources,
        IReadOnlyList<string> CueIds,
        IReadOnlySet<string> CueIdSet,
        int AudioChangeEntries,
        int JukeboxTrackEntries,
        int AlternativeReferences,
        int IdentityCollisionGroups,
        ReviewAudioProblem? Problem)
    {
        public static AudioInventory Failed(ReviewAudioProblem problem) =>
            new(
                new Dictionary<string, ReviewAudioChangeDefinition>(),
                new Dictionary<string, IReadOnlyList<ReviewAudioJukeboxReference>>(),
                new Dictionary<string, IReadOnlySet<string>>(),
                [],
                new HashSet<string>(),
                0,
                0,
                0,
                0,
                problem);
    }

    private sealed record EffectiveJukeboxAlternative(
        string CueId,
        string TrackCueId);
}

internal static class ReviewAudioArguments
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReviewAudioQuery? query,
        out ReviewAudioProblem? problem)
    {
        query = null;
        problem = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "audio", StringComparison.Ordinal)
            || !ReviewTransportToken.IsRequestId(arguments[1]))
        {
            problem = new ReviewAudioProblem(
                "audioTransportInvalid",
                "The bounded review-audio transport request is invalid.");
            return false;
        }

        string operation = arguments[2];
        int expectedCount = operation switch
        {
            ReviewAudioContract.CuesOperation => 5,
            ReviewAudioContract.CueOperation => 6,
            _ => 0,
        };
        if (expectedCount == 0
            || arguments.Count != expectedCount
            || !int.TryParse(
                arguments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int offset)
            || !int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int limit))
        {
            problem = new ReviewAudioProblem(
                "audioTransportInvalid",
                "The bounded review-audio transport request is invalid.");
            return false;
        }

        string? cueId = null;
        if (operation == ReviewAudioContract.CueOperation
            && !ReviewTransportToken.TryDecode(
                arguments[5],
                ReviewAudioContract.MaximumCueIdLength,
                out cueId))
        {
            problem = new ReviewAudioProblem(
                "audioTransportInvalid",
                "The encoded review-audio cue ID is invalid.");
            return false;
        }

        query = new ReviewAudioQuery(operation, cueId, offset, limit);
        problem = ReviewAudioOperation.Validate(query);
        return problem is null;
    }
}

internal static class ReviewAudioResponseSerializer
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] SerializeBounded(ReviewAudioResponseEnvelope envelope)
    {
        return SerializeBounded(envelope, out _);
    }

    public static byte[] SerializeBounded(
        ReviewAudioResponseEnvelope envelope,
        out ReviewAudioReport serializedReport)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Report);

        try
        {
            serializedReport = envelope.Report;
            return SerializeWithinLimit(envelope);
        }
        catch (ReviewAudioResponseTooLargeException)
        {
            string operation = envelope.Report.Operation is
                ReviewAudioContract.CuesOperation or ReviewAudioContract.CueOperation
                    ? envelope.Report.Operation
                    : "unknown";
            var boundedEnvelope = new ReviewAudioResponseEnvelope(
                ReviewAudioContract.SchemaVersion,
                envelope.RequestId,
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
                    [
                        new ReviewAudioProblem(
                            "audioResponseTooLarge",
                            "The bounded review-audio response exceeds its maximum UTF-8 size."),
                    ]));
            serializedReport = boundedEnvelope.Report;
            return SerializeWithinLimit(boundedEnvelope);
        }
    }

    private static byte[] SerializeWithinLimit(ReviewAudioResponseEnvelope envelope)
    {
        using var stream = new BoundedWriteStream(
            ReviewAudioContract.MaximumResponseBytes);
        JsonSerializer.Serialize(stream, envelope, ResponseJsonOptions);
        return stream.ToArray();
    }

    private sealed class ReviewAudioResponseTooLargeException : IOException
    {
    }

    private sealed class BoundedWriteStream : Stream
    {
        private readonly int _maximumBytes;
        private readonly MemoryStream _stream = new(capacity: 4096);

        public BoundedWriteStream(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _stream.Length;

        public override long Position
        {
            get => _stream.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _stream.ToArray();

        public override void Flush() => _stream.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _stream.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _stream.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _stream.WriteByte(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }

        private void EnsureCapacity(long additionalBytes)
        {
            if (additionalBytes < 0
                || additionalBytes > _maximumBytes - _stream.Length)
            {
                throw new ReviewAudioResponseTooLargeException();
            }
        }
    }
}

internal static class ReviewAudioResponseFile
{
    public static ReviewAudioReport Write(
        string runtimePath,
        ReviewAudioResponseEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        ArgumentNullException.ThrowIfNull(envelope);

        string absoluteRuntimePath = Path.GetFullPath(runtimePath);
        FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
        if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
            || (runtimeAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The review runtime response root is not a regular directory.");
        }

        string responsePath = ReviewAudioContract.ResponsePath(
            absoluteRuntimePath,
            envelope.RequestId);
        string temporaryPath = responsePath + ".tmp";
        if (EntryExists(responsePath) || EntryExists(temporaryPath))
        {
            throw new InvalidDataException(
                "The review-audio response target already exists.");
        }

        byte[] bytes = ReviewAudioResponseSerializer.SerializeBounded(
            envelope,
            out ReviewAudioReport serializedReport);

        var ownsTemporary = false;
        var ownsResponse = false;
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                ownsTemporary = true;
                EnsureRegularFile(temporaryPath);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            EnsureRegularFile(temporaryPath);
            File.Move(temporaryPath, responsePath);
            ownsTemporary = false;
            ownsResponse = true;
            EnsureRegularFile(responsePath);
            ownsResponse = false;
            return serializedReport;
        }
        finally
        {
            if (ownsTemporary)
            {
                TryDeleteOwnedRegularFile(temporaryPath);
            }
            if (ownsResponse)
            {
                TryDeleteOwnedRegularFile(responsePath);
            }
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
    }

    private static void EnsureRegularFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException(
                "The review-audio response is not a regular file.");
        }
    }

    private static void TryDeleteOwnedRegularFile(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0
                && (attributes & FileAttributes.Directory) == 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            // The unique owned path is already absent.
        }
    }
}

#if SDVKIT_GAME_AVAILABLE
internal sealed class StardewReviewAudioSource : IReviewAudioSource
{
    private const string AudioChangesAsset = "Data/AudioChanges";
    private const string JukeboxTracksAsset = "Data/JukeboxTracks";
    private readonly IModHelper _helper;

    public StardewReviewAudioSource(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _helper = helper;
    }

    public string GameVersion => Game1.version.ToString();

    public string GameFileVersion =>
        FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion
        ?? string.Empty;

    public IReadOnlyList<ReviewAudioChangeDefinition> LoadAudioChanges()
    {
        IDictionary<string, AudioCueData> values =
            _helper.GameContent.Load<Dictionary<string, AudioCueData>>(
                AudioChangesAsset);
        if (values is null
            || values.Count > ReviewAudioContract.MaximumAudioChangeEntries)
        {
            throw new InvalidDataException(
                "Data/AudioChanges exceeds the bounded source population.");
        }

        var definitions = new List<ReviewAudioChangeDefinition>(values.Count);
        foreach (KeyValuePair<string, AudioCueData> pair in values)
        {
            definitions.Add(new ReviewAudioChangeDefinition(
                pair.Key,
                pair.Value?.Id!,
                pair.Value?.FilePaths?.Count,
                pair.Value?.Category,
                pair.Value?.StreamedVorbis ?? false,
                pair.Value?.Looped ?? false,
                pair.Value?.UseReverb ?? false));
        }

        return definitions;
    }

    public IReadOnlyList<ReviewAudioJukeboxDefinition> LoadJukeboxTracks()
    {
        IDictionary<string, JukeboxTrackData> values =
            _helper.GameContent.Load<Dictionary<string, JukeboxTrackData>>(
                JukeboxTracksAsset);
        if (values is null
            || values.Count > ReviewAudioContract.MaximumJukeboxTrackEntries)
        {
            throw new InvalidDataException(
                "Data/JukeboxTracks exceeds the bounded source population.");
        }

        var definitions = new List<ReviewAudioJukeboxDefinition>(values.Count);
        var alternativeReferences = 0;
        foreach (KeyValuePair<string, JukeboxTrackData> pair in values)
        {
            JukeboxTrackData data = pair.Value
                ?? throw new InvalidDataException(
                    "Data/JukeboxTracks contains a null entry model.");
            List<string>? alternatives = data.AlternativeTrackIds;
            if (alternatives is not null)
            {
                if (alternatives.Count > ReviewAudioContract.MaximumAlternativesPerTrack
                    || (long)alternativeReferences + alternatives.Count
                        > ReviewAudioContract.MaximumAlternativeReferences)
                {
                    throw new InvalidDataException(
                        "Data/JukeboxTracks exceeds the bounded alternative-reference population.");
                }

                alternativeReferences += alternatives.Count;
            }

            definitions.Add(new ReviewAudioJukeboxDefinition(
                pair.Key,
                alternatives?.Select(value => (string?)value).ToArray()));
        }

        return definitions;
    }

    public ReviewAudioSoundBankStatus GetSoundBankStatus()
    {
        ISoundBank? soundBank = Game1.soundBank;
        if (soundBank is null)
        {
            return ReviewAudioSoundBankStatus.Unavailable;
        }
        if (soundBank is DummySoundBank)
        {
            return ReviewAudioSoundBankStatus.Dummy;
        }

        return soundBank.IsDisposed
            ? ReviewAudioSoundBankStatus.Disposed
            : ReviewAudioSoundBankStatus.Ready;
    }

    public ReviewAudioCueProbe ProbeCue(string cueId)
    {
        if (!ReviewAudioValidation.IsSafeCueId(cueId))
        {
            throw new ArgumentException("The cue ID is invalid.", nameof(cueId));
        }

        ISoundBank soundBank = Game1.soundBank
            ?? throw new InvalidOperationException("The active soundbank is unavailable.");
        if (soundBank is DummySoundBank || soundBank.IsDisposed)
        {
            throw new InvalidOperationException("The active soundbank cannot be queried.");
        }

        bool exists = soundBank.Exists(cueId);
        if (!exists)
        {
            return new ReviewAudioCueProbe(
                cueId,
                false,
                false,
                null,
                null);
        }

        CueDefinition? definition = soundBank.GetCueDefinition(cueId);
        return new ReviewAudioCueProbe(
            cueId,
            true,
            definition is not null,
            definition?.name,
            definition?.sounds?.Count);
    }
}

internal static class ReviewAudioCommand
{
    public static void Handle(
        string[] arguments,
        IReviewAudioSource source,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-audio runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        string? requestId = arguments.Length > 1 ? arguments[1] : null;
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log(
                "SDVKit review-audio rejected an invalid request ID.",
                LogLevel.Error);
            return;
        }

        ReviewAudioReport report;
        bool singleReview = string.Equals(
                Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW"),
                "1",
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
        if (!singleReview)
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewAudioOperation.Failure(
                operation,
                source,
                new ReviewAudioProblem(
                    "audioReviewTopologyUnsupported",
                    "Review-audio queries require an active owned single project review."));
        }
        else if (!ReviewAudioArguments.TryParse(
                arguments,
                out ReviewAudioQuery? query,
                out ReviewAudioProblem? problem))
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewAudioOperation.Failure(operation, source, problem!);
        }
        else
        {
            try
            {
                report = ReviewAudioOperation.Execute(query!, source);
            }
            catch (Exception exception) when (!ReviewAudioException.IsFatal(exception))
            {
                report = ReviewAudioOperation.Failure(
                    query!.Operation,
                    source,
                    new ReviewAudioProblem(
                        "audioQueryFailed",
                        $"The review-audio query failed closed ({exception.GetType().Name})."));
            }
        }

        var envelope = new ReviewAudioResponseEnvelope(
            ReviewAudioContract.SchemaVersion,
            requestId!,
            report);
        try
        {
            report = ReviewAudioResponseFile.Write(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-audio completed '{report.Operation}' with state '{report.State}'.",
                report.Problems.Count == 0 ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception) when (!ReviewAudioException.IsFatal(exception))
        {
            monitor.Log(
                $"SDVKit review-audio could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

}
#endif
