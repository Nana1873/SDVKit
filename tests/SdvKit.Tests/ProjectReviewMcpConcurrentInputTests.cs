using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

public sealed class ProjectReviewMcpConcurrentInputTests
{
    [Theory]
    [InlineData(null, null, 9001)]
    [InlineData(null, null, 9002)]
    [InlineData(null, 0, 9002)]
    [InlineData(null, 1, 9002)]
    [InlineData("host", null, 9002)]
    [InlineData("farmhand", null, 9002)]
    public void ExternalTransitionsKeepTheExactAcknowledgementAndEofCleanup(
        string? role, int? screen, int foregroundProcessId)
    {
        using TemporaryDirectory temporary = new();
        var reader = CreateReader(temporary, role, screen);
        string statusPath = StatusPath(temporary, role);
        UpdateStatus(statusPath, marker => marker with { ForegroundProcessId = 9001 });
        var calls = new List<string>();
        var session = Session(reader, statusPath, (query, _) =>
        {
            calls.Add(query.Action);
            return Acknowledge(statusPath, query, marker => marker with
            {
                ForegroundWindowHandle = marker.ForegroundWindowHandle + 1,
                ForegroundProcessId = foregroundProcessId,
            });
        });

        var result = session.Execute(Query(), CancellationToken.None);

        Assert.Null(result.Problem);
        Assert.True(result.Acknowledgement?.Succeeded);
        Assert.True(result.Acknowledgement?.ExternalForegroundChanged);
        Assert.Equal(role, result.Acknowledgement?.Role);
        Assert.Equal(screen, reader.Read().Snapshot?.Screen?.ScreenId);
        Assert.True(result.ActionMayHaveRun);
        Assert.Null(session.Cleanup());
        Assert.Equal([ReviewInputContract.CursorSetAction, ReviewInputContract.CursorClearAction], calls);
    }

    [Theory]
    [InlineData(null, null, 9001, 4242)]
    [InlineData(null, null, 4242, 9001)]
    [InlineData(null, 1, 9001, 4242)]
    [InlineData("host", null, 9001, 4242)]
    [InlineData("host", null, 9001, 4243)]
    [InlineData("host", null, 4243, 9001)]
    [InlineData("farmhand", null, 9001, 4242)]
    [InlineData("farmhand", null, 9001, 4243)]
    public void ForegroundTransitionsInvolvingEitherReviewProcessFailClosedWithoutReplay(
        string? role, int? screen, int beforeProcess, int afterProcess)
    {
        using TemporaryDirectory temporary = new();
        var reader = CreateReader(temporary, role, screen);
        string statusPath = StatusPath(temporary, role);
        UpdateStatus(statusPath, marker => marker with
        {
            ForegroundProcessId = beforeProcess,
            IsActive = marker.ProcessId == beforeProcess,
        });
        int dispatches = 0;
        var session = Session(reader, statusPath, (query, _) =>
        {
            dispatches++;
            return Acknowledge(statusPath, query, marker => marker with
            {
                ForegroundWindowHandle = marker.ForegroundWindowHandle + 1,
                ForegroundProcessId = afterProcess,
                IsActive = marker.ProcessId == afterProcess,
            });
        });

        var result = session.Execute(Query(), CancellationToken.None);

        Assert.Null(result.Acknowledgement);
        string expected = role is not null && afterProcess == (role == "host" ? 4242 : 4243)
            ? "reviewPairNotReady" : "inputForegroundChanged";
        Assert.Equal(expected, result.Problem?.Code);
        Assert.True(result.ActionMayHaveRun);
        Assert.Equal(1, dispatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingForegroundIsDistinctFromAChangedReview(bool afterDispatch)
    {
        using TemporaryDirectory temporary = new();
        var reader = CreateReader(temporary, null, null);
        string statusPath = StatusPath(temporary, null);
        if (!afterDispatch) UpdateStatus(statusPath, MissingForeground);
        int dispatches = 0;
        var session = Session(reader, statusPath, (query, _) =>
        {
            dispatches++;
            return Acknowledge(statusPath, query, MissingForeground);
        });

        var result = session.Execute(Query(), CancellationToken.None);

        Assert.Equal("inputForegroundUnavailable", result.Problem?.Code);
        Assert.Null(result.Acknowledgement);
        Assert.Equal(afterDispatch, result.ActionMayHaveRun);
        Assert.Equal(afterDispatch ? 1 : 0, dispatches);
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("farmer")]
    [InlineData("host")]
    [InlineData("farmhand")]
    public void ConcurrentExternalActivityDoesNotHideAChangedPlayerBinding(string changed)
    {
        using TemporaryDirectory temporary = new();
        bool local = changed is "screen" or "farmer";
        string contextId = new('1', 32);
        string farmerId = "202";
        string? role = local ? null : changed;
        var reader = local
            ? ProjectReviewMcpTests.CreateReadyLocalScreenReview(temporary, 1, command =>
            {
                ProjectReviewMcpTests.WriteScreenBindingResponse(temporary, command, farmerId, contextId);
                return WrittenCommand(temporary);
            })
            : CreateReader(temporary, role, null);
        string statusPath = StatusPath(temporary, role);
        UpdateStatus(statusPath, marker => marker with { ForegroundProcessId = 9001 });
        int dispatches = 0;
        var session = Session(reader, statusPath, (query, _) =>
        {
            dispatches++;
            if (changed == "screen") contextId = new('2', 32);
            if (changed == "farmer") farmerId = "303";
            return Acknowledge(statusPath, query, marker => marker with
            {
                ForegroundWindowHandle = marker.ForegroundWindowHandle + 1,
                ForegroundProcessId = 9002,
                NetworkTwo = local ? marker.NetworkTwo : marker.NetworkTwo! with { SessionId = new('d', 32) },
            });
        });

        var result = session.Execute(Query(), CancellationToken.None);

        Assert.Null(result.Acknowledgement);
        Assert.Equal(local ? "reviewScreenBindingChanged" : "reviewBindingChanged", result.Problem?.Code);
        Assert.True(result.ActionMayHaveRun);
        Assert.Equal(1, dispatches);
    }

    private static ProjectReviewMcpRuntimeReader CreateReader(TemporaryDirectory temporary, string? role, int? screen) =>
        role is not null ? ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, role)
        : screen is int selected ? ProjectReviewMcpTests.CreateReadyLocalScreenReview(temporary, selected, command =>
        {
            ProjectReviewMcpTests.WriteScreenBindingResponse(temporary, command, "202", new('1', 32));
            return WrittenCommand(temporary);
        }) : ProjectReviewMcpTests.CreateReadyReview(temporary);

    private static LiveLabCommandResult WrittenCommand(TemporaryDirectory temporary) =>
        new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "running", null, true, [], []));

    private static string StatusPath(TemporaryDirectory temporary, string? role)
    {
        var paths = LiveLabPaths.Resolve(temporary.Path);
        return (role is null ? paths : LiveLabPaths.ResolveNetworkRole(paths, role)).StatusPath;
    }

    private static ProjectReviewMcpInputSession Session(ProjectReviewMcpRuntimeReader reader,
        string statusPath, ProjectReviewMcpInputRunner runner) =>
        new(reader, Path.GetDirectoryName(statusPath)!, runner, postActionTimeout: TimeSpan.Zero);

    private static ReviewInputQuery Query() => new(ReviewInputContract.CursorSetAction, null, null, 20, 30);

    private static AlwaysOnStatusMarker MissingForeground(AlwaysOnStatusMarker marker) =>
        marker with { ForegroundWindowHandle = null, ForegroundProcessId = null };

    private static void UpdateStatus(string path, Func<AlwaysOnStatusMarker, AlwaysOnStatusMarker> update)
    {
        var marker = JsonSerializer.Deserialize<AlwaysOnStatusMarker>(File.ReadAllText(path), LiveLabJsonOptions.CamelCase)!;
        File.WriteAllText(path, JsonSerializer.Serialize(update(marker), LiveLabJsonOptions.CamelCase));
    }

    private static ProjectReviewInputExecutionResult Acknowledge(string path, ReviewInputQuery query,
        Func<AlwaysOnStatusMarker, AlwaysOnStatusMarker> update)
    {
        ReviewInputResponseEnvelope? response = null;
        UpdateStatus(path, marker =>
        {
            response = new(1, new('a', 32), marker.ObservedAtUtc.AddMilliseconds(1), marker.Tick,
                query.Action, true, query.Button, query.Direction, query.X, query.Y,
                query.Action != ReviewInputContract.CursorClearAction, true, null);
            var observed = marker.ObservedAtUtc.AddMilliseconds(2);
            return update(marker) with
            {
                Tick = marker.Tick + 1,
                ObservedAtUtc = observed,
                Runtime = marker.Runtime! with { ObservedAtUtc = observed },
            };
        });
        return new(response, [], ActionMayHaveRun: true, CancellationRequested: false);
    }
}
