using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewModAssetServiceTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void BuildCommandBindsEveryOperandAndFitsTheConsoleAtMaximumLength()
    {
        string requestId = Guid.NewGuid().ToString("N");
        string asset = "Mods/A/" + new string('\u0800', 505);
        string key = new('\u0800', ReviewModAssetContract.MaximumKeyLength);
        Assert.Equal(ReviewModAssetContract.MaximumAssetLength, asset.Length);
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.GetOperation,
            asset,
            key,
            0,
            1);

        string command = ProjectReviewModAssetService.BuildCommand(requestId, query);
        string[] tokens = command.Split(' ');

        Assert.Equal(8, tokens.Length);
        Assert.Equal("sdvkit", tokens[0]);
        Assert.Equal("mod-assets", tokens[1]);
        Assert.Equal(requestId, tokens[2]);
        Assert.True(ReviewModAssetContract.TryDecode(
            tokens[6],
            ReviewModAssetContract.MaximumAssetLength,
            out string decodedAsset));
        Assert.True(ReviewModAssetContract.TryDecode(
            tokens[7],
            ReviewModAssetContract.MaximumKeyLength,
            out string decodedKey));
        Assert.Equal(asset, decodedAsset);
        Assert.Equal(key, decodedKey);
        Assert.True(command.Length <= ProjectReviewConsoleLine.MaximumLength);
        Assert.Null(ProjectReviewConsoleLine.ValidationError(command));
    }

    [Fact]
    public void BuildCommandUsesExplicitMissingTokens()
    {
        string requestId = Guid.NewGuid().ToString("N");

        string command = ProjectReviewModAssetService.BuildCommand(
            requestId,
            new ReviewModAssetQuery(
                ReviewModAssetContract.AssetsOperation,
                null,
                null,
                0,
                50));

        Assert.Equal(
            $"sdvkit mod-assets {requestId} assets 0 50 - -",
            command);
    }

    [Theory]
    [InlineData("mods/Example.Mod/Words")]
    [InlineData("Mods\\Example.Mod\\Words")]
    [InlineData("Mods/Example.Mod/../Words")]
    [InlineData("Mods/Example.Mod//Words")]
    [InlineData("Mods-Example.Mod-Words")]
    public void NonCanonicalQueryShapeFailsBeforeTransport(string asset)
    {
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.KeysOperation,
            asset,
            null,
            0,
            50);

        LiveLabCommandResult result = ProjectReviewModAssetService.Execute(
            query,
            "not-used");

        Assert.Equal(3, result.ExitCode);
        ReviewModAssetReport report = Assert.IsType<ReviewModAssetReport>(result.Report);
        Assert.Equal("modAssetNameInvalid", Assert.Single(report.Problems).Code);
        Assert.Throws<ArgumentException>(() =>
            ProjectReviewModAssetService.BuildCommand(
                "0123456789abcdef0123456789abcdef",
                query));
    }

    [Fact]
    public void MalformedUtf16AndExactPaginationFailBeforeTransport()
    {
        var malformedAsset = new ReviewModAssetQuery(
            ReviewModAssetContract.KeysOperation,
            "Mods/Example.Mod/\uD800",
            null,
            0,
            50);
        var malformedKey = new ReviewModAssetQuery(
            ReviewModAssetContract.GetOperation,
            "Mods/Example.Mod/Words",
            "\uD800",
            0,
            1);
        var pagedGet = malformedKey with { Key = "Key", Offset = 1 };

        Assert.Equal(
            "modAssetNameInvalid",
            Problem(ProjectReviewModAssetService.Execute(malformedAsset, "not-used")));
        Assert.Equal(
            "modAssetKeyInvalid",
            Problem(ProjectReviewModAssetService.Execute(malformedKey, "not-used")));
        Assert.Equal(
            "modAssetPaginationInvalid",
            Problem(ProjectReviewModAssetService.Execute(pagedGet, "not-used")));
    }

    [Fact]
    public void DeserializeResponseRequiresTheExactRecursiveWireShape()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.AssetsOperation,
            null,
            null,
            0,
            1);
        byte[] valid = SerializeWire(AssetsEnvelope(requestId, query));

        ReviewModAssetResponseEnvelope? deserialized =
            ProjectReviewModAssetService.DeserializeResponse(valid);
        Assert.NotNull(deserialized);
        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            deserialized,
            query,
            requestId));

        JsonObject missingNestedMember = ParseWire(valid);
        Assert.True(missingNestedMember["report"]!["assets"]![0]!
            .AsObject()
            .Remove("providerStatus"));
        AssertInvalidWire(missingNestedMember);

        JsonObject unknownCoverageMember = ParseWire(valid);
        unknownCoverageMember["report"]!["coverage"]!["unknown"] = true;
        AssertInvalidWire(unknownCoverageMember);

        JsonObject wrongCoverageFlag = ParseWire(valid);
        wrongCoverageFlag["report"]!["coverage"]!["complete"] = false;
        AssertInvalidWire(wrongCoverageFlag);

        JsonObject wrongIntegerKind = ParseWire(valid);
        wrongIntegerKind["report"]!["assets"]![0]!["generation"] = 1.5;
        AssertInvalidWire(wrongIntegerKind);

        JsonObject wrongRecordShape = ParseWire(SerializeWire(GetEnvelope(requestId)));
        wrongRecordShape["report"]!["record"] = new JsonObject { ["value"] = "one" };
        AssertInvalidWire(wrongRecordShape);

        string duplicateMember = Encoding.UTF8.GetString(valid).Replace(
            "{\"schemaVersion\":1,",
            "{\"schemaVersion\":1,\"schemaVersion\":1,",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewModAssetService.DeserializeResponse(
                Encoding.UTF8.GetBytes(duplicateMember)));
    }

    [Fact]
    public void AssetsResponseRequiresExactPageCoverageAndSafeAssets()
    {
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.AssetsOperation,
            null,
            null,
            0,
            1);
        ReviewModAssetResponseEnvelope envelope = AssetsEnvelope(requestId, query);

        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            envelope,
            query,
            requestId));

        ReviewModAssetReport report = envelope.Report;
        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            envelope with
            {
                Report = report with
                {
                    Page = report.Page! with { Returned = 0 },
                },
            },
            query,
            requestId));
        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            envelope with
            {
                Report = report with
                {
                    Coverage = report.Coverage! with { AdapterUnavailable = 1 },
                },
            },
            query,
            requestId));
        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            envelope with
            {
                Report = report with
                {
                    Assets = [report.Assets![0] with { AssetName = "../Outside" }],
                },
            },
            query,
            requestId));
        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            envelope with
            {
                Report = report with
                {
                    Assets = [report.Assets![0] with { ProviderModId = "Invented.Mod" }],
                },
            },
            query,
            requestId));
    }

    [Fact]
    public void AssetsKeepOrdinalTypeIdentityWithCaseInsensitiveAssetIdentity()
    {
        string requestId = Guid.NewGuid().ToString("N");
        const string assetName = "Mods/Example.Mod/Values";
        (Type firstType, Type secondType) = CreateCaseVariantTypes();
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(assetName, firstType);
        catalogue.ObserveRequested(assetName.ToUpperInvariant(), secondType);
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.AssetsOperation,
            null,
            null,
            0,
            2);
        ReviewModAssetReport report = ReviewModAssetOperation.Execute(
            query,
            new ProducerSource(catalogue, new object()));
        var envelope = new ReviewModAssetResponseEnvelope(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            report);

        Assert.Equal(2, report.Assets!.Count);
        Assert.Equal(
            2,
            report.Assets.Select(asset => asset.DataType)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Single(report.Assets.Select(asset => asset.DataType)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            envelope,
            query,
            requestId));
    }

    [Fact]
    public void KeysAndGetResponsesBindCanonicalAssetAndPrimitiveShape()
    {
        string requestId = Guid.NewGuid().ToString("N");
        var keysQuery = new ReviewModAssetQuery(
            ReviewModAssetContract.KeysOperation,
            "Mods/Example.Mod/Words",
            null,
            0,
            2);
        ReviewModAssetResponseEnvelope keysEnvelope = new(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            BaseReport(keysQuery.Operation) with
            {
                Asset = Asset(),
                Keys = ["Alpha", "Beta"],
                Page = new ReviewModAssetPage(0, 2, 2, 2, null),
            });
        var getQuery = new ReviewModAssetQuery(
            ReviewModAssetContract.GetOperation,
            "Mods/Example.Mod/Words",
            "alpha",
            0,
            1);
        ReviewModAssetResponseEnvelope getEnvelope = new(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            BaseReport(getQuery.Operation) with
            {
                Asset = Asset(),
                Key = "Alpha",
                Record = JsonSerializer.SerializeToElement("one"),
            });
        ReviewModAssetResponseEnvelope? roundTrippedGet =
            ProjectReviewModAssetService.DeserializeResponse(
                SerializeWire(getEnvelope));

        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            keysEnvelope,
            keysQuery,
            requestId));
        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            getEnvelope,
            getQuery,
            requestId));
        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            roundTrippedGet,
            getQuery,
            requestId));
        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            getEnvelope with
            {
                Report = getEnvelope.Report with
                {
                    Record = JsonSerializer.SerializeToElement(1),
                },
            },
            getQuery,
            requestId));
    }

    [Theory]
    [MemberData(nameof(IntegerKeyValues))]
    public void IntegerKeyProducerMatchesTheHostsOrdinalWireOrder(
        Type dataType,
        object value)
    {
        string requestId = Guid.NewGuid().ToString("N");
        const string assetName = "Mods/Example.Mod/Numbers";
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(assetName, dataType);
        catalogue.ObserveReady(assetName);
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.KeysOperation,
            assetName,
            null,
            0,
            2);
        ReviewModAssetReport report = ReviewModAssetOperation.Execute(
            query,
            new ProducerSource(catalogue, value));
        var envelope = new ReviewModAssetResponseEnvelope(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            report);

        Assert.Equal(["10", "2"], report.Keys);
        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            envelope,
            query,
            requestId));
    }

    [Fact]
    public void ListProducerMatchesTheHostsNaturalIndexOrder()
    {
        string requestId = Guid.NewGuid().ToString("N");
        const string assetName = "Mods/Example.Mod/Values";
        List<string> value = Enumerable.Range(0, 11)
            .Select(index => $"value-{index}")
            .ToList();
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(assetName, typeof(List<string>));
        catalogue.ObserveReady(assetName);
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.KeysOperation,
            assetName,
            null,
            0,
            value.Count);
        ReviewModAssetReport report = ReviewModAssetOperation.Execute(
            query,
            new ProducerSource(catalogue, value));
        var envelope = new ReviewModAssetResponseEnvelope(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            report);

        Assert.Equal(
            Enumerable.Range(0, value.Count)
                .Select(index => index.ToString(CultureInfo.InvariantCulture)),
            report.Keys);
        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            envelope,
            query,
            requestId));
    }

    [Theory]
    [MemberData(nameof(ExactKeyIdentityValues))]
    public void NonStringKeyShapesRequireExactIdentityEndToEnd(
        Type dataType,
        object value,
        string exactKey,
        string aliasKey)
    {
        string requestId = Guid.NewGuid().ToString("N");
        const string assetName = "Mods/Example.Mod/Value";
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(assetName, dataType);
        catalogue.ObserveReady(assetName);
        var source = new ProducerSource(catalogue, value);
        var aliasQuery = new ReviewModAssetQuery(
            ReviewModAssetContract.GetOperation,
            assetName,
            aliasKey,
            0,
            1);

        ReviewModAssetReport aliasReport = ReviewModAssetOperation.Execute(
            aliasQuery,
            source);

        Assert.Equal("blocked", aliasReport.State);
        Assert.Equal("modAssetKeyUnknown", Assert.Single(aliasReport.Problems).Code);

        var exactQuery = aliasQuery with { Key = exactKey };
        ReviewModAssetReport exactReport = ReviewModAssetOperation.Execute(
            exactQuery,
            source);
        var exactEnvelope = new ReviewModAssetResponseEnvelope(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            exactReport);

        Assert.Equal("ready", exactReport.State);
        Assert.Equal(exactKey, exactReport.Key);
        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            exactEnvelope,
            exactQuery,
            requestId));
        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            exactEnvelope,
            aliasQuery,
            requestId));
    }

    [Fact]
    public void StringDictionaryStableKeyAliasRemainsBoundEndToEnd()
    {
        string requestId = Guid.NewGuid().ToString("N");
        const string assetName = "Mods/Example.Mod/Words";
        var catalogue = new ReviewModAssetCatalog(["Example.Mod"], StartedAt);
        catalogue.ObserveRequested(assetName, typeof(Dictionary<string, string>));
        catalogue.ObserveReady(assetName);
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.GetOperation,
            assetName,
            "foo_bar",
            0,
            1);
        ReviewModAssetReport report = ReviewModAssetOperation.Execute(
            query,
            new ProducerSource(
                catalogue,
                new Dictionary<string, string> { ["Foo-Bar"] = "value" }));
        var envelope = new ReviewModAssetResponseEnvelope(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            report);

        Assert.Equal("Foo-Bar", report.Key);
        Assert.True(ProjectReviewModAssetService.MatchesRequest(
            envelope,
            query,
            requestId));
    }

    [Fact]
    public void AssetAliasCannotCrossOwnerOrPathSegmentBoundaries()
    {
        Assert.True(ReviewModAssetContract.StableAssetIdentityEquals(
            "Mods/Example.Mod/Foo-Bar",
            "Mods/Example.Mod/Foo_Bar"));
        Assert.False(ReviewModAssetContract.StableAssetIdentityEquals(
            "Mods/Example-Mod/Foo",
            "Mods/Example/Mod/Foo"));
        Assert.False(ReviewModAssetContract.StableAssetIdentityEquals(
            "Mods/Example.Mod/Foo-Bar",
            "Mods/Other.Mod/Foo_Bar"));
    }

    [Fact]
    public void NullableOrContradictoryGraphMembersFailClosed()
    {
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewModAssetQuery(
            ReviewModAssetContract.AssetsOperation,
            null,
            null,
            0,
            1);
        ReviewModAssetResponseEnvelope envelope = AssetsEnvelope(requestId, query);

        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            envelope with { Report = null! },
            query,
            requestId));
        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            envelope with
            {
                Report = envelope.Report with { Problems = null! },
            },
            query,
            requestId));
        Assert.False(ProjectReviewModAssetService.MatchesRequest(
            envelope with
            {
                Report = envelope.Report with
                {
                    Problems = [new ReviewModAssetProblem("unexpected", "Unexpected.")],
                },
            },
            query,
            requestId));
    }

    private static string Problem(LiveLabCommandResult result) =>
        Assert.Single(Assert.IsType<ReviewModAssetReport>(result.Report).Problems).Code;

    private static (Type First, Type Second) CreateCaseVariantTypes()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"SdvKit.ModAssetTypes.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("Types");
        Type first = module.DefineType("Example.Record").CreateType()!;
        Type second = module.DefineType("Example.record").CreateType()!;
        return (first, second);
    }

    public static TheoryData<Type, object> IntegerKeyValues =>
        new()
        {
            {
                typeof(Dictionary<int, string>),
                new Dictionary<int, string> { [2] = "two", [10] = "ten" }
            },
            {
                typeof(Dictionary<int, int>),
                new Dictionary<int, int> { [2] = 2, [10] = 10 }
            },
        };

    public static TheoryData<Type, object, string, string> ExactKeyIdentityValues =>
        new()
        {
            {
                typeof(Dictionary<int, string>),
                new Dictionary<int, string> { [-1] = "negative" },
                "-1",
                "1"
            },
            {
                typeof(List<string>),
                new List<string> { "zero", "one" },
                "1",
                "1-"
            },
            {
                typeof(string),
                "single",
                ReviewModAssetContract.SingletonKey,
                ReviewModAssetContract.SingletonKey + "-"
            },
        };

    private static ReviewModAssetResponseEnvelope AssetsEnvelope(
        string requestId,
        ReviewModAssetQuery query) =>
        new(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            BaseReport(query.Operation) with
            {
                Assets = [Asset()],
                Page = new ReviewModAssetPage(0, 1, 1, 1, null),
                Coverage = new ReviewModAssetCoverageReport(
                    ReviewModAssetContract.CoverageScope,
                    StartedAt,
                    1,
                    1,
                    1,
                    0,
                    1,
                    0,
                    0,
                    0,
                    0,
                    0),
            });

    private static ReviewModAssetResponseEnvelope GetEnvelope(string requestId) =>
        new(
            ReviewModAssetContract.SchemaVersion,
            requestId,
            BaseReport(ReviewModAssetContract.GetOperation) with
            {
                Asset = Asset(),
                Key = "Alpha",
                Record = JsonSerializer.SerializeToElement("one"),
            });

    private static ReviewModAssetReport BaseReport(string operation) =>
        new(
            ReviewModAssetContract.SchemaVersion,
            "ready",
            operation,
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
            []);

    private static ReviewModAssetAssetReport Asset() =>
        new(
            "Mods/Example.Mod/Words",
            "Example.Mod",
            "resolved",
            null,
            "unavailableThroughPublicSmapiApi",
            "System.Collections.Generic.Dictionary<System.String,System.String>",
            "stringDictionary",
            "ready",
            0,
            1,
            1,
            true,
            true,
            false,
            false,
            null);

    private static byte[] SerializeWire(ReviewModAssetResponseEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, WireJsonOptions);

    private static JsonObject ParseWire(byte[] bytes) =>
        JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();

    private static void AssertInvalidWire(JsonNode node) =>
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewModAssetService.DeserializeResponse(
                Encoding.UTF8.GetBytes(node.ToJsonString())));

    private sealed class ProducerSource(
        ReviewModAssetCatalog catalogue,
        object value) : IReviewModAssetSource
    {
        public string GameVersion => "1.6.15";

        public string GameFileVersion => "1.6.15.24356";

        public ReviewModAssetInventorySnapshot GetInventory() => catalogue.Snapshot();

        public ReviewModAssetLoadResult Load(ReviewModAssetObservation asset)
        {
            catalogue.MarkVerifiedReady(asset.AssetName, asset.DataType);
            return new ReviewModAssetLoadResult(true, value, null);
        }
    }
}
