using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewWorldActionTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Instance = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Revision = "cccccccccccccccccccccccccccccccc";
    private const string Inventory = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("water")]
    [InlineData("harvest")]
    [InlineData("machineInsert")]
    [InlineData("machineCollect")]
    public void ContractAcceptsOnlyExactFreshBindings(string action)
    {
        ReviewWorldActionQuery query = Query(action);
        Assert.Null(ReviewWorldActionContract.Validate(query));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldActionContract.Validate(query with { Action = "grow" }));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldActionContract.Validate(query with { TargetRevision = "stale" }));
        Assert.Equal("worldActionArgumentsInvalid", ReviewWorldActionContract.Validate(query with { InventoryRevision = Revision }));
    }

    [Fact]
    public void CursorMappingAccountsForZoomUiScaleAndRejectsViewportDrift()
    {
        Assert.True(ReviewWorldActionCursor.TryMap(640, 320, .75f, 1.25f,
            800, 600, out int x, out int y));
        Assert.Equal((384, 192), (x, y));
        Assert.True(ReviewWorldActionCursor.TryMap(576, 320, .75f, 1.25f,
            800, 600, out int movedX, out int movedY));
        Assert.NotEqual((x, y), (movedX, movedY));
        Assert.False(ReviewWorldActionCursor.TryMap(1600, 320, .75f, 1.25f,
            800, 600, out _, out _));
    }

    [Fact]
    public void DecisionRejectsStaleWrongBusyUnsupportedAndInsufficientCases()
    {
        ReviewWorldActionFacts soil = CropFacts() with { TargetKind = "soil" };
        Assert.Null(ReviewWorldActionDecision.Rejection(Query("water"), soil));
        Assert.Equal("worldActionBindingInvalid", ReviewWorldActionDecision.Rejection(Query("water"), soil with { BindingReady = false }));
        Assert.Equal("worldActionTestSaveRequired", ReviewWorldActionDecision.Rejection(Query("water"), soil with { FixtureReady = false }));
        Assert.Equal("worldActionPlayerBusy", ReviewWorldActionDecision.Rejection(Query("water"), soil with { PlayerReady = false }));
        Assert.Equal("worldActionTargetNotAdjacentAndFaced", ReviewWorldActionDecision.Rejection(Query("water"), soil with { FacedAdjacent = false }));
        Assert.Equal("worldActionInventoryChanged", ReviewWorldActionDecision.Rejection(Query("water"), soil with { InventoryMatches = false }));
        Assert.Equal("worldActionTargetChanged", ReviewWorldActionDecision.Rejection(Query("water"), soil with { TargetMatches = false }));
        Assert.Equal("worldActionWrongToolOrState", ReviewWorldActionDecision.Rejection(Query("water"), soil with { WateringCanSelected = false }));
        Assert.Equal("worldActionInsufficientResource", ReviewWorldActionDecision.Rejection(Query("water"), soil with { HasWater = false }));

        ReviewWorldActionFacts harvest = soil with
        {
            TargetKind = "crop",
            SoilNeedsWater = false,
            WateringCanSelected = false,
            HasWater = false,
            HarvestReady = true,
            GrabHarvest = true,
            InventoryCanAccept = true
        };
        Assert.Null(ReviewWorldActionDecision.Rejection(Query("harvest"), harvest));
        Assert.Equal("worldActionHarvestMethodUnsupported", ReviewWorldActionDecision.Rejection(
            Query("harvest"), harvest with { GrabHarvest = false }));
        Assert.Equal("worldActionInventoryFull", ReviewWorldActionDecision.Rejection(
            Query("harvest"), harvest with { InventoryCanAccept = false }));

        ReviewWorldActionFacts machine = harvest with
        {
            TargetKind = "machine",
            MachineState = "idle",
            SelectedMachineItem = true,
            MachineAcceptsItem = true,
            EmptyHand = false
        };
        Assert.Null(ReviewWorldActionDecision.Rejection(Query("machineInsert"), machine));
        Assert.Equal("worldActionItemNotAccepted", ReviewWorldActionDecision.Rejection(
            Query("machineInsert"), machine with { MachineAcceptsItem = false }));
        machine = machine with
        {
            MachineState = "ready",
            SelectedMachineItem = false,
            EmptyHand = true,
            InventoryCanAccept = true
        };
        Assert.Null(ReviewWorldActionDecision.Rejection(Query("machineCollect"), machine));
        Assert.Equal("worldActionInventoryFull", ReviewWorldActionDecision.Rejection(
            Query("machineCollect"), machine with { InventoryCanAccept = false }));
    }

    [Fact]
    public void DispatchGateRechecksCursorAndStateAtActualEdge()
    {
        (int X, int Y)? cursor = (10, 20);
        string? stateProblem = null;
        var gate = new ReviewWorldActionDispatchGate(10, 20, () => cursor, () => stateProblem);
        Assert.Null(gate.Validate());
        cursor = (11, 20);
        Assert.Equal("worldActionViewportOrCursorChanged", gate.Validate());
        int inputSamples = 0;
        if (ReviewWorldActionDispatchGate.TryAuthorize(gate.Validate, out string? cursorProblem))
            inputSamples++;
        Assert.Equal(0, inputSamples);
        Assert.Equal("worldActionViewportOrCursorChanged", cursorProblem);
        cursor = (10, 20);
        stateProblem = "worldActionTargetChanged";
        Assert.Equal("worldActionTargetChanged", gate.Validate());
        if (ReviewWorldActionDispatchGate.TryAuthorize(gate.Validate, out string? stateRejection))
            inputSamples++;
        Assert.Equal(0, inputSamples);
        Assert.Equal("worldActionTargetChanged", stateRejection);
    }

    [Fact]
    public void ResponseParserRejectsUnknownAndDuplicateFields()
    {
        string json = JsonSerializer.Serialize(new ReviewWorldActionResponseEnvelope(1, Launch,
            Report("water", "completed", null, "completed", "MouseLeft")), JsonOptions);
        Assert.NotNull(ProjectReviewWorldActionService.Deserialize(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<InvalidDataException>(() => ProjectReviewWorldActionService.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("\"role\":null,", "\"role\":null,\"secret\":1,", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => ProjectReviewWorldActionService.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("\"role\":null,", "\"role\":null,\"role\":null,", StringComparison.Ordinal))));
    }

    [Fact]
    public void ServiceBindsCommandAndKeepsEffectProofSeparate()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ReviewWorldActionQuery query = Query("machineInsert");
        ReviewWorldActionReport result = ProjectReviewWorldActionService.Execute(reader, query, command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal(["sdvkit", "world-action"], parts.Take(2));
            Assert.Equal(Launch, parts[3]);
            Assert.Equal(["machineInsert", "7", "9", Instance, Revision, Inventory, "single"], parts.Skip(4));
            File.WriteAllText(ReviewWorldActionContract.ResponsePath(
                    LiveLabPaths.Resolve(temporary.Path).RuntimePath, parts[2]),
                JsonSerializer.Serialize(new ReviewWorldActionResponseEnvelope(1, parts[2],
                    Report("machineInsert", "completed", null, "completed", "MouseRight")), JsonOptions));
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        }, TimeSpan.Zero);
        Assert.Equal("completed", result.State);
        Assert.Equal("completed", result.DispatchState);
        Assert.DoesNotContain("effect", JsonSerializer.Serialize(result, JsonOptions), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("harvest")]
    [InlineData("machineInsert")]
    [InlineData("machineCollect")]
    public void NativeWrongItemOrStateRejectionSurvivesTransport(string action)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ReviewWorldActionReport result = ProjectReviewWorldActionService.Execute(reader, Query(action), command =>
        {
            string requestId = command.Split(' ')[2];
            File.WriteAllText(ReviewWorldActionContract.ResponsePath(
                    LiveLabPaths.Resolve(temporary.Path).RuntimePath, requestId),
                JsonSerializer.Serialize(new ReviewWorldActionResponseEnvelope(1, requestId,
                    Report(action, "unavailable", "worldActionWrongItemOrState", "notDispatched", null)),
                    JsonOptions));
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        }, TimeSpan.Zero);
        Assert.Equal("unavailable", result.State);
        Assert.Equal("worldActionWrongItemOrState", result.ErrorCode);
        Assert.Equal("notDispatched", result.DispatchState);
    }

    [Fact]
    public void CancellationAfterWriteIsNotReplayedAndReportsMayHaveRun()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        using var cancellation = new CancellationTokenSource();
        int sends = 0;
        ReviewWorldActionReport result = ProjectReviewWorldActionService.Execute(reader, Query("water"), command =>
        {
            sends++;
            if (sends == 1)
            {
                string requestId = command.Split(' ')[2];
                File.WriteAllText(ReviewWorldActionContract.ResponsePath(
                        LiveLabPaths.Resolve(temporary.Path).RuntimePath, requestId),
                    JsonSerializer.Serialize(new ReviewWorldActionResponseEnvelope(1, requestId,
                        Report("water", "completed", null, "completed", "MouseLeft")), JsonOptions));
                cancellation.Cancel();
            }
            else
            {
                Assert.StartsWith("sdvkit world-action cancel ", command, StringComparison.Ordinal);
            }
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        }, TimeSpan.FromSeconds(1), cancellationToken: cancellation.Token);
        Assert.Equal(2, sends);
        Assert.Equal("worldActionCancellationRequested", result.ErrorCode);
        Assert.Equal("mayHaveRun", result.DispatchState);
    }

    [Fact]
    public void PermissionAndTopologyFailBeforeDispatch()
    {
        using TemporaryDirectory temporary = new();
        Assert.Equal("worldActionTestSaveRequired", ProjectReviewWorldActionService.Execute(
            ProjectReviewMcpTests.CreateReadyReview(temporary), Query("water"),
            _ => throw new InvalidOperationException()).ErrorCode);
        Assert.Equal("worldActionTopologyUnsupported", ProjectReviewWorldActionService.Execute(
            ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "host"), Query("water"),
            _ => throw new InvalidOperationException()).ErrorCode);
        using TemporaryDirectory cancellationRoot = new();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectReviewWorldActionService.Execute(
            ProjectReviewMcpTests.CreateReadyReview(cancellationRoot, withTestSave: true), Query("water"),
            _ => throw new InvalidOperationException(), cancellationToken: canceled.Token));
    }

    [Fact]
    public void StartupPermissionCannotMoveToALaterLaunch()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ProjectReviewMcpVerifiedContext startup = Assert.IsType<ProjectReviewMcpVerifiedContext>(reader.ReadContext().Context);
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabState state = Assert.IsType<LiveLabState>(new JsonLiveLabStateStore(paths.StatePath).Read());
        const string later = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        AlwaysOnStatusMarker status = JsonSerializer.Deserialize<AlwaysOnStatusMarker>(
            File.ReadAllText(paths.StatusPath), LiveLabJsonOptions.CamelCase)!;
        int dispatches = 0;
        ReviewWorldActionReport result = ProjectReviewWorldActionService.Execute(reader, Query("water"),
            _ => { dispatches++; throw new InvalidOperationException("A later launch must not inherit permission."); },
            expectedContext: startup,
            beforeSnapshotRead: () =>
            {
                new JsonLiveLabStateStore(paths.StatePath).Write(state with { LaunchId = later });
                File.WriteAllText(paths.StatusPath, JsonSerializer.Serialize(status with { LaunchId = later },
                    LiveLabJsonOptions.CamelCase));
            });
        Assert.Equal("worldActionStartupBindingChanged", result.ErrorCode);
        Assert.Equal("notDispatched", result.DispatchState);
        Assert.Equal(0, dispatches);
    }

    [Fact]
    public void SnapshotChangeBetweenPermissionCheckAndDispatchIsRejected()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ProjectReviewMcpVerifiedContext permission = Assert.IsType<ProjectReviewMcpVerifiedContext>(reader.ReadContext().Context);
        ProjectReviewMcpRuntimeSnapshot snapshot = Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(reader.Read().Snapshot);
        Assert.True(ProjectReviewWorldActionService.SamePermissionSnapshot(permission, snapshot));
        Assert.False(ProjectReviewWorldActionService.SamePermissionSnapshot(permission,
            snapshot with { LaunchId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" }));
        Assert.False(ProjectReviewWorldActionService.SamePermissionSnapshot(permission,
            snapshot with { Target = snapshot.Target with { BuildIdentity = "sha256:" + new string('f', 64) } }));
        Assert.False(ProjectReviewWorldActionService.SamePermissionSnapshot(permission,
            snapshot with { TestSave = snapshot.TestSave! with { SaveId = "SDVKit_other" } }));
    }

    [Fact]
    public async Task McpToolIsDestructiveNonIdempotentAndTyped()
    {
        ReviewWorldActionQuery expected = Query("harvest");
        McpServerTool tool = ProjectReviewMcpWorldActionTools.Create((query, _) =>
            query == expected ? Report("harvest", "completed", null, "completed", "MouseRight")
                : throw new InvalidOperationException());
        Assert.True(tool.ProtocolTool.Annotations!.DestructiveHint);
        Assert.False(tool.ProtocolTool.Annotations.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations.IdempotentHint);
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "action-test", Version = "1" }, ToolCollection = [tool] };
        await using McpTestClient harness = await McpTestClient.StartAsync(options);
        CallToolResult result = await harness.Client.CallToolAsync(ProjectReviewMcpWorldActionTools.ToolName,
            new Dictionary<string, object?>
            {
                ["action"] = "harvest",
                ["x"] = 7,
                ["y"] = 9,
                ["targetInstanceId"] = Instance,
                ["targetRevision"] = Revision,
                ["inventoryRevision"] = Inventory
            }, cancellationToken: harness.Token);
        Assert.False(result.IsError);
    }

    [Theory]
    [InlineData("water 7 9 bad cccccccccccccccccccccccccccccccc sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd --json")]
    [InlineData("water 7 9 bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb cccccccccccccccccccccccccccccccc sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd --topology network-2 --json")]
    public void CliRejectsUnboundOrUnsupportedScope(string suffix)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CliApplication.Run(("project review interact " + suffix).Split(' '), output, error));
        Assert.Contains("project review interact", error.ToString(), StringComparison.Ordinal);
    }

    private static ReviewWorldActionQuery Query(string action) => new(action, 7, 9, Instance, Revision, Inventory);
    private static ReviewWorldActionFacts CropFacts() => new(true, true, true, true, true, true,
        "crop", true, true, false, true, true, true, true, false, false, false,
        null, false, false);
    private static ReviewWorldActionReport Report(string action, string state, string? error,
        string dispatch, string? button) => new(1, state, error, Launch, "single", null,
            DateTimeOffset.UtcNow, action, 7, 9, dispatch, button,
            dispatch == "completed" ? 100 : null, dispatch == "completed" ? 101 : null);
}
