using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewWorldMoveTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PlayerPositionValues Position = new("down", 64.5f, 17.6f, 64, 18, 64, 18);

    [Theory]
    [InlineData("up", 0, -1, 0)]
    [InlineData("right", 1, 0, 1)]
    [InlineData("down", 0, 1, 2)]
    [InlineData("left", -1, 0, 3)]
    public void ContractAcceptsOnlyTheFourDirections(string direction, int dx, int dy, int facing)
    {
        Assert.Null(ReviewWorldMoveContract.Validate(new ReviewWorldMoveQuery(direction, false)));
        Assert.Equal((dx, dy), ReviewWorldMoveContract.Delta(direction));
        Assert.Equal(facing, ReviewWorldMoveContract.FacingIndex(direction));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldMoveContract.Validate(new ReviewWorldMoveQuery("north", false)));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldMoveContract.Validate(new ReviewWorldMoveQuery("Up", true)));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldMoveContract.Validate(null));
    }

    [Fact]
    public void RejectionOrderMatchesTheWorldActionGatesAndSkipsTheTargetForATurn()
    {
        var ready = new ReviewWorldMoveFacts(true, true, true, true, true);
        ReviewWorldMoveQuery move = new("down", false);
        ReviewWorldMoveQuery turn = new("down", true);
        Assert.Null(ReviewWorldMoveContract.Rejection(move, ready));
        Assert.Equal("worldMoveTargetBlocked", ReviewWorldMoveContract.Rejection(move, ready with { TargetFree = false }));
        Assert.Null(ReviewWorldMoveContract.Rejection(turn, ready with { TargetFree = false }));
        Assert.Equal("worldActionBindingInvalid", ReviewWorldMoveContract.Rejection(move, ready with { BindingReady = false }));
        Assert.Equal("worldActionTestSaveRequired", ReviewWorldMoveContract.Rejection(move, ready with { FixtureReady = false }));
        Assert.Equal("worldActionWorldNotReady", ReviewWorldMoveContract.Rejection(move, ready with { WorldReady = false }));
        Assert.Equal("worldActionPlayerBusy", ReviewWorldMoveContract.Rejection(move, ready with { PlayerReady = false }));
        Assert.Equal("worldActionPlayerBusy", ReviewWorldMoveContract.Rejection(move, ready with { PlayerReady = false, TargetFree = false }));
        Assert.True(ReviewWorldMoveContract.IsGameError("worldMoveTargetBlocked"));
        Assert.True(ReviewWorldMoveContract.IsGameError("worldActionPlayerBusy"));
        Assert.False(ReviewWorldMoveContract.IsGameError("worldActionTopologyUnsupported"));
    }

    [Theory]
    // Still on the start tile: keep holding.
    [InlineData("down", "Farm", 64, 16, 64.5f, 16.9f, false)]
    // On the target tile but before its centre line: keep holding.
    [InlineData("down", "Farm", 64, 17, 64.5f, 17.2f, false)]
    [InlineData("up", "Farm", 64, 15, 64.5f, 15.8f, false)]
    [InlineData("right", "Farm", 65, 16, 65.3f, 16.5f, false)]
    [InlineData("left", "Farm", 63, 16, 63.7f, 16.5f, false)]
    // At or past the centre line of the target tile: release.
    [InlineData("down", "Farm", 64, 17, 64.5f, 17.5f, true)]
    [InlineData("down", "Farm", 64, 17, 64.5f, 17.58f, true)]
    [InlineData("up", "Farm", 64, 15, 64.5f, 15.4f, true)]
    [InlineData("right", "Farm", 65, 16, 65.6f, 16.5f, true)]
    [InlineData("left", "Farm", 63, 16, 63.5f, 16.5f, true)]
    // Somewhere else (pushed, or a second tile) or in another location (a warp): release at once.
    [InlineData("down", "Farm", 64, 18, 64.5f, 18.1f, true)]
    [InlineData("down", "Farm", 65, 16, 65.5f, 16.5f, true)]
    [InlineData("down", "BusStop", 64, 16, 64.5f, 16.9f, true)]
    [InlineData("down", null, 64, 16, 64.5f, 16.9f, true)]
    public void HoldEndsPastTheTargetCentreOrWhenThePlayerLeavesTheExpectedTiles(string direction, string? location,
        int tileX, int tileY, float standingX, float standingY, bool expected)
    {
        (int dx, int dy) = ReviewWorldMoveContract.Delta(direction);
        Assert.Equal(expected, ReviewWorldMoveContract.ShouldRelease(direction, 64, 16, 64 + dx, 16 + dy, "Farm",
            location, tileX, tileY, standingX, standingY));
    }

    [Theory]
    [InlineData(false, "Farm", 64, 17, "down", true)]
    [InlineData(false, "Farm", 64, 17, "left", false)]
    [InlineData(false, "Farm", 64, 16, "down", false)]
    [InlineData(false, "Farm", 64, 18, "down", false)]
    [InlineData(false, "BusStop", 64, 17, "down", false)]
    [InlineData(false, "Farm", 64, 17, null, false)]
    [InlineData(true, "Farm", 64, 16, "down", true)]
    [InlineData(true, "Farm", 64, 17, "down", false)]
    [InlineData(true, "Farm", 64, 16, "up", false)]
    public void ReachedNeedsTheExpectedTileLocationAndFacing(bool turnOnly, string? location, int tileX, int tileY,
        string? facing, bool expected) =>
        Assert.Equal(expected, ReviewWorldMoveContract.Reached(new ReviewWorldMoveQuery("down", turnOnly), 64, 16, 64, 17,
            "Farm", location, tileX, tileY, facing));

    [Fact]
    public void ServiceBindsTheCommandAndReturnsTheGameReport()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ReviewWorldMoveReport result = ProjectReviewWorldMoveService.Execute(reader, new("down", false), command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal(["sdvkit", "world-move"], parts.Take(2));
            Assert.Equal(Launch, parts[3]);
            Assert.Equal(["down", "false", "single"], parts.Skip(4));
            Respond(temporary, parts[2], Completed("down", false, 64, 17, true));
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("completed", result.State);
        Assert.True(result.Reached);
        Assert.Equal((64, 17), (result.TileX, result.TileY));
        Assert.Equal("down", result.Position?.Facing);

        ReviewWorldMoveReport turned = ProjectReviewWorldMoveService.Execute(reader, new("left", true), command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal(["left", "true", "single"], parts.Skip(4));
            Respond(temporary, parts[2], Completed("left", true, 64, 16, true) with { Position = Position with { Facing = "left" } });
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("completed", turned.State);
        Assert.True(turned.TurnOnly);
    }

    [Fact]
    public void ServiceRejectsReportsForAnotherDirectionOrTurnMode()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ReviewWorldMoveReport wrongDirection = ProjectReviewWorldMoveService.Execute(reader, new("down", false), command =>
        {
            Respond(temporary, command.Split(' ')[2], Completed("up", false, 64, 15, true));
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("unavailable", wrongDirection.State);
        Assert.Equal("mayHaveRun", wrongDirection.DispatchState);
        ReviewWorldMoveReport wrongMode = ProjectReviewWorldMoveService.Execute(reader, new("down", false), command =>
        {
            Respond(temporary, command.Split(' ')[2], Completed("down", true, 64, 16, true));
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("unavailable", wrongMode.State);
        ReviewWorldMoveReport missingTile = ProjectReviewWorldMoveService.Execute(reader, new("down", false), command =>
        {
            Respond(temporary, command.Split(' ')[2], Completed("down", false, 64, 17, true) with { TileX = null });
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("unavailable", missingTile.State);
    }

    [Fact]
    public void BlockedRefusalSurvivesTransportWithTheObservedTile()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ReviewWorldMoveReport result = ProjectReviewWorldMoveService.Execute(reader, new("down", false), command =>
        {
            Respond(temporary, command.Split(' ')[2], Completed("down", false, 64, 16, false) with
            {
                State = "unavailable",
                ErrorCode = "worldMoveTargetBlocked",
                DispatchState = "notDispatched",
                Button = null,
                StartTick = null,
                EndTick = null,
            });
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("worldMoveTargetBlocked", result.ErrorCode);
        Assert.Equal("notDispatched", result.DispatchState);
        Assert.False(result.Reached);
        Assert.Equal((64, 16), (result.TileX, result.TileY));
    }

    [Fact]
    public void CancellationAfterWriteSendsCancelAndReportsMayHaveRun()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        using var cancellation = new CancellationTokenSource();
        int sends = 0;
        ReviewWorldMoveReport result = ProjectReviewWorldMoveService.Execute(reader, new("down", false), command =>
        {
            sends++;
            if (sends == 1)
            {
                Respond(temporary, command.Split(' ')[2], Completed("down", false, 64, 17, true));
                cancellation.Cancel();
            }
            else
            {
                Assert.StartsWith("sdvkit world-move cancel ", command, StringComparison.Ordinal);
            }
            return Ok(temporary);
        }, TimeSpan.FromSeconds(1), cancellationToken: cancellation.Token);
        Assert.Equal(2, sends);
        Assert.Equal("worldActionCancellationRequested", result.ErrorCode);
        Assert.Equal("mayHaveRun", result.DispatchState);
    }

    [Fact]
    public void PermissionAndTopologyFailBeforeDispatch()
    {
        using TemporaryDirectory temporary = new();
        Assert.Equal("worldActionTestSaveRequired", ProjectReviewWorldMoveService.Execute(
            ProjectReviewMcpTests.CreateReadyReview(temporary), new("down", false),
            _ => throw new InvalidOperationException()).ErrorCode);
        Assert.Equal("worldActionTopologyUnsupported", ProjectReviewWorldMoveService.Execute(
            ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "host"), new("down", false),
            _ => throw new InvalidOperationException()).ErrorCode);
    }

    [Fact]
    public async Task McpToolIsDestructiveNonIdempotentAndTyped()
    {
        McpServerTool tool = ProjectReviewMcpWorldMoveTools.Create((query, _) =>
            query == new ReviewWorldMoveQuery("right", false) ? Completed("right", false, 65, 16, true)
            : query == new ReviewWorldMoveQuery("up", true) ? Completed("up", true, 64, 16, true)
            : throw new InvalidOperationException());
        Assert.True(tool.ProtocolTool.Annotations!.DestructiveHint);
        Assert.False(tool.ProtocolTool.Annotations.IdempotentHint);
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "move-test", Version = "1" }, ToolCollection = [tool] };
        await using McpTestClient harness = await McpTestClient.StartAsync(options);
        CallToolResult moved = await harness.Client.CallToolAsync(ProjectReviewMcpWorldMoveTools.ToolName,
            new Dictionary<string, object?> { ["direction"] = "right" }, cancellationToken: harness.Token);
        Assert.False(moved.IsError);
        Assert.True(moved.StructuredContent!.Value.GetProperty("reached").GetBoolean());
        CallToolResult turned = await harness.Client.CallToolAsync(ProjectReviewMcpWorldMoveTools.ToolName,
            new Dictionary<string, object?> { ["direction"] = "up", ["turnOnly"] = true }, cancellationToken: harness.Token);
        Assert.False(turned.IsError);
        CallToolResult rejected = await harness.Client.CallToolAsync(ProjectReviewMcpWorldMoveTools.ToolName,
            new Dictionary<string, object?> { ["direction"] = "north" }, cancellationToken: harness.Token);
        Assert.True(rejected.IsError);
    }

    [Fact]
    public void ServerRegistersTheToolOnlyForAnOptedInUnboundSingleReview()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpWorldMoveRunner run = (_, _) => throw new InvalidOperationException("Listing must not act.");
        string[] Names(ProjectReviewMcpRuntimeReader reader, ProjectReviewMcpWorldMoveRunner? runner) =>
            ProjectReviewMcpServer.CreateOptions(reader, _ => throw new InvalidOperationException(), runWorldMove: runner)
                .ToolCollection!.Select(tool => tool.ProtocolTool.Name).ToArray();
        ProjectReviewMcpRuntimeReader single = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        Assert.Contains(ProjectReviewMcpWorldMoveTools.ToolName, Names(single, run));
        Assert.DoesNotContain(ProjectReviewMcpWorldMoveTools.ToolName, Names(single, null));
        Assert.DoesNotContain(ProjectReviewMcpWorldMoveTools.ToolName,
            Names(ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "host"), run));
    }

    [Theory]
    [InlineData("""{"direction":"down","extra":1}""")]
    [InlineData("""{"direction":"down","turnOnly":"true"}""")]
    [InlineData("""{"direction":"down","turnOnly":1}""")]
    [InlineData("""{"direction":"Down"}""")]
    [InlineData("""{"direction":2}""")]
    [InlineData("""{"turnOnly":true}""")]
    [InlineData("""{}""")]
    public void McpArgumentsMustMatchTheClosedSchema(string json)
    {
        Dictionary<string, JsonElement> values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.False(ProjectReviewMcpWorldMoveTools.TryQuery(values, out _));
    }

    [Theory]
    [InlineData("""{"direction":"down"}""", false)]
    [InlineData("""{"direction":"left","turnOnly":true}""", true)]
    [InlineData("""{"direction":"left","turnOnly":false}""", false)]
    public void McpArgumentsAcceptDirectionAndOptionalTurn(string json, bool turnOnly)
    {
        Dictionary<string, JsonElement> values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.True(ProjectReviewMcpWorldMoveTools.TryQuery(values, out ReviewWorldMoveQuery? query));
        Assert.Equal(turnOnly, query!.TurnOnly);
    }

    [Theory]
    [InlineData("move down --topology network-2 --json")]
    [InlineData("move north --json")]
    [InlineData("move down")]
    [InlineData("move down --turn-only --turn-only --json")]
    [InlineData("move --json")]
    [InlineData("move down 3 --json")]
    public void CliRejectsInvalidArguments(string suffix)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CliApplication.Run(("project review act " + suffix).Split(' '), output, error));
        Assert.Contains("project review act move", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CliHelpNamesTheMoveForm()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["project", "review", "act", "--help"], output, error));
        Assert.Contains("act move <up|right|down|left> [--turn-only]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("worldMoveTargetBlocked", output.ToString(), StringComparison.Ordinal);
    }

    private static ReviewWorldMoveReport Completed(string direction, bool turnOnly, int tileX, int tileY, bool reached)
    {
        (int dx, int dy) = ReviewWorldMoveContract.Delta(direction);
        return new(1, "completed", null, Launch, "single", null, DateTimeOffset.UtcNow, direction, turnOnly,
            "completed", "S", 100, 112, "Farm", tileX, tileY, Position with { Facing = direction },
            64 + dx, 16 + dy, reached);
    }

    private static void Respond(TemporaryDirectory temporary, string requestId, ReviewWorldMoveReport report) =>
        File.WriteAllText(ReviewWorldMoveContract.ResponsePath(LiveLabPaths.Resolve(temporary.Path).RuntimePath, requestId),
            JsonSerializer.Serialize(new ReviewWorldMoveResponseEnvelope(1, requestId, report), JsonOptions));

    private static LiveLabCommandResult Ok(TemporaryDirectory temporary) =>
        new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
}
