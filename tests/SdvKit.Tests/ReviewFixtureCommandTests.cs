using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewFixtureCommandTests
{
    [Fact]
    public void ParserAcceptsTheExactFixtureSurface()
    {
        Assert.IsType<ReviewFixtureStatusRequest>(Parse("fixture", "status"));

        ReviewFixtureBuildingEnsureRequest building =
            Assert.IsType<ReviewFixtureBuildingEnsureRequest>(Parse(
                "fixture",
                "building",
                "ensure",
                "barn-a",
                "deluxe-barn",
                "16",
                "20"));
        Assert.Equal("barn-a", building.Alias);
        Assert.Equal(16, building.X);
        Assert.Equal(20, building.Y);

        ReviewFixtureObjectEnsureRequest item =
            Assert.IsType<ReviewFixtureObjectEnsureRequest>(Parse(
                "fixture",
                "object",
                "ensure",
                "barn-a",
                "(O)388"));
        Assert.Equal("(O)388", item.QualifiedItemId);

        Assert.IsType<ReviewFixtureObjectClearOwnedRequest>(Parse(
            "fixture",
            "object",
            "clear-owned",
            Guid.NewGuid().ToString("D")));
        Assert.IsType<ReviewFixtureAnimalEnsureRequest>(Parse(
            "fixture",
            "animal",
            "ensure",
            "barn-a",
            "white-cow"));
        Assert.IsType<ReviewFixtureEnterRequest>(Parse("fixture", "enter", "barn-a"));
        Assert.IsType<ReviewFixtureFarmRequest>(Parse("fixture", "farm"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("barn-a")]
    [InlineData("barn_2")]
    [InlineData("abcdefghijklmnopqrstuvwxyz123456")]
    public void AliasValidationAcceptsOnlyBoundedLowercaseAscii(string alias)
    {
        Assert.True(ReviewFixtureArguments.IsValidAlias(alias));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2barn")]
    [InlineData("Barn")]
    [InlineData("barn a")]
    [InlineData("barn.a")]
    [InlineData("bärn")]
    [InlineData("abcdefghijklmnopqrstuvwxyz1234567")]
    public void AliasValidationRejectsUnsafeOrAmbiguousValues(string alias)
    {
        Assert.False(ReviewFixtureArguments.IsValidAlias(alias));
    }

    [Theory]
    [InlineData("fixture", "Status")]
    [InlineData("Fixture", "status")]
    [InlineData("fixture", "building")]
    [InlineData("fixture", "farm", "extra")]
    public void ParserRejectsUnknownCaseAndArity(params string[] arguments)
    {
        Assert.False(ReviewFixtureArguments.TryParse(arguments, out _, out _));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("2147483648")]
    public void ParserRejectsInvalidBuildingCoordinates(string coordinate)
    {
        Assert.False(
            ReviewFixtureArguments.TryParse(
                [
                    "fixture",
                    "building",
                    "ensure",
                    "barn",
                    "deluxe-barn",
                    coordinate,
                    "20",
                ],
                out _,
                out _));
    }

    [Fact]
    public void EveryOperationFreshlyVerifiesBeforeDispatch()
    {
        var runtime = new FakeRuntime();
        ReviewFixtureRequest[] requests =
        [
            new ReviewFixtureStatusRequest(),
            new ReviewFixtureBuildingEnsureRequest("barn", 1, 2),
            new ReviewFixtureObjectEnsureRequest("barn", "(O)388"),
            new ReviewFixtureObjectClearOwnedRequest("barn"),
            new ReviewFixtureAnimalEnsureRequest("barn"),
            new ReviewFixtureEnterRequest("barn"),
            new ReviewFixtureFarmRequest(),
        ];

        foreach (ReviewFixtureRequest request in requests)
        {
            ReviewFixtureResult result = ReviewFixtureOperation.Execute(request, runtime);
            Assert.True(result.Succeeded, result.Message);
        }

        Assert.Equal(requests.Length, runtime.Verifications);
        Assert.Equal(requests.Length, runtime.Dispatches);
    }

    [Fact]
    public void FailedOrIncompleteVerificationNeverDispatches()
    {
        var failed = new FakeRuntime
        {
            Access = new ReviewFixtureAccess(false, false, null, null, "denied"),
        };
        var incomplete = new FakeRuntime
        {
            Access = new ReviewFixtureAccess(true, true, null, "single", "bad"),
        };

        Assert.False(
            ReviewFixtureOperation.Execute(new ReviewFixtureStatusRequest(), failed).Succeeded);
        Assert.False(
            ReviewFixtureOperation.Execute(new ReviewFixtureStatusRequest(), incomplete).Succeeded);
        Assert.Equal(0, failed.Dispatches);
        Assert.Equal(0, incomplete.Dispatches);
    }

    [Fact]
    public void FarmhandMayInspectAndWarpButCannotMutate()
    {
        var runtime = new FakeRuntime
        {
            Access = new ReviewFixtureAccess(
                true,
                false,
                Guid.NewGuid().ToString("N"),
                "farmhand",
                "verified"),
        };

        Assert.True(
            ReviewFixtureOperation.Execute(new ReviewFixtureStatusRequest(), runtime).Succeeded);
        Assert.True(
            ReviewFixtureOperation.Execute(new ReviewFixtureEnterRequest("barn"), runtime).Succeeded);
        Assert.True(
            ReviewFixtureOperation.Execute(new ReviewFixtureFarmRequest(), runtime).Succeeded);

        ReviewFixtureRequest[] mutations =
        [
            new ReviewFixtureBuildingEnsureRequest("barn", 1, 2),
            new ReviewFixtureObjectEnsureRequest("barn", "(O)388"),
            new ReviewFixtureObjectClearOwnedRequest("barn"),
            new ReviewFixtureAnimalEnsureRequest("barn"),
        ];
        foreach (ReviewFixtureRequest mutation in mutations)
        {
            Assert.False(ReviewFixtureOperation.Execute(mutation, runtime).Succeeded);
        }

        Assert.Equal(3, runtime.Dispatches);
    }

    [Fact]
    public void BuildingEnsureConfirmsOnlyOneExactOwnedAlias()
    {
        const string fixtureId = "fixture-a";
        var exact = new ReviewFixtureBuildingState(
            fixtureId,
            ReviewFixtureContract.DeluxeBarnBuildingType,
            16,
            20);

        Assert.Equal(
            ReviewFixtureEnsureDecision.Create,
            ReviewFixturePolicy.DecideBuildingEnsure([], fixtureId, 16, 20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Confirm,
            ReviewFixturePolicy.DecideBuildingEnsure([exact], fixtureId, 16, 20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact with { FixtureId = "other-fixture" }],
                fixtureId,
                16,
                20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact with { Type = "Barn" }],
                fixtureId,
                16,
                20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure([exact], fixtureId, 32, 20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure([exact, exact], fixtureId, 16, 20));
    }

    [Fact]
    public void ObjectEnsureIsStrictlyIdempotent()
    {
        Assert.Equal(
            ReviewFixtureEnsureDecision.Create,
            ReviewFixturePolicy.DecideObjectEnsure([], "(O)388"));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Confirm,
            ReviewFixturePolicy.DecideObjectEnsure(["(O)388"], "(O)388"));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideObjectEnsure(["(O)390"], "(O)388"));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideObjectEnsure(["(O)388", "(O)388"], "(O)388"));
    }

    [Fact]
    public void ClearOwnedSelectionExcludesFeedHopperUnmarkedAndOtherFixtureObjects()
    {
        const string fixtureId = "fixture-a";
        const string buildingId = "11111111-1111-1111-1111-111111111111";
        var objects = new[]
        {
            new { Id = "owned", Fixture = (string?)fixtureId, Building = (string?)buildingId },
            new { Id = "feed-hopper-(BC)99", Fixture = (string?)null, Building = (string?)null },
            new { Id = "unmarked", Fixture = (string?)null, Building = (string?)null },
            new { Id = "other-fixture", Fixture = (string?)"fixture-b", Building = (string?)buildingId },
            new { Id = "other-building", Fixture = (string?)fixtureId, Building = (string?)"22222222-2222-2222-2222-222222222222" },
        };

        string[] selected = objects
            .Where(item => ReviewFixturePolicy.IsOwnedObject(
                item.Fixture,
                item.Building,
                fixtureId,
                buildingId))
            .Select(item => item.Id)
            .ToArray();

        Assert.Equal(["owned"], selected);
    }

    [Fact]
    public void AnimalEnsureHonorsIdempotenceHomeAssignmentAndCapacity()
    {
        var exact = new ReviewFixtureAnimalState(
            ReviewFixtureContract.WhiteCowType,
            HasExactHome: true,
            HasExactAssignment: true);

        Assert.Equal(
            ReviewFixtureEnsureDecision.Create,
            ReviewFixturePolicy.DecideAnimalEnsure([], 0, 12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Confirm,
            ReviewFixturePolicy.DecideAnimalEnsure([exact], 1, 12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure([], 12, 12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { HasExactHome = false }],
                1,
                12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { HasExactAssignment = false }],
                1,
                12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { Type = "Brown Cow" }],
                1,
                12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure([exact, exact], 2, 12));
    }

    [Theory]
    [InlineData("295", "Twig", "Litter", 2)]
    [InlineData("343", "Stone", "Litter", 0)]
    [InlineData("450", "Stone", "Litter", 0)]
    [InlineData("590", "Artifact Spot", "asdf", 0)]
    [InlineData("674", "Weeds", "Litter", 2)]
    [InlineData("784", "Weeds", "Litter", 2)]
    public void BuildingPreflightAllowsOnlyExactNaturalBaselineObjectClutter(
        string itemId,
        string name,
        string type,
        int fragility)
    {
        Assert.True(ReviewFixturePolicy.IsDisposableObjectClutter(
            NaturalObject(itemId, name, type, fragility)));
    }

    [Fact]
    public void BuildingPreflightRejectsUnknownOrModifiedObjects()
    {
        ReviewFixtureObjectClutterState weeds = NaturalObject(
            "674",
            "Weeds",
            "Litter",
            2);

        Assert.False(ReviewFixturePolicy.IsDisposableObjectClutter(
            weeds with { ItemId = "388", Name = "Wood" }));
        Assert.False(ReviewFixturePolicy.IsDisposableObjectClutter(
            weeds with { Name = "Chest" }));
        Assert.False(ReviewFixturePolicy.IsDisposableObjectClutter(
            weeds with { Stack = 2 }));
        Assert.False(ReviewFixturePolicy.IsDisposableObjectClutter(
            weeds with { IsBigCraftable = true }));
        Assert.False(ReviewFixturePolicy.IsDisposableObjectClutter(
            weeds with { HasHeldObject = true }));
        Assert.False(ReviewFixturePolicy.IsDisposableObjectClutter(
            weeds with { HasModData = true }));
        Assert.False(ReviewFixturePolicy.IsDisposableObjectClutter(
            weeds with { Fragility = 0 }));
    }

    [Fact]
    public void BuildingPreflightAllowsOnlyUnownedGrassOrUntappedNonStumpTrees()
    {
        var grass = new ReviewFixtureTerrainClutterState(
            ReviewFixtureTerrainKind.Grass,
            IsTapped: false,
            IsStump: false,
            HasModData: false);
        var tree = grass with { Kind = ReviewFixtureTerrainKind.Tree };

        Assert.True(ReviewFixturePolicy.IsDisposableTerrainClutter(grass));
        Assert.True(ReviewFixturePolicy.IsDisposableTerrainClutter(tree));
        Assert.False(ReviewFixturePolicy.IsDisposableTerrainClutter(
            tree with { IsTapped = true }));
        Assert.False(ReviewFixturePolicy.IsDisposableTerrainClutter(
            tree with { IsStump = true }));
        Assert.False(ReviewFixturePolicy.IsDisposableTerrainClutter(
            tree with { HasModData = true }));
        Assert.False(ReviewFixturePolicy.IsDisposableTerrainClutter(
            tree with { Kind = ReviewFixtureTerrainKind.Other }));
    }

    [Fact]
    public void BuildingPreflightAllowsOnlyTheExactNaturalStumpClump()
    {
        var exact = new ReviewFixtureResourceClumpState(
            ParentSheetIndex: 600,
            Width: 2,
            Height: 2,
            HasModData: false);

        Assert.True(ReviewFixturePolicy.IsDisposableResourceClump(exact));
        Assert.False(ReviewFixturePolicy.IsDisposableResourceClump(
            exact with { ParentSheetIndex = 602 }));
        Assert.False(ReviewFixturePolicy.IsDisposableResourceClump(
            exact with { Width = 3 }));
        Assert.False(ReviewFixturePolicy.IsDisposableResourceClump(
            exact with { HasModData = true }));
    }

    [Theory]
    [InlineData("t", false, true)]
    [InlineData("true", false, true)]
    [InlineData("T", false, true)]
    [InlineData("TRUE", false, true)]
    [InlineData("", true, true)]
    [InlineData(null, true, true)]
    [InlineData("f", true, false)]
    [InlineData("F", true, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void BuildingPreflightRequiresBuildableOrDiggableMapProperty(
        string? buildableProperty,
        bool hasDiggableProperty,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReviewFixturePolicy.IsBuildableMapTile(
                buildableProperty,
                hasDiggableProperty));
    }

    [Fact]
    public void GameRuntimeSourceRetainsOwnershipAndSafetyBoundaries()
    {
        string source = ReadSource();

        Assert.Contains("skipSafetyChecks: true", source, StringComparison.Ordinal);
        Assert.Contains("TryValidateBuildingFootprint(farm, x, y", source, StringComparison.Ordinal);
        Assert.Contains("(long)x + buildingData.Size.X > back.LayerWidth", source, StringComparison.Ordinal);
        Assert.Contains("existing.occupiesTile(tile.X, tile.Y", source, StringComparison.Ordinal);
        Assert.Contains("IsDisposableObjectClutter(item)", source, StringComparison.Ordinal);
        Assert.Contains("IsDisposableTerrainClutter(terrain)", source, StringComparison.Ordinal);
        Assert.Contains("IsDisposableResourceClump(clump)", source, StringComparison.Ordinal);
        Assert.Contains("farm.farmers.Any(", source, StringComparison.Ordinal);
        Assert.Contains("farm.characters.Any(", source, StringComparison.Ordinal);
        Assert.Contains("farm.getAllFarmAnimals().Any(", source, StringComparison.Ordinal);
        Assert.Contains("farm.GetBuildableRectangle()", source, StringComparison.Ordinal);
        Assert.Contains("farm.isTilePlaceable(vector, itemIsPassable: false)", source, StringComparison.Ordinal);
        Assert.Contains("farm.GetFurnitureAt(vector) is not null", source, StringComparison.Ordinal);
        Assert.Contains("\"Buildable\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Diggable\"", source, StringComparison.Ordinal);
        Assert.Contains("placement.OnlyNeedsToBePassable", source, StringComparison.Ordinal);
        Assert.Contains("passableTiles.Contains(tile)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("farm.Objects.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("terrainFeatures.Remove", source, StringComparison.Ordinal);
        Assert.Contains("FinishConstruction(onGameStart: false)", source, StringComparison.Ordinal);
        Assert.Contains("ItemRegistry.Create<StardewValley.Object>", source, StringComparison.Ordinal);
        Assert.Contains("ItemRegistry.IsQualifiedItemId(qualifiedItemId)", source, StringComparison.Ordinal);
        Assert.Contains("ItemRegistry.Exists(qualifiedItemId)", source, StringComparison.Ordinal);
        Assert.Contains("indoors.tryPlaceObject(tile, item)", source, StringComparison.Ordinal);
        Assert.Contains("tile != warpSource", source, StringComparison.Ordinal);
        Assert.Contains("tile != naturalEntry", source, StringComparison.Ordinal);
        Assert.Contains("indoors.Objects.Remove(tile)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Objects.Clear", source, StringComparison.Ordinal);
        Assert.Contains("isTilePlaceable(tile, itemIsPassable: false)", source, StringComparison.Ordinal);
        Assert.Contains("animalHouse.adoptAnimal(animal)", source, StringComparison.Ordinal);
        Assert.Contains("existing.home?.id.Value == target.id.Value", source, StringComparison.Ordinal);
        Assert.Contains("animalHouse.animalsThatLiveHere.Contains(existing.myID.Value)", source, StringComparison.Ordinal);
        Assert.Contains("indoors.GetFirstPlayerWarp()", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(exit.TargetName, \"Farm\", StringComparison.Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("indoors.isTileOnMap(entry)", source, StringComparison.Ordinal);
        Assert.Contains("indoors.isTilePassable(entry)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("indoors.isTileLocationOpen(entry)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("indoors.IsTileOccupiedBy(entry)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("indoors.Objects.ContainsKey(entry)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("indoors.isTileOccupiedByFarmer(entry) is not null", source, StringComparison.Ordinal);
        Assert.Contains("mainPlayer={Context.IsMainPlayer}", source, StringComparison.Ordinal);
        Assert.Contains("multiplayer={Context.IsMultiplayer}", source, StringComparison.Ordinal);
        Assert.Contains("player={Game1.player.Name}", source, StringComparison.Ordinal);
        Assert.Contains("indoors?.mapPath.Value", source, StringComparison.Ordinal);
        Assert.Contains("players={players} objects={objects} animals={animals}", source, StringComparison.Ordinal);
        Assert.Contains("if (network.IsHost)", source, StringComparison.Ordinal);
        Assert.Contains("hostTestSave.TryVerifyReviewFixture(", source, StringComparison.Ordinal);
        Assert.Contains(ReviewFixtureContract.FixtureIdMarkerKey, source, StringComparison.Ordinal);
        Assert.Contains(ReviewFixtureContract.BuildingAliasMarkerKey, source, StringComparison.Ordinal);
        Assert.Contains(ReviewFixtureContract.ObjectMarkerKey, source, StringComparison.Ordinal);
        Assert.Contains(ReviewFixtureContract.AnimalKindMarkerKey, source, StringComparison.Ordinal);
    }

    [Fact]
    public void GameRuntimeConsumesTheReviewLaunchGuardBeforeAutomationState()
    {
        string source = ReadSource();
        int authorization = source.IndexOf(
            "public ReviewFixtureAccess VerifyExactReviewFixture()",
            StringComparison.Ordinal);
        int environment = source.IndexOf(
            "Environment.GetEnvironmentVariable(ReviewFixtureContract.ReviewEnvironmentName)",
            authorization,
            StringComparison.Ordinal);
        int expectedValue = source.IndexOf(
            "ReviewFixtureContract.ReviewEnvironmentValue",
            environment,
            StringComparison.Ordinal);
        int automation = source.IndexOf(
            "NetworkTwoAutomation? network = networkTwo();",
            expectedValue,
            StringComparison.Ordinal);

        Assert.True(authorization >= 0);
        Assert.True(environment > authorization);
        Assert.True(expectedValue > environment);
        Assert.True(automation > expectedValue);
    }

    private static ReviewFixtureRequest Parse(params string[] arguments)
    {
        Assert.True(
            ReviewFixtureArguments.TryParse(arguments, out ReviewFixtureRequest? request, out string error),
            error);
        return Assert.IsAssignableFrom<ReviewFixtureRequest>(request);
    }

    private static ReviewFixtureObjectClutterState NaturalObject(
        string itemId,
        string name,
        string type,
        int fragility) => new(
            itemId,
            name,
            type,
            Stack: 1,
            CanBeSetDown: true,
            CanBeGrabbed: true,
            IsSpawnedObject: false,
            IsQuestItem: false,
            IsBigCraftable: false,
            HasHeldObject: false,
            fragility,
            Price: 0,
            HasModData: false);

    private static string ReadSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                directory.FullName,
                "src",
                "SdvKit.AlwaysOn",
                "ReviewFixtureCommand.cs");
            if (File.Exists(path))
            {
                return File.ReadAllText(path).ReplaceLineEndings("\n");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }

    private sealed class FakeRuntime : IReviewFixtureRuntime
    {
        public ReviewFixtureAccess Access { get; init; } = new(
            true,
            true,
            Guid.NewGuid().ToString("N"),
            "single",
            "verified");

        public int Verifications { get; private set; }

        public int Dispatches { get; private set; }

        public ReviewFixtureAccess VerifyExactReviewFixture()
        {
            Verifications++;
            return Access;
        }

        public ReviewFixtureResult Status(ReviewFixtureAccess access) => Dispatched();

        public ReviewFixtureResult EnsureBuilding(
            ReviewFixtureAccess access,
            string alias,
            int x,
            int y) => Dispatched();

        public ReviewFixtureResult EnsureObject(
            ReviewFixtureAccess access,
            string building,
            string qualifiedItemId) => Dispatched();

        public ReviewFixtureResult ClearOwnedObjects(
            ReviewFixtureAccess access,
            string building) => Dispatched();

        public ReviewFixtureResult EnsureAnimal(
            ReviewFixtureAccess access,
            string building) => Dispatched();

        public ReviewFixtureResult Enter(
            ReviewFixtureAccess access,
            string building) => Dispatched();

        public ReviewFixtureResult Farm(ReviewFixtureAccess access) => Dispatched();

        private ReviewFixtureResult Dispatched()
        {
            Dispatches++;
            return new ReviewFixtureResult(true, "done");
        }
    }
}
