using System.Reflection;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewModAssetCommandTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CatalogueTracksOnlyConventionalNamespacesAndLifecycle()
    {
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);

        catalogue.ObserveRequested("Data/Objects", typeof(string));
        catalogue.ObserveRequested("Mods/Example.Mod/Words", typeof(Dictionary<string, string>));
        catalogue.ObserveReady("Mods/Example.Mod/Words");
        catalogue.ObserveInvalidated(["Mods/Example.Mod/Words"]);

        ReviewModAssetInventorySnapshot snapshot = catalogue.Snapshot();
        ReviewModAssetObservation asset = Assert.Single(snapshot.Assets);
        Assert.Equal(StartedAt, snapshot.ObservationStartedAtUtc);
        Assert.Equal("Mods/Example.Mod/Words", asset.AssetName);
        Assert.Equal("Example.Mod", asset.NamespaceOwnerId);
        Assert.Equal("resolved", asset.NamespaceOwnerStatus);
        Assert.Equal("invalidated", asset.Lifecycle);
        Assert.Equal(1, asset.Generation);
        Assert.Equal(1, asset.RequestCount);
        Assert.Equal(1, asset.ReadyCount);
        Assert.False(asset.Available);
    }

    [Fact]
    public void ReadyStateAndCountRemainStableUntilTheGenerationChanges()
    {
        const string name = "Mods/Example.Mod/Words";
        Type type = typeof(Dictionary<string, string>);
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);

        catalogue.ObserveRequested(name, type);
        catalogue.ObserveReady(name);
        catalogue.ObserveRequested(name, type);
        catalogue.ObserveReady(name);
        catalogue.MarkVerifiedReady(name, type);

        ReviewModAssetObservation firstGeneration = Assert.Single(catalogue.Snapshot().Assets);
        Assert.Equal("ready", firstGeneration.Lifecycle);
        Assert.True(firstGeneration.Available);
        Assert.Equal(0, firstGeneration.Generation);
        Assert.Equal(2, firstGeneration.RequestCount);
        Assert.Equal(1, firstGeneration.ReadyCount);

        catalogue.ObserveInvalidated([name]);
        catalogue.ObserveRequested(name, type);
        catalogue.ObserveReady(name);
        catalogue.MarkVerifiedReady(name, type);

        ReviewModAssetObservation secondGeneration = Assert.Single(catalogue.Snapshot().Assets);
        Assert.Equal("ready", secondGeneration.Lifecycle);
        Assert.True(secondGeneration.Available);
        Assert.Equal(1, secondGeneration.Generation);
        Assert.Equal(3, secondGeneration.RequestCount);
        Assert.Equal(2, secondGeneration.ReadyCount);
    }

    [Fact]
    public void VerifiedQueryGuardSuppressesOnlyItsOwnAssetSignals()
    {
        const string name = "Mods/Example.Mod/Words";
        Type type = typeof(Dictionary<string, string>);
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        var guard = new ReviewModAssetQueryObservationGuard();
        catalogue.ObserveRequested(name, type);
        catalogue.ObserveReady(name);

        using (guard.Enter(name, type))
        {
            Assert.True(guard.SuppressesRequested(name, type));
            Assert.True(guard.SuppressesReady(name.ToUpperInvariant()));
            Assert.False(guard.SuppressesRequested(name, typeof(string)));
            Assert.False(guard.SuppressesReady("Mods/Example.Mod/Other"));

            if (!guard.SuppressesRequested(name, type))
            {
                catalogue.ObserveRequested(name, type);
            }
            if (!guard.SuppressesReady(name))
            {
                catalogue.ObserveReady(name);
            }
        }
        catalogue.MarkVerifiedReady(name, type);

        ReviewModAssetObservation observed = Assert.Single(catalogue.Snapshot().Assets);
        Assert.Equal("ready", observed.Lifecycle);
        Assert.True(observed.Available);
        Assert.Equal(1, observed.RequestCount);
        Assert.Equal(1, observed.ReadyCount);
        Assert.False(guard.SuppressesRequested(name, type));
        Assert.False(guard.SuppressesReady(name));
    }

    [Fact]
    public void TypeChangingReplacementKeepsTheNameGenerationAndReadySignalAmbiguous()
    {
        const string name = "Mods/Example.Mod/Words";
        Type oldType = typeof(string);
        Type replacementType = typeof(Dictionary<string, string>);
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(name, oldType);
        catalogue.ObserveReady(name);
        catalogue.ObserveInvalidated([name]);

        catalogue.ObserveRequested(name, replacementType);
        catalogue.ObserveReady(name);

        ReviewModAssetObservation[] assets = catalogue.Snapshot().Assets.ToArray();
        Assert.Equal(2, assets.Length);
        Assert.All(assets, asset =>
        {
            Assert.Equal(1, asset.Generation);
            Assert.True(asset.TypeCollision);
            Assert.False(asset.Available);
        });
        Assert.Equal(
            "invalidated",
            Assert.Single(assets, asset => asset.DataType == oldType).Lifecycle);
        Assert.Equal(
            "requested",
            Assert.Single(assets, asset => asset.DataType == replacementType).Lifecycle);
    }

    [Fact]
    public void OversizedObservedModAssetCreatesAnExplicitCoverageGap()
    {
        string name = "Mods/Example.Mod/" + new string(
            'x',
            ReviewModAssetContract.MaximumAssetLength);
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);

        catalogue.ObserveRequested(name, typeof(string));

        ReviewModAssetInventorySnapshot snapshot = catalogue.Snapshot();
        Assert.Empty(snapshot.Assets);
        Assert.Equal(1, snapshot.Observed);
        Assert.Equal(1, snapshot.Dropped);

        ReviewModAssetReport report = Execute(
            new FakeSource(catalogue),
            ReviewModAssetContract.AssetsOperation);
        Assert.Equal("blocked", report.State);
        Assert.False(report.Coverage!.Complete);
        Assert.Equal("modAssetCoverageIncomplete", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void CatalogueKeepsUnknownOwnerWithoutInventingProvider()
    {
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested("Mods/Other.Mod/Words", typeof(string));

        ReviewModAssetObservation asset = Assert.Single(catalogue.Snapshot().Assets);

        Assert.Null(asset.NamespaceOwnerId);
        Assert.Equal("unknown", asset.NamespaceOwnerStatus);
    }

    [Fact]
    public void CatalogueConsolidatesCaseAndSlashEquivalentSmapiIdentities()
    {
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);

        catalogue.ObserveRequested(
            "mods\\example.mod\\Words",
            typeof(Dictionary<string, string>));
        catalogue.ObserveReady("MODS/EXAMPLE.MOD/WORDS");
        catalogue.ObserveRequested(
            "Mods/Example.Mod/words",
            typeof(Dictionary<string, string>));

        ReviewModAssetInventorySnapshot snapshot = catalogue.Snapshot();
        ReviewModAssetObservation asset = Assert.Single(snapshot.Assets);
        Assert.Equal("Mods/example.mod/Words", asset.AssetName);
        Assert.Equal("Example.Mod", asset.NamespaceOwnerId);
        Assert.Equal(2, asset.RequestCount);
        Assert.Equal(1, asset.ReadyCount);
        Assert.False(asset.NameCollision);
        Assert.False(asset.TypeCollision);
    }

    [Fact]
    public void StableLookupCannotFlattenAnOwnerOrPathBoundary()
    {
        const string name = "Mods/Example-Mod/Words";
        var catalogue = new ReviewModAssetCatalog(["Example-Mod"], StartedAt);
        catalogue.ObserveRequested(name, typeof(string));
        catalogue.ObserveReady(name);
        var source = new FakeSource(catalogue);
        source.Values[(name, typeof(string))] = "one";

        ReviewModAssetReport flattened = Execute(
            source,
            ReviewModAssetContract.GetOperation,
            "Mods/Example/Mod/Words",
            ReviewModAssetContract.SingletonKey);
        ReviewModAssetReport traversal = Execute(
            source,
            ReviewModAssetContract.GetOperation,
            "Mods/Example-Mod/../Words",
            ReviewModAssetContract.SingletonKey);

        Assert.Equal("modAssetUnknown", Assert.Single(flattened.Problems).Code);
        Assert.Equal("modAssetNameInvalid", Assert.Single(traversal.Problems).Code);
        Assert.Equal(0, source.LoadCount);
    }

    [Fact]
    public void AssetsDistinguishDiscoveryFromAdapterSupport()
    {
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested("Mods/Example.Mod/Words", typeof(Dictionary<string, string>));
        catalogue.ObserveRequested("Mods/Example.Mod/Unknown", typeof(UnknownAsset));
        catalogue.ObserveReady("Mods/Example.Mod/Words");
        catalogue.ObserveReady("Mods/Example.Mod/Unknown");
        var source = new FakeSource(catalogue);

        ReviewModAssetReport report = Execute(
            source,
            ReviewModAssetContract.AssetsOperation);

        Assert.Equal("ready", report.State);
        ReviewModAssetCoverageReport coverage = Assert.IsType<ReviewModAssetCoverageReport>(
            report.Coverage);
        Assert.True(coverage.Complete);
        Assert.Equal(2, coverage.Catalogued);
        Assert.Equal(1, coverage.AdapterSupported);
        Assert.Equal(1, coverage.AdapterUnavailable);
        ReviewModAssetAssetReport unknown = Assert.Single(
            report.Assets!,
            asset => asset.AssetName.EndsWith("/Unknown", StringComparison.Ordinal));
        Assert.False(unknown.AdapterSupported);
        Assert.Equal("modAssetAdapterUnavailable", unknown.ProblemCode);
        Assert.Null(unknown.ProviderModId);
        Assert.Equal("unavailableThroughPublicSmapiApi", unknown.ProviderStatus);
    }

    [Theory]
    [MemberData(nameof(KnownShapes))]
    public void ReviewedAdaptersReturnDeterministicPrimitiveRecords(
        Type dataType,
        object value,
        string[] expectedKeys,
        string selectedKey,
        string expectedJson)
    {
        const string name = "Mods/Example.Mod/Value";
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(name, dataType);
        catalogue.ObserveReady(name);
        var source = new FakeSource(catalogue);
        source.Values[(name, dataType)] = value;

        ReviewModAssetReport keys = Execute(
            source,
            ReviewModAssetContract.KeysOperation,
            name);
        ReviewModAssetReport selected = Execute(
            source,
            ReviewModAssetContract.GetOperation,
            name,
            selectedKey);

        Assert.Equal(expectedKeys, keys.Keys);
        Assert.Equal(expectedJson, selected.Record!.Value.GetRawText());
    }

    [Fact]
    public void UnknownTypeIsCataloguedButNeverLoaded()
    {
        const string name = "Mods/Example.Mod/Unknown";
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(name, typeof(UnknownAsset));
        catalogue.ObserveReady(name);
        var source = new FakeSource(catalogue);

        ReviewModAssetReport report = Execute(
            source,
            ReviewModAssetContract.KeysOperation,
            name);

        Assert.Equal("blocked", report.State);
        Assert.Equal("modAssetAdapterUnavailable", Assert.Single(report.Problems).Code);
        Assert.Equal(0, source.LoadCount);
    }

    [Fact]
    public void NameAndTypeCollisionsAreVisibleAndExactReadsFailClosed()
    {
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested("Mods/Example.Mod/Foo-Bar", typeof(string));
        catalogue.ObserveRequested("Mods/Example.Mod/Foo_Bar", typeof(string));
        catalogue.ObserveRequested("Mods/Example.Mod/Typed", typeof(string));
        catalogue.ObserveRequested(
            "Mods/Example.Mod/Typed",
            typeof(Dictionary<string, string>));
        var source = new FakeSource(catalogue);

        ReviewModAssetReport inventory = Execute(
            source,
            ReviewModAssetContract.AssetsOperation);
        ReviewModAssetReport nameRead = Execute(
            source,
            ReviewModAssetContract.KeysOperation,
            "Mods/Example.Mod/Foo-Bar");
        ReviewModAssetReport typeRead = Execute(
            source,
            ReviewModAssetContract.KeysOperation,
            "Mods/Example.Mod/Typed");

        Assert.Equal(2, inventory.Coverage!.NameCollisions);
        Assert.Equal(2, inventory.Coverage.TypeCollisions);
        Assert.Equal("modAssetNameAmbiguous", Assert.Single(nameRead.Problems).Code);
        Assert.Equal("modAssetTypeAmbiguous", Assert.Single(typeRead.Problems).Code);
    }

    [Fact]
    public void InvalidatedAssetCanBecomeReadyInANewGeneration()
    {
        const string name = "Mods/Example.Mod/Words";
        Type type = typeof(Dictionary<string, string>);
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(name, type);
        catalogue.ObserveReady(name);
        catalogue.ObserveInvalidated([name]);
        var source = new FakeSource(catalogue);
        source.Values[(name, type)] = new Dictionary<string, string> { ["Version"] = "two" };

        ReviewModAssetReport report = Execute(
            source,
            ReviewModAssetContract.GetOperation,
            name,
            "Version");

        Assert.Equal("ready", report.State);
        Assert.Equal(1, report.Asset!.Generation);
        Assert.Equal("two", report.Record!.Value.GetString());
        Assert.Equal("ready", Assert.Single(catalogue.Snapshot().Assets).Lifecycle);
    }

    [Fact]
    public void FailedReloadKeepsRemovedAssetCataloguedAsUnavailable()
    {
        const string name = "Mods/Example.Mod/Words";
        Type type = typeof(Dictionary<string, string>);
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(name, type);
        catalogue.ObserveReady(name);
        catalogue.ObserveInvalidated([name]);
        var source = new FakeSource(catalogue);
        source.Failures[(name, type)] = "modAssetUnavailable";

        ReviewModAssetReport report = Execute(
            source,
            ReviewModAssetContract.KeysOperation,
            name);

        Assert.Equal("blocked", report.State);
        Assert.Equal("modAssetUnavailable", Assert.Single(report.Problems).Code);
        ReviewModAssetObservation observed = Assert.Single(catalogue.Snapshot().Assets);
        Assert.Equal("unavailable", observed.Lifecycle);
        Assert.False(observed.Available);
    }

    [Fact]
    public void CatalogueLimitProducesAnExplicitCoverageGap()
    {
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        for (var index = 0; index <= ReviewModAssetContract.MaximumObservedAssets; index++)
        {
            catalogue.ObserveRequested($"Mods/Example.Mod/Asset{index}", typeof(string));
        }

        ReviewModAssetReport report = Execute(
            new FakeSource(catalogue),
            ReviewModAssetContract.AssetsOperation);

        Assert.Equal("blocked", report.State);
        Assert.Equal(1, report.Coverage!.Dropped);
        Assert.False(report.Coverage.Complete);
        Assert.Equal("modAssetCoverageIncomplete", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void OversizedOrInvalidStringsFailSafeAdaptation()
    {
        Assert.True(ReviewModAssetAdapterRegistry.TryGet(
            typeof(string),
            out ReviewModAssetAdapterKind adapter,
            out _,
            out _));
        string oversized = new('x', ReviewModAssetContract.MaximumStringValueLength + 1);
        string invalidUtf16 = new([char.ConvertFromUtf32(0x1F600)[0]]);

        Assert.False(ReviewModAssetAdapterRegistry.TryAdapt(
            adapter,
            oversized,
            out _,
            out string? oversizedProblem));
        Assert.False(ReviewModAssetAdapterRegistry.TryAdapt(
            adapter,
            invalidUtf16,
            out _,
            out string? invalidProblem));
        Assert.Equal("modAssetRecordNotSafelySerializable", oversizedProblem);
        Assert.Equal("modAssetRecordNotSafelySerializable", invalidProblem);
    }

    [Fact]
    public void AggregateAdaptedPayloadFailsBeforeResponseSerialization()
    {
        Assert.True(ReviewModAssetAdapterRegistry.TryGet(
            typeof(Dictionary<string, string>),
            out ReviewModAssetAdapterKind adapter,
            out _,
            out _));
        var values = Enumerable.Range(0, 65)
            .ToDictionary(
                index => $"Key{index:D2}",
                _ => new string('x', ReviewModAssetContract.MaximumStringValueLength),
                StringComparer.Ordinal);

        Assert.False(ReviewModAssetAdapterRegistry.TryAdapt(
            adapter,
            values,
            out IReadOnlyList<ReviewModAssetRecord> records,
            out string? problem));
        Assert.Empty(records);
        Assert.Equal("modAssetAdaptedPayloadTooLarge", problem);
    }

    [Fact]
    public void TypeChangeAndInvalidRequestFailClosed()
    {
        const string name = "Mods/Example.Mod/Words";
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(name, typeof(string));
        var source = new FakeSource(catalogue);
        source.Values[(name, typeof(string))] = new List<string> { "changed" };

        ReviewModAssetReport changed = Execute(
            source,
            ReviewModAssetContract.GetOperation,
            name,
            ReviewModAssetContract.SingletonKey);
        ReviewModAssetReport invalid = ReviewModAssetOperation.Execute(
            new ReviewModAssetQuery(
                ReviewModAssetContract.AssetsOperation,
                name,
                null,
                0,
                1),
            source);

        Assert.Equal("modAssetTypeChanged", Assert.Single(changed.Problems).Code);
        Assert.Equal("modAssetRequestInvalid", Assert.Single(invalid.Problems).Code);
    }

    [Fact]
    public void TransportTokensRoundTripStrictUtf8()
    {
        const string value = "Mods/Example.Mod/Wörter";
        string token = ReviewTransportToken.Encode(value);

        Assert.True(ReviewTransportToken.TryDecode(
            token,
            ReviewModAssetContract.MaximumAssetLength,
            out string decoded));
        Assert.Equal(value, decoded);
        Assert.False(ReviewTransportToken.TryDecode(
            token + "=",
            ReviewModAssetContract.MaximumAssetLength,
            out _));
    }

    [Fact]
    public void MalformedObservedIdentityCreatesCoverageGapWithoutEchoingIt()
    {
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);

        catalogue.ObserveRequested("Mods/Example.Mod/\uD800", typeof(string));

        ReviewModAssetInventorySnapshot snapshot = catalogue.Snapshot();
        Assert.Empty(snapshot.Assets);
        Assert.Equal(1, snapshot.Observed);
        Assert.Equal(1, snapshot.Dropped);
    }

    [Fact]
    public void RegistryReaderFailsClosedWhenEnumerationFails()
    {
        Assert.Empty(ReviewModAssetRegistryReader.Read(FailingModIds));
    }

    [Fact]
    public void RegistryReaderDoesNotSwallowDirectOrWrappedFatalFailures()
    {
        var direct = Assert.IsType<OutOfMemoryException>(Activator.CreateInstance(
            typeof(OutOfMemoryException),
            "Synthetic fatal failure."));
        OutOfMemoryException directThrown = Assert.Throws<OutOfMemoryException>(() =>
            ReviewModAssetRegistryReader.Read(() => throw direct));

        var wrapped = new TargetInvocationException(
            Assert.IsType<OutOfMemoryException>(Activator.CreateInstance(
                typeof(OutOfMemoryException),
                "Synthetic wrapped fatal failure.")));
        TargetInvocationException wrappedThrown = Assert.Throws<TargetInvocationException>(() =>
            ReviewModAssetRegistryReader.Read(() => throw wrapped));

        Assert.Same(direct, directThrown);
        Assert.Same(wrapped, wrappedThrown);
    }

    [Fact]
    public void ResponseWriterNeverDeletesPreExistingTemporaryTarget()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewModAssetContract.ResponsePath(
            temporary.Path,
            requestId);
        string temporaryPath = responsePath + ".tmp";
        byte[] foreignBytes = [1, 2, 3, 4];
        File.WriteAllBytes(temporaryPath, foreignBytes);
        ReviewModAssetResponseEnvelope envelope = EmptyEnvelope(requestId);

        Assert.Throws<InvalidDataException>(() =>
            ReviewModAssetResponseWriter.Write(temporary.Path, envelope));

        Assert.Equal(foreignBytes, File.ReadAllBytes(temporaryPath));
        Assert.False(File.Exists(responsePath));
    }

    [Fact]
    public void ResponseWriterNeverRemovesPreExistingTemporaryDirectory()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewModAssetContract.ResponsePath(
            temporary.Path,
            requestId);
        string temporaryPath = responsePath + ".tmp";
        Directory.CreateDirectory(temporaryPath);
        string marker = Path.Combine(temporaryPath, "foreign.txt");
        File.WriteAllText(marker, "foreign");

        Assert.Throws<InvalidDataException>(() =>
            ReviewModAssetResponseWriter.Write(
                temporary.Path,
                EmptyEnvelope(requestId)));

        Assert.True(Directory.Exists(temporaryPath));
        Assert.Equal("foreign", File.ReadAllText(marker));
        Assert.False(File.Exists(responsePath));
    }

    [Fact]
    public void ResponseWriterPublishesOneCreateNewRegularResponse()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        string responsePath = ReviewModAssetContract.ResponsePath(
            temporary.Path,
            requestId);
        ReviewModAssetResponseEnvelope envelope = EmptyEnvelope(requestId);

        ReviewModAssetResponseWriter.Write(temporary.Path, envelope);

        Assert.True(File.Exists(responsePath));
        Assert.False(File.Exists(responsePath + ".tmp"));
        FileAttributes attributes = File.GetAttributes(responsePath);
        Assert.False(attributes.HasFlag(FileAttributes.Directory));
        Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Throws<InvalidDataException>(() =>
            ReviewModAssetResponseWriter.Write(temporary.Path, envelope));
    }

    public static TheoryData<Type, object, string[], string, string> KnownShapes =>
        new()
        {
            {
                typeof(Dictionary<string, string>),
                new Dictionary<string, string> { ["Beta"] = "two", ["Alpha"] = "one" },
                ["Alpha", "Beta"],
                "Beta",
                "\"two\""
            },
            {
                typeof(Dictionary<string, int>),
                new Dictionary<string, int> { ["Beta"] = 2, ["Alpha"] = 1 },
                ["Alpha", "Beta"],
                "Alpha",
                "1"
            },
            {
                typeof(Dictionary<int, string>),
                new Dictionary<int, string> { [2] = "two", [1] = "one" },
                ["1", "2"],
                "2",
                "\"two\""
            },
            {
                typeof(Dictionary<int, int>),
                new Dictionary<int, int> { [2] = 20, [1] = 10 },
                ["1", "2"],
                "1",
                "10"
            },
            {
                typeof(List<string>),
                new List<string> { "zero", "one" },
                ["0", "1"],
                "1",
                "\"one\""
            },
            {
                typeof(string),
                "single",
                [ReviewModAssetContract.SingletonKey],
                ReviewModAssetContract.SingletonKey,
                "\"single\""
            },
        };

    private static ReviewModAssetResponseEnvelope EmptyEnvelope(string requestId) =>
        new(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            new ReviewModAssetReport(
                ReviewModAssetContract.SchemaVersion,
                "blocked",
                ReviewModAssetContract.AssetsOperation,
                "1.6.15",
                "1.6.15.24356",
                ReviewModAssetContract.CoverageScope,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [new ReviewModAssetProblem("expected", "Expected.")]));

    private static IEnumerable<string> FailingModIds()
    {
        yield return "Example.Mod";
        throw new InvalidOperationException("Synthetic registry enumeration failure.");
    }

    private static ReviewModAssetReport Execute(
        IReviewModAssetSource source,
        string operation,
        string? asset = null,
        string? key = null,
        int offset = 0,
        int? limit = null) =>
        ReviewModAssetOperation.Execute(
            new ReviewModAssetQuery(
                operation,
                asset,
                key,
                offset,
                limit ?? (operation == ReviewModAssetContract.GetOperation ? 1 : 50)),
            source);

    private sealed class FakeSource(ReviewModAssetCatalog catalogue)
        : IReviewModAssetSource
    {
        public Dictionary<(string Asset, Type Type), object> Values { get; } = [];

        public Dictionary<(string Asset, Type Type), string> Failures { get; } = [];

        public int LoadCount { get; private set; }

        public string GameVersion => "1.6.15";

        public string GameFileVersion => "1.6.15.24356";

        public ReviewModAssetInventorySnapshot GetInventory() => catalogue.Snapshot();

        public ReviewModAssetLoadResult Load(ReviewModAssetObservation asset)
        {
            LoadCount++;
            var key = (asset.AssetName, asset.DataType);
            if (Failures.TryGetValue(key, out string? failure))
            {
                catalogue.MarkUnavailable(asset.AssetName, asset.DataType);
                return new ReviewModAssetLoadResult(false, null, failure);
            }

            if (!Values.TryGetValue(key, out object? value))
            {
                catalogue.MarkUnavailable(asset.AssetName, asset.DataType);
                return new ReviewModAssetLoadResult(false, null, "modAssetUnavailable");
            }

            catalogue.MarkVerifiedReady(asset.AssetName, asset.DataType);
            return new ReviewModAssetLoadResult(true, value, null);
        }
    }

    private sealed record UnknownAsset(string Value);
}
