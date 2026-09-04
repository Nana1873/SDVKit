using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewAudioCommandTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void InventoryIsUnionedSortedPagedAndKeepsSourceCategoriesDistinct()
    {
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change("Mod.Cue", variants: 2, category: "Music", streamed: true, looped: true),
                Change("Click", variants: 1, category: "Sound"),
            ],
            tracks:
            [
                new ReviewAudioJukeboxDefinition(
                    "MainTheme",
                    ["Mod.Cue", "LegacyTheme"]),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>(StringComparer.Ordinal)
            {
                ["Click"] = Probe("Click", exists: true, variants: 1),
                ["LegacyTheme"] = Probe("LegacyTheme", exists: false),
                ["MainTheme"] = Probe("MainTheme", exists: true, variants: 3),
                ["Mod.Cue"] = Probe("Mod.Cue", exists: true, variants: 2),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            offset: 1,
            limit: 2);

        Assert.Equal("ready", report.State);
        Assert.Empty(report.Problems);
        Assert.Equal(
            new ReviewAudioPage(1, 2, 2, 4, 3),
            report.Page);
        ReviewAudioCueReport[] cues = Assert.IsAssignableFrom<
            IReadOnlyList<ReviewAudioCueReport>>(report.Cues).ToArray();
        Assert.Collection(
            cues,
            cue =>
            {
                Assert.Equal("LegacyTheme", cue.CueId);
                Assert.Equal(
                    [ReviewAudioContract.JukeboxAlternativeSource],
                    cue.Sources);
                Assert.False(cue.DataDefined);
                Assert.False(cue.SessionResident);
                Assert.False(cue.DefinitionAvailable);
                ReviewAudioJukeboxReference reference = Assert.Single(
                    cue.JukeboxReferences);
                Assert.Equal("MainTheme", reference.TrackCueId);
                Assert.Equal(
                    ReviewAudioContract.AlternativeJukeboxRelation,
                    reference.Relation);
            },
            cue =>
            {
                Assert.Equal("MainTheme", cue.CueId);
                Assert.Equal(
                    [ReviewAudioContract.JukeboxTrackSource],
                    cue.Sources);
                Assert.False(cue.DataDefined);
                Assert.True(cue.SessionResident);
                Assert.Equal(3, cue.DefinitionVariantCount);
                Assert.Equal(
                    ReviewAudioContract.PrimaryJukeboxRelation,
                    Assert.Single(cue.JukeboxReferences).Relation);
            });

        ReviewAudioCoverageReport coverage =
            Assert.IsType<ReviewAudioCoverageReport>(report.Coverage);
        Assert.Equal(2, coverage.AudioChangeEntries);
        Assert.Equal(1, coverage.JukeboxTrackEntries);
        Assert.Equal(2, coverage.JukeboxAlternativeReferences);
        Assert.Equal(4, coverage.DiscoverableCueIds);
        Assert.Equal(2, coverage.ProbedCueIds);
        Assert.Equal(1, coverage.SessionResidentCueIds);
        Assert.Equal(1, coverage.UnavailableCueIds);
        Assert.True(coverage.DataDrivenPopulationComplete);
        Assert.Null(coverage.BuiltInCueCount);
        Assert.Equal(
            ReviewAudioContract.BuiltInInventoryStatus,
            coverage.BuiltInCueInventoryStatus);
        Assert.Equal(["LegacyTheme", "MainTheme"], source.ProbedCueIds);
    }

    [Fact]
    public void AudioChangeMetadataNeverIncludesPathsOrCustomFields()
    {
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change(
                    "Mod.SafeCue",
                    variants: 4,
                    category: "Ambient",
                    streamed: true,
                    looped: true,
                    reverb: true),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Mod.SafeCue"] = Probe("Mod.SafeCue", exists: true, variants: 4),
            });

        ReviewAudioCueReport cue = Assert.Single(
            Execute(
                source,
                ReviewAudioContract.CueOperation,
                cueId: "Mod.SafeCue",
                limit: 1).Cues!);

        Assert.True(cue.DataDefined);
        Assert.True(cue.SessionResident);
        Assert.True(cue.DefinitionAvailable);
        Assert.Equal(4, cue.DataVariantCount);
        Assert.Equal(4, cue.DefinitionVariantCount);
        Assert.Equal("Ambient", cue.Category);
        Assert.True(cue.StreamedVorbis);
        Assert.True(cue.Looped);
        Assert.True(cue.UseReverb);
        string[] propertyNames = typeof(ReviewAudioCueReport)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain("FilePaths", propertyNames);
        Assert.DoesNotContain("CustomFields", propertyNames);
        Assert.DoesNotContain("Audio", propertyNames);
    }

    [Fact]
    public void MissingCategoryUsesTheGameDefault()
    {
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change("Mod.DefaultCategory", category: null),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Mod.DefaultCategory"] = Probe(
                    "Mod.DefaultCategory",
                    exists: true,
                    variants: 1),
            });

        ReviewAudioCueReport cue = Assert.Single(
            Execute(
                source,
                ReviewAudioContract.CueOperation,
                cueId: "Mod.DefaultCategory",
                limit: 1).Cues!);

        Assert.Equal("Default", cue.Category);
    }

    [Fact]
    public void ExplicitlyEmptyCategoryFailsClosedWithoutProbing()
    {
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change("Mod.EmptyCategory", category: ""),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Mod.EmptyCategory"] = Probe(
                    "Mod.EmptyCategory",
                    exists: true,
                    variants: 1),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "Mod.EmptyCategory",
            limit: 1);

        Assert.Equal("blocked", report.State);
        Assert.Equal("audioChangeInvalid", Assert.Single(report.Problems).Code);
        Assert.Empty(source.ProbedCueIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void UnspecifiedAndExplicitlyEmptyFilePathsRemainDistinctAndValid(
        int? variantCount)
    {
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change("Mod.NoFiles", variants: variantCount),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Mod.NoFiles"] = new ReviewAudioCueProbe(
                    "Mod.NoFiles",
                    true,
                    false,
                    null,
                    null),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "Mod.NoFiles",
            limit: 1);

        Assert.Equal("ready", report.State);
        Assert.Equal(variantCount, Assert.Single(report.Cues!).DataVariantCount);
    }

    [Fact]
    public void ExactBuiltInProbeDoesNotPretendTheBuiltInBankWasEnumerated()
    {
        var source = new FakeReviewAudioSource(
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["MainTheme"] = Probe("MainTheme", exists: true, variants: 3),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "MainTheme",
            limit: 1);

        Assert.Equal("ready", report.State);
        ReviewAudioCueReport cue = Assert.Single(report.Cues!);
        Assert.Equal("MainTheme", cue.CueId);
        Assert.Empty(cue.Sources);
        Assert.False(cue.DataDefined);
        Assert.True(cue.SessionResident);
        Assert.Null(report.Coverage!.BuiltInCueCount);
        Assert.Equal(
            "unavailableByPublicApi",
            report.Coverage.BuiltInCueInventoryStatus);
    }

    [Fact]
    public void RemovedDataDefinitionCanRemainSessionResidentWithoutInventedProvenance()
    {
        var source = new FakeReviewAudioSource(
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Mod.RemovedCue"] = Probe(
                    "Mod.RemovedCue",
                    exists: true,
                    variants: 1),
            });

        ReviewAudioCueReport cue = Assert.Single(
            Execute(
                source,
                ReviewAudioContract.CueOperation,
                cueId: "Mod.RemovedCue",
                limit: 1).Cues!);

        Assert.False(cue.DataDefined);
        Assert.True(cue.SessionResident);
        Assert.Empty(cue.Sources);
    }

    [Fact]
    public void AlternativeUnlockReferenceIsNotReportedAsSoundBankAvailability()
    {
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition("NewCue", ["OldCue"]),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["NewCue"] = Probe("NewCue", exists: true, variants: 1),
                ["OldCue"] = Probe("OldCue", exists: false),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "OldCue",
            limit: 1);

        Assert.Equal("ready", report.State);
        ReviewAudioCueReport cue = Assert.Single(report.Cues!);
        Assert.False(cue.SessionResident);
        Assert.False(cue.DefinitionAvailable);
        Assert.Equal(
            [ReviewAudioContract.JukeboxAlternativeSource],
            cue.Sources);
        Assert.Equal(
            ReviewAudioContract.AlternativeJukeboxRelation,
            Assert.Single(cue.JukeboxReferences).Relation);
    }

    [Fact]
    public void MissingJukeboxAlternativeListUsesTheDocumentedEmptyDefault()
    {
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition("MainTheme", null),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["MainTheme"] = Probe("MainTheme", exists: true, variants: 1),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "MainTheme",
            limit: 1);

        Assert.Equal("ready", report.State);
        Assert.Equal(0, report.Coverage!.JukeboxAlternativeReferences);
    }

    [Fact]
    public void UnknownAndCaseMismatchedCueIdsFailClosed()
    {
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition("MainTheme", []),
            ]);

        ReviewAudioReport unknown = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "Does.Not.Exist",
            limit: 1);
        Assert.Equal("audioCueUnknown", Assert.Single(unknown.Problems).Code);

        ReviewAudioReport caseMismatch = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "maintheme",
            limit: 1);
        Assert.Equal(
            "audioCueCaseMismatch",
            Assert.Single(caseMismatch.Problems).Code);
        Assert.Equal(["Does.Not.Exist"], source.ProbedCueIds);
    }

    [Fact]
    public void PlayableCaseCollisionsRemainExactAndOnlyNonExactLookupIsAmbiguous()
    {
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition("Cue", []),
                new ReviewAudioJukeboxDefinition("CUE", []),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Cue"] = Probe("Cue", exists: true, variants: 1),
                ["CUE"] = Probe("CUE", exists: true, variants: 1),
            });

        ReviewAudioReport inventory = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);
        Assert.Equal("ready", inventory.State);
        Assert.Equal(["CUE", "Cue"], inventory.Cues!.Select(cue => cue.CueId));
        Assert.Equal(1, inventory.Coverage!.IdentityCollisionGroups);
        Assert.True(inventory.Coverage.DataDrivenPopulationComplete);
        string requestId = Guid.NewGuid().ToString("N");
        Assert.True(ProjectReviewAudioService.MatchesResponse(
            new ReviewAudioResponseEnvelope(
                ReviewAudioContract.SchemaVersion,
                requestId,
                inventory),
            requestId,
            new ReviewAudioQuery(
                ReviewAudioContract.CuesOperation,
                null,
                0,
                100)));

        ReviewAudioReport ambiguous = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "cUe",
            limit: 1);
        Assert.Equal(
            "audioCueAmbiguous",
            Assert.Single(ambiguous.Problems).Code);

        ReviewAudioReport exact = Execute(
            source,
            ReviewAudioContract.CueOperation,
            cueId: "Cue",
            limit: 1);
        Assert.Equal("ready", exact.State);
        Assert.Equal("Cue", Assert.Single(exact.Cues!).CueId);
    }

    [Fact]
    public void VanillaCaseVariantAlternativeFoldsOntoItsCanonicalPlayableCue()
    {
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition(
                    "_disabled_",
                    ["jojaOfficeSoundscape"]),
                new ReviewAudioJukeboxDefinition("jojaofficesoundscape", []),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>(StringComparer.Ordinal)
            {
                ["_disabled_"] = Probe("_disabled_", exists: false),
                ["jojaofficesoundscape"] = Probe(
                    "jojaofficesoundscape",
                    exists: true,
                    variants: 1),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("ready", report.State);
        Assert.DoesNotContain(
            report.Cues!,
            cue => cue.CueId == "jojaOfficeSoundscape");
        ReviewAudioCueReport canonical = Assert.Single(
            report.Cues!,
            cue => cue.CueId == "jojaofficesoundscape");
        Assert.Equal(
            [
                ReviewAudioContract.JukeboxTrackSource,
                ReviewAudioContract.JukeboxAlternativeSource,
            ],
            canonical.Sources);
        Assert.Contains(
            canonical.JukeboxReferences,
            reference => reference.TrackCueId == "jojaofficesoundscape"
                && reference.Relation == ReviewAudioContract.PrimaryJukeboxRelation);
        Assert.Contains(
            canonical.JukeboxReferences,
            reference => reference.TrackCueId == "_disabled_"
                && reference.Relation == ReviewAudioContract.AlternativeJukeboxRelation);
        Assert.Equal(1, report.Coverage!.JukeboxAlternativeReferences);
        Assert.Equal(2, report.Coverage.DiscoverableCueIds);
        Assert.Equal(0, report.Coverage.IdentityCollisionGroups);
        Assert.Equal(
            ["_disabled_", "jojaofficesoundscape"],
            source.ProbedCueIds);
    }

    [Fact]
    public void AlternativeMatchingMultiplePlayableCaseVariantsFailsClosed()
    {
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition("Cue", []),
                new ReviewAudioJukeboxDefinition("CUE", []),
                new ReviewAudioJukeboxDefinition("History", ["cUe"]),
            ]);

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("blocked", report.State);
        Assert.Equal(
            "audioJukeboxAlternativeAmbiguous",
            Assert.Single(report.Problems).Code);
        Assert.Empty(source.ProbedCueIds);
    }

    [Theory]
    [InlineData("Unavailable", "audioSoundBankUnavailable")]
    [InlineData("Dummy", "audioSoundBankUnsupported")]
    [InlineData("Disposed", "audioSoundBankDisposed")]
    public void UnavailableDummyAndDisposedSoundBanksFailBeforeProbing(
        string statusName,
        string expectedProblem)
    {
        ReviewAudioSoundBankStatus status = Enum.Parse<ReviewAudioSoundBankStatus>(
            statusName);
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition("MainTheme", []),
            ],
            soundBankStatus: status);

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("blocked", report.State);
        Assert.Equal(expectedProblem, Assert.Single(report.Problems).Code);
        Assert.Empty(source.ProbedCueIds);
    }

    [Fact]
    public void AudioChangesUsesTheDeclaredCueIdInsteadOfTheModificationKey()
    {
        string modificationKey = new('m', ReviewAudioContract.MaximumCueIdLength + 1);
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change("Playable.Cue", modificationKey: modificationKey),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>(StringComparer.Ordinal)
            {
                ["Playable.Cue"] = Probe("Playable.Cue", exists: true, variants: 1),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("ready", report.State);
        ReviewAudioCueReport cue = Assert.Single(report.Cues!);
        Assert.Equal("Playable.Cue", cue.CueId);
        Assert.DoesNotContain(modificationKey, report.Cues!.Select(value => value.CueId));
        Assert.Equal(["Playable.Cue"], source.ProbedCueIds);
    }

    [Fact]
    public void LaterAudioChangeModificationWinsForTheSameDeclaredCueId()
    {
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change(
                    "Same.Cue",
                    variants: 1,
                    category: "Sound",
                    modificationKey: "Patch.Entry.One"),
                Change(
                    "Same.Cue",
                    variants: 3,
                    category: "Music",
                    streamed: true,
                    looped: true,
                    modificationKey: "Patch.Entry.Two"),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>(StringComparer.Ordinal)
            {
                ["Same.Cue"] = Probe("Same.Cue", exists: true, variants: 3),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("ready", report.State);
        ReviewAudioCueReport cue = Assert.Single(report.Cues!);
        Assert.Equal(3, cue.DataVariantCount);
        Assert.Equal("Music", cue.Category);
        Assert.True(cue.StreamedVorbis);
        Assert.True(cue.Looped);
        Assert.Equal(2, report.Coverage!.AudioChangeEntries);
        Assert.Equal(1, report.Coverage.DiscoverableCueIds);
        string requestId = Guid.NewGuid().ToString("N");
        Assert.True(ProjectReviewAudioService.MatchesResponse(
            new ReviewAudioResponseEnvelope(
                ReviewAudioContract.SchemaVersion,
                requestId,
                report),
            requestId,
            new ReviewAudioQuery(
                ReviewAudioContract.CuesOperation,
                null,
                0,
                100)));
    }

    [Fact]
    public void LaterJukeboxTrackWinsGlobalCaseInsensitiveAlternativeCollisions()
    {
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition("First.Track", ["Old"]),
                new ReviewAudioJukeboxDefinition("Later.Track", ["old"]),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>(StringComparer.Ordinal)
            {
                ["First.Track"] = Probe("First.Track", exists: true, variants: 1),
                ["Later.Track"] = Probe("Later.Track", exists: true, variants: 1),
                ["old"] = Probe("old", exists: false),
            });

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("ready", report.State);
        Assert.DoesNotContain(report.Cues!, cue => cue.CueId == "Old");
        ReviewAudioCueReport effective = Assert.Single(
            report.Cues!,
            cue => cue.CueId == "old");
        ReviewAudioJukeboxReference reference = Assert.Single(
            effective.JukeboxReferences);
        Assert.Equal("Later.Track", reference.TrackCueId);
        Assert.Equal(ReviewAudioContract.AlternativeJukeboxRelation, reference.Relation);
        Assert.Equal(2, report.Coverage!.JukeboxAlternativeReferences);
        Assert.Equal(3, report.Coverage.DiscoverableCueIds);
        string requestId = Guid.NewGuid().ToString("N");
        Assert.True(ProjectReviewAudioService.MatchesResponse(
            new ReviewAudioResponseEnvelope(
                ReviewAudioContract.SchemaVersion,
                requestId,
                report),
            requestId,
            new ReviewAudioQuery(
                ReviewAudioContract.CuesOperation,
                null,
                0,
                100)));
    }

    [Theory]
    [InlineData("unsafeCategory")]
    [InlineData("unsafeCueId")]
    [InlineData("nullJukebox")]
    public void MalformedDataDrivenEntriesFailClosed(string kind)
    {
        IReadOnlyList<ReviewAudioChangeDefinition> changes = kind switch
        {
            "unsafeCategory" =>
            [
                Change("Cue", category: "Sound\nunsafe"),
            ],
            "unsafeCueId" =>
            [
                Change("\ud800"),
            ],
            _ => [],
        };
        IReadOnlyList<ReviewAudioJukeboxDefinition> tracks = kind switch
        {
            "nullJukebox" => [null!],
            _ => [],
        };
        var source = new FakeReviewAudioSource(changes, tracks);

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("blocked", report.State);
        Assert.StartsWith("audio", Assert.Single(report.Problems).Code);
        Assert.Empty(source.ProbedCueIds);
    }

    [Fact]
    public void OversizedPopulationFailsClosedWithoutProbing()
    {
        ReviewAudioJukeboxDefinition[] tracks = Enumerable
            .Range(0, 4097)
            .Select(index => new ReviewAudioJukeboxDefinition($"Cue{index}", []))
            .ToArray();
        var source = new FakeReviewAudioSource(tracks: tracks);

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("audioInventoryTooLarge", Assert.Single(report.Problems).Code);
        Assert.Empty(source.ProbedCueIds);
    }

    [Fact]
    public void OversizedAudioChangePopulationFailsClosedWithoutProbing()
    {
        ReviewAudioChangeDefinition[] changes = Enumerable
            .Range(0, ReviewAudioContract.MaximumAudioChangeEntries + 1)
            .Select(index => Change($"Cue{index}"))
            .ToArray();
        var source = new FakeReviewAudioSource(changes: changes);

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("audioInventoryTooLarge", Assert.Single(report.Problems).Code);
        Assert.Empty(source.ProbedCueIds);
    }

    [Fact]
    public void PerTrackAndGlobalAlternativeBoundsFailClosedWithoutProbing()
    {
        var perTrackSource = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition(
                    "Track",
                    Enumerable
                        .Range(0, ReviewAudioContract.MaximumAlternativesPerTrack + 1)
                        .Select(index => $"Alternative{index}")
                        .ToArray()),
            ]);
        ReviewAudioReport perTrack = Execute(
            perTrackSource,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        ReviewAudioJukeboxDefinition[] globallyOversized = Enumerable
            .Range(0, 65)
            .Select(trackIndex => new ReviewAudioJukeboxDefinition(
                $"Track{trackIndex:D2}",
                Enumerable
                    .Range(
                        0,
                        trackIndex < 64
                            ? ReviewAudioContract.MaximumAlternativesPerTrack
                            : 1)
                    .Select(alternativeIndex =>
                        $"Alternative{trackIndex:D2}_{alternativeIndex:D3}")
                    .ToArray()))
            .ToArray();
        var globalSource = new FakeReviewAudioSource(tracks: globallyOversized);
        ReviewAudioReport global = Execute(
            globalSource,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal(
            "audioJukeboxEntryInvalid",
            Assert.Single(perTrack.Problems).Code);
        Assert.Equal(
            "audioJukeboxAlternativeInvalid",
            Assert.Single(global.Problems).Code);
        Assert.Empty(perTrackSource.ProbedCueIds);
        Assert.Empty(globalSource.ProbedCueIds);
    }

    [Fact]
    public void DiscoverableIdentityBoundFailsClosedWithoutProbing()
    {
        ReviewAudioChangeDefinition[] changes = Enumerable
            .Range(0, ReviewAudioContract.MaximumAudioChangeEntries)
            .Select(index => Change($"Change{index:D4}"))
            .ToArray();
        ReviewAudioJukeboxDefinition[] tracks = Enumerable
            .Range(0, ReviewAudioContract.MaximumJukeboxTrackEntries)
            .Select(index => new ReviewAudioJukeboxDefinition(
                $"Track{index:D4}",
                index == 0 ? ["Extra"] : []))
            .ToArray();
        var source = new FakeReviewAudioSource(changes, tracks);

        ReviewAudioReport report = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal("audioInventoryTooLarge", Assert.Single(report.Problems).Code);
        Assert.Empty(source.ProbedCueIds);
    }

    [Fact]
    public void MismatchedOrOversizedProbeMetadataFailsClosed()
    {
        var mismatched = new FakeReviewAudioSource(
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Cue"] = new ReviewAudioCueProbe(
                    "Cue",
                    true,
                    true,
                    "Other",
                    1),
            });
        Assert.Equal(
            "audioCueProbeInvalid",
            Assert.Single(Execute(
                mismatched,
                ReviewAudioContract.CueOperation,
                cueId: "Cue",
                limit: 1).Problems).Code);

        var oversized = new FakeReviewAudioSource(
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Cue"] = Probe("Cue", exists: true, variants: 4097),
            });
        Assert.Equal(
            "audioCueProbeInvalid",
            Assert.Single(Execute(
                oversized,
                ReviewAudioContract.CueOperation,
                cueId: "Cue",
                limit: 1).Problems).Code);
    }

    [Fact]
    public void InventoryProbeFailureRemainsBoundToTheInventoryOperation()
    {
        var source = new FakeReviewAudioSource(
            tracks:
            [
                new ReviewAudioJukeboxDefinition("Cue", []),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["Cue"] = new ReviewAudioCueProbe(
                    "Cue",
                    true,
                    true,
                    "Other",
                    1),
            });
        var query = new ReviewAudioQuery(
            ReviewAudioContract.CuesOperation,
            null,
            0,
            100);

        ReviewAudioReport report = ReviewAudioOperation.Execute(query, source);

        Assert.Equal("blocked", report.State);
        Assert.Null(report.CueId);
        Assert.Equal("audioCueProbeInvalid", Assert.Single(report.Problems).Code);
        string requestId = Guid.NewGuid().ToString("N");
        Assert.True(ProjectReviewAudioService.MatchesResponse(
            new ReviewAudioResponseEnvelope(
                ReviewAudioContract.SchemaVersion,
                requestId,
                report),
            requestId,
            query));
    }

    [Fact]
    public void RepeatedQueriesAreDeterministic()
    {
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change("B"),
                Change("A"),
            ],
            probes: new Dictionary<string, ReviewAudioCueProbe>
            {
                ["A"] = Probe("A", exists: true, variants: 1),
                ["B"] = Probe("B", exists: true, variants: 1),
            });

        ReviewAudioReport first = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);
        ReviewAudioReport second = Execute(
            source,
            ReviewAudioContract.CuesOperation,
            limit: 100);

        Assert.Equal(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second));
        Assert.Equal(["A", "B", "A", "B"], source.ProbedCueIds);
    }

    [Fact]
    public void TransportParserAcceptsOnlyCanonicalBoundedRequests()
    {
        string requestId = Guid.NewGuid().ToString("N");
        string cueToken = ReviewTransportToken.Encode("Main Theme");

        Assert.True(ReviewAudioArguments.TryParse(
            ["audio", requestId, "cue", "0", "1", cueToken],
            out ReviewAudioQuery? cue,
            out _));
        Assert.Equal(
            new ReviewAudioQuery(
                ReviewAudioContract.CueOperation,
                "Main Theme",
                0,
                1),
            cue);

        Assert.True(ReviewAudioArguments.TryParse(
            ["audio", requestId, "cues", "5", "10"],
            out ReviewAudioQuery? cues,
            out _));
        Assert.Equal(
            new ReviewAudioQuery(
                ReviewAudioContract.CuesOperation,
                null,
                5,
                10),
            cues);

        Assert.False(ReviewAudioArguments.TryParse(
            ["audio", requestId, "cue", "0", "1", cueToken + "="],
            out _,
            out ReviewAudioProblem? invalidToken));
        Assert.Equal("audioTransportInvalid", invalidToken!.Code);
        Assert.False(ReviewAudioArguments.TryParse(
            ["audio", requestId, "cues", "0", "101"],
            out _,
            out ReviewAudioProblem? invalidLimit));
        Assert.Equal("audioPaginationInvalid", invalidLimit!.Code);
    }

    [Fact]
    public void CueTokensAreCanonicalInjectiveAndRejectMalformedUtf16()
    {
        string[] cueIds = ["A/B", "A?B", "é", "e\u0301", "🎵"];
        string[] tokens = cueIds.Select(ReviewTransportToken.Encode).ToArray();

        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < tokens.Length; index++)
        {
            Assert.True(ReviewTransportToken.TryDecode(
                tokens[index],
                ReviewAudioContract.MaximumCueIdLength,
                out string decoded));
            Assert.Equal(cueIds[index], decoded);
        }

        Assert.False(ReviewAudioValidation.IsSafeCueId("\ud800"));
        Assert.False(ReviewAudioValidation.IsSafeCueId("\udc00"));
        Assert.True(ReviewAudioValidation.IsSafeCueId("🎵"));
        var malformed = new ReviewAudioQuery(
            ReviewAudioContract.CueOperation,
            "\ud800",
            0,
            1);
        Assert.NotNull(ProjectReviewAudioService.Validate(malformed));
        Assert.Throws<ArgumentException>(() => ProjectReviewAudioService.BuildCommand(
            Guid.NewGuid().ToString("N"),
            malformed));
    }

    [Fact]
    public void ResponseDeserializerRequiresTheExactRecursiveWireShape()
    {
        string requestId = Guid.NewGuid().ToString("N");
        JsonObject baseline = JsonNode.Parse(
            JsonSerializer.Serialize(InventoryEnvelope(requestId), CamelCaseJson))!
            .AsObject();
        JsonObject report = baseline["report"]!.AsObject();
        JsonObject cue = report["cues"]!.AsArray()[0]!.AsObject();
        cue["jukeboxReferences"]!.AsArray().Add(
            new JsonObject
            {
                ["trackCueId"] = "Track",
                ["relation"] = ReviewAudioContract.AlternativeJukeboxRelation,
            });
        report["problems"]!.AsArray().Add(
            new JsonObject
            {
                ["code"] = "synthetic",
                ["message"] = "Synthetic bounded problem.",
            });
        Assert.NotNull(ProjectReviewAudioService.DeserializeResponse(
            System.Text.Encoding.UTF8.GetBytes(baseline.ToJsonString())));

        Func<JsonObject, JsonObject>[] nestedObjects =
        [
            root => root,
            root => root["report"]!.AsObject(),
            root => root["report"]!["cues"]![0]!.AsObject(),
            root => root["report"]!["cues"]![0]!["jukeboxReferences"]![0]!.AsObject(),
            root => root["report"]!["page"]!.AsObject(),
            root => root["report"]!["coverage"]!.AsObject(),
            root => root["report"]!["problems"]![0]!.AsObject(),
        ];
        foreach (Func<JsonObject, JsonObject> select in nestedObjects)
        {
            JsonObject changed = baseline.DeepClone().AsObject();
            select(changed)["unexpected"] = true;
            Assert.Throws<InvalidDataException>(() =>
                ProjectReviewAudioService.DeserializeResponse(
                    System.Text.Encoding.UTF8.GetBytes(changed.ToJsonString())));
        }

        JsonObject wrongCase = baseline.DeepClone().AsObject();
        JsonObject wrongCaseReport = wrongCase["report"]!.AsObject();
        wrongCaseReport.Remove("cueId");
        wrongCaseReport["CueId"] = null;
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewAudioService.DeserializeResponse(
                System.Text.Encoding.UTF8.GetBytes(wrongCase.ToJsonString())));

        string validJson = baseline.ToJsonString();
        int requestIdProperty = validJson.IndexOf("\"requestId\"", StringComparison.Ordinal);
        Assert.True(requestIdProperty > 0);
        string duplicate = validJson.Insert(requestIdProperty, "\"schemaVersion\":1,");
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewAudioService.DeserializeResponse(
                System.Text.Encoding.UTF8.GetBytes(duplicate)));

        JsonObject wrongType = baseline.DeepClone().AsObject();
        wrongType["report"]!["cues"]![0]!["sessionResident"] = "true";
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewAudioService.DeserializeResponse(
                System.Text.Encoding.UTF8.GetBytes(wrongType.ToJsonString())));
    }

    [Fact]
    public void ResponseDeserializerRejectsAnUnpairedEscapedSurrogate()
    {
        string requestId = Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(
            ExactEnvelope(requestId, "MainTheme"),
            CamelCaseJson);
        string malformed = json.Replace(
            "MainTheme",
            "\\uD800",
            StringComparison.Ordinal);

        Exception? exception = Record.Exception(() =>
            ProjectReviewAudioService.DeserializeResponse(
                System.Text.Encoding.UTF8.GetBytes(malformed)));

        Assert.NotNull(exception);
        Assert.True(exception is JsonException or InvalidDataException, exception.ToString());
    }

    [Fact]
    public void ResponseFilePublishesOnlyItsOwnRegularCreateNewTarget()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        ReviewAudioResponseEnvelope envelope = ExactEnvelope(requestId, "MainTheme");

        ReviewAudioReport report = ReviewAudioResponseFile.Write(temporary.Path, envelope);

        string responsePath = ReviewAudioContract.ResponsePath(temporary.Path, requestId);
        Assert.Equal("ready", report.State);
        Assert.True(File.Exists(responsePath));
        FileAttributes attributes = File.GetAttributes(responsePath);
        Assert.False(
            (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0);
        Assert.False(File.Exists(responsePath + ".tmp"));
        Assert.NotNull(ProjectReviewAudioService.DeserializeResponse(File.ReadAllBytes(responsePath)));
    }

    [Fact]
    public void ResponseFileNeverDeletesAPreexistingForeignTemporaryEntry()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewAudioContract.ResponsePath(temporary.Path, requestId);
        string temporaryPath = responsePath + ".tmp";
        File.WriteAllText(temporaryPath, "foreign");

        Assert.Throws<InvalidDataException>(() => ReviewAudioResponseFile.Write(
            temporary.Path,
            ExactEnvelope(requestId, "MainTheme")));

        Assert.Equal("foreign", File.ReadAllText(temporaryPath));
        Assert.False(File.Exists(responsePath));
    }

    [Fact]
    public void ResponseFileNeverDeletesAPreexistingForeignTemporaryDirectory()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewAudioContract.ResponsePath(temporary.Path, requestId);
        string temporaryPath = responsePath + ".tmp";
        Directory.CreateDirectory(temporaryPath);

        Assert.Throws<InvalidDataException>(() => ReviewAudioResponseFile.Write(
            temporary.Path,
            ExactEnvelope(requestId, "MainTheme")));

        Assert.True(Directory.Exists(temporaryPath));
        Assert.False(File.Exists(responsePath));
    }

    [Fact]
    public void FatalAudioFailuresAreNeverConvertedIntoBlockedReports()
    {
        var fatal = new TargetInvocationException(
            Assert.IsType<OutOfMemoryException>(Activator.CreateInstance(
                typeof(OutOfMemoryException),
                "Synthetic fatal.")));

        Assert.True(ReviewAudioException.IsFatal(fatal));
        Assert.False(ReviewAudioException.IsFatal(
            new TargetInvocationException(new InvalidDataException("Synthetic controlled."))));
    }

    [Fact]
    public void GameAdapterDefersGameAccessAndUsesOnlyTheExactMetadataProbe()
    {
        string source = ReadAlwaysOnSource();
        int constructorStart = source.IndexOf(
            "public StardewReviewAudioSource(",
            StringComparison.Ordinal);
        int gameVersionStart = source.IndexOf(
            "public string GameVersion",
            constructorStart,
            StringComparison.Ordinal);

        Assert.True(constructorStart >= 0);
        Assert.True(gameVersionStart > constructorStart);
        string constructor = source[constructorStart..gameVersionStart];
        Assert.DoesNotContain("Game1", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("GameContent", constructor, StringComparison.Ordinal);
        Assert.Contains("pair.Value?.Id!", source, StringComparison.Ordinal);
        Assert.Contains("soundBank.Exists(cueId)", source, StringComparison.Ordinal);
        Assert.Contains("soundBank.GetCueDefinition(cueId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("soundBank.GetCue(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("soundBank.Play", source, StringComparison.Ordinal);
        Assert.Contains(
            "new Dictionary<string, string?>(\n            StringComparer.OrdinalIgnoreCase)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "playableCueIds\n                .Where",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "cueIds.Add(effectiveCueId)\n                && cueIds.Count > ReviewAudioContract.MaximumDiscoverableCueIds",
            source,
            StringComparison.Ordinal);
        int audioLoadStart = source.IndexOf(
            "public IReadOnlyList<ReviewAudioChangeDefinition> LoadAudioChanges()",
            StringComparison.Ordinal);
        int jukeboxLoadStart = source.IndexOf(
            "public IReadOnlyList<ReviewAudioJukeboxDefinition> LoadJukeboxTracks()",
            audioLoadStart,
            StringComparison.Ordinal);
        int soundBankStatusStart = source.IndexOf(
            "public ReviewAudioSoundBankStatus GetSoundBankStatus()",
            jukeboxLoadStart,
            StringComparison.Ordinal);
        Assert.True(audioLoadStart >= 0);
        Assert.True(jukeboxLoadStart > audioLoadStart);
        Assert.True(soundBankStatusStart > jukeboxLoadStart);
        string audioLoad = source[audioLoadStart..jukeboxLoadStart];
        string jukeboxLoad = source[jukeboxLoadStart..soundBankStatusStart];
        Assert.True(
            audioLoad.IndexOf(
                "values.Count > ReviewAudioContract.MaximumAudioChangeEntries",
                StringComparison.Ordinal)
            < audioLoad.IndexOf(
                "new List<ReviewAudioChangeDefinition>(values.Count)",
                StringComparison.Ordinal));
        Assert.True(
            jukeboxLoad.IndexOf(
                "values.Count > ReviewAudioContract.MaximumJukeboxTrackEntries",
                StringComparison.Ordinal)
            < jukeboxLoad.IndexOf(
                "new List<ReviewAudioJukeboxDefinition>(values.Count)",
                StringComparison.Ordinal));
        Assert.True(
            jukeboxLoad.IndexOf(
                "alternatives.Count > ReviewAudioContract.MaximumAlternativesPerTrack",
                StringComparison.Ordinal)
            < jukeboxLoad.IndexOf(
                "alternatives?.Select(value => (string?)value).ToArray()",
                StringComparison.Ordinal));
        Assert.Equal(
            2,
            source.Split(
                "when (!ReviewAudioException.IsFatal(exception))",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void PublicDocumentationUsesTheCanonicalKnownCueExample()
    {
        string reference = ReadRepositoryFile("docs", "inspection.md");

        Assert.Contains(
            "project review audio cue \"maintheme\"",
            reference,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "project review audio cue \"MainTheme\"",
            reference,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExactResponseBindingRejectsMismatchedAndNullGraphs()
    {
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewAudioQuery(
            ReviewAudioContract.CueOperation,
            "MainTheme",
            0,
            1);
        ReviewAudioResponseEnvelope valid = ExactEnvelope(requestId, "MainTheme");

        Assert.True(ProjectReviewAudioService.MatchesResponse(valid, requestId, query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(null, requestId, query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(valid, requestId, null));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid,
            requestId,
            query with { CueId = "\ud800" }));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with { Report = null! },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with { RequestId = Guid.NewGuid().ToString("N") },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Operation = ReviewAudioContract.CuesOperation,
                },
            },
            requestId,
            query));

        ReviewAudioCueReport dataCue = DataCue("MainTheme");
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Cues = [dataCue],
                },
            },
            requestId,
            query));

        ReviewAudioCueReport alternativeCue = valid.Report.Cues![0] with
        {
            Sources = [ReviewAudioContract.JukeboxAlternativeSource],
            JukeboxReferences =
            [
                new ReviewAudioJukeboxReference(
                    "Track",
                    ReviewAudioContract.AlternativeJukeboxRelation),
            ],
        };
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Cues = [alternativeCue],
                    Coverage = valid.Report.Coverage! with
                    {
                        AudioChangeEntries = 1,
                        DiscoverableCueIds = 1,
                    },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with { Report = valid.Report with { CueId = "Other" } },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Cues = [valid.Report.Cues![0] with { CueId = "Other" }],
                },
            },
            requestId,
            query));

        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with { Report = valid.Report with { Cues = null } },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with { Report = valid.Report with { Coverage = null } },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with { Report = valid.Report with { Problems = null! } },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with { Report = valid.Report with { Cues = [null!] } },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Cues = [valid.Report.Cues![0] with { Sources = null! }],
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Cues = [valid.Report.Cues![0] with { JukeboxReferences = null! }],
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Cues =
                    [
                        valid.Report.Cues![0] with
                        {
                            JukeboxReferences = [null!],
                        },
                    ],
                },
            },
            requestId,
            query));
    }

    [Fact]
    public void InventoryResponseBindingRequiresTheExactPageAndStableCueOrder()
    {
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewAudioQuery(
            ReviewAudioContract.CuesOperation,
            null,
            1,
            2);
        ReviewAudioResponseEnvelope valid = InventoryEnvelope(requestId);

        Assert.True(ProjectReviewAudioService.MatchesResponse(valid, requestId, query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Page = valid.Report.Page! with { Offset = 0 },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Page = valid.Report.Page! with { NextOffset = 2 },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Cues = valid.Report.Cues!.Reverse().ToArray(),
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    Coverage = valid.Report.Coverage! with
                    {
                        AudioChangeEntries = 1,
                        JukeboxTrackEntries = 2,
                    },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewAudioService.MatchesResponse(
            valid with
            {
                Report = valid.Report with
                {
                    State = "blocked",
                    CueId = "unexpected",
                    Cues = null,
                    Page = null,
                    Problems =
                    [
                        new ReviewAudioProblem("synthetic", "Synthetic failure."),
                    ],
                },
            },
            requestId,
            query));
    }

    [Fact]
    public void OversizedUtf8ResponsePublishesASmallExplicitBlockedEnvelope()
    {
        string requestId = Guid.NewGuid().ToString("N");
        ReviewAudioJukeboxReference[] references = Enumerable
            .Range(0, ReviewAudioContract.MaximumJukeboxTrackEntries)
            .Select(index => new ReviewAudioJukeboxReference(
                $"Track{index:D4}{new string('x', 240)}",
                ReviewAudioContract.AlternativeJukeboxRelation))
            .ToArray();
        var cue = new ReviewAudioCueReport(
            "Target",
            [ReviewAudioContract.JukeboxAlternativeSource],
            false,
            false,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            references);
        var envelope = new ReviewAudioResponseEnvelope(
            ReviewAudioContract.SchemaVersion,
            requestId,
            new ReviewAudioReport(
                ReviewAudioContract.SchemaVersion,
                "ready",
                ReviewAudioContract.CueOperation,
                "1.6.15",
                "1.6.15.24356",
                cue.CueId,
                [cue],
                null,
                new ReviewAudioCoverageReport(
                    0,
                    ReviewAudioContract.MaximumJukeboxTrackEntries,
                    ReviewAudioContract.MaximumJukeboxTrackEntries,
                    ReviewAudioContract.MaximumJukeboxTrackEntries + 1,
                    1,
                    0,
                    1,
                    0,
                    true,
                    null,
                    ReviewAudioContract.BuiltInInventoryStatus),
                []));

        byte[] bytes = ReviewAudioResponseSerializer.SerializeBounded(envelope);

        Assert.InRange(bytes.Length, 1, ReviewAudioContract.MaximumResponseBytes);
        ReviewAudioResponseEnvelope published = Assert.IsType<ReviewAudioResponseEnvelope>(
            JsonSerializer.Deserialize<ReviewAudioResponseEnvelope>(
                bytes,
                CaseInsensitiveJson));
        Assert.Equal(requestId, published.RequestId);
        Assert.Equal("blocked", published.Report.State);
        Assert.Equal(
            "audioResponseTooLarge",
            Assert.Single(published.Report.Problems).Code);
        Assert.Null(published.Report.Cues);
        Assert.Null(published.Report.Page);
        Assert.True(ProjectReviewAudioService.MatchesResponse(
            published,
            requestId,
            new ReviewAudioQuery(
                ReviewAudioContract.CueOperation,
                "Target",
                0,
                1)));
    }

    private static ReviewAudioResponseEnvelope ExactEnvelope(
        string requestId,
        string cueId)
    {
        var cue = new ReviewAudioCueReport(
            cueId,
            [],
            false,
            true,
            true,
            3,
            null,
            null,
            null,
            null,
            null,
            []);
        return new ReviewAudioResponseEnvelope(
            ReviewAudioContract.SchemaVersion,
            requestId,
            new ReviewAudioReport(
                ReviewAudioContract.SchemaVersion,
                "ready",
                ReviewAudioContract.CueOperation,
                "1.6.15",
                "1.6.15.24356",
                cueId,
                [cue],
                null,
                new ReviewAudioCoverageReport(
                    0,
                    0,
                    0,
                    0,
                    1,
                    1,
                    0,
                    0,
                    true,
                    null,
                    ReviewAudioContract.BuiltInInventoryStatus),
                []));
    }

    private static ReviewAudioResponseEnvelope InventoryEnvelope(string requestId)
    {
        ReviewAudioCueReport[] cues = [DataCue("B"), DataCue("C")];
        return new ReviewAudioResponseEnvelope(
            ReviewAudioContract.SchemaVersion,
            requestId,
            new ReviewAudioReport(
                ReviewAudioContract.SchemaVersion,
                "ready",
                ReviewAudioContract.CuesOperation,
                "1.6.15",
                "1.6.15.24356",
                null,
                cues,
                new ReviewAudioPage(1, 2, 2, 3, null),
                new ReviewAudioCoverageReport(
                    3,
                    0,
                    0,
                    3,
                    2,
                    2,
                    0,
                    0,
                    true,
                    null,
                    ReviewAudioContract.BuiltInInventoryStatus),
                []));
    }

    private static ReviewAudioCueReport DataCue(string cueId) =>
        new(
            cueId,
            [ReviewAudioContract.AudioChangesSource],
            true,
            true,
            true,
            1,
            1,
            "Sound",
            false,
            false,
            false,
            []);

    private static ReviewAudioChangeDefinition Change(
        string cueId,
        int? variants = 1,
        string? category = "Sound",
        bool streamed = false,
        bool looped = false,
        bool reverb = false,
        string? modificationKey = null) =>
        new(
            modificationKey ?? cueId,
            cueId,
            variants,
            category,
            streamed,
            looped,
            reverb);

    private static ReviewAudioCueProbe Probe(
        string cueId,
        bool exists,
        int? variants = null) =>
        new(
            cueId,
            exists,
            exists && variants is not null,
            exists && variants is not null ? cueId : null,
            exists ? variants : null);

    private static ReviewAudioReport Execute(
        IReviewAudioSource source,
        string operation,
        string? cueId = null,
        int offset = 0,
        int limit = 50) =>
        ReviewAudioOperation.Execute(
            new ReviewAudioQuery(operation, cueId, offset, limit),
            source);

    private static string ReadAlwaysOnSource() =>
        ReadRepositoryFile(
            "src",
            "SdvKit.AlwaysOn",
            "ReviewAudioCommand.cs");

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                [directory.FullName, .. relativeSegments]);
            if (File.Exists(path))
            {
                return File.ReadAllText(path).ReplaceLineEndings("\n");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }

    private sealed class FakeReviewAudioSource(
        IReadOnlyList<ReviewAudioChangeDefinition>? changes = null,
        IReadOnlyList<ReviewAudioJukeboxDefinition>? tracks = null,
        IReadOnlyDictionary<string, ReviewAudioCueProbe>? probes = null,
        ReviewAudioSoundBankStatus soundBankStatus = ReviewAudioSoundBankStatus.Ready)
        : IReviewAudioSource
    {
        private readonly IReadOnlyList<ReviewAudioChangeDefinition> _changes =
            changes ?? [];
        private readonly IReadOnlyList<ReviewAudioJukeboxDefinition> _tracks =
            tracks ?? [];
        private readonly IReadOnlyDictionary<string, ReviewAudioCueProbe> _probes =
            probes ?? new Dictionary<string, ReviewAudioCueProbe>();

        public string GameVersion => "1.6.15";

        public string GameFileVersion => "1.6.15.24356";

        public List<string> ProbedCueIds { get; } = [];

        public IReadOnlyList<ReviewAudioChangeDefinition> LoadAudioChanges() =>
            _changes;

        public IReadOnlyList<ReviewAudioJukeboxDefinition> LoadJukeboxTracks() =>
            _tracks;

        public ReviewAudioSoundBankStatus GetSoundBankStatus() => soundBankStatus;

        public ReviewAudioCueProbe ProbeCue(string cueId)
        {
            ProbedCueIds.Add(cueId);
            return _probes.TryGetValue(cueId, out ReviewAudioCueProbe? probe)
                ? probe
                : Probe(cueId, exists: false);
        }
    }
}
