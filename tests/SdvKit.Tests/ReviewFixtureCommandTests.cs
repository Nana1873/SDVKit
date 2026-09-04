using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

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
        Assert.Equal("deluxe-barn", building.Kind);
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
        ReviewFixtureAnimalEnsureRequest animal =
            Assert.IsType<ReviewFixtureAnimalEnsureRequest>(Parse(
            "fixture",
            "animal",
            "ensure",
            "barn-a",
            "white-cow"));
        Assert.Equal("white-cow", animal.Kind);
        Assert.IsType<ReviewFixtureEnterRequest>(Parse("fixture", "enter", "barn-a"));
        ReviewFixtureEnterRequest greenhouse = Assert.IsType<ReviewFixtureEnterRequest>(
            Parse("fixture", "enter", "greenhouse"));
        Assert.Equal(ReviewFixtureContract.GreenhouseTarget, greenhouse.Building);
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
    [InlineData("fixture", "enter", "Greenhouse")]
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

    [Theory]
    [InlineData("deluxe-barn", "Deluxe Barn", "deluxe-barn")]
    [InlineData("DELUXE-BARN", "Deluxe Barn", "deluxe-barn")]
    [InlineData("coop", "Coop", "coop")]
    [InlineData("white-cow", "White Cow", "white-cow")]
    [InlineData("White_Chicken", "White Chicken", "white-chicken")]
    public void KindResolverUsesStableCanonicalIdsThroughOneNormalizedPath(
        string input,
        string expectedId,
        string expectedToken)
    {
        Assert.True(
            ReviewFixtureKindResolver.TryResolve(
                input,
                ["Deluxe Barn", "Coop", "White Cow", "White Chicken"],
                "fixture",
                out ReviewFixtureKindResolution? resolved,
                out string error),
            error);
        Assert.Equal(expectedId, resolved!.CanonicalId);
        Assert.Equal(expectedToken, resolved.CanonicalToken);
    }

    [Fact]
    public void KindResolverRejectsUnknownAndLocalizedDisplayNames()
    {
        string[] canonicalIds = ["Deluxe Barn", "Coop"];

        Assert.False(ReviewFixtureKindResolver.TryResolve(
            "shed-that-does-not-exist",
            canonicalIds,
            "building",
            out _,
            out string unknownError));
        Assert.Contains("Canonical candidates:", unknownError, StringComparison.Ordinal);
        Assert.False(ReviewFixtureKindResolver.TryResolve(
            "Hühnerstall",
            canonicalIds,
            "building",
            out _,
            out string localizedError));
        Assert.Contains("no unambiguous building kind", localizedError, StringComparison.Ordinal);
    }

    [Fact]
    public void KindResolverRejectsNormalizationCollisionsBeforeSelection()
    {
        Assert.False(ReviewFixtureKindResolver.TryResolve(
            "future-building",
            ["Future Building", "future_building"],
            "building",
            out _,
            out string error));
        Assert.Contains("ambiguous", error, StringComparison.Ordinal);
        Assert.Contains("Future Building", error, StringComparison.Ordinal);
        Assert.Contains("future_building", error, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOperationFreshlyVerifiesBeforeDispatch()
    {
        var runtime = new FakeRuntime();
        ReviewFixtureRequest[] requests =
        [
            new ReviewFixtureStatusRequest(),
            new ReviewFixtureBuildingEnsureRequest("barn", "deluxe-barn", 1, 2),
            new ReviewFixtureObjectEnsureRequest("barn", "(O)388"),
            new ReviewFixtureObjectClearOwnedRequest("barn"),
            new ReviewFixtureAnimalEnsureRequest("barn", "white-cow"),
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
    public void RequestBindingIsRecheckedImmediatelyBeforeMutation()
    {
        const string launchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string fixtureId = "fixture-a";
        const string saveId = "SDVKit_123";
        var runtime = new FakeRuntime
        {
            Access = new ReviewFixtureAccess(
                true,
                true,
                fixtureId,
                "single",
                "verified",
                launchId,
                LiveLabState.SingleTopology,
                saveId),
        };
        var request = new ReviewFixtureBuildingEnsureRequest(
            "barn",
            "deluxe-barn",
            1,
            2);
        var changed = new ReviewFixtureRequestBinding(
            launchId,
            LiveLabState.SingleTopology,
            null,
            fixtureId,
            "SDVKit_other");

        ReviewFixtureExecution rejected = ReviewFixtureOperation.ExecuteBound(
            request,
            runtime,
            changed);

        Assert.False(rejected.Result.Succeeded);
        Assert.Equal("fixtureBindingChanged", rejected.Problem?.Code);
        Assert.Equal(1, runtime.Verifications);
        Assert.Equal(0, runtime.Dispatches);

        ReviewFixtureExecution accepted = ReviewFixtureOperation.ExecuteBound(
            request,
            runtime,
            changed with { SaveId = saveId });
        Assert.True(accepted.Result.Succeeded);
        Assert.Equal(2, runtime.Verifications);
        Assert.Equal(1, runtime.Dispatches);
    }

    [Fact]
    public void NavigationCompletesOnlyAfterWarpCallbackAndReturnsActualPosition()
    {
        const string launchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string fixtureId = "fixture-a";
        const string saveId = "SDVKit_123";
        var runtime = new FakeRuntime
        {
            Access = new ReviewFixtureAccess(
                true,
                true,
                fixtureId,
                "single",
                "verified",
                launchId,
                LiveLabState.SingleTopology,
                saveId),
            DeferNavigation = true,
            NavigationResult = new ReviewFixtureResult(
                true,
                "warp completed",
                Navigation: new ReviewFixtureNavigationReport(
                    "Barn-actual",
                    8,
                    9,
                    Changed: true)),
        };
        var binding = new ReviewFixtureRequestBinding(
            launchId,
            LiveLabState.SingleTopology,
            null,
            fixtureId,
            saveId);
        ReviewFixtureExecution? completed = null;
        var completionCount = 0;

        ReviewFixtureNavigationOperation.ExecuteBound(
            new ReviewFixtureEnterRequest("barn"),
            runtime,
            binding,
            result =>
            {
                completionCount++;
                completed = result;
            });

        Assert.Null(completed);
        Assert.Equal(1, runtime.Verifications);
        Assert.Equal(1, runtime.Dispatches);

        runtime.CompleteNavigation();
        runtime.CompleteNavigation();

        ReviewFixtureExecution execution = Assert.IsType<ReviewFixtureExecution>(completed);
        Assert.True(execution.Result.Succeeded);
        Assert.Equal("Barn-actual", execution.Result.Navigation?.LocationId);
        Assert.Equal(8, execution.Result.Navigation?.TileX);
        Assert.Equal(9, execution.Result.Navigation?.TileY);
        Assert.Equal(2, runtime.Verifications);
        Assert.Equal(1, completionCount);
    }

    [Fact]
    public void NavigationCompletionFailsClosedWhenFixtureBindingChangesDuringWarp()
    {
        const string launchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string fixtureId = "fixture-a";
        const string saveId = "SDVKit_123";
        var runtime = new FakeRuntime
        {
            Access = new ReviewFixtureAccess(
                true,
                true,
                fixtureId,
                "single",
                "verified",
                launchId,
                LiveLabState.SingleTopology,
                saveId),
            DeferNavigation = true,
            NavigationResult = new ReviewFixtureResult(
                true,
                "warp completed",
                Navigation: new ReviewFixtureNavigationReport(
                    "Barn-actual",
                    8,
                    9,
                    Changed: true)),
        };
        var binding = new ReviewFixtureRequestBinding(
            launchId,
            LiveLabState.SingleTopology,
            null,
            fixtureId,
            saveId);
        ReviewFixtureExecution? completed = null;

        ReviewFixtureNavigationOperation.ExecuteBound(
            new ReviewFixtureEnterRequest("barn"),
            runtime,
            binding,
            result => completed = result);
        runtime.Access = runtime.Access with { SaveId = "SDVKit_other" };
        runtime.CompleteNavigation();

        ReviewFixtureExecution execution = Assert.IsType<ReviewFixtureExecution>(completed);
        Assert.False(execution.Result.Succeeded);
        Assert.Null(execution.Result.Navigation);
        Assert.Equal("fixtureBindingChanged", execution.Problem?.Code);
        Assert.Contains("completed warp result", execution.Result.Message, StringComparison.Ordinal);
        Assert.Equal(2, runtime.Verifications);
    }

    [Fact]
    public void IdempotentNavigationCanCompleteSynchronouslyAfterSecondVerification()
    {
        const string launchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string fixtureId = "fixture-a";
        const string saveId = "SDVKit_123";
        var runtime = new FakeRuntime
        {
            Access = new ReviewFixtureAccess(
                true,
                true,
                fixtureId,
                "single",
                "verified",
                launchId,
                LiveLabState.SingleTopology,
                saveId),
            NavigationResult = new ReviewFixtureResult(
                true,
                "already there",
                Navigation: new ReviewFixtureNavigationReport(
                    "Farm",
                    64,
                    15,
                    Changed: false)),
        };
        var binding = new ReviewFixtureRequestBinding(
            launchId,
            LiveLabState.SingleTopology,
            null,
            fixtureId,
            saveId);
        ReviewFixtureExecution? completed = null;

        ReviewFixtureNavigationOperation.ExecuteBound(
            new ReviewFixtureFarmRequest(),
            runtime,
            binding,
            result => completed = result);

        ReviewFixtureExecution execution = Assert.IsType<ReviewFixtureExecution>(completed);
        Assert.True(execution.Result.Succeeded);
        Assert.False(execution.Result.Navigation?.Changed);
        Assert.Equal(2, runtime.Verifications);
        Assert.Equal(1, runtime.Dispatches);
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
            ReviewFixtureOperation.Execute(
                new ReviewFixtureEnterRequest(ReviewFixtureContract.GreenhouseTarget),
                runtime).Succeeded);
        Assert.True(
            ReviewFixtureOperation.Execute(new ReviewFixtureFarmRequest(), runtime).Succeeded);

        ReviewFixtureRequest[] mutations =
        [
            new ReviewFixtureBuildingEnsureRequest("barn", "deluxe-barn", 1, 2),
            new ReviewFixtureObjectEnsureRequest("barn", "(O)388"),
            new ReviewFixtureObjectClearOwnedRequest("barn"),
            new ReviewFixtureAnimalEnsureRequest("barn", "white-cow"),
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
        const string buildingType = "Deluxe Barn";
        var exact = new ReviewFixtureBuildingState(
            fixtureId,
            buildingType,
            16,
            20);

        Assert.Equal(
            ReviewFixtureEnsureDecision.Create,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [], fixtureId, buildingType, 16, 20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Confirm,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact], fixtureId, buildingType, 16, 20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact with { FixtureId = "other-fixture" }],
                fixtureId,
                buildingType,
                16,
                20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact with { Type = "Barn" }],
                fixtureId,
                buildingType,
                16,
                20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact], fixtureId, buildingType, 32, 20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact, exact], fixtureId, buildingType, 16, 20));
    }

    [Fact]
    public void CoopBuildingEnsureUsesTheSameGenericIdempotencePolicy()
    {
        Assert.True(ReviewFixtureKindResolver.TryResolve(
            "CoOp",
            ["Deluxe Barn", "Coop"],
            "building",
            out ReviewFixtureKindResolution? resolved,
            out string error), error);
        var exact = new ReviewFixtureBuildingState(
            "fixture-a",
            resolved!.CanonicalId,
            24,
            18);

        Assert.Equal(
            ReviewFixtureEnsureDecision.Create,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [], "fixture-a", resolved.CanonicalId, 24, 18));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Confirm,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact], "fixture-a", resolved.CanonicalId, 24, 18));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact], "fixture-a", "Deluxe Barn", 24, 18));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [exact], "fixture-a", resolved.CanonicalId, 25, 18));
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
        const string animalKind = "white-cow";
        const string animalType = "White Cow";
        var exact = new ReviewFixtureAnimalState(
            animalKind,
            animalType,
            HasExactHome: true,
            HasExactAssignment: true);

        Assert.Equal(
            ReviewFixtureEnsureDecision.Create,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [], animalKind, animalType, 0, 12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Confirm,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact], animalKind, animalType, 1, 12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [], animalKind, animalType, 12, 12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { HasExactHome = false }],
                animalKind,
                animalType,
                1,
                12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { HasExactAssignment = false }],
                animalKind,
                animalType,
                1,
                12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { Type = "Brown Cow" }],
                animalKind,
                animalType,
                1,
                12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { Kind = "white-chicken" }],
                animalKind,
                animalType,
                1,
                12));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact, exact], animalKind, animalType, 2, 12));
    }

    [Fact]
    public void AnimalCompatibilityUsesCanonicalHouseAndBuildingOccupantTypes()
    {
        Assert.True(ReviewFixturePolicy.IsAnimalHouseCompatible(
            "Coop",
            ["Coop"]));
        Assert.True(ReviewFixturePolicy.IsAnimalHouseCompatible(
            "Barn",
            ["Barn"]));
        Assert.False(ReviewFixturePolicy.IsAnimalHouseCompatible(
            "Coop",
            ["Barn"]));
        Assert.False(ReviewFixturePolicy.IsAnimalHouseCompatible(
            "Barn",
            ["Coop"]));
        Assert.False(ReviewFixturePolicy.IsAnimalHouseCompatible(null, ["Coop"]));
    }

    [Fact]
    public void WhiteChickenEnsureUsesTheSameGenericHomeAndTypePolicy()
    {
        Assert.True(ReviewFixtureKindResolver.TryResolve(
            "WHITE-CHICKEN",
            ["White Cow", "White Chicken"],
            "animal",
            out ReviewFixtureKindResolution? resolved,
            out string error), error);
        var exact = new ReviewFixtureAnimalState(
            resolved!.CanonicalToken,
            resolved.CanonicalId,
            HasExactHome: true,
            HasExactAssignment: true);

        Assert.True(ReviewFixturePolicy.IsAnimalHouseCompatible("Coop", ["Coop"]));
        Assert.False(ReviewFixturePolicy.IsAnimalHouseCompatible("Coop", ["Barn"]));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Confirm,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact],
                resolved.CanonicalToken,
                resolved.CanonicalId,
                assignedAnimalCount: 1,
                animalCapacity: 4));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { Type = "White Cow" }],
                resolved.CanonicalToken,
                resolved.CanonicalId,
                assignedAnimalCount: 1,
                animalCapacity: 4));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [exact with { HasExactHome = false }],
                resolved.CanonicalToken,
                resolved.CanonicalId,
                assignedAnimalCount: 1,
                animalCapacity: 4));
    }

    [Fact]
    public void BuildingPlacementAreaCombinesFootprintAdditionalAreasAndHumanDoorAccess()
    {
        Assert.True(ReviewFixturePolicy.TryCreateBuildingPlacementArea(
            x: 10,
            y: 20,
            width: 2,
            height: 2,
            additionalAreas:
            [
                new ReviewFixtureAdditionalPlacementArea(
                    X: -1,
                    Y: 2,
                    Width: 4,
                    Height: 1,
                    OnlyNeedsToBePassable: false),
                new ReviewFixtureAdditionalPlacementArea(
                    X: 2,
                    Y: 0,
                    Width: 1,
                    Height: 2,
                    OnlyNeedsToBePassable: true),
            ],
            humanDoor: new ReviewFixtureTile(0, 1),
            mapWidth: 100,
            mapHeight: 100,
            out ReviewFixtureBuildingPlacementArea? area,
            out string error), error);

        ReviewFixtureTile[] expected =
        [
            new(10, 20),
            new(11, 20),
            new(12, 20),
            new(10, 21),
            new(11, 21),
            new(12, 21),
            new(9, 22),
            new(10, 22),
            new(11, 22),
            new(12, 22),
        ];
        Assert.Equal(expected, area!.Tiles);
        Assert.True(area.IsFootprint(new ReviewFixtureTile(10, 20)));
        Assert.False(area.IsFootprint(new ReviewFixtureTile(12, 20)));
        Assert.True(area.MustBePassable(new ReviewFixtureTile(12, 20)));
        Assert.False(area.MustBeBuildable(new ReviewFixtureTile(12, 20)));
        Assert.True(area.MustBePassable(new ReviewFixtureTile(10, 22)));
        Assert.True(area.MustBeBuildable(new ReviewFixtureTile(10, 22)));
    }

    [Fact]
    public void BuildingPlacementAreaSelectionIgnoresObjectIdentityAndModData()
    {
        Assert.True(ReviewFixturePolicy.TryCreateBuildingPlacementArea(
            x: 16,
            y: 20,
            width: 2,
            height: 2,
            additionalAreas: null,
            humanDoor: null,
            mapWidth: 100,
            mapHeight: 100,
            out ReviewFixtureBuildingPlacementArea? area,
            out string error), error);
        var contents = new[]
        {
            new
            {
                Tile = new ReviewFixtureTile(16, 20),
                ItemId = "future-vanilla-id",
                HasModData = true,
            },
            new
            {
                Tile = new ReviewFixtureTile(18, 20),
                ItemId = "outside",
                HasModData = false,
            },
        }.ToDictionary(content => content.Tile);

        ReviewFixtureTile[] selectedTiles = area!
            .SelectOccupiedTiles(contents.ContainsKey)
            .ToArray();

        ReviewFixtureTile selected = Assert.Single(selectedTiles);
        Assert.Equal("future-vanilla-id", contents[selected].ItemId);
        Assert.True(contents[selected].HasModData);
        Assert.DoesNotContain(new ReviewFixtureTile(18, 20), selectedTiles);
    }

    [Theory]
    [InlineData(99, 20, 2, 2, 100, 100)]
    [InlineData(10, 99, 2, 2, 100, 100)]
    [InlineData(-1, 20, 2, 2, 100, 100)]
    public void BuildingPlacementAreaRejectsOutOfMapFootprints(
        int x,
        int y,
        int width,
        int height,
        int mapWidth,
        int mapHeight)
    {
        Assert.False(ReviewFixturePolicy.TryCreateBuildingPlacementArea(
            x,
            y,
            width,
            height,
            additionalAreas: null,
            humanDoor: null,
            mapWidth,
            mapHeight,
            out _,
            out _));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(-1, 2)]
    [InlineData(2, -1)]
    public void BuildingPlacementAreaRejectsNonPlaceableDataSizes(
        int width,
        int height)
    {
        Assert.False(ReviewFixturePolicy.TryCreateBuildingPlacementArea(
            x: 10,
            y: 20,
            width,
            height,
            additionalAreas: null,
            humanDoor: null,
            mapWidth: 100,
            mapHeight: 100,
            out _,
            out _));
    }

    [Fact]
    public void BuildingPlacementAreaRejectsOutOfMapAdditionalAndDoorTiles()
    {
        Assert.False(ReviewFixturePolicy.TryCreateBuildingPlacementArea(
            x: 98,
            y: 20,
            width: 2,
            height: 2,
            additionalAreas:
            [
                new ReviewFixtureAdditionalPlacementArea(
                    X: 2,
                    Y: 0,
                    Width: 1,
                    Height: 1,
                    OnlyNeedsToBePassable: true),
            ],
            humanDoor: null,
            mapWidth: 100,
            mapHeight: 100,
            out _,
            out _));
        Assert.False(ReviewFixturePolicy.TryCreateBuildingPlacementArea(
            x: 10,
            y: 98,
            width: 2,
            height: 2,
            additionalAreas: null,
            humanDoor: new ReviewFixtureTile(0, 1),
            mapWidth: 100,
            mapHeight: 100,
            out _,
            out _));
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
    public void NaturalFarmWarpMustBeOneExactPlayerWarp()
    {
        var expected = new ReviewFixtureWarpState(3, 7, "Farm", 64, 15, NpcOnly: false);
        var npc = expected with { X = 4, NpcOnly = true };
        var other = expected with { X = 5, TargetName = "Town" };

        Assert.True(
            ReviewFixturePolicy.TrySelectNaturalFarmWarp(
                [npc, other, expected],
                out ReviewFixtureWarpState? selected));
        Assert.Equal(expected, selected);

        Assert.False(ReviewFixturePolicy.TrySelectNaturalFarmWarp(null, out _));
        Assert.False(ReviewFixturePolicy.TrySelectNaturalFarmWarp([], out _));
        Assert.False(
            ReviewFixturePolicy.TrySelectNaturalFarmWarp(
                [expected with { TargetName = "farm" }],
                out _));
        Assert.False(
            ReviewFixturePolicy.TrySelectNaturalFarmWarp(
                [expected, expected with { X = 9 }],
                out _));
    }

    [Fact]
    public void GameRuntimeSourceRetainsOwnershipAndSafetyBoundaries()
    {
        string source = ReadSource();
        int ensure = source.IndexOf(
            "public ReviewFixtureResult EnsureBuilding(",
            StringComparison.Ordinal);
        int farmLocationCheck = source.IndexOf(
            "if (!ReferenceEquals(Game1.currentLocation, farm))",
            ensure,
            StringComparison.Ordinal);
        int confirmDecision = source.IndexOf(
            "if (decision == ReviewFixtureEnsureDecision.Confirm)",
            ensure,
            StringComparison.Ordinal);
        int planCall = source.IndexOf(
            "if (!TryPlanBuildingPlacement(",
            ensure,
            StringComparison.Ordinal);
        int buildingResolve = source.IndexOf(
            "ReviewFixtureKindResolver.TryResolve(",
            ensure,
            StringComparison.Ordinal);
        int buildingInstantiate = source.IndexOf(
            "Building.CreateInstanceFromId(",
            buildingResolve,
            StringComparison.Ordinal);
        int buildCondition = source.IndexOf(
            "GameStateQuery.CheckConditions(",
            buildingResolve,
            StringComparison.Ordinal);
        int applyCall = source.IndexOf(
            "if (!TryApplyBuildingPlacementPreparation(",
            planCall,
            StringComparison.Ordinal);
        int buildCall = source.IndexOf(
            "if (!farm.buildStructure(",
            applyCall,
            StringComparison.Ordinal);
        int planDefinition = source.IndexOf(
            "private static bool TryPlanBuildingPlacement(",
            StringComparison.Ordinal);
        int applyDefinition = source.IndexOf(
            "private static bool TryApplyBuildingPlacementPreparation(",
            planDefinition,
            StringComparison.Ordinal);
        string preflight = source[planDefinition..applyDefinition];

        Assert.True(ensure >= 0);
        Assert.True(buildingResolve > ensure);
        Assert.True(confirmDecision > buildingResolve);
        Assert.True(farmLocationCheck > confirmDecision);
        Assert.True(buildCondition > farmLocationCheck);
        Assert.True(buildingInstantiate > buildCondition);
        Assert.True(planCall > buildingInstantiate);
        Assert.True(applyCall > planCall);
        Assert.True(buildCall > applyCall);
        Assert.True(planDefinition > buildCall);
        Assert.True(applyDefinition > planDefinition);
        Assert.Contains("skipSafetyChecks: false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("skipSafetyChecks: true", source, StringComparison.Ordinal);
        Assert.Contains("Run 'sdvkit fixture farm' first; no placement content was changed.", source, StringComparison.Ordinal);
        Assert.Contains("Game1.buildingData.Keys", source, StringComparison.Ordinal);
        Assert.Contains("out BuildingData? buildingData", source, StringComparison.Ordinal);
        Assert.Contains("resolved.CanonicalId,\n                    buildingData,", source, StringComparison.Ordinal);
        Assert.Contains("TryRollbackFailedBuilding(farm, constructed)", source, StringComparison.Ordinal);
        Assert.Contains("ReviewFixturePolicy.TryCreateBuildingPlacementArea(", preflight, StringComparison.Ordinal);
        Assert.Contains("buildingData.Size.X", preflight, StringComparison.Ordinal);
        Assert.Contains(".AdditionalPlacementTiles", preflight, StringComparison.Ordinal);
        Assert.Contains("buildingData.HumanDoor", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.buildings.FirstOrDefault", preflight, StringComparison.Ordinal);
        Assert.Contains("existing.occupiesTile(tile.X, tile.Y", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.farmers.Any(", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.characters.Any(", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.animals.Values.Any(", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.largeTerrainFeatures.Any(", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.GetBuildableRectangle()", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.isWaterTile(tile.X, tile.Y)", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.isTilePlaceable(vector, itemIsPassable: false)", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.isTilePassable(vector)", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.furniture", preflight, StringComparison.Ordinal);
        Assert.Contains("item.GetBoundingBox().Intersects(GetTileBounds(tile))", preflight, StringComparison.Ordinal);
        Assert.Contains("TryOrderFurniturePreparation(", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.Objects.ContainsKey(new Vector2(tile.X, tile.Y))", preflight, StringComparison.Ordinal);
        Assert.Contains("farm.terrainFeatures.ContainsKey(new Vector2(tile.X, tile.Y))", preflight, StringComparison.Ordinal);
        Assert.Contains("clump.occupiesTile(tile.X, tile.Y)", preflight, StringComparison.Ordinal);
        Assert.Contains("\"Buildable\"", preflight, StringComparison.Ordinal);
        Assert.Contains("\"Diggable\"", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain(".Remove(", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("QualifiedItemId", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain(".ItemId", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain(".Name", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain(".Type", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain(".modData", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain(".Fragility", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain(".Price", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposableObjectKinds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDisposable", source, StringComparison.Ordinal);
        Assert.Contains("farm.Objects.Remove(new Vector2(tile.X, tile.Y))", source, StringComparison.Ordinal);
        Assert.Contains("farm.terrainFeatures.Remove(new Vector2(tile.X, tile.Y))", source, StringComparison.Ordinal);
        Assert.Contains("farm.resourceClumps.Remove(clump)", source, StringComparison.Ordinal);
        Assert.Contains("item.AttemptRemoval(candidate =>", source, StringComparison.Ordinal);
        Assert.Contains("candidate.performRemoveAction()", source, StringComparison.Ordinal);
        Assert.Contains("farm.furniture.Remove(candidate)", source, StringComparison.Ordinal);
        Assert.Contains("item.canBeRemoved(Game1.player)", source, StringComparison.Ordinal);
        Assert.Contains("CanPrepareFurniture(farm, item, plannedObjectTiles, scheduled)", source, StringComparison.Ordinal);
        Assert.Contains("item.AllowLocalRemoval", source, StringComparison.Ordinal);
        Assert.Contains("item.HasSittingFarmers()", source, StringComparison.Ordinal);
        Assert.Contains("!plannedObjectTiles.Contains(tile)", source, StringComparison.Ordinal);
        Assert.Contains("Stardew threw while preparing the placement area", source, StringComparison.Ordinal);
        Assert.Contains("Reset the disposable fixture before retrying", source, StringComparison.Ordinal);
        Assert.Contains(
            "$\"objects={Objects} terrainFeatures={TerrainFeatures} \"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "+ $\"resourceClumps={ResourceClumps} furniture={Furniture}\"",
            source,
            StringComparison.Ordinal);
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
        Assert.Contains("DataLoader.FarmAnimals(Game1.content)", source, StringComparison.Ordinal);
        Assert.Contains("targetData.ValidOccupantTypes", source, StringComparison.Ordinal);
        Assert.Contains("animalData.House", source, StringComparison.Ordinal);
        Assert.Contains("animal.CanLiveIn(target)", source, StringComparison.Ordinal);
        Assert.Contains("TryRollbackFailedAnimal(animalHouse, animal)", source, StringComparison.Ordinal);
        Assert.Contains("existing.home?.id.Value == target.id.Value", source, StringComparison.Ordinal);
        Assert.Contains("animalHouse.animalsThatLiveHere.Contains(existing.myID.Value)", source, StringComparison.Ordinal);
        Assert.Contains("ReviewFixturePolicy.TrySelectNaturalFarmWarp(", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(warp.TargetName, \"Farm\", StringComparison.Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("warp.npcOnly.Value", source, StringComparison.Ordinal);
        Assert.Contains("current is not FarmHouse", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveEnterBuilding(", source, StringComparison.Ordinal);
        Assert.Contains("candidate is GreenhouseBuilding", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(candidate.GetIndoors(), canonical)", source, StringComparison.Ordinal);
        Assert.Contains(ReviewFixtureContract.GreenhouseBuildingType, source, StringComparison.Ordinal);
        Assert.Contains("indoors.isTileOnMap(entry)", source, StringComparison.Ordinal);
        Assert.Contains("indoors.isTilePassable(entry)", source, StringComparison.Ordinal);
        Assert.Contains("locationRequest.OnWarp +=", source, StringComparison.Ordinal);
        Assert.Contains("requestedLocation = locationRequest.Location", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(actualLocation, requestedLocation)", source, StringComparison.Ordinal);
        Assert.Contains("actualLocation.NameOrUniqueName", source, StringComparison.Ordinal);
        Assert.Contains("actualLocation.isStructure.Value != expectedIsStructure", source, StringComparison.Ordinal);
        Assert.Contains("Game1.player.TilePoint.X", source, StringComparison.Ordinal);
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
        Assert.DoesNotContain("Deluxe Barn", source, StringComparison.Ordinal);
        Assert.DoesNotContain("White Cow", source, StringComparison.Ordinal);

        int animalEnsure = source.IndexOf(
            "public ReviewFixtureResult EnsureAnimal(",
            StringComparison.Ordinal);
        int animalResolve = source.IndexOf(
            "ReviewFixtureKindResolver.TryResolve(",
            animalEnsure,
            StringComparison.Ordinal);
        int compatibility = source.IndexOf(
            "ReviewFixturePolicy.IsAnimalHouseCompatible(",
            animalResolve,
            StringComparison.Ordinal);
        int allocateId = source.IndexOf(
            "getNewMultiplayerId()",
            compatibility,
            StringComparison.Ordinal);
        int adopt = source.IndexOf(
            "animalHouse.adoptAnimal(animal)",
            allocateId,
            StringComparison.Ordinal);
        Assert.True(animalEnsure >= 0);
        Assert.True(animalResolve > animalEnsure);
        Assert.True(compatibility > animalResolve);
        Assert.True(allocateId > compatibility);
        Assert.True(adopt > allocateId);
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

    [Fact]
    public void McpTransportAcceptsOnlyTheSixClosedTypedFixtureOperations()
    {
        string requestId = Guid.NewGuid().ToString("N");
        string building = ReviewTransportToken.Encode("barn-a");
        string buildingKind = ReviewTransportToken.Encode("Deluxe Barn");
        string animalKind = ReviewTransportToken.Encode("White Cow");

        AssertTransport(
            ["fixture", requestId, ReviewFixtureTransportContract.StatusOperation],
            ReviewFixtureTransportContract.StatusOperation,
            typeof(ReviewFixtureStatusRequest));
        AssertTransport(
            ["fixture", requestId, ReviewFixtureTransportContract.EnterOperation, building],
            ReviewFixtureTransportContract.EnterOperation,
            typeof(ReviewFixtureEnterRequest));
        AssertTransport(
            ["fixture", requestId, ReviewFixtureTransportContract.FarmOperation],
            ReviewFixtureTransportContract.FarmOperation,
            typeof(ReviewFixtureFarmRequest));
        AssertTransport(
            [
                "fixture",
                requestId,
                ReviewFixtureTransportContract.BuildingEnsureOperation,
                building,
                buildingKind,
                "16",
                "20",
            ],
            ReviewFixtureTransportContract.BuildingEnsureOperation,
            typeof(ReviewFixtureBuildingEnsureRequest));
        AssertTransport(
            [
                "fixture",
                requestId,
                ReviewFixtureTransportContract.AnimalEnsureOperation,
                building,
                animalKind,
            ],
            ReviewFixtureTransportContract.AnimalEnsureOperation,
            typeof(ReviewFixtureAnimalEnsureRequest));
        AssertTransport(
            ["fixture", requestId, ReviewFixtureTransportContract.SaveOperation],
            ReviewFixtureTransportContract.SaveOperation,
            requestType: null);

        Assert.False(ReviewFixtureTransportArguments.TryParse(
            Bound(["fixture", requestId, "object", "ensure", building]),
            out _,
            out _,
            out _,
            out _,
            out ReviewFixtureProblem? objectProblem));
        Assert.Equal("fixtureTransportInvalid", objectProblem?.Code);
        Assert.False(ReviewFixtureTransportArguments.TryParse(
            Bound(["fixture", requestId, "console", ReviewTransportToken.Encode("anything")]),
            out _,
            out _,
            out _,
            out _,
            out _));
        Assert.False(ReviewFixtureTransportArguments.TryParse(
            Bound(["fixture", requestId, ReviewFixtureTransportContract.EnterOperation, "not+base64"]),
            out _,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void McpBoundaryKeepsForeignStateOutAndRetainsExistingRollbackPaths()
    {
        const string expectedFixture = "fixture-a";
        const string foreignFixture = "fixture-b";
        var foreignBuilding = new ReviewFixtureBuildingState(
            foreignFixture,
            "Deluxe Barn",
            16,
            20);
        var foreignAnimal = new ReviewFixtureAnimalState(
            "brown-cow",
            "Brown Cow",
            HasExactHome: true,
            HasExactAssignment: true);

        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideBuildingEnsure(
                [foreignBuilding],
                expectedFixture,
                "Deluxe Barn",
                16,
                20));
        Assert.Equal(
            ReviewFixtureEnsureDecision.Reject,
            ReviewFixturePolicy.DecideAnimalEnsure(
                [foreignAnimal],
                "white-cow",
                "White Cow",
                assignedAnimalCount: 1,
                animalCapacity: 12));
        Assert.False(ReviewFixturePolicy.IsOwnedObject(
            foreignFixture,
            "11111111-1111-1111-1111-111111111111",
            expectedFixture,
            "11111111-1111-1111-1111-111111111111"));

        string source = ReadSource();
        Assert.Contains("TryRollbackFailedBuilding(farm, constructed)", source, StringComparison.Ordinal);
        Assert.Contains("TryRollbackFailedAnimal(animalHouse, animal)", source, StringComparison.Ordinal);
        Assert.Contains("No partial building remains", source, StringComparison.Ordinal);
        Assert.Contains("No partial animal remains", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FinishingAnExistingConstructionIsReportedAsAWorldChange()
    {
        string source = ReadSource();
        int observed = source.IndexOf(
            "bool finishedConstruction = existing.isUnderConstruction",
            StringComparison.Ordinal);
        int finished = source.IndexOf(
            "existing.FinishConstruction(onGameStart: false);",
            observed,
            StringComparison.Ordinal);
        int reported = source.IndexOf(
            "changed: finishedConstruction",
            finished,
            StringComparison.Ordinal);

        Assert.True(observed >= 0);
        Assert.True(finished > observed);
        Assert.True(reported > finished);
    }

    [Fact]
    public void ReviewSaveTransportReusesTheOnlyDurableSaveIteratorAndPublishesAfterCompletion()
    {
        string source = ReadRepositorySource("src", "SdvKit.AlwaysOn", "TestSaveAutomation.cs");
        string fixtureSource = ReadSource();

        Assert.Equal(
            1,
            source.Split("SaveGame.Save()", StringSplitOptions.None).Length - 1);
        Assert.Contains("public bool TryStartReviewSave(", source, StringComparison.Ordinal);
        Assert.Contains("StartDurableSave();", source, StringComparison.Ordinal);
        Assert.Contains("DriveDurableSave();", source, StringComparison.Ordinal);
        Assert.Contains("if (!_saveReachedCompletion)", source, StringComparison.Ordinal);
        Assert.Contains("Action<bool, string>? _reviewSaveCompletion", source, StringComparison.Ordinal);
        Assert.Contains("completion(true, message);", source, StringComparison.Ordinal);
        Assert.Contains("completion?.Invoke(false, message);", source, StringComparison.Ordinal);
        Assert.Contains("automation.TryStartReviewSave(", fixtureSource, StringComparison.Ordinal);
        Assert.Contains("new ReviewFixtureSaveReport(", fixtureSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveGame.Save()", fixtureSource, StringComparison.Ordinal);
    }

    private static void AssertTransport(
        IReadOnlyList<string> arguments,
        string expectedOperation,
        Type? requestType)
    {
        Assert.True(ReviewFixtureTransportArguments.TryParse(
            Bound(arguments),
            out string? requestId,
            out ReviewFixtureRequestBinding? binding,
            out ReviewFixtureQuery? query,
            out ReviewFixtureRequest? request,
            out ReviewFixtureProblem? problem),
            problem?.Message);
        Assert.True(ReviewTransportToken.IsRequestId(requestId));
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", binding?.LaunchId);
        Assert.Equal("fixture-a", binding?.FixtureId);
        Assert.Equal("SDVKit_123", binding?.SaveId);
        Assert.Equal(expectedOperation, query?.Operation);
        if (requestType is null)
        {
            Assert.Null(request);
        }
        else
        {
            Assert.IsType(requestType, request);
        }
    }

    private static IReadOnlyList<string> Bound(IReadOnlyList<string> arguments) =>
        [
            arguments[0],
            arguments[1],
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            LiveLabState.SingleTopology,
            ReviewFixtureTransportContract.SingleRoleToken,
            ReviewTransportToken.Encode("fixture-a"),
            ReviewTransportToken.Encode("SDVKit_123"),
            .. arguments.Skip(2),
        ];

    private static ReviewFixtureRequest Parse(params string[] arguments)
    {
        Assert.True(
            ReviewFixtureArguments.TryParse(arguments, out ReviewFixtureRequest? request, out string error),
            error);
        return Assert.IsAssignableFrom<ReviewFixtureRequest>(request);
    }

    private static string ReadSource()
        => ReadRepositorySource("src", "SdvKit.AlwaysOn", "ReviewFixtureCommand.cs");

    private static string ReadRepositorySource(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine([directory.FullName, .. parts]);
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
        public ReviewFixtureAccess Access { get; set; } = new(
            true,
            true,
            Guid.NewGuid().ToString("N"),
            "single",
            "verified");

        public bool DeferNavigation { get; init; }

        public ReviewFixtureResult NavigationResult { get; init; } =
            new(true, "navigation completed");

        private Action<ReviewFixtureResult>? NavigationCompletion { get; set; }

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
            string kind,
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
            string building,
            string kind) => Dispatched();

        public ReviewFixtureResult Enter(
            ReviewFixtureAccess access,
            string building) => Dispatched();

        public ReviewFixtureResult Farm(ReviewFixtureAccess access) => Dispatched();

        public void BeginNavigation(
            ReviewFixtureAccess access,
            ReviewFixtureRequest request,
            Action<ReviewFixtureResult> completed)
        {
            Dispatches++;
            if (DeferNavigation)
            {
                NavigationCompletion = completed;
                return;
            }

            completed(NavigationResult);
        }

        public void CompleteNavigation() =>
            Assert.IsType<Action<ReviewFixtureResult>>(NavigationCompletion)(NavigationResult);

        private ReviewFixtureResult Dispatched()
        {
            Dispatches++;
            return new ReviewFixtureResult(true, "done");
        }
    }
}
