using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

/// <summary>
/// World actions and container transfer act as the selected network-2 role or local split-screen screen: the
/// command names that selection, is routed to it, and only a report from the same selection is accepted.
/// </summary>
[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewWorldActionSelectionTests
{
    private const string Inventory = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string ScreenFarmer = "202";
    private const string ScreenContext = "11111111111111111111111111111111";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(null, null, "single")]
    [InlineData("host", null, "host")]
    [InlineData("farmhand", null, "farmhand")]
    [InlineData(null, 0, "screen-0")]
    [InlineData(null, 3, "screen-3")]
    public void SelectionTokenRoundTrips(string? role, int? screenId, string token)
    {
        Assert.Equal(token, ReviewWorldActionContract.SelectionToken(role, screenId));
        Assert.True(ReviewWorldActionContract.TryParseSelection(token, out string? parsedRole, out int? parsedScreen));
        Assert.Equal(role, parsedRole);
        Assert.Equal(screenId, parsedScreen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Host")]
    [InlineData("network-2")]
    [InlineData("screen-")]
    [InlineData("screen-01")]
    [InlineData("screen--1")]
    [InlineData("screen-+1")]
    [InlineData("screen=1")]
    public void SelectionTokenRejectsAnythingElse(string? token) =>
        Assert.False(ReviewWorldActionContract.TryParseSelection(token, out _, out _));

    [Fact]
    public void FarmhandActionNamesItsRoleAndAcceptsOnlyAFarmhandReport()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "farmhand");
        string runtime = ProjectReviewInputService.RuntimePath(temporary.Path, "network-2", "farmhand");
        string? sent = null;
        ReviewWorldActionReport completed = ProjectReviewWorldActService.Execute(reader, Act(), command =>
        {
            sent = command;
            string[] parts = command.Split(' ');
            RespondAct(runtime, parts[2], ActReport(parts[3], "network-2", "farmhand"));
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("completed", completed.State);
        Assert.Equal(("network-2", "farmhand"), (completed.Topology, completed.Role));
        Assert.EndsWith(" farmhand", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("screen=", sent, StringComparison.Ordinal);

        using TemporaryDirectory other = new();
        ProjectReviewMcpRuntimeReader farmhand = ProjectReviewMcpTests.CreateReadyNetworkReview(other, "farmhand");
        string otherRuntime = ProjectReviewInputService.RuntimePath(other.Path, "network-2", "farmhand");
        foreach ((string topology, string? role) in new[] { ("network-2", "host"), ("single", (string?)null) })
        {
            ReviewWorldActionReport foreign = ProjectReviewWorldActService.Execute(farmhand, Act(), command =>
            {
                string[] parts = command.Split(' ');
                RespondAct(otherRuntime, parts[2], ActReport(parts[3], topology, role));
                return Ok(other);
            }, TimeSpan.Zero);
            Assert.Equal("unavailable", foreign.State);
            Assert.Equal("mayHaveRun", foreign.DispatchState);
        }
    }

    [Fact]
    public void FarmhandMoveNamesItsRole()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "farmhand");
        string runtime = ProjectReviewInputService.RuntimePath(temporary.Path, "network-2", "farmhand");
        string? sent = null;
        ReviewWorldMoveReport moved = ProjectReviewWorldMoveService.Execute(reader, new("down", true), command =>
        {
            sent = command;
            string[] parts = command.Split(' ');
            File.WriteAllText(ReviewWorldMoveContract.ResponsePath(runtime, parts[2]), JsonSerializer.Serialize(
                new ReviewWorldMoveResponseEnvelope(1, parts[2], new ReviewWorldMoveReport(1, "completed", null,
                    parts[3], "network-2", "farmhand", DateTimeOffset.UtcNow, "down", true, "completed", "S", 10, 11,
                    "FarmHouse", 3, 4, null, 3, 5, false)), JsonOptions));
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal("completed", moved.State);
        Assert.Equal("farmhand", moved.Role);
        Assert.EndsWith(" down true farmhand", sent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ScreenActionAndCancelCarryTheScreenBindingAndToken(int screenId)
    {
        using TemporaryDirectory temporary = new();
        string runtime = LiveLabPaths.Resolve(temporary.Path).RuntimePath;
        var actions = new List<string>();
        using var cancellation = new CancellationTokenSource();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyLocalScreenReview(temporary, screenId,
            command =>
            {
                ProjectReviewMcpTests.WriteScreenBindingResponse(temporary, command, ScreenFarmer, ScreenContext);
                return Ok(temporary);
            });
        Assert.True(reader.Read().Succeeded);
        string suffix = $"binding={ScreenContext} farmer={ScreenFarmer} screen={screenId}";
        ReviewWorldActionReport result = ProjectReviewWorldActService.Execute(reader, Act(),
            Send, TimeSpan.FromSeconds(1),
            cancellationToken: cancellation.Token);
        Assert.Equal("worldActionCancellationRequested", result.ErrorCode);
        Assert.Equal(2, actions.Count);
        Assert.EndsWith($" {Inventory} screen-{screenId} {suffix}", actions[0], StringComparison.Ordinal);
        Assert.StartsWith("sdvkit world-act cancel ", actions[1], StringComparison.Ordinal);
        Assert.EndsWith(" " + suffix, actions[1], StringComparison.Ordinal);

        LiveLabCommandResult Send(string command)
        {
            actions.Add(command);
            string[] parts = command.Split(' ');
            if (actions.Count == 1)
            {
                RespondAct(runtime, parts[2], ActReport(parts[3], "single", null));
                cancellation.Cancel();
            }
            return Ok(temporary);
        }
    }

    [Fact]
    public void TransferReportMustNameTheSelectedRole()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeSnapshot farmhand = Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(
            ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "farmhand").Read().Snapshot);
        ReviewContainerTransferQuery query = TransferQuery();
        ReviewContainerTransferReport Refused(string topology, string? role) =>
            new(1, "refused", "containerTransferSelectionStale", farmhand.LaunchId, topology, role,
                DateTimeOffset.UtcNow, null);
        Assert.True(ProjectReviewContainerTransferService.Valid(Refused("network-2", "farmhand"), farmhand, query,
            DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(Refused("network-2", "host"), farmhand, query,
            DateTimeOffset.UtcNow));
        Assert.False(ProjectReviewContainerTransferService.Valid(Refused("single", null), farmhand, query,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void HostActionsNameTheHostAndNeedThePassedPairBeforeAndAfterDispatch()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader host = ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "host");
        Assert.True(host.Read().Succeeded);
        string runtime = ProjectReviewInputService.RuntimePath(temporary.Path, "network-2", "host");
        string? sent = null;
        ReviewWorldActionReport departedAfter = ProjectReviewWorldActService.Execute(host, Act(), command =>
        {
            sent = command;
            string[] parts = command.Split(' ');
            RespondAct(runtime, parts[2], ActReport(parts[3], "network-2", "host"));
            DepartFarmhand(temporary);
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.EndsWith($" {Inventory} host", sent, StringComparison.Ordinal);
        Assert.Equal(("worldActionBindingChanged", "mayHaveRun"), (departedAfter.ErrorCode, departedAfter.DispatchState));

        ReviewWorldMoveReport departedBefore = ProjectReviewWorldMoveService.Execute(host, new("down", false),
            _ => throw new InvalidOperationException("No dispatch without the passed pair."), TimeSpan.Zero);
        Assert.Equal(("reviewPairNotReady", "notDispatched"), (departedBefore.ErrorCode, departedBefore.DispatchState));
        ReviewContainerTransferReport transfer = ProjectReviewContainerTransferService.Execute(TransferQuery(), host,
            _ => throw new InvalidOperationException("No dispatch without the passed pair."), TimeSpan.Zero);
        Assert.Equal(("refused", "reviewPairNotReady"), (transfer.State, transfer.ErrorCode));
    }

    [Fact]
    public void ScreenBindingChangeIsRefusedBeforeAndReportedAfterDispatch()
    {
        using TemporaryDirectory temporary = new();
        string runtime = LiveLabPaths.Resolve(temporary.Path).RuntimePath;
        string farmer = ScreenFarmer;
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyLocalScreenReview(temporary, 1,
            command =>
            {
                ProjectReviewMcpTests.WriteScreenBindingResponse(temporary, command, farmer, ScreenContext);
                return Ok(temporary);
            });
        Assert.True(reader.Read().Succeeded);
        ReviewWorldActionReport changedAfter = ProjectReviewWorldActService.Execute(reader, Act(), command =>
        {
            string[] parts = command.Split(' ');
            RespondAct(runtime, parts[2], ActReport(parts[3], "single", null));
            farmer = "303";
            return Ok(temporary);
        }, TimeSpan.Zero);
        Assert.Equal(("worldActionBindingChanged", "mayHaveRun"), (changedAfter.ErrorCode, changedAfter.DispatchState));

        ReviewWorldActionReport changedBefore = ProjectReviewWorldActService.Execute(reader, Act(),
            _ => throw new InvalidOperationException("No dispatch after the screen binding changed."), TimeSpan.Zero);
        Assert.Equal(("reviewScreenBindingChanged", "notDispatched"), (changedBefore.ErrorCode, changedBefore.DispatchState));
    }

    private static void DepartFarmhand(TemporaryDirectory temporary)
    {
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewMcpTests.RewriteNetworkPhase(
            LiveLabPaths.ResolveNetworkRole(paths, NetworkTwoContract.HostRole).StatusPath, "waitingForFarmhand");
        ProjectReviewMcpTests.RewriteNetworkPhase(
            LiveLabPaths.ResolveNetworkRole(paths, NetworkTwoContract.FarmhandRole).StatusPath, "waitingForRejoin",
            identityVerified: false, sessionId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", worldReady: false);
    }

    private static ReviewContainerTransferQuery TransferQuery() => new("deposit", 0, 1, "(O)24",
        "sha256:" + new string('a', 64), "sha256:" + new string('b', 64),
        "sha256:" + new string('c', 64), "sha256:" + new string('d', 64));

    private static ReviewWorldActQuery Act() => new("check", 7, 9, Inventory);

    private static ReviewWorldActionReport ActReport(string launch, string topology, string? role) =>
        new(1, "completed", null, launch, topology, role, DateTimeOffset.UtcNow, "check", 7, 9, "completed",
            "MouseRight", 100, 101);

    private static void RespondAct(string runtime, string requestId, ReviewWorldActionReport report)
    {
        Directory.CreateDirectory(runtime);
        File.WriteAllText(ReviewWorldActContract.ResponsePath(runtime, requestId),
            JsonSerializer.Serialize(new ReviewWorldActionResponseEnvelope(1, requestId, report), JsonOptions));
    }

    private static LiveLabCommandResult Ok(TemporaryDirectory temporary) =>
        new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
}
