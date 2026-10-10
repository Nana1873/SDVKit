using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.GameSide;

namespace SdvKit.Tests;

public sealed class ReviewFixtureLabTests
{
    private static readonly ReviewFixtureRequestBinding Binding = new(
        "0123456789abcdef0123456789abcdef", "single", null, "fixture-1", "Save_1");

    private static readonly ReviewFixtureQuery[] ValidQueries =
    [
        new ReviewFixtureQuery(ReviewFixtureTransportContract.ObjectEnsureOperation, Building: "coop", Kind: "(O)388"),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.ObjectClearOperation, Building: "coop"),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.FishingPrepareOperation, SkipTutorial: true),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.FishingPrepareOperation),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.FishingPrepareOperation, X: 40, Y: 12, SkipTutorial: true),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.SetTimeOperation, X: 1930),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.SetWeatherOperation, Value: "Rain"),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.SetSeasonOperation, Value: "winter"),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.WarpOperation, Value: "Town"),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.WarpOperation, Value: "Town", X: 10, Y: 20),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.SetZoomOperation, Zoom: 1.25),
        new ReviewFixtureQuery(ReviewFixtureTransportContract.SetWindowOperation, X: 1600, Y: 900),
    ];

    public static TheoryData<int> ValidQueryIndexes => new(Enumerable.Range(0, ValidQueries.Length));

    [Theory]
    [MemberData(nameof(ValidQueryIndexes))]
    public void CliCommandRoundTripsThroughTheGameParser(int index)
    {
        ReviewFixtureQuery query = ValidQueries[index];
        Assert.Null(ProjectReviewFixtureService.Validate(query, "single", null));
        string command = ProjectReviewFixtureService.BuildCommand("fedcba9876543210fedcba9876543210", Binding, query);
        Assert.True(ReviewCommandLine.TryParse(command, out string[] arguments, out _, out _));

        string[] fixtureArguments = arguments.SkipWhile(argument => argument != "fixture").ToArray();
        Assert.True(ReviewFixtureTransportArguments.TryParse(
            fixtureArguments, out _, out ReviewFixtureRequestBinding? binding, out ReviewFixtureQuery? parsed, out ReviewFixtureRequest? request, out _));
        Assert.Equal(Binding, binding);
        Assert.Equal(query, parsed);
        Assert.NotNull(request);
    }

    [Theory]
    [InlineData("setTime", 2600, null, null)]
    [InlineData("setTime", 1935, null, null)]
    [InlineData("setWeather", null, "Hail", null)]
    [InlineData("setSeason", null, "Spring", null)]
    [InlineData("setZoom", null, null, 3.0)]
    public void InvalidLabValuesAreRejectedBeforeSending(string operation, int? x, string? value, double? zoom) =>
        Assert.NotNull(ProjectReviewFixtureService.Validate(
            new ReviewFixtureQuery(operation, X: x, Value: value, Zoom: zoom), "single", null));

    [Fact]
    public void WarpNeedsBothCoordinatesOrNeither() =>
        Assert.NotNull(ProjectReviewFixtureService.Validate(
            new ReviewFixtureQuery(ReviewFixtureTransportContract.WarpOperation, Value: "Town", X: 3), "single", null));

    [Theory]
    [InlineData("setTime", false)]
    [InlineData("setWeather", false)]
    [InlineData("objectEnsure", false)]
    [InlineData("fishingPrepare", false)]
    [InlineData("warp", true)]
    [InlineData("setZoom", true)]
    [InlineData("setWindow", true)]
    public void FarmhandMayOnlyChangeItsOwnPlayerAndWindow(string operation, bool allowed)
    {
        var query = operation switch
        {
            "setTime" => new ReviewFixtureQuery(operation, X: 900),
            "setWeather" => new ReviewFixtureQuery(operation, Value: "Sun"),
            "objectEnsure" => new ReviewFixtureQuery(operation, Building: "coop", Kind: "(O)388"),
            "fishingPrepare" => new ReviewFixtureQuery(operation),
            "warp" => new ReviewFixtureQuery(operation, Value: "Town"),
            "setZoom" => new ReviewFixtureQuery(operation, Zoom: 1.0),
            _ => new ReviewFixtureQuery(operation, X: 1280, Y: 720),
        };

        ReviewFixtureProblem? problem = ProjectReviewFixtureService.Validate(query, "network-2", "farmhand");

        Assert.Equal(allowed, problem is null);
        Assert.Null(ProjectReviewFixtureService.Validate(query, "network-2", "host"));
    }

    [Fact]
    public void OriginalOperationsRejectTheNewOperands() =>
        Assert.NotNull(ProjectReviewFixtureService.Validate(
            new ReviewFixtureQuery(ReviewFixtureTransportContract.StatusOperation, Value: "x"), "single", null));
}
