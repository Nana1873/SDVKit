using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.GameSide;

namespace SdvKit.Tests;

public sealed class ReviewMapActionsTests
{
    private const string RequestId = "0123456789abcdef0123456789abcdef";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ActionsPreserveAllPropertySourcesAndSortBeforePaging()
    {
        var source = new MapSource();
        source.Tiles[("Buildings", 0, 0)] = Static("Buildings", 0, 0, [Property("Action", "Mailbox")]);
        source.Tiles[("Back", 2, 1)] = Static("Back", 2, 1,
            [Property("TouchAction", "Warp Town 4 5"), Property("Action", "  Message hello  ")],
            [Property("Action", "Door")]);
        source.Tiles[("Back", 3, 0)] = Animated("Back", 3, 0);
        source.Tiles[("Back", 0, 2)] = Static("Back", 0, 2, [Property("Action", "LockedDoorWarp 4 5 Town")]);

        ReviewMapReport report = ReviewMapOperation.Execute(Query(), source);
        Assert.Equal("ready", report.State);
        Assert.Equal(new ReviewMapPage(0, 50, 8, 8, null), report.Page);
        Assert.False(report.Truncated);
        Assert.Collection(report.Actions!,
            action => Assert.Equal(new ReviewMapActionReport("Back", 3, 0, "direct", null, "Action", "Animate", "Animate"), action),
            action => Assert.Equal(new ReviewMapActionReport("Back", 3, 0, "tile-index", 0, "Action", "Message first", "Message"), action),
            action => Assert.Equal(new ReviewMapActionReport("Back", 3, 0, "tile-index", 1, "Action", "Message second", "Message"), action),
            action => Assert.Equal(new ReviewMapActionReport("Back", 2, 1, "direct", null, "Action", "  Message hello  ", "Message"), action),
            action => Assert.Equal(new ReviewMapActionReport("Back", 2, 1, "tile-index", null, "Action", "Door", "Door"), action),
            action => Assert.Equal("TouchAction", action.Name),
            action => Assert.Equal("LockedDoorWarp", action.FirstToken),
            action => Assert.Equal("Buildings", action.LayerId));
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(ReviewMapOperation.Execute(Query(), source)));
        ReviewMapReport middle = ReviewMapOperation.Execute(Query(3, 2), source);
        Assert.Equal(report.Actions!.Skip(3).Take(2), middle.Actions);
        Assert.Equal(new ReviewMapPage(3, 2, 2, 8, 5), middle.Page);
        Assert.True(middle.Truncated);
        Assert.True(Matches(report, Query()));
        Assert.True(Matches(middle, Query(3, 2)));
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 1, 1, null)]
    [InlineData(2, 50, 0, null)]
    [InlineData(int.MaxValue, 100, 0, null)]
    public void PagingReportsOmittedEntriesIncludingEarlierPages(int offset, int limit, int returned, int? next)
    {
        var source = new MapSource();
        source.Tiles[("Back", 0, 0)] = Static("Back", 0, 0,
            [Property("Action", "Message sign"), Property("TouchAction", "Warp Farm 1 2")]);
        ReviewMapReport report = ReviewMapOperation.Execute(Query(offset, limit), source);
        Assert.Equal(new ReviewMapPage(offset, limit, returned, 2, next), report.Page);
        Assert.True(report.Truncated);
        Assert.True(Matches(report, Query(offset, limit)));
    }

    [Fact]
    public void EmptyMapHasNoTruncationAndUnrelatedPropertiesAreIgnored()
    {
        var source = new MapSource();
        source.Tiles[("Back", 0, 0)] = Static("Back", 0, 0, [Property("Passable", true), Property("action", "Door")]);
        ReviewMapReport report = ReviewMapOperation.Execute(Query(), source);
        Assert.Empty(report.Actions!);
        Assert.False(report.Truncated);
        Assert.Equal(new ReviewMapPage(0, 50, 0, 0, null), report.Page);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Message text ", "Message")]
    [InlineData("\u2003Mailbox", "Mailbox")]
    public void RawStringsRemainExactAndTokenIsOptional(string value, string? firstToken)
    {
        var source = new MapSource();
        source.Tiles[("Back", 0, 0)] = Static("Back", 0, 0, [Property("Action", value)]);
        ReviewMapReport report = ReviewMapOperation.Execute(Query(), source);
        ReviewMapActionReport action = Assert.Single(report.Actions!);
        Assert.Equal(value, action.Value);
        Assert.Equal(firstToken, action.FirstToken);
        Assert.True(Matches(report, Query()));
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public void InvalidPageBoundsNeverScan(int offset, int limit)
    {
        var source = new MapSource();
        ReviewMapReport report = ReviewMapOperation.Execute(Query(offset, limit), source);
        Assert.Equal("mapPaginationInvalid", Assert.Single(report.Problems).Code);
        Assert.Equal(0, source.TileReads);
    }

    [Theory]
    [InlineData("control")]
    [InlineData("number")]
    [InlineData("bytes")]
    [InlineData("unicode")]
    [InlineData("duplicate")]
    public void MalformedValuesAnywhereFailClosedEvenOutsideThePage(string kind)
    {
        var source = new MapSource();
        ReviewMapPropertyValue invalid = kind switch
        {
            "control" => Property("Action", "Message\nunsafe"),
            "number" => Property("Action", 42),
            "bytes" => Property("Action", new string('é', ReviewMapContract.MaximumPropertyValueBytes / 2 + 1)),
            "unicode" => new("Action", "string", JsonDocument.Parse("\"\\uD800\"").RootElement.Clone(), 3),
            _ => Property("Action", "Door"),
        };
        source.Tiles[("Back", 0, 0)] = Static("Back", 0, 0, [Property("Action", "Mailbox")]);
        source.Tiles[("Buildings", 3, 2)] = Static("Buildings", 3, 2,
            kind == "duplicate" ? [invalid, invalid] : [invalid]);
        ReviewMapReport report = ReviewMapOperation.Execute(Query(0, 1), source);
        Assert.Equal("blocked", report.State);
        Assert.Equal("mapActionsReadFailed", Assert.Single(report.Problems).Code);
        Assert.Null(report.Actions);
        Assert.Null(report.Page);
        Assert.Null(report.Truncated);
        Assert.True(Matches(report, Query(0, 1)));
    }

    [Fact]
    public void MaximumRawValueIsPreservedWithoutTruncation()
    {
        var source = new MapSource();
        string value = "Message " + new string('a', ReviewMapContract.MaximumPropertyValueBytes - 8);
        source.Tiles[("Back", 0, 0)] = Static("Back", 0, 0, [Property("Action", value)]);
        ReviewMapReport report = ReviewMapOperation.Execute(Query(), source);
        Assert.Equal(value, Assert.Single(report.Actions!).Value);
        Assert.False(report.Truncated);
    }

    [Fact]
    public void AggregateCellLimitFailsBeforeReadingAnyTile()
    {
        var source = new MapSource();
        source.Map = source.Map with
        {
            Layers = source.Map.Layers.Select(layer => layer with
            {
                Report = layer.Report with { Width = 2048, Height = 2048 },
            }).ToArray(),
        };
        ReviewMapReport report = ReviewMapOperation.Execute(Query(), source);
        Assert.Equal("mapActionsReadFailed", Assert.Single(report.Problems).Code);
        Assert.Equal(0, source.TileReads);
    }

    [Fact]
    public void AnimatedFramesAlsoConsumeTheAggregateScanBudget()
    {
        var source = new MapSource();
        source.SetDimensions(2048, 2048);
        source.Tiles[("Back", 0, 0)] = Animated("Back", 0, 0);
        ReviewMapReport report = ReviewMapOperation.Execute(Query(), source);
        Assert.Equal("mapActionsReadFailed", Assert.Single(report.Problems).Code);
        Assert.Equal(1, source.TileReads);
    }

    [Fact]
    public void AggregatePropertyPayloadAndActionCountAreBounded()
    {
        var source = new MapSource();
        source.SetDimensions(4096, 1);
        for (int x = 0; x < 4096; x++)
            source.Tiles[("Back", x, 0)] = Static("Back", x, 0, [Property("Action", "Mailbox")]);
        ReviewMapReport maximum = ReviewMapOperation.Execute(Query(), source);
        Assert.Equal(4096, maximum.Page!.Total);
        Assert.True(maximum.Truncated);
        source.Tiles[("Back", 0, 0)] = Static("Back", 0, 0,
            [Property("Action", "Mailbox"), Property("TouchAction", "Warp Town 1 2")]);
        Assert.Equal("mapActionsReadFailed", Assert.Single(ReviewMapOperation.Execute(Query(), source).Problems).Code);
        source.Tiles.Clear();
        for (int x = 0; x < 65; x++)
            source.Tiles[("Back", x, 0)] = Static("Back", x, 0,
                [Property("Action", "Mailbox"), Property("Other", new string('a', 65536))]);
        Assert.Equal("mapActionsReadFailed", Assert.Single(ReviewMapOperation.Execute(Query(), source).Problems).Code);
    }

    [Fact]
    public void EscapedJsonResponseLimitFailsClosedAndSmallerPageSucceeds()
    {
        var source = new MapSource();
        source.SetDimensions(64, 1);
        for (int x = 0; x < 60; x++)
            source.Tiles[("Back", x, 0)] = Static("Back", x, 0,
                [Property("Action", "Message " + new string('é', 30000))]);
        ReviewMapReport large = ReviewMapOperation.Execute(Query(0, 100), source);
        Assert.Equal("mapResponseTooLarge", Assert.Single(large.Problems).Code);
        Assert.Null(large.Actions);
        ReviewMapReport small = ReviewMapOperation.Execute(Query(0, 1), source);
        Assert.Equal("ready", small.State);
        Assert.Equal(60, small.Page!.Total);
        Assert.True(small.Truncated);
    }

    [Fact]
    public void ResponseValidationRejectsMalformedPayloadsAndUnexpectedFields()
    {
        var source = new MapSource();
        source.Tiles[("Back", 0, 0)] = Static("Back", 0, 0, [Property("Action", "Mailbox")]);
        ReviewMapReport report = ReviewMapOperation.Execute(Query(), source);
        ReviewMapActionReport action = Assert.Single(report.Actions!);
        ReviewMapActionReport[] malformed =
        [
            action with { Name = "Other" }, action with { FirstToken = "Door" },
            action with { Source = "other" }, action with { FrameIndex = 0 },
            action with { X = -1 }, action with { Y = 4096 },
            action with { Value = "\ud800" }, action with { Value = "Mailbox\t" },
            action with { LayerId = " " }, action with { Source = "tile-index", FrameIndex = 1024 },
        ];
        foreach (ReviewMapActionReport invalid in malformed)
            Assert.False(Matches(report with { Actions = [invalid] }, Query()));
        Assert.False(Matches(report with { Truncated = true }, Query()));
        Assert.False(Matches(report with { Truncated = null }, Query()));
        Assert.False(Matches(report with { Warps = [] }, Query()));
        Assert.False(Matches(report with { Actions = [action, action], Page = new(0, 50, 2, 2, null) }, Query()));
        var later = action with { X = 1 };
        Assert.False(Matches(report with { Actions = [later, action], Page = new(0, 50, 2, 2, null) }, Query()));
        ReviewMapQuery oldQuery = Query() with { Operation = ReviewMapContract.WarpsOperation };
        ReviewMapReport oldReport = ReviewMapOperation.Execute(oldQuery, source);
        Assert.True(Matches(oldReport, oldQuery));
        Assert.False(Matches(oldReport with { Actions = [] }, oldQuery));
        Assert.False(Matches(oldReport with { Truncated = false }, oldQuery));
        var envelope = new ReviewMapResponseEnvelope(1, RequestId, report);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        Assert.True(ProjectReviewMapService.MatchesResponse(ProjectReviewMapService.DeserializeResponse(bytes), RequestId, Query()));
        JsonNode json = JsonNode.Parse(bytes)!;
        json["report"]!["actions"]![0]!["unexpected"] = true;
        Assert.Throws<InvalidDataException>(() => ProjectReviewMapService.DeserializeResponse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    private static bool Matches(ReviewMapReport report, ReviewMapQuery query) =>
        ProjectReviewMapService.MatchesResponse(new(1, RequestId, report), RequestId, query);

    private static ReviewMapQuery Query(int offset = 0, int limit = 50) =>
        new(ReviewMapContract.ActionsOperation, "Maps/Farm", null, null, null, null, null, null, null, offset, limit);

    private static ReviewMapPropertyValue Property<T>(string name, T value) =>
        new(name, value is string ? "string" : value is bool ? "boolean" : "integer",
            JsonSerializer.SerializeToElement(value), value is string text
                ? Encoding.UTF8.GetByteCount(text) : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value)));

    private static ReviewMapTileSnapshot Static(string layer, int x, int y,
        ReviewMapPropertyValue[] properties, ReviewMapPropertyValue[]? index = null) =>
        new(new(layer, x, y, true, "static", "sheet", 0, "Alpha", null, null,
            properties.Length, index?.Length ?? 0), properties, index ?? [], []);

    private static ReviewMapTileSnapshot Animated(string layer, int x, int y)
    {
        ReviewMapTileFrameSnapshot[] frames =
        [
            new(new(0, "sheet", 0, "Alpha", 1), [Property("Action", "Message first")]),
            new(new(1, "sheet", 0, "Alpha", 1), [Property("Action", "Message second")]),
        ];
        return new(new(layer, x, y, true, "animated", null, null, null, 100,
            frames.Select(frame => frame.Report).ToArray(), 1, 0),
            [Property("Action", "Animate")], [], frames);
    }

    private sealed class MapSource : IReviewMapSource
    {
        public Dictionary<(string, int, int), ReviewMapTileSnapshot> Tiles { get; } = [];
        public int TileReads { get; private set; }
        public ReviewMapAssetSnapshot Map { get; set; } = new(new(64, 48, 2, 1, 0, 0),
            [new(new(0, "Buildings", 4, 3, 16, 16, true, 0), []), new(new(1, "Back", 4, 3, 16, 16, true, 0), [])],
            [new(0, "sheet", "Maps/sheet", 1, 1, 16, 16, 0, 0, 0, 0, 1, 0)], [], []);
        public string GameVersion => "1.6.15";
        public string GameFileVersion => "1.6.15.24356";
        public IReadOnlyList<string> DiscoverCanonicalAssetNames() => ["Maps/Farm"];
        public ReviewMapAssetIdentity CanonicalizeAssetName(string assetName) => new(assetName, assetName, null);
        public bool AssetExistsForMapRequest(string assetName) => assetName == "Maps/Farm";
        public ReviewMapLoadedAsset LoadAsset(string assetName) => new("xTile.Map", Map);
        public ReviewMapLoadedAsset LoadMapAsset(string assetName) => LoadAsset(assetName);
        public IReadOnlyList<ReviewMapActionReport> ReadActions(string assetName) =>
            ReviewMapOperation.CaptureActions(Map, (layer, x, y) => ReadTile(assetName, layer, x, y));
        public ReviewMapTileSnapshot ReadTile(string assetName, string layerId, int x, int y)
        {
            TileReads++;
            return Tiles.GetValueOrDefault((layerId, x, y))
                ?? new(new(layerId, x, y, false, null, null, null, null, null, null, 0, 0), [], [], []);
        }
        public void SetDimensions(int width, int height) => Map = Map with
        {
            Summary = Map.Summary with { LayerCount = 1, DisplayWidth = width * 16, DisplayHeight = height * 16 },
            Layers = [new(new(0, "Back", width, height, 16, 16, true, 0), [])],
        };
    }
}
