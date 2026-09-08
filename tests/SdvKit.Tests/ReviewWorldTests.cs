using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewWorldTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LocationInstance = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SoilInstance = "cccccccccccccccccccccccccccccccc";
    private const string CropInstance = "dddddddddddddddddddddddddddddddd";
    private const string MachineInstance = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string Revision = "ffffffffffffffffffffffffffffffff";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(0, 0, 1, 1, null)]
    [InlineData(-100000, -100000, 16, 16, null)]
    [InlineData(0, 0, 32, 8, null)]
    [InlineData(0, 0, 0, 1, "worldAreaLimit")]
    [InlineData(0, 0, 33, 1, "worldAreaLimit")]
    [InlineData(0, 0, 17, 16, "worldAreaLimit")]
    [InlineData(100000, 0, 2, 1, "worldAreaInvalid")]
    [InlineData(-100001, 0, 1, 1, "worldAreaInvalid")]
    public void RectangleBoundsAreExplicit(int x, int y, int width, int height, string? expected) =>
        Assert.Equal(expected, ReviewWorldContract.QueryProblem(new(x, y, width, height)));

    [Fact]
    public void EmptyAreaIsCompleteAndRowMajor()
    {
        ReviewWorldValues data = Data(new(10, 20, 2, 2),
        [
            Missing(10, 20), Missing(11, 20), Missing(10, 21), Missing(11, 21),
        ]);
        Assert.True(ReviewWorldContract.DataValid(data));
        Assert.All(data.Tiles, tile =>
        {
            Assert.Null(tile.Soil);
            Assert.Equal("missing", tile.ObjectState);
        });
        Assert.False(ReviewWorldContract.DataValid(data with { Complete = false }));
        Assert.False(ReviewWorldContract.DataValid(data with { Tiles = data.Tiles.Take(3).ToArray() }));
        Assert.False(ReviewWorldContract.DataValid(data with
        {
            Tiles = [data.Tiles[0], data.Tiles[2], data.Tiles[1], data.Tiles[3]],
        }));
    }

    [Fact]
    public void CropTransitionsKeepInstanceAndChangeRevision()
    {
        ReviewWorldCrop dryCrop = Crop(0, 0, false, false, false, "11111111111111111111111111111111");
        ReviewWorldSoil dry = Soil(false, true, AvailableCrop(dryCrop), "22222222222222222222222222222222");
        ReviewWorldCrop wateredCrop = dryCrop with
        {
            CurrentPhase = 1,
            Revision = "33333333333333333333333333333333",
        };
        ReviewWorldSoil watered = dry with
        {
            Watered = true,
            Crop = AvailableCrop(wateredCrop),
            Revision = "44444444444444444444444444444444",
        };
        ReviewWorldCrop readyCrop = wateredCrop with
        {
            CurrentPhase = 3,
            FullyGrown = true,
            ReadyForHarvest = true,
            Revision = "55555555555555555555555555555555",
        };
        ReviewWorldSoil ready = watered with { Crop = AvailableCrop(readyCrop), Revision = "66666666666666666666666666666666" };
        foreach (ReviewWorldSoil state in new[] { dry, watered, ready })
            Assert.True(ReviewWorldContract.DataValid(Data(new(4, 5, 1, 1), [Missing(4, 5) with { Soil = state }])));
        Assert.Equal(dry.Crop.Data!.InstanceId, ready.Crop.Data!.InstanceId);
        Assert.NotEqual(dry.Crop.Data.Revision, ready.Crop.Data.Revision);
        Assert.False(ReviewWorldContract.DataValid(Data(new(4, 5, 1, 1),
            [Missing(4, 5) with { Soil = ready with { Crop = AvailableCrop(readyCrop with { Dead = true }) } }])));
    }

    [Theory]
    [InlineData("idle", -1, "absent")]
    [InlineData("idle", 0, "absent")]
    [InlineData("processing", 120, "available")]
    [InlineData("ready", 0, "available")]
    public void MachineStatesHaveConsistentNativeFacts(string state, int minutes, string outputState)
    {
        ReviewWorldItemObservation output = outputState == "absent" ? Absent() : Available("(O)334", 1, 0);
        ReviewWorldMachine machine = Machine(state, minutes, output);
        ReviewWorldTile tile = Missing(2, 3) with
        {
            ObjectState = "machine",
            ObjectQualifiedItemId = machine.QualifiedItemId,
            Machine = machine,
        };
        Assert.True(ReviewWorldContract.DataValid(Data(new(2, 3, 1, 1), [tile])));
        Assert.False(ReviewWorldContract.DataValid(Data(new(2, 3, 1, 1),
            [tile with { Machine = machine with { MinutesUntilReady = state == "processing" ? 0 : 1 } }])));
    }

    [Fact]
    public void ReplacementAndStateChangeAreSeparatelyComparable()
    {
        ReviewWorldMachine first = Machine("processing", 120, Available("(O)334", 1, 0));
        ReviewWorldMachine progressed = first with
        {
            MinutesUntilReady = 110,
            Revision = "11111111111111111111111111111111",
        };
        ReviewWorldMachine replacement = progressed with
        {
            InstanceId = "22222222222222222222222222222222",
            Revision = "33333333333333333333333333333333",
        };
        Assert.Equal(first.InstanceId, progressed.InstanceId);
        Assert.NotEqual(first.Revision, progressed.Revision);
        Assert.NotEqual(progressed.InstanceId, replacement.InstanceId);
        foreach (ReviewWorldMachine machine in new[] { first, progressed, replacement })
            Assert.True(ReviewWorldContract.DataValid(Data(new(0, 0, 1, 1),
                [Missing(0, 0) with { ObjectState = "machine", ObjectQualifiedItemId = machine.QualifiedItemId, Machine = machine }])));
    }

    [Fact]
    public void IdleMachineMayRetainHistoricalLastInput()
    {
        ReviewWorldMachine machine = Machine("idle", -1, Absent()) with
        {
            Input = Available("(O)330", 1, 0),
        };
        ReviewWorldTile tile = Missing(0, 0) with
        {
            ObjectState = "machine",
            ObjectQualifiedItemId = machine.QualifiedItemId,
            Machine = machine,
        };
        Assert.True(ReviewWorldContract.DataValid(Data(new(0, 0, 1, 1), [tile])));
    }

    [Fact]
    public void UnsupportedAndUnavailableObjectsStayDistinctFromMissing()
    {
        ReviewWorldTile[] tiles =
        [
            Missing(0, 0),
            new(1, 0, null, "unsupported", "worldObjectFamilyUnsupported", "(O)388", null),
            new(2, 0, null, "unavailable", "worldObjectPropertiesUnavailable", null, null),
        ];
        Assert.True(ReviewWorldContract.DataValid(Data(new(0, 0, 3, 1), tiles)));
        Assert.Equal(3, tiles.Select(tile => tile.ObjectState).Distinct().Count());
        Assert.False(ReviewWorldContract.DataValid(Data(new(0, 0, 3, 1),
            [tiles[0] with { ObjectReason = "worldObjectFamilyUnsupported" }, tiles[1], tiles[2]])));
    }

    [Theory]
    [InlineData("missing", null)]
    [InlineData("unsupported", "worldCropFamilyUnsupported")]
    [InlineData("unavailable", "worldCropPropertiesUnavailable")]
    public void MissingUnsupportedAndUnavailableCropsAreExplicit(string state, string? reason)
    {
        var observation = new ReviewWorldCropObservation(state, reason, null);
        ReviewWorldSoil soil = Soil(false, false, observation, Revision);
        Assert.True(ReviewWorldContract.DataValid(Data(new(0, 0, 1, 1),
            [Missing(0, 0) with { Soil = soil }])));
        Assert.False(ReviewWorldContract.DataValid(Data(new(0, 0, 1, 1),
            [Missing(0, 0) with { Soil = soil with { Crop = observation with { Reason = "wrong" } } }])));
    }

    [Fact]
    public void MalformedUnknownAndOversizedCapturesAreRejected()
    {
        ReviewWorldReport report = Report();
        string json = JsonSerializer.Serialize(new ReviewWorldResponseEnvelope(1, Launch, report), JsonOptions);
        Assert.NotNull(ProjectReviewWorldService.DeserializeResponse(Encoding.UTF8.GetBytes(json)));
        foreach (string invalid in new[]
        {
            json.Replace("\"role\":null,", "", StringComparison.Ordinal),
            json.Replace("\"role\":null,", "\"role\":null,\"role\":null,", StringComparison.Ordinal),
            json.Replace("\"captureTick\":42,", "\"captureTick\":42,\"privatePath\":\"hidden\",", StringComparison.Ordinal),
            json.Replace("\"objectState\":\"missing\",", "\"objectState\":\"missing\",\"objectState\":\"missing\",", StringComparison.Ordinal),
            JsonSerializer.Serialize(new ReviewWorldResponseEnvelope(1, Launch, report with
            {
                Data = Data(new(0, 0, 1, 1), Enumerable.Range(0, 257).Select(i => Missing(i, 0)).ToArray()),
            }), JsonOptions),
        }) Assert.Throws<InvalidDataException>(() =>
            ProjectReviewWorldService.DeserializeResponse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(5, 5, null)]
    [InlineData(6, 6, "worldResponseInvalid")]
    [InlineData(1, 6, "worldResponseStale")]
    public void TransportRequiresFreshCaptureAndFinalBinding(int responseSeconds, int returnSeconds, string? expected)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        int reads = 0;
        DateTimeOffset Clock() => start.AddSeconds(++reads switch { 1 => 0, 2 => responseSeconds, _ => returnSeconds });
        ReviewWorldReport result = ProjectReviewWorldService.Execute(reader, new(0, 0, 1, 1), command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal(["sdvkit", "world"], parts.Take(2));
            Assert.Equal(Launch, parts[3]);
            Assert.Equal(["0", "0", "1", "1"], parts.Skip(4));
            Publish(temporary.Path, parts[2], Report() with { CapturedAtUtc = start });
            return Sent(temporary.Path);
        }, TimeSpan.Zero, Clock);
        Assert.Equal(expected, result.ErrorCode);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("launch")]
    [InlineData("role")]
    [InlineData("area")]
    [InlineData("rebind")]
    [InlineData("unsupported")]
    public void TransportBindsExactReviewAndPreservesUnavailable(string change)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewWorldReport result = ProjectReviewWorldService.Execute(reader, new(0, 0, 1, 1), command =>
        {
            string id = command.Split(' ')[2];
            ReviewWorldReport report = Report();
            if (change == "launch") report = report with { LaunchId = new string('b', 32) };
            if (change == "role") report = report with { Role = "host" };
            if (change == "area") report = report with
            {
                Data = Data(new(1, 1, 1, 1), [Missing(1, 1)]),
            };
            if (change == "unsupported") report = report with { State = "unavailable", ErrorCode = "worldAreaLimit", Data = null };
            Publish(temporary.Path, id, report, change == "request" ? new string('b', 32) : null);
            if (change == "rebind")
            {
                var store = new JsonLiveLabStateStore(LiveLabPaths.Resolve(temporary.Path).StatePath);
                store.Write(store.Read()! with { LaunchId = new string('b', 32) });
            }
            return Sent(temporary.Path);
        }, TimeSpan.Zero);
        Assert.Equal(change switch { "rebind" => "reviewBindingChanged", "unsupported" => "worldAreaLimit", _ => "worldResponseInvalid" }, result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("player")]
    public void TransportRejectsChangedWorldOrPlayerBinding(string change)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewWorldReport result = ProjectReviewWorldService.Execute(reader, new(0, 0, 1, 1), command =>
        {
            string id = command.Split(' ')[2];
            ReviewWorldValues data = Report().Data!;
            data = change == "location" ? data with { LocationName = "Town" } : data with { PlayerId = "456" };
            Publish(temporary.Path, id, Report() with { Data = data });
            return Sent(temporary.Path);
        }, TimeSpan.Zero);
        Assert.Equal("worldResponseInvalid", result.ErrorCode);
    }

    [Fact]
    public void NetworkInvalidAreaAndCancellationDoNotSend()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "host");
        Assert.Equal("worldTopologyUnsupported", ProjectReviewWorldService.Execute(reader, new(0, 0, 1, 1),
            _ => throw new InvalidOperationException("Must not send.")).ErrorCode);
        Assert.Equal("worldAreaLimit", ProjectReviewWorldService.Execute(reader, new(0, 0, 32, 32),
            _ => throw new InvalidOperationException("Must not send.")).ErrorCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectReviewWorldService.Execute(reader, new(0, 0, 1, 1),
            _ => throw new InvalidOperationException("Must not send."), cancellationToken: cancellation.Token));
        Assert.DoesNotContain(ProjectReviewMcpServer.CreateOptions(reader).ToolCollection!,
            tool => tool.ProtocolTool.Name == ProjectReviewMcpWorldTools.ToolName);
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("0 0 1 1 --json --json", 2)]
    [InlineData("0 0 1 1 --topology network-2 --json", 2)]
    [InlineData("0 0 32 32 --json", 2)]
    [InlineData("0 0 1 1 --mutate --json", 2)]
    public void CliRoutingRejectsMutationAndUnsupportedScope(string suffix, int expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(expected, CliApplication.Run(("project review world " + suffix).Split(' '), output, error));
        Assert.Contains("project review world", expected == 0 ? output.ToString() : error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public void CliAndMcpUseTheSameTypedReport(bool unavailable, int expectedExit)
    {
        ReviewWorldReport report = Report();
        if (unavailable) report = report with { State = "unavailable", ErrorCode = "worldNotReady", Data = null };
        using var output = new StringWriter();
        Assert.Equal(expectedExit, CliApplication.WriteReviewWorldReport(report, output));
        Assert.Equal(JsonSerializer.Serialize(report, JsonOptions).Trim(), output.ToString().Trim());
    }

    [Fact]
    public async Task McpIsReadOnlyAndReturnsTheSameCapture()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewWorldReport report = Report();
        McpServerTool tool = ProjectReviewMcpWorldTools.Create(reader, (area, _) =>
            area == report.Data!.Area ? report : throw new InvalidOperationException());
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations.DestructiveHint);
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "world-test", Version = "1" }, ToolCollection = [tool] };
        await using McpTestClient harness = await McpTestClient.StartAsync(options);
        CallToolResult result = await harness.Client.CallToolAsync(ProjectReviewMcpWorldTools.ToolName,
            new Dictionary<string, object?> { ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1 },
            cancellationToken: harness.Token);
        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(report, JsonOptions),
            JsonSerializer.Serialize(Assert.IsType<JsonElement>(result.StructuredContent)
                .Deserialize<ReviewWorldReport>(JsonOptions), JsonOptions));
        result = await harness.Client.CallToolAsync(ProjectReviewMcpWorldTools.ToolName,
            new Dictionary<string, object?> { ["x"] = 0, ["y"] = 0, ["width"] = 32, ["height"] = 32 },
            cancellationToken: harness.Token);
        Assert.True(result.IsError);
    }

    private static ReviewWorldValues Data(ReviewWorldArea area, IReadOnlyList<ReviewWorldTile> tiles) =>
        new("Farm", LocationInstance, "101", 42, area, true, tiles);
    private static ReviewWorldTile Missing(int x, int y) => new(x, y, null, "missing", null, null, null);
    private static ReviewWorldCrop Crop(int phase, int day, bool grown, bool dead, bool ready, string revision) =>
        new(CropInstance, revision, "(O)472", "(O)24", phase, day, 4, grown, dead, ready, false);
    private static ReviewWorldCropObservation AvailableCrop(ReviewWorldCrop crop) => new("available", null, crop);
    private static ReviewWorldSoil Soil(bool watered, bool needsWatering, ReviewWorldCropObservation crop, string revision) =>
        new(SoilInstance, revision, watered, needsWatering, null, crop);
    private static ReviewWorldItemObservation Absent() => new("absent", null, null);
    private static ReviewWorldItemObservation Available(string id, int stack, int quality) =>
        new("available", null, new(id, stack, quality));
    private static ReviewWorldMachine Machine(string state, int minutes, ReviewWorldItemObservation output) =>
        new(MachineInstance, Revision, "(BC)12", state, state == "ready", minutes,
            state == "idle" ? Absent() : Available("(O)330", 1, 0), output);
    private static ReviewWorldReport Report() => new(1, "ready", null, Launch, "single", null,
        DateTimeOffset.UtcNow, Data(new(0, 0, 1, 1), [Missing(0, 0)]));
    private static LiveLabCommandResult Sent(string root) => new(0,
        new ProjectReviewCommandReport(1, null, root, "ready", null, true, [], []));
    private static void Publish(string root, string id, ReviewWorldReport report, string? envelopeId = null) =>
        File.WriteAllText(ReviewWorldContract.ResponsePath(LiveLabPaths.Resolve(root).RuntimePath, id),
            JsonSerializer.Serialize(new ReviewWorldResponseEnvelope(1, envelopeId ?? id, report), JsonOptions));
}
