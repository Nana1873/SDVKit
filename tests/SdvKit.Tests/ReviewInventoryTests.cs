using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewInventoryTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Capture = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SharedItemAndSlotContractDistinguishesEmptyOccupiedAndUnavailable()
    {
        Assert.True(ReviewItemSlotContract.SlotValid(new(0, "empty", null, null), 0));
        Assert.True(ReviewItemSlotContract.SlotValid(new(1, "occupied", null, new("(O)388", 5, 0)), 1));
        Assert.True(ReviewItemSlotContract.SlotValid(new(2, "unavailable", "itemDataUnavailable", null), 2));
        Assert.False(ReviewItemSlotContract.SlotValid(new(0, "empty", null, new("(O)388", 1, 0)), 0));
        Assert.False(ReviewItemSlotContract.ItemValid(new("(O)388", 0, 0)));
        Assert.False(ReviewItemSlotContract.ItemValid(new("invalid", 1, null)));
    }

    [Fact]
    public void OccupiedCaptureWithInvalidOrThrowingGettersIsExplicitlyUnavailable()
    {
        Assert.Equal("occupied", ReviewItemSlotContract.ReadOccupied(0,
            () => new SelectedItemValues("(O)388", 2, 0)).State);
        Assert.Equal(new ReviewItemSlot(1, "unavailable", "itemDataUnavailable", null),
            ReviewItemSlotContract.ReadOccupied(1, () => new SelectedItemValues("bad", 2, 0)));
        Assert.Equal(new ReviewItemSlot(2, "unavailable", "itemDataUnavailable", null),
            ReviewItemSlotContract.ReadOccupied(2,
                () => throw new InvalidOperationException("custom item getter failed")));
    }

    [Fact]
    public void RevisionChangesWithLaunchPlayerSelectionSlotsAndQuantity()
    {
        IReadOnlyList<ReviewItemSlot> slots = [new(0, "occupied", null, new("(O)388", 5, 0)), new(1, "empty", null, null)];
        string revision = ReviewInventoryContract.Revision(Launch, "123", 2, 0, slots);
        Assert.True(ReviewInventoryContract.IsRevision(revision));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(new string('c', 32), "123", 2, 0, slots));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(Launch, "124", 2, 0, slots));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(Launch, "123", 2, 1, slots));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(Launch, "123", 2, null, slots));
        Assert.NotEqual(revision, ReviewInventoryContract.Revision(Launch, "123", 2, 0,
            [slots[0] with { Item = slots[0].Item! with { Stack = 4 } }, slots[1]]));
    }

    [Fact]
    public void CompleteAndPartialCapturesRequireEveryBoundedSlot()
    {
        ReviewInventoryValues complete = Values([
            new(0, "occupied", null, new("(O)388", 5, 0)),
            new(1, "empty", null, null),
        ]);
        Assert.True(ReviewInventoryContract.DataValid(complete, Launch));
        Assert.True(ReviewInventoryContract.DataValid(WithRevision(complete with { SelectedSlot = null }), Launch));

        ReviewInventoryValues partial = Values([
            complete.Slots[0],
            new(1, "unavailable", "itemDataUnavailable", null),
        ], complete: false);
        Assert.True(ReviewInventoryContract.DataValid(partial, Launch));

        Assert.False(ReviewInventoryContract.DataValid(complete with { Capacity = 3 }, Launch));
        Assert.False(ReviewInventoryContract.DataValid(complete with { SelectedSlot = 2 }, Launch));
        Assert.False(ReviewInventoryContract.DataValid(complete with { Slots = [complete.Slots[1], complete.Slots[0]] }, Launch));
        Assert.False(ReviewInventoryContract.DataValid(partial with { Complete = true, Limitations = [] }, Launch));
        Assert.False(ReviewInventoryContract.DataValid(complete with { Slots = [null!, complete.Slots[1]] }, Launch));
    }

    [Fact]
    public void StrictTransportRejectsMalformedOrUnboundedPayloads()
    {
        string json = JsonSerializer.Serialize(new ReviewInventoryResponseEnvelope(1, Capture, Report()), JsonOptions);
        Assert.NotNull(ProjectReviewInventoryService.DeserializeResponse(Encoding.UTF8.GetBytes(json)));
        foreach (string invalid in new[]
        {
            json.Replace("\"role\":null,", "", StringComparison.Ordinal),
            json.Replace("\"role\":null,", "\"role\":null,\"role\":null,", StringComparison.Ordinal),
            json.Replace("\"stack\":5,", "\"stack\":5,\"privateData\":true,", StringComparison.Ordinal),
            json.Replace("\"slots\":[", "\"slots\":[null,", StringComparison.Ordinal),
            JsonSerializer.Serialize(new ReviewInventoryResponseEnvelope(1, Capture, Report() with
            {
                Data = Values(Enumerable.Range(0, 145).Select(index =>
                    new ReviewItemSlot(index, "empty", null, null)).ToArray()),
            }), JsonOptions),
        })
            Assert.Throws<InvalidDataException>(() =>
                ProjectReviewInventoryService.DeserializeResponse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Theory]
    [InlineData("ready", null)]
    [InlineData("launch", "inventoryResponseInvalid")]
    [InlineData("player", "inventoryResponseInvalid")]
    [InlineData("rebind", "reviewBindingChanged")]
    [InlineData("title", "reviewBindingChanged")]
    [InlineData("unavailable", "inventoryCapacityUnsupported")]
    public void TransportBindsCapturePlayerReviewAndWorld(string change, string? expected)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewInventoryReport result = ProjectReviewInventoryService.Execute(reader, command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal("inventory", parts[1]);
            Assert.Equal(Launch, parts[3]);
            ReviewInventoryReport report = Report(parts[2]);
            if (change == "launch") report = report with { LaunchId = new string('c', 32) };
            if (change == "player") report = report with { Data = Values(report.Data!.Slots, playerId: "124", captureId: parts[2]) };
            if (change == "unavailable") report = report with
            {
                State = "unavailable",
                ErrorCode = "inventoryCapacityUnsupported",
                Data = null,
            };
            Publish(temporary.Path, parts[2], report);
            if (change == "rebind")
            {
                var store = new JsonLiveLabStateStore(LiveLabPaths.Resolve(temporary.Path).StatePath);
                store.Write(store.Read()! with { LaunchId = new string('c', 32) });
            }
            if (change == "title") MoveStatusToTitle(temporary.Path);
            return Sent(temporary.Path);
        }, TimeSpan.Zero);
        Assert.Equal(expected, result.ErrorCode);
        Assert.Equal(expected is null, result.Data is not null);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(5, 5, null)]
    [InlineData(6, 6, "inventoryResponseInvalid")]
    [InlineData(1, 6, "inventoryResponseStale")]
    public void TransportRequiresFreshCaptureBeforeAndAfterFinalRead(int responseSeconds, int returnSeconds,
        string? expected)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        int reads = 0;
        DateTimeOffset Clock() => start.AddSeconds(++reads switch { 1 => 0, 2 => responseSeconds, _ => returnSeconds });
        ReviewInventoryReport result = ProjectReviewInventoryService.Execute(reader, command =>
        {
            string id = command.Split(' ')[2];
            Publish(temporary.Path, id, Report(id) with { CapturedAtUtc = start });
            return Sent(temporary.Path);
        }, TimeSpan.Zero, Clock);
        Assert.Equal(expected, result.ErrorCode);
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole)]
    [InlineData(NetworkTwoContract.FarmhandRole)]
    public void NetworkDispatchesTheSelectedRoleAndCancellationStillStopsBeforeDispatch(string role)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, role);
        ProjectReviewMcpRuntimeSnapshot selected = reader.Read().Snapshot!;
        ReviewInventoryReport ready = ProjectReviewInventoryService.Execute(reader, command =>
        {
            string requestId = command.Split(' ')[2];
            ReviewInventoryReport report = Report(requestId) with
            {
                LaunchId = selected.LaunchId,
                Topology = selected.Topology,
                Role = selected.Role,
                Data = Values([new(0, "occupied", null, new("(O)388", 5, 0)),
                    new(1, "empty", null, null)], playerId: selected.Runtime.LocalPlayer!.Data!.PlayerId,
                    captureId: requestId),
            };
            ReviewInventoryValues data = report.Data!;
            report = report with
            {
                Data = data with
                {
                    InventoryRevision = ReviewInventoryContract.Revision(selected.LaunchId,
                        data.PlayerId, data.Capacity, data.SelectedSlot, data.Slots),
                },
            };
            string runtimePath = ProjectReviewInputService.RuntimePath(temporary.Path, selected.Topology, selected.Role);
            File.WriteAllText(ReviewInventoryContract.ResponsePath(runtimePath, requestId),
                JsonSerializer.Serialize(new ReviewInventoryResponseEnvelope(1, requestId, report), JsonOptions));
            return Sent(temporary.Path);
        }, TimeSpan.Zero);
        Assert.Equal("ready", ready.State);
        Assert.Equal(role, ready.Role);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectReviewInventoryService.Execute(reader,
            _ => throw new InvalidOperationException("Must not send."), cancellationToken: cancellation.Token));
        Assert.Contains(ProjectReviewMcpServer.CreateOptions(reader).ToolCollection!,
            tool => tool.ProtocolTool.Name == ProjectReviewMcpInventoryTools.ToolName);
    }

    [Fact]
    public void LocalScreenReplacementDuringResponseFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        int bindingReads = 0;
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyLocalScreenReview(
            temporary, 1, command =>
            {
                bindingReads++;
                ProjectReviewMcpTests.WriteScreenBindingResponse(temporary, command,
                    bindingReads == 1 ? "202" : "303",
                    bindingReads == 1
                        ? "11111111111111111111111111111111"
                        : "22222222222222222222222222222222");
                return Sent(temporary.Path);
            });
        ReviewInventoryReport result = ProjectReviewInventoryService.Execute(reader, command =>
        {
            string requestId = command.Split(' ')[2];
            Publish(temporary.Path, requestId, Report(requestId) with
            {
                Data = Values([new(0, "occupied", null, new("(O)388", 5, 0)),
                    new(1, "empty", null, null)], playerId: "202", captureId: requestId),
            });
            return Sent(temporary.Path);
        }, TimeSpan.Zero);

        Assert.Equal("reviewBindingChanged", result.ErrorCode);
        Assert.Equal(2, bindingReads);
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("--json --json", 2)]
    [InlineData("--topology network-2 --role host --json", 3)]
    [InlineData("--set 0 1 --json", 2)]
    public void CliRoutingIsReadOnlyAndAcceptsOwnedSelections(string suffix, int expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(expected, CliApplication.Run(("project review inventory " + suffix).Split(' '), output, error));
        if (expected == 3)
        {
            Assert.Contains("\"state\":", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        else
        {
            Assert.Contains("project review inventory", expected == 0 ? output.ToString() : error.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task McpReturnsTheTypedCaptureAndRejectsArguments()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewInventoryReport report = Report();
        McpServerTool tool = ProjectReviewMcpInventoryTools.Create(reader, _ => report);
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations.DestructiveHint);
        JsonElement schema = Assert.IsType<JsonElement>(tool.ProtocolTool.OutputSchema);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "inventory-test", Version = "1" },
            ToolCollection = [tool],
        };
        await using McpTestClient harness = await McpTestClient.StartAsync(options);
        CallToolResult result = await harness.Client.CallToolAsync(ProjectReviewMcpInventoryTools.ToolName,
            new Dictionary<string, object?>(), cancellationToken: harness.Token);
        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(report, JsonOptions),
            JsonSerializer.Serialize(Assert.IsType<JsonElement>(result.StructuredContent)
                .Deserialize<ReviewInventoryReport>(JsonOptions), JsonOptions));
        result = await harness.Client.CallToolAsync(ProjectReviewMcpInventoryTools.ToolName,
            new Dictionary<string, object?> { ["slot"] = 0 }, cancellationToken: harness.Token);
        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
    }

    private static ReviewInventoryValues Values(IReadOnlyList<ReviewItemSlot> slots, bool complete = true,
        string playerId = "101", string captureId = Capture)
    {
        var value = new ReviewInventoryValues(captureId, string.Empty, playerId, slots.Count, 0, complete,
            complete ? [] : ["itemDataUnavailable"], slots);
        return WithRevision(value);
    }

    private static ReviewInventoryValues WithRevision(ReviewInventoryValues value) => value with
    {
        InventoryRevision = ReviewInventoryContract.Revision(Launch, value.PlayerId,
            value.Capacity, value.SelectedSlot, value.Slots),
    };

    private static ReviewInventoryReport Report(string captureId = Capture) => new(1, "ready", null, Launch,
        "single", null, DateTimeOffset.UtcNow, Values([
            new(0, "occupied", null, new("(O)388", 5, 0)),
            new(1, "empty", null, null),
        ], captureId: captureId));

    private static LiveLabCommandResult Sent(string root) => new(0,
        new ProjectReviewCommandReport(1, null, root, "ready", null, true, [], []));

    private static void Publish(string root, string id, ReviewInventoryReport report) =>
        File.WriteAllText(ReviewInventoryContract.ResponsePath(LiveLabPaths.Resolve(root).RuntimePath, id),
            JsonSerializer.Serialize(new ReviewInventoryResponseEnvelope(1, id, report), JsonOptions));

    private static void MoveStatusToTitle(string root)
    {
        string path = LiveLabPaths.Resolve(root).StatusPath;
        AlwaysOnStatusMarker marker = JsonSerializer.Deserialize<AlwaysOnStatusMarker>(
            File.ReadAllText(path), LiveLabJsonOptions.CamelCase)!;
        var title = new RuntimeSnapshotMarker(1, false, null, null, null, null, null, null, null, true,
            marker.ObservedAtUtc, LocalPlayerSnapshotContract.WithoutData("worldNotReady"));
        File.WriteAllText(path,
            JsonSerializer.Serialize(marker with { Runtime = title }, LiveLabJsonOptions.CamelCase));
    }
}
