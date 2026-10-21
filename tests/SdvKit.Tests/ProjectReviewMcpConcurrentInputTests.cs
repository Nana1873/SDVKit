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
        // Only the pre-dispatch refusal may tell a caller that a retry is safe.
        Assert.Equal(!afterDispatch, result.Problem!.Message.Contains("retrying the action is safe", StringComparison.Ordinal));
        Assert.Equal(afterDispatch, result.Problem.Message.Contains("After the action was sent", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnobservedForegroundBeforeDispatchWaitsForTheNextSample()
    {
        using TemporaryDirectory temporary = new();
        var reader = CreateReader(temporary, null, null);
        string statusPath = StatusPath(temporary, null);
        long handle = ReadStatus(statusPath).ForegroundWindowHandle!.Value;
        int processId = ReadStatus(statusPath).ForegroundProcessId!.Value;
        UpdateStatus(statusPath, MissingForeground);
        int dispatches = 0;
        var session = Bounded(reader, statusPath, (query, _) =>
        {
            dispatches++;
            return Acknowledge(statusPath, query, marker => marker);
        }, _ => Advance(statusPath, marker => marker with
        {
            ForegroundWindowHandle = handle,
            ForegroundProcessId = processId,
        }));

        var result = session.Execute(Query(), CancellationToken.None);

        Assert.Null(result.Problem);
        Assert.True(result.Acknowledgement?.Succeeded);
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public void ForegroundThatStaysUnobservedBeforeDispatchIsRefusedAsSafeToRetry()
    {
        using TemporaryDirectory temporary = new();
        var reader = CreateReader(temporary, null, null);
        string statusPath = StatusPath(temporary, null);
        UpdateStatus(statusPath, MissingForeground);
        int dispatches = 0, delays = 0;
        var session = Bounded(reader, statusPath, (query, _) =>
        {
            dispatches++;
            return Acknowledge(statusPath, query, marker => marker);
        }, _ =>
        {
            delays++;
            Advance(statusPath, marker => marker);
        });

        var result = session.Execute(Query(), CancellationToken.None);

        Assert.Equal("inputForegroundUnavailable", result.Problem?.Code);
        Assert.Contains("retrying the action is safe", result.Problem!.Message, StringComparison.Ordinal);
        Assert.False(result.ActionMayHaveRun);
        Assert.Equal(0, dispatches);
        Assert.Equal(2, delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnobservedForegroundAfterDispatchIsDecidedByTheNextSample(bool reviewTookFocus)
    {
        using TemporaryDirectory temporary = new();
        var reader = CreateReader(temporary, null, null);
        string statusPath = StatusPath(temporary, null);
        GameSideStatusMarker initial = ReadStatus(statusPath);
        int dispatches = 0;
        var session = Bounded(reader, statusPath, (query, _) =>
        {
            dispatches++;
            return Acknowledge(statusPath, query, MissingForeground);
        }, _ => Advance(statusPath, marker => marker with
        {
            ForegroundWindowHandle = reviewTookFocus ? initial.ForegroundWindowHandle + 1 : initial.ForegroundWindowHandle,
            ForegroundProcessId = reviewTookFocus ? marker.ProcessId : initial.ForegroundProcessId,
            IsActive = reviewTookFocus || initial.IsActive,
        }));

        var result = session.Execute(Query(), CancellationToken.None);

        Assert.True(result.ActionMayHaveRun);
        Assert.Equal(1, dispatches);
        if (reviewTookFocus)
        {
            Assert.Null(result.Acknowledgement);
            Assert.Equal("inputForegroundChanged", result.Problem?.Code);
        }
        else
        {
            Assert.Null(result.Problem);
            Assert.True(result.Acknowledgement?.Succeeded);
            Assert.Null(result.Acknowledgement?.ExternalForegroundChanged);
        }
    }

    [Fact]
    public void ForegroundThatStaysUnobservedAfterDispatchFailsWithoutReplay()
    {
        using TemporaryDirectory temporary = new();
        var reader = CreateReader(temporary, null, null);
        string statusPath = StatusPath(temporary, null);
        int dispatches = 0, delays = 0;
        var session = Bounded(reader, statusPath, (query, _) =>
        {
            dispatches++;
            return Acknowledge(statusPath, query, MissingForeground);
        }, _ =>
        {
            delays++;
            Advance(statusPath, marker => marker);
        });

        var result = session.Execute(Query(), CancellationToken.None);

        Assert.Null(result.Acknowledgement);
        Assert.Equal("inputForegroundUnavailable", result.Problem?.Code);
        Assert.StartsWith("After the action was sent", result.Problem!.Message, StringComparison.Ordinal);
        Assert.True(result.ActionMayHaveRun);
        Assert.Equal(1, dispatches);
        Assert.Equal(2, delays);
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

    // A long observation bound, so only the sample limit ends the wait; each delay publishes the next status sample.
    private static ProjectReviewMcpInputSession Bounded(ProjectReviewMcpRuntimeReader reader,
        string statusPath, ProjectReviewMcpInputRunner runner, Action<TimeSpan> delay) =>
        new(reader, Path.GetDirectoryName(statusPath)!, runner, delay, postActionTimeout: TimeSpan.FromMinutes(1));

    private static GameSideStatusMarker ReadStatus(string path) =>
        JsonSerializer.Deserialize<GameSideStatusMarker>(File.ReadAllText(path), LiveLabJsonOptions.CamelCase)!;

    private static void Advance(string path, Func<GameSideStatusMarker, GameSideStatusMarker> update) =>
        UpdateStatus(path, marker =>
        {
            var observed = marker.ObservedAtUtc.AddMilliseconds(10);
            return update(marker) with
            {
                Tick = marker.Tick + 1,
                ObservedAtUtc = observed,
                Runtime = marker.Runtime! with { ObservedAtUtc = observed },
            };
        });

    private static ReviewInputQuery Query() => new(ReviewInputContract.CursorSetAction, null, null, 20, 30);

    private static GameSideStatusMarker MissingForeground(GameSideStatusMarker marker) =>
        marker with { ForegroundWindowHandle = null, ForegroundProcessId = null };

    private static void UpdateStatus(string path, Func<GameSideStatusMarker, GameSideStatusMarker> update)
    {
        var marker = JsonSerializer.Deserialize<GameSideStatusMarker>(File.ReadAllText(path), LiveLabJsonOptions.CamelCase)!;
        File.WriteAllText(path, JsonSerializer.Serialize(update(marker), LiveLabJsonOptions.CamelCase));
    }

    private static ProjectReviewInputExecutionResult Acknowledge(string path, ReviewInputQuery query,
        Func<GameSideStatusMarker, GameSideStatusMarker> update)
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
