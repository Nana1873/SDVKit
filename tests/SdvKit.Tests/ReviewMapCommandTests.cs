using System.Text;
using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewMapCommandTests
{
    [Fact]
    public void InventoryClassifiesMapsNonMapsAndCoverageGapsDeterministically()
    {
        FakeMapSource source = Source(includeBroken: true);
        var query = Query(ReviewMapContract.AssetsOperation, offset: 0, limit: 2);

        ReviewMapReport first = ReviewMapOperation.Execute(query, source);
        ReviewMapReport second = ReviewMapOperation.Execute(query, source);

        Assert.Equal("blocked", first.State);
        Assert.Equal("mapCoverageIncomplete", Assert.Single(first.Problems).Code);
        Assert.Collection(
            first.Assets!,
            asset =>
            {
                Assert.Equal("Maps/Broken", asset.AssetName);
                Assert.Equal("gap", asset.Kind);
            },
            asset =>
            {
                Assert.Equal("Maps/Town", asset.AssetName);
                Assert.Equal("map", asset.Kind);
            });
        Assert.Equal(new ReviewMapPage(0, 2, 2, 3, 2), first.Page);
        Assert.Equal(
            new ReviewMapCoverageReport(3, 2, 1, 1, 1, 0, 1, 0),
            first.Coverage);
        Assert.Equal(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second));

        ReviewMapReport last = ReviewMapOperation.Execute(
            query with { Offset = 2 },
            source);
        ReviewMapAssetReport nonMap = Assert.Single(last.Assets!);
        Assert.Equal("Maps/ZTexture", nonMap.AssetName);
        Assert.Equal("nonMap", nonMap.Kind);
        Assert.Null(last.Page!.NextOffset);
    }

    [Fact]
    public void CompleteInventoryCountsKnownNonMapWithoutCreatingAGap()
    {
        FakeMapSource source = Source();
        Assert.True(source.AssetExistsForMapRequest("Maps/ZTexture"));

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.AssetsOperation),
            source);

        Assert.Equal("ready", report.State);
        Assert.True(report.Coverage!.Complete);
        Assert.Equal(2, report.Coverage.Classified);
        Assert.Equal(1, report.Coverage.MapAssets);
        Assert.Equal(1, report.Coverage.NonMapAssets);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void PhysicalMapInventoryUsesTheTypedActivePipelineContract()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var source = new FakeMapSource(
            ["Maps/TypedPhysical"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/TypedPhysical"] = new("xTile.Map", map),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>(),
            typedOnlyMapAssets: new HashSet<string>(StringComparer.Ordinal)
            {
                "Maps/TypedPhysical",
            });

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.AssetsOperation),
            source);

        Assert.Equal("ready", report.State);
        ReviewMapAssetReport asset = Assert.Single(report.Assets!);
        Assert.Equal("map", asset.Kind);
        Assert.True(asset.Supported);
        Assert.NotNull(asset.Map);
        Assert.True(report.Coverage!.Complete);
    }

    [Fact]
    public void TypedMapFailureCannotBeDowngradedToAGenericMapResult()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var source = new FakeMapSource(
            ["Maps/TypedFailure"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/TypedFailure"] = new("xTile.Map", map),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>(),
            typedLoadFailures: new HashSet<string>(StringComparer.Ordinal)
            {
                "Maps/TypedFailure",
            });

        ReviewMapReport inventory = ReviewMapOperation.Execute(
            Query(ReviewMapContract.AssetsOperation),
            source);
        ReviewMapReport exact = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/TypedFailure"),
            source);

        ReviewMapAssetReport gap = Assert.Single(inventory.Assets!);
        Assert.Equal("gap", gap.Kind);
        Assert.Equal("mapAssetLoadFailed", gap.ProblemCode);
        Assert.Equal("mapAssetLoadFailed", Assert.Single(exact.Problems).Code);
    }

    [Theory]
    [InlineData(ReviewMapContract.GetOperation)]
    [InlineData(ReviewMapContract.LayerOperation)]
    [InlineData(ReviewMapContract.TileOperation)]
    public void ExactStructuralReadsReturnOnlyTheSelectedShape(string operation)
    {
        ReviewMapQuery query = operation switch
        {
            ReviewMapContract.GetOperation => Query(operation, asset: "maps\\town"),
            ReviewMapContract.LayerOperation => Query(operation, asset: "Maps/Town", layer: "back-ground"),
            ReviewMapContract.TileOperation => Query(operation, asset: "Maps/Town", layer: "Back Ground", x: 1, y: 1),
            _ => throw new InvalidOperationException(),
        };

        ReviewMapReport report = ReviewMapOperation.Execute(query, Source());

        Assert.Equal("ready", report.State);
        Assert.Equal("Maps/Town", report.AssetName);
        if (operation == ReviewMapContract.GetOperation)
        {
            Assert.NotNull(report.Map);
            Assert.Null(report.Layers);
        }
        else if (operation == ReviewMapContract.LayerOperation)
        {
            Assert.Equal("Back Ground", report.Layer!.Id);
            Assert.Equal(0, report.Layer.Ordinal);
        }
        else
        {
            Assert.Equal("static", report.Tile!.Kind);
            Assert.Equal(1, report.Tile.DirectPropertyCount);
            Assert.Equal(1, report.Tile.TileIndexPropertyCount);
        }
    }

    [Theory]
    [InlineData(ReviewMapContract.LayersOperation, 2)]
    [InlineData(ReviewMapContract.TileSheetsOperation, 1)]
    [InlineData(ReviewMapContract.WarpsOperation, 2)]
    public void ListsPreserveStableOrderAndPagination(string operation, int total)
    {
        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(operation, asset: "Maps/Town", offset: 0, limit: 1),
            Source());

        Assert.Equal("ready", report.State);
        Assert.Equal(total, report.Page!.Total);
        Assert.Equal(total > 1 ? 1 : null, report.Page.NextOffset);
        if (operation == ReviewMapContract.LayersOperation)
        {
            Assert.Equal(0, Assert.Single(report.Layers!).Ordinal);
        }
        else if (operation == ReviewMapContract.TileSheetsOperation)
        {
            Assert.Equal(0, Assert.Single(report.TileSheets!).Ordinal);
        }
        else
        {
            ReviewMapWarpReport warp = Assert.Single(report.Warps!);
            Assert.Equal(0, warp.Ordinal);
            Assert.Equal("NPCWarp", warp.SourceProperty);
            Assert.Equal(0, warp.SourceIndex);
        }
    }

    [Fact]
    public void EmptyAndAnimatedTilesHaveStableBoundedReports()
    {
        FakeMapSource source = Source();
        ReviewMapReport empty = ReviewMapOperation.Execute(
            Query(ReviewMapContract.TileOperation, asset: "Maps/Town", layer: "Back Ground", x: 0, y: 0),
            source);
        ReviewMapReport animated = ReviewMapOperation.Execute(
            Query(ReviewMapContract.TileOperation, asset: "Maps/Town", layer: "Back Ground", x: 2, y: 1),
            source);

        Assert.False(empty.Tile!.Present);
        Assert.Null(empty.Tile.Frames);
        Assert.Equal("animated", animated.Tile!.Kind);
        Assert.Collection(
            animated.Tile.Frames!,
            frame =>
            {
                Assert.Equal(0, frame.Ordinal);
                Assert.Equal(1, frame.TileIndexPropertyCount);
            },
            frame =>
            {
                Assert.Equal(1, frame.Ordinal);
                Assert.Equal(1, frame.TileIndexPropertyCount);
            });
    }

    [Theory]
    [InlineData("map", null, null, null, "direct", null, "Outdoors", "string")]
    [InlineData("layer", "Back Ground", null, null, "direct", null, "LayerFlag", "boolean")]
    [InlineData("tile", "Back Ground", 1, 1, "direct", null, "Action", "string")]
    [InlineData("tile", "Back Ground", 1, 1, "tile-index", null, "Passable", "boolean")]
    [InlineData("tile", "Back Ground", 2, 1, "tile-index", 1, "Speed", "float")]
    public void ExactPropertyReadsPreserveScopeSourceFrameAndTypedJson(
        string scope,
        string? layer,
        int? x,
        int? y,
        string sourceKind,
        int? frameIndex,
        string property,
        string expectedType)
    {
        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(
                ReviewMapContract.PropertyOperation,
                asset: "Maps/Town",
                layer: layer,
                x: x,
                y: y,
                propertyScope: scope,
                propertySource: sourceKind,
                frameIndex: frameIndex,
                property: property),
            Source());

        Assert.Equal("ready", report.State);
        Assert.Equal(scope, report.Property!.Scope);
        Assert.Equal(sourceKind, report.Property.Source);
        Assert.Equal(frameIndex, report.Property.FrameIndex);
        Assert.Equal(expectedType, report.Property.Type);
        Assert.NotEqual(JsonValueKind.Undefined, report.Property.Value.ValueKind);
    }

    [Theory]
    [InlineData("Maps/Missing", null, null, "mapAssetUnavailableInGameVersion")]
    [InlineData("Data/Town", null, null, "mapAssetUnknown")]
    [InlineData("Maps/Town", "Missing", null, "mapLayerUnknown")]
    [InlineData("Maps/Town", "Back Ground", 4, "mapTileOutOfBounds")]
    public void UnknownAndOutOfBoundsSelectionsFailClosed(
        string asset,
        string? layer,
        int? x,
        string expectedCode)
    {
        ReviewMapQuery query = layer is null
            ? Query(ReviewMapContract.GetOperation, asset: asset)
            : Query(ReviewMapContract.TileOperation, asset: asset, layer: layer, x: x ?? 0, y: 0);

        ReviewMapReport report = ReviewMapOperation.Execute(query, Source());

        Assert.Equal("blocked", report.State);
        Assert.Equal(expectedCode, Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void ExactReadUsesTheTypedContractForAnActivePipelineOnlyMap()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var source = new FakeMapSource(
            [],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/ModOnly"] = new("xTile.Map", map),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>(),
            typedOnlyMapAssets: new HashSet<string>(StringComparer.Ordinal)
            {
                "Maps/ModOnly",
            });

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/ModOnly"),
            source);

        Assert.Equal("ready", report.State);
        Assert.Equal("Maps/ModOnly", report.AssetName);
        Assert.NotNull(report.Map);
    }

    [Fact]
    public void ExistingModOnlyMapLoadFailureIsNotReportedAsVersionAbsence()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var source = new FakeMapSource(
            [],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/UnsafeModOnly"] = new("xTile.Map", map),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>(),
            new HashSet<string>(StringComparer.Ordinal) { "Maps/UnsafeModOnly" });

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/UnsafeModOnly"),
            source);

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapAssetLoadFailed", Assert.Single(report.Problems).Code);
    }

    [Theory]
    [InlineData("Maps/../Town")]
    [InlineData("Maps/./Town")]
    [InlineData("Maps//Town")]
    [InlineData("Maps/   /Town")]
    [InlineData("Maps/---")]
    [InlineData("Maps/Town/")]
    [InlineData("Maps/Town.xnb")]
    [InlineData("Maps/Town.eo")]
    [InlineData("Maps/Town.fr-fr")]
    [InlineData("Maps/Town.custom")]
    public void NonCanonicalMapTokensFailBeforeNormalization(string asset)
    {
        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: asset),
            Source());

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapAssetUnknown", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void NonLocaleSuffixRemainsAValidExactMapName()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var source = new FakeMapSource(
            [],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/Area.no"] = new("xTile.Map", map),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/Area.no"),
            source);

        Assert.Equal("ready", report.State);
        Assert.Equal("Maps/Area.no", report.AssetName);
        Assert.NotNull(report.Map);
    }

    [Fact]
    public void AnimatedTileIndexPropertyRequiresExplicitValidFrame()
    {
        ReviewMapQuery query = Query(
            ReviewMapContract.PropertyOperation,
            asset: "Maps/Town",
            layer: "Back Ground",
            x: 2,
            y: 1,
            propertyScope: ReviewMapContract.TileScope,
            propertySource: ReviewMapContract.TileIndexSource,
            property: "Speed");

        ReviewMapReport report = ReviewMapOperation.Execute(query, Source());

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapPropertyFrameInvalid", Assert.Single(report.Problems).Code);
    }

    [Theory]
    [InlineData("1 2 Town 3", "mapWarpInvalid")]
    [InlineData("1 two Town 3 4", "mapWarpInvalid")]
    [InlineData("1 2 Town 3 2147483648", "mapWarpInvalid")]
    public void MalformedWarpGroupsFailAsOneCoverageGap(
        string value,
        string expectedProblem)
    {
        IReadOnlyList<ReviewMapWarpReport> warps = ReviewMapOperation.CaptureWarps(
            [Property("Warp", value)],
            out string? problem);

        Assert.Empty(warps);
        Assert.Equal(expectedProblem, problem);
    }

    [Fact]
    public void WarpParserUsesRuntimeOrderAndKeepsDuplicateIdentityStable()
    {
        IReadOnlyList<ReviewMapWarpReport> warps = ReviewMapOperation.CaptureWarps(
            [
                Property("Warp", "1 2 Town 3 4 1 2 Town 3 4"),
                Property("NPCWarp", "5 6 Farm 7 8"),
            ],
            out string? problem);

        Assert.Null(problem);
        Assert.Collection(
            warps,
            warp =>
            {
                Assert.Equal(0, warp.Ordinal);
                Assert.Equal("NPCWarp", warp.SourceProperty);
                Assert.Equal(0, warp.SourceIndex);
                Assert.Equal("npc", warp.Kind);
            },
            warp =>
            {
                Assert.Equal(1, warp.Ordinal);
                Assert.Equal("Warp", warp.SourceProperty);
                Assert.Equal(0, warp.SourceIndex);
                Assert.Equal("playerAndNpc", warp.Kind);
            },
            warp =>
            {
                Assert.Equal(2, warp.Ordinal);
                Assert.Equal("Warp", warp.SourceProperty);
                Assert.Equal(1, warp.SourceIndex);
            });
    }

    [Fact]
    public void NormalizedMapIdentityCollisionsFailBeforeLoadingEitherCandidate()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var source = new FakeMapSource(
            ["Maps/A-B", "Maps/A_B"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/A-B"] = new("xTile.Map", map),
                ["Maps/A_B"] = new("xTile.Map", map),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/A B"),
            source);

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapAssetAmbiguous", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void ExactPipelineAssetCollidingWithPhysicalAliasFailsClosed()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var source = new FakeMapSource(
            ["Maps/A-B"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/A-B"] = new("xTile.Map", map),
                ["Maps/A_B"] = new("xTile.Map", map),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/A_B"),
            source);

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapAssetAmbiguous", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void NormalizedNonMapIdentityCollisionsAreCoverageGaps()
    {
        var source = new FakeMapSource(
            ["Maps/A-B", "Maps/A_B"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/A-B"] = new("Texture2D", null),
                ["Maps/A_B"] = new("Texture2D", null),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.AssetsOperation),
            source);

        Assert.Equal("blocked", report.State);
        Assert.Equal(2, report.Coverage!.Unclassified);
        Assert.Equal(0, report.Coverage.Classified);
        Assert.All(
            report.Assets!,
            asset =>
            {
                Assert.Equal("gap", asset.Kind);
                Assert.Equal("mapAssetNormalizationCollision", asset.ProblemCode);
            });
    }

    [Fact]
    public void PhysicalInventoryBoundsDirectoriesAndNonXnbEntries()
    {
        using TemporaryDirectory temporary = new();
        string contentRoot = Path.Combine(temporary.Path, "Content");
        string mapRoot = Path.Combine(contentRoot, "Maps");
        Directory.CreateDirectory(Path.Combine(mapRoot, "Nested"));
        File.WriteAllText(Path.Combine(mapRoot, "one.txt"), string.Empty);
        File.WriteAllText(Path.Combine(mapRoot, "two.json"), "{}");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ReviewMapFileInventory.Discover(contentRoot, mapRoot, maximumVisitedEntries: 2));

        Assert.Contains("bounded entry maximum", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalInventoryExcludesLocaleSuffixesCaseInsensitively()
    {
        using TemporaryDirectory temporary = new();
        string contentRoot = Path.Combine(temporary.Path, "Content");
        string mapRoot = Path.Combine(contentRoot, "Maps");
        Directory.CreateDirectory(mapRoot);
        File.WriteAllText(Path.Combine(mapRoot, "Town.xnb"), string.Empty);
        File.WriteAllText(Path.Combine(mapRoot, "Town.fr-fr.xnb"), string.Empty);
        File.WriteAllText(Path.Combine(mapRoot, "Town.eo.xnb"), string.Empty);
        File.WriteAllText(Path.Combine(mapRoot, "Town.custom.xnb"), string.Empty);

        IReadOnlyList<string> discovered = ReviewMapFileInventory.Discover(
            contentRoot,
            mapRoot,
            isLocalizedAsset: assetName =>
                assetName.EndsWith(".eo", StringComparison.OrdinalIgnoreCase)
                || assetName.EndsWith(".fr-fr", StringComparison.OrdinalIgnoreCase)
                || assetName.EndsWith(".custom", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["Maps/Town"], discovered);
    }

    [Fact]
    public void UnsafeSnapshotAndOversizedRequestFailClosed()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        ReviewMapAssetSnapshot unsafeMap = map with
        {
            Summary = map.Summary with { DisplayWidth = 0 },
        };
        var unsafeSource = new FakeMapSource(
            ["Maps/Unsafe"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/Unsafe"] = new("xTile.Map", unsafeMap),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport unsafeReport = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/Unsafe"),
            unsafeSource);
        ReviewMapQuery inventoryQuery = Query(ReviewMapContract.AssetsOperation);
        ReviewMapReport unsafeInventory = ReviewMapOperation.Execute(
            inventoryQuery,
            unsafeSource);
        ReviewMapReport oversizedRequest = ReviewMapOperation.Execute(
            Query(
                ReviewMapContract.GetOperation,
                asset: "Maps/" + new string('a', ReviewMapContract.MaximumAssetLength)),
            Source());

        Assert.Equal("mapDimensionsInvalid", Assert.Single(unsafeReport.Problems).Code);
        ReviewMapAssetReport unsupported = Assert.Single(unsafeInventory.Assets!);
        Assert.Equal("map", unsupported.Kind);
        Assert.False(unsupported.Supported);
        Assert.Null(unsupported.Map);
        const string requestId = "0123456789abcdef0123456789abcdef";
        Assert.True(ProjectReviewMapService.MatchesResponse(
            new ReviewMapResponseEnvelope(
                ReviewMapContract.SchemaVersion,
                requestId,
                unsafeInventory),
            requestId,
            inventoryQuery));
        Assert.Equal("mapAssetInvalid", Assert.Single(oversizedRequest.Problems).Code);
    }

    [Fact]
    public void UndefinedPropertyJsonFailsClosedWithoutThrowing()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        ReviewMapPropertyValue[] invalidProperties =
        [
            new("Broken", "string", default, 0),
        ];
        ReviewMapAssetSnapshot invalidMap = map with
        {
            Summary = map.Summary with { PropertyCount = invalidProperties.Length },
            Properties = invalidProperties,
        };
        var source = new FakeMapSource(
            ["Maps/Invalid"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/Invalid"] = new("xTile.Map", invalidMap),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/Invalid"),
            source);

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapPropertyShapeInvalid", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void UnsafeInventoryAndMapIdentitiesFailBeforeNormalization()
    {
        string oversizedName = "Maps/" + new string(
            'a',
            ReviewMapContract.MaximumAssetLength + 1);
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var inventorySource = new FakeMapSource(
            [oversizedName],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal),
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());
        ReviewMapLayerSnapshot invalidLayer = map.Layers[0] with
        {
            Report = map.Layers[0].Report with
            {
                Id = new string('b', ReviewMapContract.MaximumIdentityLength + 1),
            },
        };
        ReviewMapAssetSnapshot invalidMap = map with
        {
            Layers = [invalidLayer, .. map.Layers.Skip(1)],
        };
        var mapSource = new FakeMapSource(
            ["Maps/Unsafe"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/Unsafe"] = new("xTile.Map", invalidMap),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport inventory = ReviewMapOperation.Execute(
            Query(ReviewMapContract.AssetsOperation),
            inventorySource);
        ReviewMapReport exact = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/Unsafe"),
            mapSource);

        ReviewMapAssetReport gap = Assert.Single(inventory.Assets!);
        Assert.Equal("invalid-map-asset-0000", gap.AssetName);
        Assert.DoesNotContain(oversizedName, JsonSerializer.Serialize(inventory), StringComparison.Ordinal);
        Assert.Equal("mapAssetNameInvalid", gap.ProblemCode);
        Assert.Equal("mapCoverageIncomplete", Assert.Single(inventory.Problems).Code);
        Assert.Equal("mapIdentityInvalid", Assert.Single(exact.Problems).Code);
        const string requestId = "0123456789abcdef0123456789abcdef";
        Assert.True(ProjectReviewMapService.MatchesResponse(
            new ReviewMapResponseEnvelope(
                ReviewMapContract.SchemaVersion,
                requestId,
                inventory),
            requestId,
            Query(ReviewMapContract.AssetsOperation)));
    }

    [Fact]
    public void WhitespacePropertyAndImageSourceFailClosedAsUnsupportedMaps()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        var whitespaceProperty = new ReviewMapPropertyValue(
            "   ",
            "string",
            JsonSerializer.SerializeToElement("value"),
            5);
        ReviewMapAssetSnapshot invalidPropertyMap = map with
        {
            Properties = [whitespaceProperty],
            Summary = map.Summary with { PropertyCount = 1 },
        };
        ReviewMapTileSheetReport invalidSheet = map.TileSheets[0] with
        {
            ImageSource = "   ",
        };
        ReviewMapAssetSnapshot invalidSheetMap = map with
        {
            TileSheets = [invalidSheet],
        };
        var source = new FakeMapSource(
            ["Maps/BadProperty", "Maps/BadSheet"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/BadProperty"] = new("xTile.Map", invalidPropertyMap),
                ["Maps/BadSheet"] = new("xTile.Map", invalidSheetMap),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapQuery inventoryQuery = Query(ReviewMapContract.AssetsOperation);
        ReviewMapReport inventory = ReviewMapOperation.Execute(inventoryQuery, source);
        ReviewMapReport exactPropertyMap = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/BadProperty"),
            source);
        ReviewMapReport exactSheetMap = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/BadSheet"),
            source);
        ReviewMapReport whitespaceQuery = ReviewMapOperation.Execute(
            Query(
                ReviewMapContract.PropertyOperation,
                asset: "Maps/Town",
                propertyScope: ReviewMapContract.MapScope,
                propertySource: ReviewMapContract.DirectSource,
                property: "   "),
            Source());

        Assert.Equal("blocked", inventory.State);
        Assert.Equal(2, inventory.Coverage!.Unsupported);
        Assert.All(
            inventory.Assets!,
            asset =>
            {
                Assert.Equal("map", asset.Kind);
                Assert.False(asset.Supported);
                Assert.Null(asset.Map);
            });
        Assert.Equal("mapPropertyShapeInvalid", Assert.Single(exactPropertyMap.Problems).Code);
        Assert.Equal("mapTileSheetShapeInvalid", Assert.Single(exactSheetMap.Problems).Code);
        Assert.Equal("mapPropertyInvalid", Assert.Single(whitespaceQuery.Problems).Code);
        const string requestId = "0123456789abcdef0123456789abcdef";
        Assert.True(ProjectReviewMapService.MatchesResponse(
            new ReviewMapResponseEnvelope(
                ReviewMapContract.SchemaVersion,
                requestId,
                inventory),
            requestId,
            inventoryQuery));
    }

    [Fact]
    public void EmptyNormalizedLayerIdentityCannotMatchAnotherPunctuationToken()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        ReviewMapLayerSnapshot invalidLayer = map.Layers[0] with
        {
            Report = map.Layers[0].Report with { Id = "---" },
        };
        ReviewMapAssetSnapshot invalidMap = map with
        {
            Layers = [invalidLayer, .. map.Layers.Skip(1)],
        };
        var source = new FakeMapSource(
            ["Maps/InvalidIdentity"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/InvalidIdentity"] = new("xTile.Map", invalidMap),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(
                ReviewMapContract.LayerOperation,
                asset: "Maps/InvalidIdentity",
                layer: "!!!"),
            source);

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapIdentityInvalid", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void AggregateMapPropertyPayloadFailsClosedBeforeItCanGrowUnbounded()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        string boundedValue = new('a', ReviewMapContract.MaximumPropertyValueBytes);
        ReviewMapPropertyValue[] properties = Enumerable
            .Range(0, (ReviewMapContract.MaximumPropertyPayloadBytes / boundedValue.Length) + 1)
            .Select(index => Property($"Property{index}", boundedValue))
            .ToArray();
        ReviewMapAssetSnapshot oversized = map with
        {
            Summary = map.Summary with { PropertyCount = properties.Length },
            Properties = properties,
        };
        var source = new FakeMapSource(
            ["Maps/Oversized"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/Oversized"] = new("xTile.Map", oversized),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>());

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(ReviewMapContract.GetOperation, asset: "Maps/Oversized"),
            source);

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapPropertyPayloadTooLarge", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void AggregateAnimatedTilePropertyPayloadFailsClosed()
    {
        ReviewMapAssetSnapshot map = Source().LoadAsset("Maps/Town").Map!;
        string boundedValue = new('a', ReviewMapContract.MaximumPropertyValueBytes);
        ReviewMapTileFrameSnapshot[] frames = Enumerable
            .Range(0, (ReviewMapContract.MaximumPropertyPayloadBytes / boundedValue.Length) + 1)
            .Select(index =>
            {
                ReviewMapPropertyValue property = Property($"FrameProperty{index}", boundedValue);
                return new ReviewMapTileFrameSnapshot(
                    new ReviewMapTileFrameReport(index, "outdoors", index % 16, "Alpha", 1),
                    [property]);
            })
            .ToArray();
        var tile = new ReviewMapTileSnapshot(
            new ReviewMapTileReport(
                "Back Ground",
                2,
                1,
                true,
                "animated",
                null,
                null,
                null,
                100,
                frames.Select(frame => frame.Report).ToArray(),
                0,
                0),
            [],
            [],
            frames);
        var source = new FakeMapSource(
            ["Maps/Town"],
            new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
            {
                ["Maps/Town"] = new("xTile.Map", map),
            },
            new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>
            {
                [("Back Ground", 2, 1)] = tile,
            });

        ReviewMapReport report = ReviewMapOperation.Execute(
            Query(
                ReviewMapContract.TileOperation,
                asset: "Maps/Town",
                layer: "Back Ground",
                x: 2,
                y: 1),
            source);

        Assert.Equal("blocked", report.State);
        Assert.Equal("mapTilePropertyPayloadTooLarge", Assert.Single(report.Problems).Code);
    }

    private static ReviewMapQuery Query(
        string operation,
        string? asset = null,
        string? layer = null,
        int? x = null,
        int? y = null,
        string? propertyScope = null,
        string? propertySource = null,
        int? frameIndex = null,
        string? property = null,
        int offset = 0,
        int? limit = null) =>
        new(
            operation,
            asset,
            layer,
            x,
            y,
            propertyScope,
            propertySource,
            frameIndex,
            property,
            offset,
            limit ?? (operation is ReviewMapContract.AssetsOperation
                or ReviewMapContract.LayersOperation
                or ReviewMapContract.TileSheetsOperation
                or ReviewMapContract.WarpsOperation
                    ? ReviewMapContract.DefaultPageLimit
                    : 1));

    private static FakeMapSource Source(bool includeBroken = false)
    {
        ReviewMapPropertyValue[] mapProperties = [Property("Outdoors", "yes")];
        ReviewMapLayerSnapshot[] layers =
        [
            new(
                new ReviewMapLayerReport(0, "Back Ground", 4, 3, 16, 16, true, 1),
                [Property("LayerFlag", true)]),
            new(
                new ReviewMapLayerReport(1, "Buildings", 4, 3, 16, 16, true, 0),
                []),
        ];
        ReviewMapTileSheetReport[] sheets =
        [new(0, "outdoors", "Maps/spring_outdoorsTileSheet", 4, 4, 16, 16, 0, 0, 0, 0, 16, 0)];
        ReviewMapWarpReport[] warps =
        [
            new(0, "NPCWarp", 0, "npc", 1, 2, "Town", 3, 4),
            new(1, "Warp", 0, "playerAndNpc", 5, 6, "Farm", 7, 8),
        ];
        var map = new ReviewMapAssetSnapshot(
            new ReviewMapSummary(64, 48, 2, 1, 2, 1),
            layers,
            sheets,
            warps,
            mapProperties);
        var assets = new Dictionary<string, ReviewMapLoadedAsset>(StringComparer.Ordinal)
        {
            ["Maps/Town"] = new("xTile.Map", map),
            ["Maps/ZTexture"] = new("Microsoft.Xna.Framework.Graphics.Texture2D", null),
        };
        string[] discovered = includeBroken
            ? ["Maps/ZTexture", "Maps/Town", "Maps/Broken"]
            : ["Maps/ZTexture", "Maps/Town"];
        var tiles = new Dictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot>
        {
            [("Back Ground", 0, 0)] = new(
                new ReviewMapTileReport("Back Ground", 0, 0, false, null, null, null, null, null, null, 0, 0),
                [],
                [],
                []),
            [("Back Ground", 1, 1)] = new(
                new ReviewMapTileReport("Back Ground", 1, 1, true, "static", "outdoors", 2, "Alpha", null, null, 1, 1),
                [Property("Action", "Door")],
                [Property("Passable", true)],
                []),
            [("Back Ground", 2, 1)] = AnimatedTile(),
        };
        return new FakeMapSource(discovered, assets, tiles);
    }

    private static ReviewMapTileSnapshot AnimatedTile()
    {
        ReviewMapTileFrameSnapshot[] frames =
        [
            new(new ReviewMapTileFrameReport(0, "outdoors", 3, "Alpha", 1), [Property("Speed", 1)]),
            new(new ReviewMapTileFrameReport(1, "outdoors", 4, "Alpha", 1), [Property("Speed", 1.5f)]),
        ];
        return new ReviewMapTileSnapshot(
            new ReviewMapTileReport(
                "Back Ground",
                2,
                1,
                true,
                "animated",
                null,
                null,
                null,
                100,
                frames.Select(frame => frame.Report).ToArray(),
                1,
                0),
            [Property("Action", "Animate")],
            [],
            frames);
    }

    private static ReviewMapPropertyValue Property<T>(string name, T value)
    {
        JsonElement element = JsonSerializer.SerializeToElement(value);
        string type = value switch
        {
            string => "string",
            bool => "boolean",
            int => "integer",
            float => "float",
            _ => throw new InvalidOperationException(),
        };
        return new ReviewMapPropertyValue(
            name,
            type,
            element,
            value is string text
                ? Encoding.UTF8.GetByteCount(text)
                : Encoding.UTF8.GetByteCount(element.GetRawText()));
    }

    private sealed class FakeMapSource(
        IReadOnlyList<string> discovered,
        IReadOnlyDictionary<string, ReviewMapLoadedAsset> assets,
        IReadOnlyDictionary<(string Layer, int X, int Y), ReviewMapTileSnapshot> tiles,
        IReadOnlySet<string>? loadFailures = null,
        IReadOnlySet<string>? typedOnlyMapAssets = null,
        IReadOnlySet<string>? typedLoadFailures = null)
        : IReviewMapSource
    {
        public string GameVersion => "1.6.15";

        public string GameFileVersion => "1.6.15.24356";

        public IReadOnlyList<string> DiscoverCanonicalAssetNames() => discovered;

        public ReviewMapAssetIdentity CanonicalizeAssetName(string assetName)
        {
            string normalized = assetName.Replace('\\', '/').Trim();
            string? locale = normalized.EndsWith(".fr-fr", StringComparison.OrdinalIgnoreCase)
                ? "fr-fr"
                : normalized.EndsWith(".eo", StringComparison.OrdinalIgnoreCase)
                ? "eo"
                : normalized.EndsWith(".custom", StringComparison.OrdinalIgnoreCase)
                    ? "custom"
                    : null;
            if (locale is not null)
            {
                return new ReviewMapAssetIdentity(
                    normalized,
                    normalized[..^(locale.Length + 1)],
                    locale);
            }

            return new ReviewMapAssetIdentity(normalized, normalized, null);
        }

        public bool AssetExistsForMapRequest(string assetName) =>
            discovered.Contains(assetName, StringComparer.Ordinal)
            || (assets.TryGetValue(assetName, out ReviewMapLoadedAsset? asset)
                && asset.Map is not null);

        public ReviewMapLoadedAsset LoadAsset(string assetName) =>
            typedOnlyMapAssets?.Contains(assetName) == true
                ? throw new InvalidDataException("fixture requires a typed map request")
                : loadFailures?.Contains(assetName) == true
                ? throw new InvalidDataException("fixture load failed")
                : assets.TryGetValue(assetName, out ReviewMapLoadedAsset? asset)
                ? asset
                : throw new InvalidDataException("fixture load failed");

        public ReviewMapLoadedAsset LoadMapAsset(string assetName)
        {
            if (loadFailures?.Contains(assetName) == true
                || typedLoadFailures?.Contains(assetName) == true)
            {
                throw new InvalidDataException("fixture load failed");
            }
            if (!assets.TryGetValue(assetName, out ReviewMapLoadedAsset? asset))
            {
                throw new InvalidDataException("fixture typed map load failed");
            }
            if (asset.Map is null)
            {
                throw new InvalidCastException("fixture is a physical non-map XNB");
            }

            return asset;
        }

        public ReviewMapTileSnapshot ReadTile(string assetName, string layerId, int x, int y) =>
            tiles.TryGetValue((layerId, x, y), out ReviewMapTileSnapshot? tile)
                ? tile
                : throw new InvalidDataException("fixture tile unavailable");
    }
}
