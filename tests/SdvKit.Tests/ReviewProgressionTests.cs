using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewProgressionTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Fixture = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly ReviewProgressionSelection Selection = new("Leah", "SDVKit.Probe_Mail", "SDVKit.Probe_Quest");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("")]
    [InlineData("Leah Smith")]
    [InlineData("Leah\n")]
    [InlineData("Leah/child")]
    [InlineData("Léah")]
    [InlineData("\"Leah\"")]
    public void MalformedSelectionsNeverReachRuntime(string id)
    {
        using TemporaryDirectory temporary = new();
        var reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        foreach (var selection in new[] { Selection with { NpcId = id }, Selection with { MailId = id }, Selection with { QuestId = id } })
            Assert.Equal("progressionSelectionInvalid", ProjectReviewProgressionService.Execute(reader, selection,
                _ => throw new InvalidOperationException("Invalid selection was dispatched.")).ErrorCode);
        Assert.False(ReviewProgressionContract.IdValid(new string('a', 129)));
        Assert.True(ReviewProgressionContract.IdValid(new string('a', 128)));
    }

    [Fact]
    public void MissingNpcAndFriendshipRemainDistinctAndRawPointsAreNotClamped()
    {
        Assert.True(ReviewProgressionContract.DataValid(Data()));
        Assert.True(ReviewProgressionContract.DataValid(Data() with { Npc = new("available", int.MinValue) }));
        Assert.True(ReviewProgressionContract.DataValid(Data() with { Npc = new("missing", null) }));
        Assert.True(ReviewProgressionContract.DataValid(Data() with { Npc = new("noFriendship", null) }));
        foreach (var invalid in new[]
        {
            Data() with { Npc = new("missing", 0) }, Data() with { Npc = new("available", null) },
            Data() with { PlayerId = "0" }, Data() with { PlayerId = " 101 " }, Data() with { FixtureId = "fixture" },
            Data() with { CaptureTick = -1 }, Data() with { Mail = new(false, false, 101) },
            Data() with { Quest = new(1, 2, 0) }, Data() with { Quest = new(0, 0, 1) },
            Data() with { Quest = new(-1, 0, 0) }, Data() with { Quest = new(101, 1, 1) },
        }) Assert.False(ReviewProgressionContract.DataValid(invalid));
    }

    [Fact]
    public void PayloadRequiresExactFieldsAndRejectsUnknownOrDuplicateFacts()
    {
        string json = JsonSerializer.Serialize(new ReviewProgressionResponseEnvelope(1, Launch, Report()), JsonOptions);
        Assert.NotNull(ProjectReviewProgressionService.DeserializeResponse(Encoding.UTF8.GetBytes(json)));
        foreach (string invalid in new[]
        {
            json.Replace("\"role\":null,", "", StringComparison.Ordinal),
            json.Replace("\"role\":null,", "\"role\":null,\"role\":null,", StringComparison.Ordinal),
            json.Replace("\"npcId\":\"Leah\",", "", StringComparison.Ordinal),
            json.Replace("\"friendshipPoints\":500", "\"friendshipPoints\":500,\"allNpcs\":[]", StringComparison.Ordinal),
            json.Replace("\"matchCount\":1", "\"matchCount\":1,\"matchCount\":1", StringComparison.Ordinal),
        }) Assert.Throws<InvalidDataException>(() => ProjectReviewProgressionService.DeserializeResponse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("fixture")]
    [InlineData("player")]
    [InlineData("request")]
    [InlineData("launch")]
    [InlineData("role")]
    [InlineData("rebind")]
    [InlineData("unavailable")]
    public void TransportBindsSelectionFixturePlayerAndExactOwnership(string change)
    {
        using TemporaryDirectory temporary = new();
        var reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        var result = ProjectReviewProgressionService.Execute(reader, Selection, command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal(["sdvkit", "progression"], parts[..2]);
            Assert.Equal([Selection.NpcId, Selection.MailId, Selection.QuestId], parts[4..]);
            var report = Report();
            if (change == "selection") report = report with { Data = Data() with { Selection = Selection with { NpcId = "Pierre" } } };
            if (change == "fixture") report = report with { Data = Data() with { FixtureId = new string('b', 32) } };
            if (change == "player") report = report with { Data = Data() with { PlayerId = "102" } };
            if (change == "launch") report = report with { LaunchId = new string('b', 32) };
            if (change == "role") report = report with { Role = "host" };
            if (change == "unavailable") report = report with { State = "unavailable", ErrorCode = "progressionValuesInvalid", Data = null };
            Publish(temporary.Path, parts[2], report, change == "request" ? new string('b', 32) : null);
            if (change == "rebind")
            {
                var store = new JsonLiveLabStateStore(LiveLabPaths.Resolve(temporary.Path).StatePath);
                store.Write(store.Read()! with { LaunchId = new string('b', 32) });
            }
            return Sent(temporary.Path);
        }, TimeSpan.Zero);
        Assert.Equal(change switch { "rebind" => "reviewBindingChanged", "unavailable" => "progressionValuesInvalid", _ => "progressionResponseInvalid" }, result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(5, 5, null)]
    [InlineData(6, 6, "progressionResponseInvalid")]
    [InlineData(1, 6, "progressionResponseStale")]
    public void TransportRequiresFreshCaptureBeforeAndAfterBindingCheck(int responseSeconds, int returnSeconds, string? expected)
    {
        using TemporaryDirectory temporary = new();
        var reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        int reads = 0;
        DateTimeOffset Clock() => start.AddSeconds(++reads switch { 1 => 0, 2 => responseSeconds, _ => returnSeconds });
        var result = ProjectReviewProgressionService.Execute(reader, Selection, command =>
        {
            Publish(temporary.Path, command.Split(' ')[2], Report() with { CapturedAtUtc = start });
            return Sent(temporary.Path);
        }, TimeSpan.Zero, Clock);
        Assert.Equal(expected, result.ErrorCode);
    }

    [Fact]
    public void NonFixtureNetworkAndCanceledReadsDoNotDispatch()
    {
        using TemporaryDirectory temporary = new();
        var reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        Assert.Equal("progressionTestSaveRequired", ProjectReviewProgressionService.Execute(reader, Selection,
            _ => throw new InvalidOperationException("Must not send.")).ErrorCode);
        using TemporaryDirectory network = new();
        var host = ProjectReviewMcpTests.CreateReadyNetworkReview(network, "host");
        Assert.Equal("progressionTopologyUnsupported", ProjectReviewProgressionService.Execute(host, Selection,
            _ => throw new InvalidOperationException("Must not send.")).ErrorCode);
        Assert.DoesNotContain(ProjectReviewMcpServer.CreateOptions(host).ToolCollection!, tool => tool.ProtocolTool.Name == ProjectReviewMcpProgressionTools.ToolName);
        var screen = new ProjectReviewMcpRuntimeReader(temporary.Path, "single", null, screenId: 0);
        Assert.Equal("progressionTopologyUnsupported", ProjectReviewProgressionService.Execute(screen, Selection,
            _ => throw new InvalidOperationException("Must not send.")).ErrorCode);
        Assert.DoesNotContain(ProjectReviewMcpServer.CreateOptions(screen).ToolCollection!, tool => tool.ProtocolTool.Name == ProjectReviewMcpProgressionTools.ToolName);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectReviewProgressionService.Execute(reader, Selection,
            cancellationToken: cancel.Token));
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("--npc Leah --mail x --json", 2)]
    [InlineData("--npc Leah --mail x --quest y --json --json", 2)]
    [InlineData("--npc Leah --npc Pierre --mail x --quest y --json", 2)]
    [InlineData("--npc Leah --mail x --quest y --topology network-2 --json", 2)]
    [InlineData("--npc Leah --mail x --quest y --complete --json", 2)]
    public void CliRequiresExplicitSelectionAndRejectsMutation(string suffix, int expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(expected, CliApplication.Run(("project review progression " + suffix).Split(' '), output, error));
    }

    [Fact]
    public async Task NativeMcpReturnsSameReadOnlyReportAndRejectsMissingOrUnexpectedInputs()
    {
        using TemporaryDirectory temporary = new();
        var reader = ProjectReviewMcpTests.CreateReadyReview(temporary, withTestSave: true);
        var report = Report();
        int dispatches = 0;
        var tool = ProjectReviewMcpProgressionTools.Create(reader, (selection, _) =>
        { Assert.Equal(Selection, selection); dispatches++; return report; });
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations.DestructiveHint);
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "progression-test", Version = "1" }, ToolCollection = [tool] };
        await using McpTestClient harness = await McpTestClient.StartAsync(options);
        var arguments = new Dictionary<string, object?> { ["npcId"] = Selection.NpcId, ["mailId"] = Selection.MailId, ["questId"] = Selection.QuestId };
        var result = await harness.Client.CallToolAsync(ProjectReviewMcpProgressionTools.ToolName, arguments, cancellationToken: harness.Token);
        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(report, JsonOptions),
            JsonSerializer.Serialize(result.StructuredContent!.Value.Deserialize<ReviewProgressionReport>(JsonOptions), JsonOptions));
        report = report with { State = "unavailable", ErrorCode = "progressionWorldNotReady", Data = null };
        Assert.True((await harness.Client.CallToolAsync(ProjectReviewMcpProgressionTools.ToolName, arguments, cancellationToken: harness.Token)).IsError);
        arguments["questId"] = 5;
        Assert.True((await harness.Client.CallToolAsync(ProjectReviewMcpProgressionTools.ToolName, arguments, cancellationToken: harness.Token)).IsError);
        arguments.Remove("questId");
        Assert.True((await harness.Client.CallToolAsync(ProjectReviewMcpProgressionTools.ToolName, arguments, cancellationToken: harness.Token)).IsError);
        arguments["questId"] = Selection.QuestId;
        arguments["complete"] = true;
        Assert.True((await harness.Client.CallToolAsync(ProjectReviewMcpProgressionTools.ToolName, arguments, cancellationToken: harness.Token)).IsError);
        Assert.Equal(2, dispatches);
    }

    private static ReviewProgressionValues Data() => new("101", Fixture, 600, Selection,
        new("available", 500), new(false, false, 1), new(1, 1, 1));
    private static ReviewProgressionReport Report() => new(1, "ready", null, Launch, "single", null, DateTimeOffset.UtcNow, Data());
    private static LiveLabCommandResult Sent(string root) => new(0, new ProjectReviewCommandReport(1, null, root, "ready", null, true, [], []));
    private static void Publish(string root, string id, ReviewProgressionReport report, string? envelopeId = null) =>
        File.WriteAllText(ReviewProgressionContract.ResponsePath(LiveLabPaths.Resolve(root).RuntimePath, id),
            JsonSerializer.Serialize(new ReviewProgressionResponseEnvelope(1, envelopeId ?? id, report), JsonOptions));
}
