using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;
using Xunit.Abstractions;

namespace SdvKit.Tests;

public sealed class ReviewWorldFeatureTests(ITestOutputHelper output)
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("tree", "1", "{\"growthStage\":5,\"stump\":false,\"health\":10}")]
    [InlineData("tree", "Mod.Tree", "{\"growthStage\":1000000,\"stump\":true,\"health\":-100}")]
    [InlineData("fruitTree", "628", "{\"growthStage\":4,\"stump\":true,\"health\":5}")]
    [InlineData("grass", "7", "{\"numberOfWeeds\":4}")]
    [InlineData("grass", "0", "{\"numberOfWeeds\":0}")]
    [InlineData("grass", "255", "{\"numberOfWeeds\":1}")]
    [InlineData("bush", "2", "{\"health\":0,\"originX\":-100000,\"originY\":-100000}")]
    [InlineData("resourceClump", "672", "{\"health\":1000000,\"originX\":2,\"originY\":3}")]
    [InlineData("resourceClump", "2147483647", "{\"health\":0.25,\"originX\":2,\"originY\":3}")]
    [InlineData("furniture", "(F)Mod.Table", "{\"originX\":2,\"originY\":3}")]
    public void EachFeatureKindHasABoundedRoundTrip(string kind, string id, string stateJson)
    {
        var feature = new ReviewWorldFeature(kind, id,
            JsonSerializer.Deserialize<ReviewWorldFeatureState>(stateJson, JsonOptions)!);
        ReviewWorldReport report = Report(feature);
        Assert.True(ReviewWorldContract.DataValid(report.Data));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewWorldResponseEnvelope(1, Token, report), JsonOptions);
        ReviewWorldResponseEnvelope roundTrip = ProjectReviewWorldService.DeserializeResponse(bytes)!;
        Assert.Equal(feature, Assert.Single(roundTrip.Report.Data!.Tiles).Feature);
        Assert.True(ReviewWorldContract.DataValid(roundTrip.Report.Data));
        using var output = new StringWriter();
        Assert.Equal(0, CliApplication.WriteReviewWorldReport(roundTrip.Report, output));
        using JsonDocument cli = JsonDocument.Parse(output.ToString());
        Assert.Equal(kind, cli.RootElement.GetProperty("data").GetProperty("tiles")[0].GetProperty("feature").GetProperty("kind").GetString());
    }

    [Fact]
    public void AbsenceAndExplicitNullRemainCompatibleWithOlderGameSide()
    {
        JsonObject root = JsonSerializer.SerializeToNode(new ReviewWorldResponseEnvelope(1, Token, Report(null)), JsonOptions)!.AsObject();
        JsonObject tile = root["report"]!["data"]!["tiles"]![0]!.AsObject();
        Assert.Null(ProjectReviewWorldService.DeserializeResponse(Encoding.UTF8.GetBytes(root.ToJsonString()))!.Report.Data!.Tiles[0].Feature);
        Assert.True(tile.Remove("feature"));
        ReviewWorldReport old = ProjectReviewWorldService.DeserializeResponse(Encoding.UTF8.GetBytes(root.ToJsonString()))!.Report;
        Assert.True(ReviewWorldContract.DataValid(old.Data));
        Assert.Null(old.Data!.Tiles[0].Feature);
    }

    [Fact]
    public void UnexpectedKindsIdsStatesAndNonFiniteHealthFailClosed()
    {
        var tree = new ReviewWorldFeature("tree", "1", new(5, false, 10));
        var origin = new ReviewWorldFeatureState(Health: 10, OriginX: 2, OriginY: 3);
        ReviewWorldFeature[] invalid =
        [
            tree with { Kind = "unknown" }, tree with { Kind = "Tree" },
            tree with { Id = "" }, tree with { Id = null! }, tree with { Id = "a\nb" },
            tree with { Id = new string('x', ReviewWorldContract.MaximumFeatureIdLength + 1) },
            tree with { State = null! }, tree with { State = new() },
            tree with { State = tree.State with { GrowthStage = -1 } },
            tree with { State = tree.State with { GrowthStage = ReviewWorldContract.MaximumFeatureGrowthStage + 1 } },
            tree with { State = tree.State with { Stump = null } },
            tree with { State = tree.State with { Health = null } },
            tree with { State = tree.State with { Health = float.NaN } },
            tree with { State = tree.State with { Health = float.PositiveInfinity } },
            tree with { State = tree.State with { Health = float.NegativeInfinity } },
            tree with { State = tree.State with { Health = -101 } },
            tree with { State = tree.State with { Health = 1_000_001 } },
            tree with { State = tree.State with { NumberOfWeeds = 1 } },
            tree with { State = tree.State with { OriginX = 0 } },
            tree with { Kind = "fruitTree" },
            new("grass", "1", new(NumberOfWeeds: -1)), new("grass", "1", new(NumberOfWeeds: 5)),
            new("grass", "256", new(NumberOfWeeds: 1)), new("grass", "01", new(NumberOfWeeds: 1)),
            new("grass", "1", new(Health: 1, NumberOfWeeds: 1)),
            new("bush", "5", origin), new("bush", "-1", origin),
            new("resourceClump", "(O)672", origin), new("resourceClump", "2147483648", origin),
            new("resourceClump", "672", origin with { OriginX = -100001 }),
            new("resourceClump", "672", origin with { OriginY = 100001 }),
            new("resourceClump", "672", origin with { OriginY = null }),
            new("resourceClump", "672", origin with { GrowthStage = 0 }),
            new("furniture", "(O)1", new(OriginX: 2, OriginY: 3)),
            new("furniture", "(F)", new(OriginX: 2, OriginY: 3)),
            new("furniture", "(F)1", origin),
        ];
        Assert.All(invalid, feature => Assert.False(ReviewWorldContract.DataValid(Report(feature).Data)));
    }

    [Theory]
    [InlineData("{\"kind\":\"unknown\",\"id\":\"1\",\"state\":{}}")]
    [InlineData("{\"kind\":\"grass\",\"id\":\"1\",\"state\":{}}")]
    [InlineData("{\"kind\":\"grass\",\"id\":\"1\",\"state\":{\"numberOfWeeds\":1,\"health\":null}}")]
    [InlineData("{\"kind\":\"grass\",\"id\":\"1\",\"state\":{\"numberOfWeeds\":1,\"numberOfWeeds\":2}}")]
    [InlineData("{\"kind\":\"grass\",\"id\":\"1\",\"state\":{\"numberOfWeeds\":1},\"extra\":true}")]
    [InlineData("{\"kind\":\"grass\",\"id\":\"1\",\"id\":\"2\",\"state\":{\"numberOfWeeds\":1}}")]
    [InlineData("{\"kind\":\"grass\",\"id\":\"1\",\"state\":null}")]
    public void FeatureWireShapeRejectsMissingUnknownAndDuplicateMembers(string featureJson)
    {
        string json = JsonSerializer.Serialize(new ReviewWorldResponseEnvelope(1, Token, Report(null)), JsonOptions)
            .Replace("\"feature\":null", "\"feature\":" + featureJson, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ProjectReviewWorldService.DeserializeResponse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void MultiTileFeatureKeepsItsOriginOutsideTheRequestedRectangle()
    {
        var feature = new ReviewWorldFeature("resourceClump", "672", new(Health: 10, OriginX: 2, OriginY: 3));
        ReviewWorldValues data = Report(feature).Data! with
        {
            Area = new(3, 3, 1, 2),
            Tiles = [Tile(3, 3, feature), Tile(3, 4, feature)],
        };
        Assert.True(ReviewWorldContract.DataValid(data));
        Assert.All(data.Tiles, tile => Assert.Equal(feature, tile.Feature));
        Assert.False(ReviewWorldContract.DataValid(Report(feature with { State = feature.State with { OriginX = 3 } }).Data));
        Assert.False(ReviewWorldContract.DataValid(Report(feature with { State = feature.State with { OriginY = 4 } }).Data));
    }

    [Fact]
    public void FullSixteenSquareFeatureCaptureStaysWithinTheResponseLimit()
    {
        // Escaping non-ASCII IDs is larger than ASCII; a tree can share its tile with an object.
        var feature = new ReviewWorldFeature("tree", new string('\uffff', ReviewWorldContract.MaximumFeatureIdLength),
            new(ReviewWorldContract.MaximumFeatureGrowthStage, false, float.Epsilon));
        ReviewWorldReport report = Report(feature) with
        {
            Data = Report(feature).Data! with
            {
                Area = new(99985, 99985, 16, 16),
                Tiles = Enumerable.Range(0, 256).Select(i => Tile(99985 + i % 16, 99985 + i / 16, feature) with
                {
                    ObjectState = "unsupported",
                    ObjectReason = "worldObjectFamilyUnsupported",
                    ObjectQualifiedItemId = "(O)388",
                }).ToArray(),
            },
        };
        Assert.True(ReviewWorldContract.DataValid(report.Data));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewWorldResponseEnvelope(1, Token, report), JsonOptions);
        Assert.True(bytes.Length <= ReviewWorldContract.MaximumResponseBytes, $"Response was {bytes.Length} bytes.");
        output.WriteLine($"Full 16x16 escaped feature ID plus ordinary object: {bytes.Length} bytes.");
        Assert.True(ReviewWorldContract.DataValid(ProjectReviewWorldService.DeserializeResponse(bytes)!.Report.Data));
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("grass")]
    public void FullSixteenSquareFeaturesAndMachinesStayWithinTheResponseLimit(string kind)
    {
        ReviewWorldFeature feature = kind == "tree"
            ? new("tree", new string('x', ReviewWorldContract.MaximumFeatureIdLength),
                new(ReviewWorldContract.MaximumFeatureGrowthStage, false, ReviewWorldContract.MaximumFeatureHealth))
            : new("grass", "255", new(NumberOfWeeds: 4));
        var machine = new ReviewWorldMachine(Token, Token, "(BC)105", "ready", true, -1,
            new("available", null, new("(O)388", 1, 0)), new("available", null, new("(O)724", 1, 0)));
        ReviewWorldReport report = Report(feature) with
        {
            Data = Report(feature).Data! with
            {
                Area = new(0, 0, 16, 16),
                Tiles = Enumerable.Range(0, 256).Select(i => Tile(i % 16, i / 16, feature) with
                {
                    ObjectState = "machine",
                    ObjectQualifiedItemId = machine.QualifiedItemId,
                    Machine = machine,
                }).ToArray(),
            },
        };
        Assert.True(ReviewWorldContract.DataValid(report.Data));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewWorldResponseEnvelope(1, Token, report), JsonOptions);
        Assert.True(bytes.Length <= ReviewWorldContract.MaximumResponseBytes, $"Response was {bytes.Length} bytes.");
        output.WriteLine($"Full 16x16 {kind} plus complete ready machine: {bytes.Length} bytes.");
        Assert.True(ReviewWorldContract.DataValid(ProjectReviewWorldService.DeserializeResponse(bytes)!.Report.Data));
    }

    [Fact]
    public void McpFeatureSchemaIsOptionalClosedAndBounded()
    {
        var reader = new ProjectReviewMcpRuntimeReader(".", "single", null);
        JsonElement schema = ProjectReviewMcpWorldTools.Create(reader).ProtocolTool.OutputSchema!.Value;
        JsonElement definitions = schema.GetProperty("$defs");
        JsonElement tile = definitions.GetProperty("tile");
        Assert.DoesNotContain(tile.GetProperty("required").EnumerateArray(), field => field.GetString() == "feature");
        Assert.Equal("#/$defs/feature", tile.GetProperty("properties").GetProperty("feature").GetProperty("$ref").GetString());
        JsonElement[] kinds = definitions.GetProperty("feature").GetProperty("oneOf").EnumerateArray().Skip(1).ToArray();
        Assert.Equal(6, kinds.Length);
        foreach (JsonElement kind in kinds)
        {
            Assert.False(kind.GetProperty("additionalProperties").GetBoolean());
            string stateRef = kind.GetProperty("properties").GetProperty("state").GetProperty("$ref").GetString()!;
            Assert.False(definitions.GetProperty(stateRef[8..]).GetProperty("additionalProperties").GetBoolean());
        }
        Assert.Equal(ReviewWorldContract.MaximumFeatureIdLength, definitions.GetProperty("featureId").GetProperty("maxLength").GetInt32());
        Assert.Equal(ReviewWorldContract.MaximumFeatureHealth, definitions.GetProperty("featureHealth").GetProperty("maximum").GetSingle());
        Assert.Equal(ReviewWorldContract.MaximumTileCoordinate, definitions.GetProperty("featureOrigin").GetProperty("maximum").GetInt32());
    }

    private static ReviewWorldTile Tile(int x, int y, ReviewWorldFeature? feature) =>
        new(x, y, null, "missing", null, null, null, feature);

    private static ReviewWorldReport Report(ReviewWorldFeature? feature) => new(1, "ready", null, Token, "single", null,
        DateTimeOffset.UtcNow, new("Farm", Token, "101", 42, new(2, 3, 1, 1), true, [Tile(2, 3, feature)]));
}
