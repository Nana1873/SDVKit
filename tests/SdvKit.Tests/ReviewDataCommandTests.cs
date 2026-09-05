using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewDataCommandTests
{
    [Fact]
    public void IndependentInventoryClassifiesEveryDictionaryListAndSingleton()
    {
        var source = new FakeReviewDataSource(new Dictionary<string, Func<object>>
        {
            ["Data/Dictionary"] = () => new Dictionary<string, ReviewDataSample>
            {
                ["internal"] = new("Localized", 1),
            },
            ["Data/List"] = () => new List<ReviewDataSample>
            {
                new("First", 2),
            },
            ["Data/Singleton"] = () => new ReviewDataSample("Only", 3),
        });

        ReviewDataReport report = Execute(
            source,
            ReviewDataContract.AssetsOperation,
            offset: 0,
            limit: 100);

        Assert.Equal("ready", report.State);
        Assert.Empty(report.Problems);
        ReviewDataCoverageReport coverage = Assert.IsType<ReviewDataCoverageReport>(
            report.Coverage);
        Assert.True(coverage.Complete);
        Assert.Equal(3, coverage.Discovered);
        Assert.Equal(3, coverage.Classified);
        Assert.Equal(3, coverage.Supported);
        Assert.Equal(0, coverage.Unknown);
        Assert.Equal(0, coverage.Unclassified);
        Assert.Equal(0, coverage.Unsupported);
        ReviewDataAssetReport[] assets = Assert.IsAssignableFrom<
            IReadOnlyList<ReviewDataAssetReport>>(report.Assets).ToArray();
        Assert.Collection(
            assets,
            asset =>
            {
                Assert.Equal("Data/Dictionary", asset.AssetName);
                Assert.Equal("dictionary", asset.Shape);
                Assert.Equal("string", asset.KeyKind);
            },
            asset =>
            {
                Assert.Equal("Data/List", asset.AssetName);
                Assert.Equal("list", asset.Shape);
                Assert.Equal("index", asset.KeyKind);
            },
            asset =>
            {
                Assert.Equal("Data/Singleton", asset.AssetName);
                Assert.Equal("singleton", asset.Shape);
                Assert.Equal("singleton", asset.KeyKind);
            });
    }

    [Fact]
    public void StringKeysAreCanonicalSortedPaginatedAndNeverUseLocalizedValues()
    {
        var source = new FakeReviewDataSource(new Dictionary<string, Func<object>>
        {
            ["Data/Things"] = () => new Dictionary<string, ReviewDataSample>
            {
                ["internal-b"] = new("Localized B", 2),
                ["internal-a"] = new("Localized A", 1),
            },
        });

        ReviewDataReport keys = Execute(
            source,
            ReviewDataContract.KeysOperation,
            "data_things",
            offset: 1,
            limit: 1);

        Assert.Equal(["internal-b"], keys.Keys);
        Assert.Equal(
            new ReviewDataPage(1, 1, 1, 2, null),
            keys.Page);

        ReviewDataReport selected = Execute(
            source,
            ReviewDataContract.GetOperation,
            "DATA/THINGS",
            "INTERNAL_A");
        Assert.Equal("internal-a", selected.Key);
        Assert.Equal(
            "{\"DisplayName\":\"Localized A\",\"Value\":1}",
            selected.Record!.Value.GetRawText());

        ReviewDataReport localized = Execute(
            source,
            ReviewDataContract.GetOperation,
            "Data/Things",
            "Localized A");
        Assert.Equal("blocked", localized.State);
        Assert.Equal("dataKeyUnknown", Assert.Single(localized.Problems).Code);
    }

    [Fact]
    public void IntegerAndListKeysUseCanonicalNumericOrdering()
    {
        var source = new FakeReviewDataSource(new Dictionary<string, Func<object>>
        {
            ["Data/Numbers"] = () => new Dictionary<int, string>
            {
                [10] = "ten",
                [2] = "two",
            },
            ["Data/List"] = () => new List<string> { "zero", "one", "two" },
        });

        ReviewDataReport integerKeys = Execute(
            source,
            ReviewDataContract.KeysOperation,
            "Data/Numbers");
        Assert.Equal(["2", "10"], integerKeys.Keys);
        Assert.Equal("integer", integerKeys.KeyKind);

        ReviewDataReport nonCanonicalInteger = Execute(
            source,
            ReviewDataContract.GetOperation,
            "Data/Numbers",
            "02");
        Assert.Equal(
            "dataKeyUnknown",
            Assert.Single(nonCanonicalInteger.Problems).Code);

        ReviewDataReport listRecord = Execute(
            source,
            ReviewDataContract.GetOperation,
            "Data/List",
            "1");
        Assert.Equal("index", listRecord.KeyKind);
        Assert.Equal("1", listRecord.Key);
        Assert.Equal("\"one\"", listRecord.Record!.Value.GetRawText());
    }

    [Fact]
    public void SingletonUsesOneExplicitStableKey()
    {
        var source = new FakeReviewDataSource(new Dictionary<string, Func<object>>
        {
            ["Data/Only"] = () => new ReviewDataSample("Only", 7),
        });

        ReviewDataReport keys = Execute(
            source,
            ReviewDataContract.KeysOperation,
            "Data/Only");
        Assert.Equal([ReviewDataContract.SingletonKey], keys.Keys);

        ReviewDataReport record = Execute(
            source,
            ReviewDataContract.GetOperation,
            "Data/Only",
            "SINGLETON");
        Assert.Equal("singleton", record.Shape);
        Assert.Equal(ReviewDataContract.SingletonKey, record.Key);
        Assert.Equal(7, record.Record!.Value.GetProperty("Value").GetInt32());
    }

    [Fact]
    public void CanonicalJsonSortsNestedObjectPropertiesDeterministically()
    {
        var first = new Dictionary<string, object>
        {
            ["z"] = 2,
            ["a"] = new Dictionary<string, int>
            {
                ["right"] = 2,
                ["left"] = 1,
            },
        };
        var second = new Dictionary<string, object>
        {
            ["a"] = new Dictionary<string, int>
            {
                ["left"] = 1,
                ["right"] = 2,
            },
            ["z"] = 2,
        };

        Assert.True(ReviewDataJson.TrySerialize(first, out JsonElement left, out _));
        Assert.True(ReviewDataJson.TrySerialize(second, out JsonElement right, out _));
        Assert.Equal(left.GetRawText(), right.GetRawText());
        Assert.Equal(
            "{\"a\":{\"left\":1,\"right\":2},\"z\":2}",
            left.GetRawText());
    }

    [Fact]
    public void CanonicalJsonIncludesPublicGameDataStyleFields()
    {
        var source = new FakeReviewDataSource(new Dictionary<string, Func<object>>
        {
            ["Data/Fields"] = () => new Dictionary<string, ReviewDataFieldSample>
            {
                ["one"] = new() { InternalName = "Field value", Count = 4 },
            },
        });

        ReviewDataReport selected = Execute(
            source,
            ReviewDataContract.GetOperation,
            "Data/Fields",
            "one");

        Assert.Equal("ready", selected.State);
        Assert.Equal(
            "{\"Count\":4,\"InternalName\":\"Field value\"}",
            selected.Record!.Value.GetRawText());
    }

    [Fact]
    public void NormalizationCollisionsFailClosed()
    {
        var assetCollision = new FakeReviewDataSource(
            new Dictionary<string, Func<object>>
            {
                ["Data/A-B"] = () => new Dictionary<string, int> { ["one"] = 1 },
                ["Data/A_B"] = () => new Dictionary<string, int> { ["two"] = 2 },
            });
        ReviewDataReport asset = Execute(
            assetCollision,
            ReviewDataContract.KeysOperation,
            "Data/A B");
        Assert.Equal("dataAssetAmbiguous", Assert.Single(asset.Problems).Code);

        var keyCollision = new FakeReviewDataSource(
            new Dictionary<string, Func<object>>
            {
                ["Data/Keys"] = () => new Dictionary<string, int>
                {
                    ["a-b"] = 1,
                    ["a_b"] = 2,
                },
            });
        ReviewDataReport ambiguous = Execute(
            keyCollision,
            ReviewDataContract.GetOperation,
            "Data/Keys",
            "A B");
        Assert.Equal("dataKeyAmbiguous", Assert.Single(ambiguous.Problems).Code);

        ReviewDataReport exact = Execute(
            keyCollision,
            ReviewDataContract.GetOperation,
            "Data/Keys",
            "a-b");
        Assert.Equal("ready", exact.State);
        Assert.Equal("a-b", exact.Key);
    }

    [Fact]
    public void MissingVersionAssetIsDistinctFromAnUnknownNamespace()
    {
        var source = new FakeReviewDataSource(new Dictionary<string, Func<object>>
        {
            ["Data/Present"] = () => new Dictionary<string, int> { ["one"] = 1 },
        });

        ReviewDataReport unavailable = Execute(
            source,
            ReviewDataContract.KeysOperation,
            "Data/FutureAsset");
        Assert.Equal(
            "dataAssetUnavailableInGameVersion",
            Assert.Single(unavailable.Problems).Code);

        ReviewDataReport unknown = Execute(
            source,
            ReviewDataContract.KeysOperation,
            "Mods/Other");
        Assert.Equal("dataAssetUnknown", Assert.Single(unknown.Problems).Code);
    }

    [Fact]
    public void CoverageExposesLoadAndSerializationGapsInsteadOfHidingThem()
    {
        var source = new FakeReviewDataSource(new Dictionary<string, Func<object>>
        {
            ["Data/LoadFailure"] = () => throw new InvalidDataException("broken"),
            ["Data/Unsafe"] = () => new Dictionary<string, double>
            {
                ["nan"] = double.NaN,
            },
        });

        ReviewDataReport report = Execute(
            source,
            ReviewDataContract.AssetsOperation,
            limit: 100);

        Assert.Equal("blocked", report.State);
        Assert.Equal("dataCoverageIncomplete", Assert.Single(report.Problems).Code);
        ReviewDataCoverageReport coverage = Assert.IsType<ReviewDataCoverageReport>(
            report.Coverage);
        Assert.False(coverage.Complete);
        Assert.Equal(2, coverage.Discovered);
        Assert.Equal(1, coverage.Classified);
        Assert.Equal(0, coverage.Supported);
        Assert.Equal(0, coverage.Unknown);
        Assert.Equal(1, coverage.Unclassified);
        Assert.Equal(1, coverage.Unsupported);
        Assert.Contains(
            report.Assets!,
            asset => asset.ProblemCode == "dataAssetLoadFailed");
        Assert.Contains(
            report.Assets!,
            asset => asset.ProblemCode == "dataRecordNotSafelySerializable");
    }

    [Fact]
    public void LocaleSpecificOrNonDataInventoryEntriesAreUnknownCoverage()
    {
        var source = new FakeReviewDataSource(
            new Dictionary<string, Func<object>>
            {
                ["Data/Mail.de-DE"] = () => new Dictionary<string, string>(),
                ["Maps/Farm"] = () => new Dictionary<string, string>(),
            });

        ReviewDataReport report = Execute(
            source,
            ReviewDataContract.AssetsOperation,
            limit: 100);

        ReviewDataCoverageReport coverage = Assert.IsType<ReviewDataCoverageReport>(
            report.Coverage);
        Assert.Equal(2, coverage.Unknown);
        Assert.Equal(0, coverage.Supported);
    }

    [Fact]
    public void TransportEncodingIsCanonicalBoundedAndStrictUtf8()
    {
        const string value = "Data/Events/Farm key_1";
        string token = ReviewTransportToken.Encode(value);

        Assert.True(ReviewTransportToken.TryDecode(
            token,
            ReviewDataContract.MaximumKeyLength,
            out string decoded));
        Assert.Equal(value, decoded);
        Assert.False(ReviewTransportToken.TryDecode(
            token + "=",
            ReviewDataContract.MaximumKeyLength,
            out _));
        Assert.False(ReviewTransportToken.TryDecode(
            "A",
            ReviewDataContract.MaximumKeyLength,
            out _));
    }

    private static ReviewDataReport Execute(
        IReviewDataSource source,
        string operation,
        string? asset = null,
        string? key = null,
        int offset = 0,
        int limit = 50) =>
        ReviewDataOperation.Execute(
            new ReviewDataQuery(operation, asset, key, offset, limit),
            source);

    private sealed class FakeReviewDataSource(
        IReadOnlyDictionary<string, Func<object>> assets)
        : IReviewDataSource
    {
        public string GameVersion => "1.6.15";

        public string GameFileVersion => "1.6.15.24356";

        public IReadOnlyList<string> DiscoverCanonicalAssetNames() =>
            assets.Keys.ToArray();

        public object LoadAsset(string assetName) =>
            assets[assetName]();
    }
}

public sealed record ReviewDataSample(string DisplayName, int Value);

#pragma warning disable CA1051 // Models Stardew GameData types, which expose public fields.
public sealed class ReviewDataFieldSample
{
    public string InternalName = string.Empty;

    public int Count;
}
#pragma warning restore CA1051
