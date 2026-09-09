using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

public sealed class ReviewContainerTransferTests
{
    private static readonly string Revision = "sha256:" + new string('a', 64);
    private static readonly string Launch = new('a', 32);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void UnboundSinglePlayerServerAdvertisesOptedInTransferTool()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(
            temporary,
            withTestSave: true);

        var options = ProjectReviewMcpServer.CreateOptions(
            reader,
            runContainerTransfer: (_, _) => throw new InvalidOperationException(
                "No transfer should run while listing tools."));

        Assert.Contains(
            options.ToolCollection!,
            tool => tool.ProtocolTool.Name == ProjectReviewMcpContainerTransferTools.ToolName);
        Assert.Contains(
            "Container transfers were separately enabled",
            options.ServerInstructions,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deposit", 0, 1, null)]
    [InlineData("withdraw", 35, 99, null)]
    [InlineData("move", 0, 1, "containerTransferArgumentsInvalid")]
    [InlineData("deposit", -1, 1, "containerTransferArgumentsInvalid")]
    [InlineData("deposit", 0, 0, "containerTransferArgumentsInvalid")]
    [InlineData("deposit", 0, 100, "containerTransferArgumentsInvalid")]
    public void QueryValidationIsBounded(string direction, int slot, int quantity, string? expected) =>
        Assert.Equal(expected, ReviewContainerTransferContract.Validate(Query(direction, slot, quantity)));

    [Theory]
    [InlineData("deposit", 3, 0, 1, 2, 2)]
    [InlineData("withdraw", 0, 3, 2, 1, 2)]
    public void BeforeAfterQuantityUsesBothSidesAndConservation(string direction, int playerBefore,
        int chestBefore, int playerAfter, int chestAfter, int expected)
    {
        ReviewContainerValues before = Values(new string('b', 32), playerBefore, chestBefore);
        ReviewContainerValues after = Values(new string('c', 32), playerAfter, chestAfter);
        ReviewContainerTransferQuery query = Query(direction: direction, selection: before.SelectionIdentity,
            container: before.ContainerRevision);
        Assert.Equal(expected, ReviewContainerTransferContract.ObservedQuantity(before, after, query));
        Assert.True(ReviewContainerTransferContract.Conserved(before, after, query));
        Assert.False(ReviewContainerTransferContract.Conserved(before, Values(new string('d', 32), playerAfter, chestAfter + 1), query));
    }

    [Fact]
    public void ProductionProgressCheckRejectsOverTransferAndHeldState()
    {
        ReviewContainerValues before = Values(new string('b', 32), 3, 0);
        ReviewContainerTransferQuery query = Query(selection: before.SelectionIdentity, container: before.ContainerRevision);
        Assert.True(ReviewContainerTransferContract.ProgressValid(before, Values(new string('c', 32), 2, 1), query, 2));
        Assert.False(ReviewContainerTransferContract.ProgressValid(before, Values(new string('d', 32), 0, 3), query, 2));
        ReviewContainerValues held = Values(new string('e', 32), 2, 0) with
        { HeldItem = new("occupied", null, new("(O)390", 1, 0), new(Revision, Revision)) };
        Assert.False(ReviewContainerTransferContract.ProgressValid(before, held, query, 2));
    }

    [Fact]
    public void ProductionObservationPreflightRejectsStaleHeldReplacedAndInsufficientSources()
    {
        ReviewContainerValues before = Values(new string('b', 32), 3, 0);
        ReviewContainerTransferQuery exact = QueryFrom(before, "deposit", 0, 2);
        Assert.Null(ReviewContainerTransferContract.ObservationProblem(exact, before));
        Assert.Equal("containerTransferSelectionStale",
            ReviewContainerTransferContract.ObservationProblem(exact with { ContainerRevision = Revision }, before));
        Assert.Equal("containerTransferInsufficientSource",
            ReviewContainerTransferContract.ObservationProblem(QueryFrom(before, "deposit", 0, 4), before));
        Assert.Equal("containerTransferSourceStale",
            ReviewContainerTransferContract.ObservationProblem(exact with { InstanceIdentity = Revision }, before));
        ReviewObservedItem heldItem = new("occupied", null, new("(O)388", 1, 0), new(Revision, Revision));
        ReviewContainerValues held = before with { HeldItem = heldItem };
        held = held with
        { ContainerRevision = ReviewContainerContract.Revision(held.SelectionIdentity, held.Player, held.Container, held.HeldItem) };
        Assert.Equal("containerTransferHeldItemOccupied",
            ReviewContainerTransferContract.ObservationProblem(QueryFrom(held, "deposit", 0, 2), held));
    }

    [Fact]
    public void StartupAndPostResponseBindingChangesRemainUncertain()
    {
        ProjectReviewMcpRuntimeSnapshot expected = Snapshot(Launch);
        Assert.True(ProjectReviewContainerTransferService.PermissionBindingValid(expected, expected));
        Assert.False(ProjectReviewContainerTransferService.PermissionBindingValid(expected,
            expected with { LaunchId = new string('9', 32) }));
        Assert.False(ProjectReviewContainerTransferService.PermissionBindingValid(expected,
            expected with { TestSave = null }));
        ReviewContainerValues before = Values(new string('b', 32), 3, 0);
        ReviewContainerTransferReport completed = new(1, "completed", null, Launch, "single", null, DateTimeOffset.UtcNow,
            new("deposit", "player", 0, 2, 2, "(O)390", true, false, "completed", before,
                Values(new string('c', 32), 1, 2), []));
        ReviewContainerTransferReport uncertain = ProjectReviewContainerTransferService.BindingChangedFailure(
            completed, true, DateTimeOffset.UtcNow);
        Assert.Equal("uncertain", uncertain.State);
        Assert.Equal("containerTransferBindingChanged", uncertain.ErrorCode);
        Assert.True(uncertain.Data!.CancellationRequested);
        Assert.Contains("postBindingChanged", uncertain.Data.Limitations);
    }

    [Fact]
    public void CompletedResponseRequiresExactRequestedObservationAndAfterCapture()
    {
        ReviewContainerValues before = Values(new string('b', 32), 3, 0);
        var query = QueryFrom(before, "deposit", 0, 2);
        var snapshot = Snapshot(Launch);
        var complete = new ReviewContainerTransferReport(1, "completed", null, Launch, "single", null,
            DateTimeOffset.UtcNow, new("deposit", "player", 0, 2, 2, "(O)390", true, false,
                "completed", before, Values(new string('b', 32), 1, 2), []));
        Assert.True(ProjectReviewContainerTransferService.Valid(complete, snapshot, query, DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(complete with
        { Data = complete.Data! with { ObservedQuantity = 1 } }, snapshot, query, DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(complete with
        { Data = complete.Data! with { After = null } }, snapshot, query, DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(complete with
        { Data = complete.Data! with { After = before } }, snapshot, query, DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(complete with
        { Data = complete.Data! with { After = Values(new string('d', 32), 1, 3) } }, snapshot, query, DateTimeOffset.UtcNow));
        Assert.True(ProjectReviewContainerTransferService.Valid(complete with
        { Data = complete.Data! with { After = Values(new string('d', 32), 1, 2) } }, snapshot, query, DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(complete with
        { Data = complete.Data! with { After = Values(new string('d', 32), 1, 2, tileX: 8) } }, snapshot, query, DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(complete with
        { Data = complete.Data! with { SourceSide = "container" } }, snapshot, query, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PartialAndUncertainResponsesStayExplicit()
    {
        ReviewContainerValues before = Values(new string('b', 32), 3, 0);
        var query = QueryFrom(before, "deposit", 0, 2);
        var snapshot = Snapshot(Launch);
        var partial = new ReviewContainerTransferReport(1, "partial", "containerTransferPartial", Launch, "single", null,
            DateTimeOffset.UtcNow, new("deposit", "player", 0, 2, 1, "(O)390", true, true,
                "partial", before, Values(new string('b', 32), 2, 1), []));
        Assert.True(ProjectReviewContainerTransferService.Valid(partial, snapshot, query, DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(partial with
        { Data = partial.Data! with { ObservedQuantity = 2 } }, snapshot, query, DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(partial with
        { State = "refused" }, snapshot, query, DateTimeOffset.UtcNow));
        Assert.True(ProjectReviewContainerTransferService.Valid(partial with
        {
            State = "uncertain",
            ErrorCode = "containerTransferAfterUnavailable",
            Data = partial.Data! with { Outcome = "uncertain", ObservedQuantity = null, After = null, Limitations = ["afterObservationUnavailable"] }
        },
            snapshot, query, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExecuteTurnsPostResponseBindingChangeIntoUncertainty()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ProjectReviewMcpRuntimeSnapshot expected = reader.Read().Snapshot!;
        ReviewContainerValues before = Values(new string('b', 32), 3, 0);
        ReviewContainerValues capturedBefore = before;
        ReviewContainerTransferQuery query = QueryFrom(before, "deposit", 0, 2);
        ReviewContainerTransferReport result = ProjectReviewContainerTransferService.Execute(query, reader, command =>
        {
            string[] parts = command.Split(' ');
            if (parts[1] == "container")
            {
                capturedBefore = before with { CaptureId = parts[2] };
                PublishContainer(temporary.Path, parts[2], capturedBefore);
            }
            else
            {
                Assert.Equal("container-transfer", parts[1]);
                PublishTransfer(temporary.Path, parts[2], new(1, "completed", null, Launch, "single", null,
                    DateTimeOffset.UtcNow, new("deposit", "player", 0, 2, 2, "(O)390", true, false,
                        "completed", capturedBefore, Values(new string('b', 32), 1, 2), [])));
                var store = new JsonLiveLabStateStore(LiveLabPaths.Resolve(temporary.Path).StatePath);
                store.Write(store.Read()! with { LaunchId = new string('9', 32) });
            }
            return Sent(temporary.Path);
        }, TimeSpan.Zero, expectedSnapshot: expected);
        Assert.True(result.State == "uncertain", $"{result.State}:{result.ErrorCode}");
        Assert.Equal("containerTransferBindingChanged", result.ErrorCode);
        Assert.Contains("postBindingChanged", result.Data!.Limitations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecuteTimeoutAndCancellationAfterDispatchRemainUncertain(bool cancelAfterDispatch)
    {
        using TemporaryDirectory temporary = new();
        using var cancellation = new CancellationTokenSource();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        ReviewContainerValues before = Values(new string('b', 32), 3, 0);
        ReviewContainerTransferQuery query = QueryFrom(before, "deposit", 0, 2);
        int cancellationSignals = 0;
        ReviewContainerTransferReport result = ProjectReviewContainerTransferService.Execute(query, reader, command =>
        {
            string[] parts = command.Split(' ');
            if (parts[1] == "container") PublishContainer(temporary.Path, parts[2], before with { CaptureId = parts[2] });
            else if (parts[2] == "cancel") cancellationSignals++;
            else if (cancelAfterDispatch) cancellation.Cancel();
            return Sent(temporary.Path);
        }, TimeSpan.Zero, cancellationToken: cancellation.Token);
        Assert.True(result.State == "uncertain", $"{result.State}:{result.ErrorCode}");
        Assert.True(result.Data!.Dispatched);
        Assert.Null(result.Data.After);
        Assert.Equal(cancelAfterDispatch, result.Data.CancellationRequested);
        Assert.Equal(cancelAfterDispatch ? 1 : 0, cancellationSignals);
    }

    [Theory]
    [InlineData(false, false, "refused", false)]
    [InlineData(true, false, "uncertain", true)]
    [InlineData(true, true, "uncertain", true)]
    public void MissingResponseNeverClaimsSafeRefusalAfterPossibleDispatch(bool mayHaveRun, bool canceled,
        string expectedState, bool expectedDispatched)
    {
        ReviewContainerValues before = Values(new string('b', 32), 3, 0);
        ReviewContainerTransferQuery query = Query(selection: before.SelectionIdentity, container: before.ContainerRevision);
        ReviewContainerTransferReport report = ProjectReviewContainerTransferService.TransportFailure(query,
            Snapshot(Launch), before, "containerTransferResponseTimedOut", mayHaveRun, canceled, DateTimeOffset.UtcNow);
        Assert.Equal(expectedState, report.State);
        Assert.Equal(expectedDispatched, report.Data?.Dispatched ?? false);
        if (mayHaveRun)
        {
            Assert.Equal("uncertain", report.Data!.Outcome);
            Assert.Null(report.Data.ObservedQuantity);
            Assert.Equal(canceled, report.Data.CancellationRequested);
        }
    }

    [Fact]
    public void DuplicateJsonMembersAreRejected()
    {
        byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1,\"requestId\":\"" + new string('a', 32) + "\",\"report\":null}");
        Assert.Throws<InvalidDataException>(() => ProjectReviewContainerTransferService.Deserialize(json));
    }

    [Fact]
    public void TransferFingerprintPreservesInstalledStackingDistinctions()
    {
        Assert.True(ReviewContainerContract.TryTransferItemFingerprint("(O)390", 0, "StardewValley.Object", 999,
            "Stone", null, null, out string nullOrder));
        Assert.True(ReviewContainerContract.TryTransferItemFingerprint("(O)390", 0, "StardewValley.Object", 999,
            "Stone", null, "", out string emptyOrder));
        Assert.True(ReviewContainerContract.TryTransferItemFingerprint("(O)390", 2, "StardewValley.Object", 999,
            "Stone", null, null, out string quality));
        Assert.NotEqual(nullOrder, emptyOrder);
        Assert.NotEqual(nullOrder, quality);
    }

    private static ReviewContainerTransferQuery Query(string direction = "deposit", int slot = 0, int quantity = 2,
        string? selection = null, string? container = null) => new(direction, slot, quantity, "(O)390",
            selection ?? Revision, container ?? Revision, Revision, Revision);

    private static ReviewContainerTransferQuery QueryFrom(ReviewContainerValues values, string direction,
        int slot, int quantity)
    {
        ReviewContainerSlot source = (direction == "deposit" ? values.Player : values.Container).Slots[slot];
        return new(direction, slot, quantity, source.Item!.QualifiedItemId, values.SelectionIdentity,
            values.ContainerRevision, source.Identity!.InstanceIdentity, source.Identity.ItemRevision);
    }

    private static ReviewContainerValues Values(string scope, int playerStack, int chestStack, int tileX = 7)
    {
        string launch = Launch;
        string backing = ReviewContainerContract.OpaqueIdentity(launch, scope, "chest", 99);
        ReviewContainerSide Side(string side, int capacity, int stack, long id)
        {
            var slots = Enumerable.Range(0, capacity).Select(index => index == 0 && stack > 0
                ? Occupied(index, stack, id)
                : new ReviewContainerSlot(index, "empty", null, null, null)).ToArray();
            return new(side, capacity, slots);
            ReviewContainerSlot Occupied(int index, int value, long observation)
            {
                string instance = ReviewContainerContract.OpaqueIdentity(launch, scope, "item", observation);
                Assert.True(ReviewContainerContract.TryNativeItemRevision(instance, "StardewValley.Object", 999, "Stone", null, null, out string revision));
                return new(index, "occupied", null, new("(O)390", value, 0), new(instance, revision));
            }
        }
        ReviewContainerSide player = Side("player", 12, playerStack, 1), chest = Side("container", 36, chestStack, 2);
        ReviewObservedItem held = new("empty", null, null, null);
        string selection = ReviewContainerContract.SelectionIdentity(launch, scope, backing, "101", "Farm", tileX, 7, "(BC)130");
        return new(scope, selection, ReviewContainerContract.Revision(selection, player, chest, held), scope, backing, "101",
            "Farm", tileX, 7, "(BC)130", player, chest, held, true, []);
    }

    private static ProjectReviewMcpRuntimeSnapshot Snapshot(string launch)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(1, launch, "single", null, now,
            new("Nana.Target", "1.0.0", Revision), new(new string('f', 32), "SDVKit_1"),
            new(1, true, "spring", 1, 1, 600, "Farm", 7, 7, true), 1, now, 1, Environment.ProcessId);
    }

    private static LiveLabCommandResult Sent(string root) => new(0,
        new ProjectReviewCommandReport(1, null, root, "ready", null, true, [], []));

    private static void PublishContainer(string root, string id, ReviewContainerValues values)
    {
        var report = new ReviewContainerReport(1, "ready", null, Launch, "single", null,
            DateTimeOffset.UtcNow, values);
        File.WriteAllText(ReviewContainerContract.ResponsePath(LiveLabPaths.Resolve(root).RuntimePath, id),
            JsonSerializer.Serialize(new ReviewContainerResponseEnvelope(1, id, report), JsonOptions));
    }

    private static void PublishTransfer(string root, string id, ReviewContainerTransferReport report) =>
        File.WriteAllText(ReviewContainerTransferContract.ResponsePath(LiveLabPaths.Resolve(root).RuntimePath, id),
            JsonSerializer.Serialize(new ReviewContainerTransferResponseEnvelope(1, id, report), JsonOptions));
}
