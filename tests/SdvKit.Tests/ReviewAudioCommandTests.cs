using System.Text.Json;
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingOrEmptyCategoryUsesTheGameDefault(string? category)
    {
        var source = new FakeReviewAudioSource(
            changes:
            [
                Change("Mod.DefaultCategory", category: category),
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
    public void CaseCollisionsBlockInventoryAndAmbiguousLookupButExactLookupWorks()
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
        Assert.Equal(
            "audioCueIdentityCollision",
            Assert.Single(inventory.Problems).Code);
        Assert.Equal(1, inventory.Coverage!.IdentityCollisionGroups);

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

    [Theory]
    [InlineData("mismatchedId")]
    [InlineData("unsafeCategory")]
    [InlineData("duplicateAlternative")]
    [InlineData("nullJukebox")]
    public void MalformedDataDrivenEntriesFailClosed(string kind)
    {
        IReadOnlyList<ReviewAudioChangeDefinition> changes = kind switch
        {
            "mismatchedId" =>
            [
                new ReviewAudioChangeDefinition(
                    "Cue",
                    "Other",
                    1,
                    "Sound",
                    false,
                    false,
                    false),
            ],
            "unsafeCategory" =>
            [
                Change("Cue", category: "Sound\nunsafe"),
            ],
            _ => [],
        };
        IReadOnlyList<ReviewAudioJukeboxDefinition> tracks = kind switch
        {
            "duplicateAlternative" =>
            [
                new ReviewAudioJukeboxDefinition("Cue", ["Old", "old"]),
            ],
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
        bool reverb = false) =>
        new(
            cueId,
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
