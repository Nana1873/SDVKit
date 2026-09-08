using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewContainerTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Capture = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Scope = "cccccccccccccccccccccccccccccccc";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SupportBoundaryRejectsAmbiguousSpecialRemoteAndUnboundedContainers()
    {
        Assert.Null(Reason());
        Assert.Equal("containerMenuUnsupported", Reason(exactMenu: false));
        Assert.Equal("containerMenuUnsupported", Reason(hasChild: true));
        Assert.Equal("containerBackingAmbiguous", Reason(sameSourceItem: false));
        Assert.Equal("containerFamilyUnsupported", Reason(regularVanillaId: false));
        Assert.Equal("containerFamilyUnsupported", Reason(regularFlags: false));
        Assert.Equal("containerLocationUnavailable", Reason(placedAtCurrentTile: false));
        Assert.Equal("containerBackingAmbiguous", Reason(sameContainerInventory: false));
        Assert.Equal("containerBackingAmbiguous", Reason(samePlayerInventory: false));
        Assert.Equal("containerCapacityUnsupported", Reason(containerCapacity: 70));
        Assert.Equal("containerCapacityUnsupported", Reason(containerCount: 37));
        Assert.Equal("containerCapacityUnsupported", Reason(playerCount: 145));
    }

    [Fact]
    public void SelectionAndRevisionInvalidateAtTheirDocumentedLifetimes()
    {
        ReviewContainerValues first = Values();
        Assert.True(ReviewContainerContract.DataValid(first, Launch));
        ReviewContainerValues reopened = Values(scope: new string('d', 32));
        ReviewContainerValues otherChest = Values(tileX: 8);
        ReviewContainerValues replacedBacking = Values(backingObservationId: 100);
        Assert.NotEqual(first.SelectionIdentity, reopened.SelectionIdentity);
        Assert.NotEqual(first.SelectionIdentity, otherChest.SelectionIdentity);
        Assert.NotEqual(first.SelectionIdentity, replacedBacking.SelectionIdentity);

        ReviewContainerValues quantity = Values(containerItem: new("(O)390", 3, 0));
        ReviewContainerValues quality = Values(containerItem: new("(O)390", 2, 2));
        ReviewContainerValues player = Values(playerItem: new("(O)388", 4, 0));
        ReviewContainerValues held = Values(held: new("(O)390", 1, 0));
        ReviewContainerValues replacedSameFacts = Values(containerObservationId: 8);
        ReviewContainerValues stackingState = Values(containerName: "Stone (different stacking name)");
        Assert.Equal(first.SelectionIdentity, quantity.SelectionIdentity);
        Assert.NotEqual(first.ContainerRevision, quantity.ContainerRevision);
        Assert.NotEqual(first.ContainerRevision, quality.ContainerRevision);
        Assert.NotEqual(first.ContainerRevision, player.ContainerRevision);
        Assert.NotEqual(first.ContainerRevision, held.ContainerRevision);
        Assert.NotEqual(first.ContainerRevision, replacedSameFacts.ContainerRevision);
        Assert.NotEqual(first.ContainerRevision, stackingState.ContainerRevision);
    }

    [Fact]
    public void NativeItemRevisionCoversEveryInstalledVanillaStackComparisonInput()
    {
        string instance = ReviewContainerContract.OpaqueIdentity(Launch, Scope, "item", 1);
        string Baseline() => NativeRevision(instance, "StardewValley.Object", 999, "Stone", null, null);
        Assert.NotEqual(Baseline(), NativeRevision(instance,
            "StardewValley.Objects.ColoredObject", 999, "Stone", null, null));
        Assert.NotEqual(Baseline(), NativeRevision(instance, "StardewValley.Object", 1, "Stone", null, null));
        Assert.NotEqual(Baseline(), NativeRevision(instance,
            "StardewValley.Object", 999, "Flavored Stone", null, null));
        Assert.NotEqual(Baseline(), NativeRevision(instance,
            "StardewValley.Object", 999, "Stone", 0xff00ffff, null));
        Assert.NotEqual(Baseline(), NativeRevision(instance,
            "StardewValley.Object", 999, "Stone", null, "order-a"));
        Assert.NotEqual(Baseline(), NativeRevision(instance,
            "StardewValley.Object", 999, "Stone", null, string.Empty));
        Assert.False(ReviewContainerContract.TryNativeItemRevision(instance, "StardewValley.Object", 999,
            new string('x', 257), null, null, out _));
        Assert.False(ReviewContainerContract.TryNativeItemRevision(instance, "StardewValley.Object", 999,
            "Stone", null, new string('x', 257), out _));
    }

    [Fact]
    public void CompletePartialAndExplicitSidesAreValidated()
    {
        ReviewContainerValues complete = Values();
        Assert.Equal("player", complete.Player.Side);
        Assert.Equal("container", complete.Container.Side);
        Assert.Equal(36, complete.Container.Slots.Count);
        ReviewContainerValues partial = WithRevisions(complete with
        {
            Container = complete.Container with
            {
                Slots = complete.Container.Slots.Select((slot, index) => index == 1
                    ? new ReviewContainerSlot(1, "unavailable", "itemDataUnavailable", null,
                        Identity(Scope, 7)) : slot).ToArray(),
            },
            Complete = false,
            Limitations = ["itemDataUnavailable"],
        });
        Assert.True(ReviewContainerContract.DataValid(partial, Launch));
        Assert.False(ReviewContainerContract.DataValid(partial with { Complete = true, Limitations = [] }, Launch));
        Assert.False(ReviewContainerContract.DataValid(WithRevisions(complete with
        {
            Container = complete.Container with { Side = "player" },
        }), Launch));
        Assert.False(ReviewContainerContract.DataValid(complete with { ChestItemId = "(BC)BigChest" }, Launch));
    }

    [Fact]
    public void HeldItemGetterFailureIsPartialRatherThanFatal()
    {
        ReviewObservedItem empty = ReviewContainerContract.ReadItem(new(false,
            () => throw new InvalidOperationException("not called")));
        ReviewObservedItem failed = ReviewContainerContract.ReadItem(new(true,
            () => throw new InvalidOperationException("custom item getter"), Identity(Scope, 4)));
        Assert.Equal("empty", empty.State);
        Assert.Equal(new("unavailable", "itemDataUnavailable", null, Identity(Scope, 4)), failed);
    }

    [Fact]
    public void StrictTransportRejectsUnknownOrOversizedData()
    {
        string json = JsonSerializer.Serialize(new ReviewContainerResponseEnvelope(1, Capture, Report()), JsonOptions);
        Assert.NotNull(ProjectReviewContainerService.DeserializeResponse(Encoding.UTF8.GetBytes(json)));
        foreach (string invalid in new[]
        {
            json.Replace("\"role\":null,", "", StringComparison.Ordinal),
            json.Replace("\"tileX\":7,", "\"tileX\":7,\"privateHandle\":1,", StringComparison.Ordinal),
            json.Replace("\"stack\":2,", "\"stack\":2,\"privateData\":true,", StringComparison.Ordinal),
            json.Replace("\"slots\":[", "\"slots\":[null,", StringComparison.Ordinal),
        })
            Assert.Throws<InvalidDataException>(() =>
                ProjectReviewContainerService.DeserializeResponse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Theory]
    [InlineData("ready", null)]
    [InlineData("launch", "containerResponseInvalid")]
    [InlineData("player", "containerResponseInvalid")]
    [InlineData("rebind", "reviewBindingChanged")]
    [InlineData("unavailable", "containerMenuUnsupported")]
    public void TransportBindsCapturePlayerAndReview(string change, string? expected)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewContainerReport result = ProjectReviewContainerService.Execute(reader, command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal("container", parts[1]);
            ReviewContainerReport report = Report(parts[2]);
            if (change == "launch") report = report with { LaunchId = new string('d', 32) };
            if (change == "player") report = report with { Data = Values(captureId: parts[2], playerId: "124") };
            if (change == "unavailable") report = report with
            {
                State = "unavailable",
                ErrorCode = "containerMenuUnsupported",
                Data = null,
            };
            Publish(temporary.Path, parts[2], report);
            if (change == "rebind")
            {
                var store = new JsonLiveLabStateStore(LiveLabPaths.Resolve(temporary.Path).StatePath);
                store.Write(store.Read()! with { LaunchId = new string('d', 32) });
            }
            return Sent(temporary.Path);
        }, TimeSpan.Zero);
        Assert.Equal(expected, result.ErrorCode);
    }

    [Fact]
    public void NetworkAndCliMutationShapesAreRejected()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "host");
        Assert.Equal("containerTopologyUnsupported", ProjectReviewContainerService.Execute(reader,
            _ => throw new InvalidOperationException("Must not send.")).ErrorCode);
        Assert.DoesNotContain(ProjectReviewMcpServer.CreateOptions(reader).ToolCollection!,
            tool => tool.ProtocolTool.Name == ProjectReviewMcpContainerTools.ToolName);

        foreach ((string suffix, int exit) in new[]
        {
            ("--help", 0), ("--json --json", 2), ("--topology network-2 --role host --json", 2),
            ("--take 0 --json", 2),
        })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(exit, CliApplication.Run(("project review container " + suffix).Split(' '), output, error));
            Assert.Contains("project review container", exit == 0 ? output.ToString() : error.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task McpReturnsTypedCaptureAndRejectsArguments()
    {
        using TemporaryDirectory temporary = new();
        McpServerTool tool = ProjectReviewMcpContainerTools.Create(
            ProjectReviewMcpTests.CreateReadyReview(temporary), _ => Report());
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations.DestructiveHint);
        await using McpTestClient harness = await McpTestClient.StartAsync(new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "container-test", Version = "1" },
            ToolCollection = [tool],
        });
        CallToolResult result = await harness.Client.CallToolAsync(ProjectReviewMcpContainerTools.ToolName,
            new Dictionary<string, object?>(), cancellationToken: harness.Token);
        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        result = await harness.Client.CallToolAsync(ProjectReviewMcpContainerTools.ToolName,
            new Dictionary<string, object?> { ["take"] = 0 }, cancellationToken: harness.Token);
        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
    }

    private static string? Reason(bool exactMenu = true, bool hasChild = false, bool chestSource = true,
        bool exactChest = true, bool sameSourceItem = true, bool regularVanillaId = true,
        bool regularFlags = true, bool placedAtCurrentTile = true, bool sameContainerInventory = true,
        bool samePlayerInventory = true, int containerCapacity = 36, int containerCount = 36, int playerCount = 12) =>
        ReviewContainerContract.UnavailableReason(exactMenu, hasChild, chestSource, exactChest, sameSourceItem,
            regularVanillaId, regularFlags, placedAtCurrentTile, sameContainerInventory, samePlayerInventory,
            containerCapacity, containerCount, playerCount);

    private static ReviewContainerValues Values(string captureId = Capture, string scope = Scope,
        string playerId = "101", int tileX = 7, SelectedItemValues? playerItem = null,
        SelectedItemValues? containerItem = null, SelectedItemValues? held = null,
        long containerObservationId = 2, string containerName = "Stone", long backingObservationId = 99)
    {
        ReviewContainerSide player = Side("player", 12, playerItem ?? new("(T)Axe", 1, null),
            Identity(scope, 1, maximumStackSize: 1));
        ReviewContainerSide container = Side("container", 36, containerItem ?? new("(O)390", 2, 0),
            Identity(scope, containerObservationId, name: containerName));
        ReviewObservedItem heldItem = held is null
            ? new("empty", null, null, null)
            : new("occupied", null, held, Identity(scope, 3));
        string backing = ReviewContainerContract.OpaqueIdentity(Launch, scope, "chest", backingObservationId);
        return WithRevisions(new(captureId, "", "", scope, backing, playerId, "Farm", tileX, 9, "(BC)130",
            player, container, heldItem, true, []));
    }

    private static ReviewContainerValues WithRevisions(ReviewContainerValues value)
    {
        string selection = ReviewContainerContract.SelectionIdentity(Launch, value.IdentityScope,
            value.BackingIdentity, value.PlayerId, value.LocationName, value.TileX, value.TileY, value.ChestItemId);
        return value with
        {
            SelectionIdentity = selection,
            ContainerRevision = ReviewContainerContract.Revision(selection, value.Player, value.Container, value.HeldItem)
        };
    }

    private static ReviewContainerSide Side(string side, int capacity, SelectedItemValues first,
        ReviewContainerItemIdentity identity) => new(side, capacity,
        Enumerable.Range(0, capacity).Select(index => index == 0
            ? new ReviewContainerSlot(0, "occupied", null, first, identity)
            : new ReviewContainerSlot(index, "empty", null, null, null)).ToArray());

    private static ReviewContainerItemIdentity Identity(string scope, long observationId,
        int maximumStackSize = 999, string name = "Item", uint? packedColor = null, string? orderData = null)
    {
        string instance = ReviewContainerContract.OpaqueIdentity(Launch, scope, "item", observationId);
        return new(instance, NativeRevision(instance,
            "StardewValley.Object", maximumStackSize, name, packedColor, orderData));
    }

    private static string NativeRevision(string instance, string runtimeType, int maximumStackSize,
        string name, uint? packedColor, string? orderData)
    {
        Assert.True(ReviewContainerContract.TryNativeItemRevision(instance, runtimeType, maximumStackSize,
            name, packedColor, orderData, out string revision));
        return revision;
    }

    private static ReviewContainerReport Report(string captureId = Capture) => new(1, "ready", null, Launch,
        "single", null, DateTimeOffset.UtcNow, Values(captureId));

    private static LiveLabCommandResult Sent(string root) => new(0,
        new ProjectReviewCommandReport(1, null, root, "ready", null, true, [], []));

    private static void Publish(string root, string id, ReviewContainerReport report) =>
        File.WriteAllText(ReviewContainerContract.ResponsePath(LiveLabPaths.Resolve(root).RuntimePath, id),
            JsonSerializer.Serialize(new ReviewContainerResponseEnvelope(1, id, report), JsonOptions));
}
