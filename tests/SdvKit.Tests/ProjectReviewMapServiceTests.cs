using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewMapServiceTests
{
    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void BuildCommandUsesFixedArityCanonicalTokensAndInvariantIntegers()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewMapQuery(
            ReviewMapContract.PropertyOperation,
            "Maps/Ö Town",
            "Back Ground",
            12,
            34,
            ReviewMapContract.TileScope,
            ReviewMapContract.TileIndexSource,
            5,
            "Action Name",
            0,
            1);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            string command = ProjectReviewMapService.BuildCommand(requestId, query);

            string[] tokens = command.Split(' ');
            Assert.Equal(14, tokens.Length);
            Assert.Equal("sdvkit", tokens[0]);
            Assert.Equal("map", tokens[1]);
            Assert.Equal(requestId, tokens[2]);
            Assert.Equal("12", tokens[8]);
            Assert.Equal("34", tokens[9]);
            Assert.Equal("5", tokens[12]);
            Assert.DoesNotContain("Ö Town", command, StringComparison.Ordinal);
            Assert.DoesNotContain("Back Ground", command, StringComparison.Ordinal);
            Assert.DoesNotContain("Action Name", command, StringComparison.Ordinal);
            Assert.True(ReviewTransportToken.TryDecode(
                tokens[6],
                ReviewMapContract.MaximumAssetLength,
                out string asset));
            Assert.Equal(query.Asset, asset);
            Assert.True(ReviewTransportToken.TryDecode(
                tokens[13],
                ReviewMapContract.MaximumPropertyNameLength,
                out string property));
            Assert.Equal(query.Property, property);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void BuildCommandKeepsMissingOperandsAsFixedSentinels()
    {
        string command = ProjectReviewMapService.BuildCommand(
            "0123456789abcdef0123456789abcdef",
            new ReviewMapQuery(
                ReviewMapContract.AssetsOperation,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                7,
                9));

        string[] tokens = command.Split(' ');
        Assert.Equal(14, tokens.Length);
        Assert.Equal(8, tokens[6..].Length);
        Assert.All(tokens[6..], token => Assert.Equal("-", token));
    }

    [Fact]
    public void ValidateRejectsMalformedAndUnexpectedOperands()
    {
        (ReviewMapQuery Query, string Code)[] invalidQueries =
        {
            (new ReviewMapQuery("unknown", null, null, null, null, null, null, null, null, 0, 1), "mapOperationUnknown"),
            (new ReviewMapQuery("get", "Maps/Town", null, null, null, null, null, null, null, 1, 1), "mapPaginationInvalid"),
            (new ReviewMapQuery("tile", "Maps/Town", "Buildings", -1, 0, null, null, null, null, 0, 1), "mapRequestInvalid"),
            (new ReviewMapQuery("property", "Maps/Town", null, null, null, "map", "tile-index", null, "Outdoors", 0, 1), "mapPropertyScopeInvalid"),
            (new ReviewMapQuery("get", "Maps/\uD800", null, null, null, null, null, null, null, 0, 1), "mapAssetInvalid"),
            (new ReviewMapQuery("layer", "Maps/Town", "\uD800", null, null, null, null, null, null, 0, 1), "mapLayerInvalid"),
            (new ReviewMapQuery("property", "Maps/Town", null, null, null, "map", "direct", null, "\uD800", 0, 1), "mapPropertyInvalid"),
        };

        foreach ((ReviewMapQuery query, string expectedCode) in invalidQueries)
        {
            ReviewMapProblem problem = Assert.IsType<ReviewMapProblem>(
                ProjectReviewMapService.Validate(query));

            Assert.Equal(expectedCode, problem.Code);
        }
    }

    [Fact]
    public void InvalidUtf16IsBlockedBeforeTransportEncoding()
    {
        var query = new ReviewMapQuery(
            ReviewMapContract.GetOperation,
            "Maps/\uD800",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            1);

        LiveLabCommandResult result = ProjectReviewMapService.Execute(
            query,
            "unused-lab-root");

        Assert.Equal(3, result.ExitCode);
        ReviewMapReport report = Assert.IsType<ReviewMapReport>(result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal("mapAssetInvalid", Assert.Single(report.Problems).Code);
        Assert.Throws<ArgumentException>(() => ProjectReviewMapService.BuildCommand(
            "0123456789abcdef0123456789abcdef",
            query));
    }

    [Fact]
    public void DeserializeResponseRequiresTheExactRecursiveWireShape()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var asset = new ReviewMapAssetReport(
            "Maps/Town",
            "xTile.Map",
            "map",
            new ReviewMapSummary(8320, 7040, 8, 6, 19, 10),
            true,
            null);
        var report = new ReviewMapReport(
            ReviewMapContract.SchemaVersion,
            "ready",
            ReviewMapContract.AssetsOperation,
            "1.6.15",
            "1.6.15.24356",
            null,
            null,
            null,
            null,
            null,
            null,
            [asset],
            null,
            null,
            null,
            new ReviewMapPage(0, 1, 1, 1, null),
            new ReviewMapCoverageReport(1, 1, 1, 0, 1, 0, 0, 0),
            []);
        byte[] valid = SerializeWire(new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            report));

        Assert.NotNull(ProjectReviewMapService.DeserializeResponse(valid));

        JsonObject missingPageMember = ParseWire(valid);
        Assert.True(missingPageMember["report"]!["page"]!.AsObject().Remove("offset"));
        AssertInvalidWire(missingPageMember);

        JsonObject unknownNestedMember = ParseWire(valid);
        unknownNestedMember["report"]!["assets"]![0]!["unknown"] = 1;
        AssertInvalidWire(unknownNestedMember);

        JsonObject wrongCoverageFlag = ParseWire(valid);
        wrongCoverageFlag["report"]!["coverage"]!["complete"] = false;
        AssertInvalidWire(wrongCoverageFlag);

        JsonObject oversizedCoverageInteger = ParseWire(valid);
        oversizedCoverageInteger["report"]!["coverage"]!["discovered"] =
            (long)int.MaxValue + 1;
        AssertInvalidWire(oversizedCoverageInteger);

        string duplicateMember = Encoding.UTF8.GetString(valid).Replace(
            "{\"schemaVersion\":1,",
            "{\"schemaVersion\":1,\"schemaVersion\":1,",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewMapService.DeserializeResponse(
                Encoding.UTF8.GetBytes(duplicateMember)));

        var tile = new ReviewMapTileReport(
            "Back",
            0,
            0,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            0);
        byte[] tileWire = SerializeWire(new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            ReadyReport(
                ReviewMapContract.TileOperation,
                assetName: "Maps/Town",
                tile: tile)));
        JsonObject missingPresence = ParseWire(tileWire);
        Assert.True(missingPresence["report"]!["tile"]!.AsObject().Remove("present"));
        AssertInvalidWire(missingPresence);
    }

    [Fact]
    public void MatchesResponseBindsExactResultToRequestedAssetAndOperands()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewMapQuery(
            ReviewMapContract.PropertyOperation,
            "maps\\adventureguild",
            "background",
            4,
            7,
            ReviewMapContract.TileScope,
            ReviewMapContract.TileIndexSource,
            2,
            "ActionName",
            0,
            1);
        ReviewMapReport report = ReadyReport(
            query.Operation,
            assetName: "Maps/AdventureGuild",
            layer: new ReviewMapLayerReport(0, "BackGround", 10, 10, 16, 16, true, 0),
            tile: new ReviewMapTileReport(
                "BackGround",
                4,
                7,
                true,
                "animated",
                null,
                null,
                null,
                100,
                [
                    new ReviewMapTileFrameReport(0, "outdoors", 10, "Alpha", 0),
                    new ReviewMapTileFrameReport(1, "outdoors", 11, "Alpha", 0),
                    new ReviewMapTileFrameReport(2, "outdoors", 12, "Alpha", 1),
                ],
                0,
                0),
            property: new ReviewMapPropertyReport(
                ReviewMapContract.TileScope,
                ReviewMapContract.TileIndexSource,
                2,
                "ActionName",
                "string",
                JsonSerializer.SerializeToElement("Open")));
        var envelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            report);

        Assert.True(ProjectReviewMapService.MatchesResponse(envelope, requestId, query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { AssetName = "Maps/Town" } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Layer = report.Layer! with { Id = "Buildings" } } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Tile = report.Tile! with { X = 5 } } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Property = report.Property! with { FrameIndex = 1 } } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Property = report.Property! with { Name = "TouchAction" } } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Property = report.Property! with { Name = "actionname" } } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Tile = report.Tile! with
                    {
                        Frames = report.Tile.Frames!
                            .Select((frame, index) => index == 2
                                ? frame with { Ordinal = 1 }
                                : frame)
                            .ToArray(),
                    },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Property = report.Property! with { Type = "integer" },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with { Map = new ReviewMapSummary(1, 1, 1, 0, 0, 0) },
            },
            requestId,
            query));
        var suffixQuery = query with { Asset = "Maps/Area.no" };
        Assert.True(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with { AssetName = suffixQuery.Asset },
            },
            requestId,
            suffixQuery));
        var unstableQuery = query with { Asset = "Maps/   /Town" };
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with { AssetName = unstableQuery.Asset },
            },
            requestId,
            unstableQuery));
        foreach (string invalidAsset in new[]
        {
            "Maps/../Town",
            "Maps//Town",
            "maps-town",
        })
        {
            ReviewMapQuery invalidQuery = query with { Asset = invalidAsset };
            Assert.False(ProjectReviewMapService.MatchesResponse(
                envelope with
                {
                    Report = report with { AssetName = invalidAsset },
                },
                requestId,
                invalidQuery));
        }
    }

    [Fact]
    public void MatchesResponseRequiresExactPageShape()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewMapQuery(
            ReviewMapContract.LayersOperation,
            "Maps/Town",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            2,
            2);
        ReviewMapLayerReport[] layers =
        [
            new ReviewMapLayerReport(2, "Buildings", 10, 10, 16, 16, true, 0),
            new ReviewMapLayerReport(3, "Front", 10, 10, 16, 16, true, 0),
        ];
        ReviewMapReport report = ReadyReport(
            query.Operation,
            assetName: "Maps/Town",
            layers: layers,
            page: new ReviewMapPage(2, 2, 2, 5, 4));
        var envelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            report);

        Assert.True(ProjectReviewMapService.MatchesResponse(envelope, requestId, query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Page = report.Page! with { NextOffset = 3 } } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Page = report.Page! with { Total = 3 } } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Layers = [layers[0]] } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Layers = [layers[0] with { Ordinal = 1 }, layers[1]],
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Layers = [layers[0] with { Width = 0 }, layers[1]],
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Warps = [] } },
            requestId,
            query));
    }

    [Fact]
    public void MatchesResponseRequiresCoverageToDescribeTheExactInventoryPage()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewMapQuery(
            ReviewMapContract.AssetsOperation,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            1);
        var asset = new ReviewMapAssetReport(
            "Maps/Town",
            "xTile.Map",
            "map",
            new ReviewMapSummary(8320, 7040, 8, 6, 19, 10),
            true,
            null);
        ReviewMapReport report = new(
            ReviewMapContract.SchemaVersion,
            "ready",
            query.Operation,
            "1.6.15",
            "1.6.15.24356",
            null,
            null,
            null,
            null,
            null,
            null,
            [asset],
            null,
            null,
            null,
            new ReviewMapPage(0, 1, 1, 2, 1),
            new ReviewMapCoverageReport(2, 2, 2, 0, 2, 0, 0, 0),
            []);
        var envelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            report);

        Assert.True(ProjectReviewMapService.MatchesResponse(envelope, requestId, query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Coverage = report.Coverage! with
                    {
                        MapAssets = 0,
                        NonMapAssets = 2,
                        Supported = 0,
                    },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Coverage = report.Coverage! with
                    {
                        Discovered = 1,
                        Classified = 1,
                        MapAssets = 1,
                        Supported = 1,
                    },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Assets =
                    [
                        asset with
                        {
                            AssetName = new string(
                                'a',
                                ReviewMapContract.MaximumAssetLength + 1),
                        },
                    ],
                },
            },
            requestId,
            query));
    }

    [Fact]
    public void MatchesResponseRejectsNullableOrIrrelevantGetGraphMembers()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewMapQuery(
            ReviewMapContract.GetOperation,
            "Maps/Town",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            1);
        ReviewMapReport report = ReadyReport(
            query.Operation,
            assetName: "Maps/Town",
            map: new ReviewMapSummary(8320, 7040, 8, 6, 19, 10));
        var envelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            report);

        Assert.True(ProjectReviewMapService.MatchesResponse(envelope, requestId, query));
        Assert.False(ProjectReviewMapService.MatchesResponse(null, requestId, query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = null! },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Problems = null! } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { GameVersion = null } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Map = null } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Layers = [] } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Map = report.Map! with
                    {
                        LayerCount = ReviewMapContract.MaximumLayersPerMap + 1,
                    },
                },
            },
            requestId,
            query));
    }

    [Fact]
    public void BlockedResponseRequiresBoundedProblemsAndAnEmptyOperationGraph()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewMapQuery(
            ReviewMapContract.LayerOperation,
            "Maps/Town",
            "Buildings",
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            1);
        ReviewMapReport report = BlockedReport(query.Operation);
        var envelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            report);

        Assert.True(ProjectReviewMapService.MatchesResponse(envelope, requestId, query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Layer = new ReviewMapLayerReport(
                        0,
                        "Buildings",
                        10,
                        10,
                        16,
                        16,
                        true,
                        0),
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Problems = [] } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { Problems = [null!] } },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Problems = [new ReviewMapProblem("bad-code", "Rejected.")],
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Problems = [new ReviewMapProblem("mapBlocked", new string('x', 513))],
                },
            },
            requestId,
            query));
    }

    [Fact]
    public void BlockedAssetInventoryRequiresAConsistentBoundedPartialGraph()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewMapQuery(
            ReviewMapContract.AssetsOperation,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            2);
        ReviewMapAssetReport[] assets =
        [
            new ReviewMapAssetReport(
                "Maps/Town",
                "xTile.Map",
                "map",
                new ReviewMapSummary(8320, 7040, 8, 6, 19, 10),
                true,
                null),
            new ReviewMapAssetReport(
                "invalid-map-asset-0001",
                null,
                "gap",
                null,
                false,
                "mapAssetNameInvalid"),
        ];
        ReviewMapReport report = BlockedReport(query.Operation) with
        {
            Assets = assets,
            Page = new ReviewMapPage(0, 2, 2, 2, null),
            Coverage = new ReviewMapCoverageReport(2, 1, 1, 0, 1, 1, 0, 0),
        };
        var envelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            report);

        Assert.True(ProjectReviewMapService.MatchesResponse(envelope, requestId, query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Coverage = report.Coverage! with { Unknown = 0 },
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with
            {
                Report = report with
                {
                    Assets = [assets[0], assets[1] with { ProblemCode = null }],
                },
            },
            requestId,
            query));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            envelope with { Report = report with { AssetName = "Maps/Town" } },
            requestId,
            query));
    }

    [Fact]
    public void ExactLayerAndTileRequireTheirCompleteExclusiveShapes()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var layerQuery = new ReviewMapQuery(
            ReviewMapContract.LayerOperation,
            "Maps/Town",
            "buildings",
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            1);
        ReviewMapReport layerReport = ReadyReport(
            layerQuery.Operation,
            assetName: "Maps/Town",
            layer: new ReviewMapLayerReport(
                2,
                "Buildings",
                10,
                10,
                16,
                16,
                true,
                2));
        var layerEnvelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            layerReport);
        Assert.True(ProjectReviewMapService.MatchesResponse(
            layerEnvelope,
            requestId,
            layerQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            layerEnvelope with
            {
                Report = layerReport with
                {
                    Layer = layerReport.Layer! with { Ordinal = -1 },
                },
            },
            requestId,
            layerQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            layerEnvelope with { Report = layerReport with { Page = new(0, 1, 1, 1, null) } },
            requestId,
            layerQuery));

        var tileQuery = new ReviewMapQuery(
            ReviewMapContract.TileOperation,
            "Maps/Town",
            "Buildings",
            3,
            4,
            null,
            null,
            null,
            null,
            0,
            1);
        ReviewMapReport tileReport = ReadyReport(
            tileQuery.Operation,
            assetName: "Maps/Town",
            tile: new ReviewMapTileReport(
                "Buildings",
                3,
                4,
                true,
                "static",
                "outdoors",
                42,
                "Alpha",
                null,
                null,
                1,
                2));
        var tileEnvelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            tileReport);
        Assert.True(ProjectReviewMapService.MatchesResponse(
            tileEnvelope,
            requestId,
            tileQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            tileEnvelope with
            {
                Report = tileReport with
                {
                    Tile = tileReport.Tile! with { Frames = [] },
                },
            },
            requestId,
            tileQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            tileEnvelope with
            {
                Report = tileReport with
                {
                    Tile = tileReport.Tile! with
                    {
                        TileIndex = ReviewMapContract.MaximumTileSheetTiles,
                    },
                },
            },
            requestId,
            tileQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            tileEnvelope with
            {
                Report = tileReport with
                {
                    Layer = layerReport.Layer,
                },
            },
            requestId,
            tileQuery));
    }

    [Fact]
    public void TileSheetAndWarpListsRequireGlobalOrdinalsAndSafeShapes()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var tileSheetQuery = new ReviewMapQuery(
            ReviewMapContract.TileSheetsOperation,
            "Maps/Town",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            1,
            1);
        var tileSheet = new ReviewMapTileSheetReport(
            1,
            "outdoors",
            "Maps/spring_outdoorsTileSheet",
            16,
            16,
            16,
            16,
            0,
            0,
            0,
            0,
            256,
            0);
        ReviewMapReport tileSheetReport = ReadyReport(
            tileSheetQuery.Operation,
            assetName: "Maps/Town",
            tileSheets: [tileSheet],
            page: new ReviewMapPage(1, 1, 1, 2, null));
        var tileSheetEnvelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            tileSheetReport);
        Assert.True(ProjectReviewMapService.MatchesResponse(
            tileSheetEnvelope,
            requestId,
            tileSheetQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            tileSheetEnvelope with
            {
                Report = tileSheetReport with
                {
                    TileSheets = [tileSheet with { Ordinal = 0 }],
                },
            },
            requestId,
            tileSheetQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            tileSheetEnvelope with
            {
                Report = tileSheetReport with
                {
                    TileSheets = [tileSheet with { TileCount = 255 }],
                },
            },
            requestId,
            tileSheetQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            tileSheetEnvelope with
            {
                Report = tileSheetReport with
                {
                    TileSheets = [tileSheet with { ImageSource = "C:\\outside.png" }],
                },
            },
            requestId,
            tileSheetQuery));

        var warpQuery = tileSheetQuery with
        {
            Operation = ReviewMapContract.WarpsOperation,
        };
        var warp = new ReviewMapWarpReport(
            1,
            "Warp",
            0,
            "playerAndNpc",
            1,
            2,
            "Farm",
            3,
            4);
        ReviewMapReport warpReport = ReadyReport(
            warpQuery.Operation,
            assetName: "Maps/Town",
            warps: [warp],
            page: new ReviewMapPage(1, 1, 1, 2, null));
        var warpEnvelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId,
            warpReport);
        Assert.True(ProjectReviewMapService.MatchesResponse(
            warpEnvelope,
            requestId,
            warpQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            warpEnvelope with
            {
                Report = warpReport with { Warps = [warp with { Ordinal = 0 }] },
            },
            requestId,
            warpQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            warpEnvelope with
            {
                Report = warpReport with { Warps = [warp with { Kind = "npc" }] },
            },
            requestId,
            warpQuery));
        Assert.False(ProjectReviewMapService.MatchesResponse(
            warpEnvelope with { Report = warpReport with { TileSheets = [] } },
            requestId,
            warpQuery));
    }

    [Fact]
    public void PropertyResponseAcceptsOnlyBoundedMatchingPrimitiveJsonValues()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewMapQuery(
            ReviewMapContract.PropertyOperation,
            "Maps/Town",
            null,
            null,
            null,
            ReviewMapContract.MapScope,
            ReviewMapContract.DirectSource,
            null,
            "Season",
            0,
            1);
        (string Type, JsonElement Value)[] validValues =
        [
            ("string", JsonSerializer.SerializeToElement("spring")),
            ("boolean", JsonSerializer.SerializeToElement(true)),
            ("integer", JsonSerializer.SerializeToElement(42)),
            ("float", JsonSerializer.SerializeToElement(1.25f)),
        ];

        foreach ((string type, JsonElement value) in validValues)
        {
            ReviewMapReport report = ReadyReport(
                query.Operation,
                assetName: "Maps/Town",
                property: new ReviewMapPropertyReport(
                    ReviewMapContract.MapScope,
                    ReviewMapContract.DirectSource,
                    null,
                    "Season",
                    type,
                    value));
            Assert.True(ProjectReviewMapService.MatchesResponse(
                new ReviewMapResponseEnvelope(
                    ReviewMapContract.SchemaVersion,
                    requestId,
                    report),
                requestId,
                query));
        }

        (string Type, JsonElement Value)[] invalidValues =
        [
            ("string", JsonSerializer.SerializeToElement(1)),
            ("integer", JsonSerializer.SerializeToElement(1.5)),
            ("float", JsonSerializer.SerializeToElement("1.5")),
            ("object", JsonSerializer.SerializeToElement(new { value = 1 })),
            ("string", JsonSerializer.SerializeToElement("line\nbreak")),
            ("string", JsonSerializer.SerializeToElement(
                new string('x', ReviewMapContract.MaximumPropertyValueBytes + 1))),
            ("string", default),
        ];
        foreach ((string type, JsonElement value) in invalidValues)
        {
            ReviewMapReport report = ReadyReport(
                query.Operation,
                assetName: "Maps/Town",
                property: new ReviewMapPropertyReport(
                    ReviewMapContract.MapScope,
                    ReviewMapContract.DirectSource,
                    null,
                    "Season",
                    type,
                    value));
            Assert.False(ProjectReviewMapService.MatchesResponse(
                new ReviewMapResponseEnvelope(
                    ReviewMapContract.SchemaVersion,
                    requestId,
                    report),
                requestId,
                query));
        }
    }

    private static ReviewMapReport ReadyReport(
        string operation,
        string? assetName = null,
        ReviewMapSummary? map = null,
        ReviewMapLayerReport? layer = null,
        ReviewMapTileReport? tile = null,
        ReviewMapPropertyReport? property = null,
        IReadOnlyList<ReviewMapLayerReport>? layers = null,
        IReadOnlyList<ReviewMapTileSheetReport>? tileSheets = null,
        IReadOnlyList<ReviewMapWarpReport>? warps = null,
        ReviewMapPage? page = null) =>
        new(
            ReviewMapContract.SchemaVersion,
            "ready",
            operation,
            "1.6.15",
            "1.6.15.24356",
            assetName,
            assetName is null ? null : "xTile.Map",
            map,
            layer,
            tile,
            property,
            null,
            layers,
            tileSheets,
            warps,
            page,
            null,
            []);

    private static byte[] SerializeWire(ReviewMapResponseEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, WireJsonOptions);

    private static JsonObject ParseWire(byte[] bytes) =>
        JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();

    private static void AssertInvalidWire(JsonNode node) =>
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewMapService.DeserializeResponse(
                Encoding.UTF8.GetBytes(node.ToJsonString())));

    private static ReviewMapReport BlockedReport(string operation) =>
        new(
            ReviewMapContract.SchemaVersion,
            "blocked",
            operation,
            "1.6.15",
            "1.6.15.24356",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [new ReviewMapProblem("mapBlocked", "The request was blocked safely.")]);
}
