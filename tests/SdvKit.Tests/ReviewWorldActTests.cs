using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewWorldActTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Inventory = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("check")]
    [InlineData("useTool")]
    public void ContractAcceptsOnlyKnownActionsBoundedTilesAndExactRevisions(string action)
    {
        ReviewWorldActQuery query = Query(action);
        Assert.Null(ReviewWorldActContract.Validate(query));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldActContract.Validate(query with { Action = "water" }));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldActContract.Validate(query with { X = 100001 }));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldActContract.Validate(query with { InventoryRevision = "stale" }));
        Assert.Equal(action == "useTool" ? "MouseLeft" : "MouseRight", ReviewWorldActContract.ButtonFor(action));
    }

    [Theory]
    [InlineData(5, 5, 5, 6, true)]
    [InlineData(5, 5, 6, 5, true)]
    [InlineData(5, 5, 4, 5, true)]
    [InlineData(5, 5, 5, 4, true)]
    [InlineData(5, 5, 5, 5, false)]
    [InlineData(5, 5, 6, 6, false)]
    [InlineData(5, 5, 5, 7, false)]
    public void TargetMustBeOneOfTheFourNeighbours(int px, int py, int tx, int ty, bool expected) =>
        Assert.Equal(expected, ReviewWorldActContract.IsAdjacent(px, py, tx, ty));

    [Fact]
    public void RejectionOrderMatchesTheWorldActionGates()
    {
        var ready = new ReviewWorldActFacts(true, true, true, true, true, true, true, true);
        Assert.Null(ReviewWorldActContract.Rejection(Query("check"), ready));
        Assert.Null(ReviewWorldActContract.Rejection(Query("check"), ready with { ToolSelected = false }));
        Assert.Equal("worldActionWrongToolOrState", ReviewWorldActContract.Rejection(Query("useTool"), ready with { ToolSelected = false }));
        Assert.Equal("worldActionBindingInvalid", ReviewWorldActContract.Rejection(Query("check"), ready with { BindingReady = false }));
        Assert.Equal("worldActionTestSaveRequired", ReviewWorldActContract.Rejection(Query("check"), ready with { FixtureReady = false }));
        Assert.Equal("worldActionWorldNotReady", ReviewWorldActContract.Rejection(Query("check"), ready with { WorldReady = false }));
        Assert.Equal("worldActionPlayerBusy", ReviewWorldActContract.Rejection(Query("check"), ready with { PlayerReady = false }));
        Assert.Equal("worldActionCursorAimUnavailable", ReviewWorldActContract.Rejection(Query("check"), ready with { CursorAimed = false }));
        Assert.Equal("worldActionPlayerBusy", ReviewWorldActContract.Rejection(Query("check"), ready with { PlayerReady = false, CursorAimed = false }));
        Assert.Equal("worldActionTargetNotAdjacent", ReviewWorldActContract.Rejection(Query("check"), ready with { Adjacent = false }));
        Assert.Equal("worldActionInventoryChanged", ReviewWorldActContract.Rejection(Query("check"), ready with { InventoryMatches = false }));
        Assert.True(ReviewWorldActionContract.IsGameError("worldActionTargetNotAdjacent"));
        Assert.True(ReviewWorldActionContract.IsGameError("worldActionCursorAimUnavailable"));
    }

    [Theory]
    [InlineData(true, Inventory, false, true, "notDispatched")]
    [InlineData(false, Inventory, false, true, "mayHaveRun")]
    [InlineData(true, "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", false, true, "mayHaveRun")]
    [InlineData(true, null, false, true, "mayHaveRun")]
    [InlineData(true, Inventory, true, true, "mayHaveRun")]
    [InlineData(true, Inventory, false, false, "mayHaveRun")]
    public void UnsentActNeedsSettledInputSameBackpackNoMenuAndSameSurroundings(bool settled, string? inventory,
        bool menuOpen, bool surroundings, string expected) =>
        Assert.Equal(expected, ReviewWorldActContract.UnsentDispatchState(Query("check"), settled, inventory,
            menuOpen, surroundings));

    [Fact]
    public void ServiceBindsTheCommandAndChecksTheButton()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ReviewWorldActionReport result = ProjectReviewWorldActService.Execute(reader, Query("useTool"), command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal(["sdvkit", "world-act"], parts.Take(2));
            Assert.Equal(Launch, parts[3]);
            Assert.Equal(["useTool", "7", "9", Inventory, "single"], parts.Skip(4));
            Respond(temporary, parts[2], Report("useTool", "completed", null, "completed", "MouseLeft"));
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("completed", result.State);

        ReviewWorldActionReport wrongButton = ProjectReviewWorldActService.Execute(reader, Query("check"), command =>
        {
            Respond(temporary, command.Split(' ')[2], Report("check", "completed", null, "completed", "MouseLeft"));
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("unavailable", wrongButton.State);
        Assert.Equal("mayHaveRun", wrongButton.DispatchState);
    }

    [Fact]
    public void NotAdjacentRefusalSurvivesTransport()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ReviewWorldActionReport result = ProjectReviewWorldActService.Execute(reader, Query("check"), command =>
        {
            Respond(temporary, command.Split(' ')[2], Report("check", "unavailable", "worldActionTargetNotAdjacent", "notDispatched", null));
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("worldActionTargetNotAdjacent", result.ErrorCode);
        Assert.Equal("notDispatched", result.DispatchState);
    }

    [Fact]
    public void CancellationAfterWriteSendsCancelAndReportsMayHaveRun()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        using var cancellation = new CancellationTokenSource();
        int sends = 0;
        ReviewWorldActionReport result = ProjectReviewWorldActService.Execute(reader, Query("check"), command =>
        {
            sends++;
            if (sends == 1)
            {
                Respond(temporary, command.Split(' ')[2], Report("check", "completed", null, "completed", "MouseRight"));
                cancellation.Cancel();
            }
            else
            {
                Assert.StartsWith("sdvkit world-act cancel ", command, StringComparison.Ordinal);
            }
            return Ok(temporary);
        }, TimeSpan.FromSeconds(1), cancellationToken: cancellation.Token);
        Assert.Equal(2, sends);
        Assert.Equal("worldActionCancellationRequested", result.ErrorCode);
        Assert.Equal("mayHaveRun", result.DispatchState);
    }

    [Fact]
    public void PermissionFailsBeforeDispatch()
    {
        using TemporaryDirectory temporary = new();
        Assert.Equal("worldActionTestSaveRequired", ProjectReviewWorldActService.Execute(
            ProjectReviewMcpTests.CreateReadyReview(temporary), Query("check"),
            _ => throw new InvalidOperationException()).ErrorCode);
    }

    [Fact]
    public async Task McpToolIsDestructiveNonIdempotentAndTyped()
    {
        ReviewWorldActQuery expected = Query("check");
        McpServerTool tool = ProjectReviewMcpWorldActTools.Create((query, _) =>
            query == expected ? Report("check", "completed", null, "completed", "MouseRight")
                : throw new InvalidOperationException());
        Assert.True(tool.ProtocolTool.Annotations!.DestructiveHint);
        Assert.False(tool.ProtocolTool.Annotations.IdempotentHint);
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "act-test", Version = "1" }, ToolCollection = [tool] };
        await using McpTestClient harness = await McpTestClient.StartAsync(options);
        CallToolResult result = await harness.Client.CallToolAsync(ProjectReviewMcpWorldActTools.ToolName,
            new Dictionary<string, object?> { ["action"] = "check", ["x"] = 7, ["y"] = 9, ["inventoryRevision"] = Inventory },
            cancellationToken: harness.Token);
        Assert.False(result.IsError);
        CallToolResult rejected = await harness.Client.CallToolAsync(ProjectReviewMcpWorldActTools.ToolName,
            new Dictionary<string, object?> { ["action"] = "water", ["x"] = 7, ["y"] = 9, ["inventoryRevision"] = Inventory },
            cancellationToken: harness.Token);
        Assert.True(rejected.IsError);
    }

    [Fact]
    public void ServerRegistersTheToolOnlyWhenOptedInForEverySelection()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpWorldActRunner run = (_, _) => throw new InvalidOperationException("Listing must not act.");
        string[] Names(ProjectReviewMcpRuntimeReader reader, ProjectReviewMcpWorldActRunner? runner) =>
            ProjectReviewMcpServer.CreateOptions(reader, _ => throw new InvalidOperationException(), runWorldAct: runner)
                .ToolCollection!.Select(tool => tool.ProtocolTool.Name).ToArray();
        ProjectReviewMcpRuntimeReader single = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        Assert.Contains(ProjectReviewMcpWorldActTools.ToolName, Names(single, run));
        Assert.DoesNotContain(ProjectReviewMcpWorldActTools.ToolName, Names(single, null));
        Assert.Contains(ProjectReviewMcpWorldActTools.ToolName,
            Names(ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "farmhand"), run));
    }

    [Theory]
    [InlineData("""{"action":"check","x":7,"y":9,"inventoryRevision":"INV","extra":1}""")]
    [InlineData("""{"action":"check","x":7.5,"y":9,"inventoryRevision":"INV"}""")]
    [InlineData("""{"action":"check","x":"7","y":9,"inventoryRevision":"INV"}""")]
    [InlineData("""{"action":"check","x":7,"inventoryRevision":"INV"}""")]
    [InlineData("""{"action":"check","x":7,"y":100001,"inventoryRevision":"INV"}""")]
    public void McpArgumentsMustMatchTheClosedSchema(string json)
    {
        Dictionary<string, JsonElement> values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            json.Replace("INV", Inventory, StringComparison.Ordinal))!;
        Assert.False(ProjectReviewMcpWorldActTools.TryQuery(values, out _));
    }

    [Theory]
    [InlineData("check 7 9 sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd --topology network-2 --json")]
    [InlineData("water 7 9 sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd --json")]
    [InlineData("check 7 9 stale --json")]
    [InlineData("check 7 9 sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")]
    public void CliRejectsInvalidArguments(string suffix)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CliApplication.Run(("project review act " + suffix).Split(' '), output, error));
        Assert.Contains("project review act", error.ToString(), StringComparison.Ordinal);
    }

    private static ReviewWorldActQuery Query(string action) => new(action, 7, 9, Inventory);

    private static ReviewWorldActionReport Report(string action, string state, string? error, string dispatch, string? button) =>
        new(1, state, error, Launch, "single", null, DateTimeOffset.UtcNow, action, 7, 9, dispatch, button,
            dispatch == "completed" ? 100 : null, dispatch == "completed" ? 101 : null);

    private static void Respond(TemporaryDirectory temporary, string requestId, ReviewWorldActionReport report) =>
        File.WriteAllText(ReviewWorldActContract.ResponsePath(LiveLabPaths.Resolve(temporary.Path).RuntimePath, requestId),
            JsonSerializer.Serialize(new ReviewWorldActionResponseEnvelope(1, requestId, report), JsonOptions));

    private static LiveLabCommandResult Ok(TemporaryDirectory temporary) =>
        new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
}
