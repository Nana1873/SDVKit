using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewInputServiceTests
{
    private const string RequestId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset RequestedAt =
        new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string?, string?, int?, int?, string> Commands => new()
    {
        {
            "press", "MouseLeft", null, null, null,
            $"sdvkit input request {RequestId} press MouseLeft"
        },
        {
            "wheel", null, "up", null, null,
            $"sdvkit input request {RequestId} wheel up"
        },
        {
            "cursorSet", null, null, 12, 34,
            $"sdvkit input request {RequestId} cursor 12 34"
        },
        {
            "cursorClear", null, null, null, null,
            $"sdvkit input request {RequestId} cursor clear"
        },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void BuildsOnlyTheTypedRequestBoundConsoleForms(
        string action,
        string? button,
        string? direction,
        int? x,
        int? y,
        string expected)
    {
        var query = new ReviewInputQuery(action, button, direction, x, y);
        Assert.Equal(
            expected,
            ProjectReviewInputService.BuildCommand(RequestId, query));
        Assert.True(ProjectReviewConsoleLine.CanRunBeforeScenarioReady(expected));
    }

    [Theory]
    [InlineData("press", "MouseWheelUp", null, null, null)]
    [InlineData("press", "Mouse-Left", null, null, null)]
    [InlineData("wheel", null, "left", null, null)]
    [InlineData("cursorSet", null, null, -1, 0)]
    [InlineData("cursorClear", "F8", null, null, null)]
    [InlineData("macro", null, null, null, null)]
    public void RejectsUnsupportedAmbiguousOrUnboundedQueries(
        string action,
        string? button,
        string? direction,
        int? x,
        int? y)
    {
        Assert.NotNull(ProjectReviewInputService.Validate(
            new ReviewInputQuery(action, button, direction, x, y)));
    }

    [Fact]
    public void ResponseValidationRequiresExactRequestAndFreshness()
    {
        ReviewInputQuery query = new(
            ReviewInputContract.CursorSetAction,
            null,
            null,
            12,
            34);
        ReviewInputResponseEnvelope response = Response(query, RequestedAt);

        Assert.True(ProjectReviewInputService.MatchesResponse(
            response,
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
        Assert.False(ProjectReviewInputService.MatchesResponse(
            response with { RequestId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
        Assert.False(ProjectReviewInputService.MatchesResponse(
            response with { ObservedAtUtc = RequestedAt.AddMinutes(-1) },
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
        Assert.False(ProjectReviewInputService.MatchesResponse(
            response with { CursorSet = false },
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
    }

    [Fact]
    public void WheelAcknowledgementRequiresTheCursorAndMenuOnSuccess()
    {
        ReviewInputQuery query = new(
            ReviewInputContract.WheelAction,
            null,
            "down",
            null,
            null);
        ReviewInputResponseEnvelope response = Response(query, RequestedAt) with
        {
            CursorSet = true,
            MenuOpen = true,
        };

        Assert.True(ProjectReviewInputService.MatchesResponse(
            response,
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
        Assert.False(ProjectReviewInputService.MatchesResponse(
            response with { MenuOpen = false },
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
        Assert.False(ProjectReviewInputService.MatchesResponse(
            response with { CursorSet = false },
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
    }

    [Fact]
    public void UnsupportedButtonFailureAcknowledgementMustMatchTheExactRequest()
    {
        var query = new ReviewInputQuery(
            ReviewInputContract.PressAction,
            "DefinitelyNotAnSButton",
            null,
            null,
            null);
        ReviewInputResponseEnvelope response = Response(query, RequestedAt) with
        {
            Succeeded = false,
            Problem = new ReviewInputProblem(
                "inputButtonUnsupported",
                "The exact button is unsupported."),
        };

        Assert.True(ProjectReviewInputService.MatchesResponse(
            response,
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
        Assert.False(ProjectReviewInputService.MatchesResponse(
            response with { Button = null },
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
        Assert.False(ProjectReviewInputService.MatchesResponse(
            response with { Button = "F8" },
            RequestId,
            query,
            RequestedAt,
            RequestedAt.AddSeconds(1)));
    }

    [Fact]
    public void DeserializerRejectsUnknownAndDuplicateMembers()
    {
        byte[] unknown = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            requestId = RequestId,
            observedAtUtc = RequestedAt,
            gameTick = 10,
            action = "cursorClear",
            succeeded = true,
            button = (string?)null,
            direction = (string?)null,
            x = (int?)null,
            y = (int?)null,
            cursorSet = false,
            menuOpen = false,
            problem = (object?)null,
            unexpected = true,
        });
        byte[] duplicate = System.Text.Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"requestId\":\""
            + RequestId
            + "\",\"observedAtUtc\":\"2026-09-04T08:00:00Z\",\"gameTick\":10,\"action\":\"cursorClear\",\"succeeded\":true,\"button\":null,\"direction\":null,\"x\":null,\"y\":null,\"cursorSet\":false,\"menuOpen\":false,\"problem\":null}");

        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewInputService.DeserializeResponse(unknown));
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewInputService.DeserializeResponse(duplicate));
    }

    private static ReviewInputResponseEnvelope Response(
        ReviewInputQuery query,
        DateTimeOffset observedAt) => new(
            ReviewInputContract.SchemaVersion,
            RequestId,
            observedAt,
            100,
            query.Action,
            true,
            query.Button,
            query.Direction,
            query.X,
            query.Y,
            true,
            true,
            null);
}
