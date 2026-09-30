using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewSharedValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("unknown", "dataOperationUnknown")]
    [InlineData("page", "dataPaginationInvalid")]
    [InlineData("asset", "dataAssetInvalid")]
    [InlineData("key", "dataKeyInvalid")]
    [InlineData("operand", "dataRequestInvalid")]
    public void DataRejectsAtBothBoundariesBeforeReadingOrSending(string change, string code)
    {
        ReviewDataQuery query = change switch
        {
            "unknown" => new("GET", "Data/Objects", "388", -1, 0),
            "page" => new("keys", "Data/Objects", null, 0, 101),
            "asset" => new("keys", "Data/Ob\njects", null, 0, 100),
            "key" => new("get", "Data/Objects", new string('x', 2049), 0, 1),
            _ => new("assets", "Data/Objects", null, 0, 100),
        };
        var source = new UnreadSource();
        ReviewDataProblem cli = Assert.Single(Assert.IsType<ReviewDataReport>(
            ProjectReviewDataService.Execute(query, "not-used", inputSender: new NoInput()).Report).Problems);
        ReviewDataProblem runtime = Assert.Single(ReviewDataOperation.Execute(query, source).Problems);
        Assert.Equal(code, cli.Code);
        Assert.Equal(cli, runtime);
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData("page", "texturePaginationInvalid")]
    [InlineData("asset", "textureAssetInvalid")]
    [InlineData("operand", "textureRequestInvalid")]
    [InlineData("exactPage", "textureRequestInvalid")]
    public void TexturePreservesBoundaryDiagnosticsWithoutTouchingContent(string change, string code)
    {
        ReviewTextureQuery query = change switch
        {
            "page" => new("assets", null, -1, 100),
            "asset" => new("get", "Maps/\ud800", 0, 1),
            "operand" => new("assets", "Maps/Farm", 0, 100),
            _ => new("preview", "Maps/Farm", 1, 1),
        };
        var source = new UnreadSource();
        ReviewTextureProblem cli = Assert.Single(Assert.IsType<ReviewTextureReport>(
            ProjectReviewTextureService.Execute(query, "not-used", inputSender: new NoInput()).Report).Problems);
        ReviewTextureProblem runtime = Assert.Single(ReviewTextureOperation.Execute(query, source,
            "not-used", new string('a', 32)).Problems);
        Assert.Equal(code, cli.Code);
        Assert.Equal(code, runtime.Code);
        if (code == "textureRequestInvalid")
        {
            Assert.Equal("The review-texture request has unexpected operands or pagination.", cli.Message);
            Assert.Equal(change == "operand" ? "The review-texture request has unexpected operands."
                : "Exact texture operations do not accept pagination.", runtime.Message);
        }
        else Assert.Equal(cli, runtime);
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData("unknown", "audioOperationUnknown")]
    [InlineData("page", "audioPaginationInvalid")]
    [InlineData("operand", "audioRequestInvalid")]
    [InlineData("exactPage", "audioRequestInvalid")]
    [InlineData("cue", "audioCueIdInvalid")]
    public void AudioPreservesEncodingDiagnosticAndValidationOrder(string change, string code)
    {
        ReviewAudioQuery query = change switch
        {
            "unknown" => new("CUE", "\ud800", -1, 0),
            "page" => new("cues", null, 0, 101),
            "operand" => new("cues", "spring_day", -1, 0),
            "exactPage" => new("cue", "\ud800", 1, 1),
            _ => new("cue", "\ud800", 0, 1),
        };
        var source = new UnreadSource();
        ReviewAudioProblem cli = Assert.Single(Assert.IsType<ReviewAudioReport>(
            ProjectReviewAudioService.Execute(query, "not-used", inputSender: new NoInput()).Report).Problems);
        ReviewAudioProblem runtime = Assert.Single(ReviewAudioOperation.Execute(query, source).Problems);
        Assert.Equal(code, cli.Code);
        Assert.Equal(code, runtime.Code);
        if (code == "audioCueIdInvalid")
        {
            Assert.Equal("A cue ID must contain 1-256 well-formed non-control characters.", cli.Message);
            Assert.Equal("A cue ID must contain 1-256 non-control characters.", runtime.Message);
        }
        else Assert.Equal(cli, runtime);
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData("page", "modAssetPaginationInvalid")]
    [InlineData("asset", "modAssetNameInvalid")]
    [InlineData("key", "modAssetKeyInvalid")]
    [InlineData("operand", "modAssetRequestInvalid")]
    public void ModAssetsRejectBeforeRegistryAccessOrDispatch(string change, string code)
    {
        ReviewModAssetQuery query = change switch
        {
            "page" => new("get", "Mods/Example/Words", "key", 0, 2),
            "asset" => new("keys", "Mods/Example/../Words", null, 0, 100),
            "key" => new("get", "Mods/Example/Words", "\ud800", 0, 1),
            _ => new("keys", "Mods/Example/Words", "unexpected", 0, 100),
        };
        var source = new UnreadSource();
        ReviewModAssetProblem cli = Assert.Single(Assert.IsType<ReviewModAssetReport>(
            ProjectReviewModAssetService.Execute(query, "not-used", inputSender: new NoInput()).Report).Problems);
        ReviewModAssetProblem runtime = Assert.Single(ReviewModAssetOperation.Execute(query, source).Problems);
        Assert.Equal(code, cli.Code);
        Assert.Equal(cli, runtime);
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData("oldBoundary", true)]
    [InlineData("oldTick", false)]
    [InlineData("futureBoundary", true)]
    [InlineData("futureTick", false)]
    [InlineData("beforeStart", false)]
    [InlineData("nonUtc", false)]
    [InlineData("launch", false)]
    [InlineData("topology", false)]
    [InlineData("role", false)]
    public void EveryCaptureBoundaryChecksExactBindingAndInclusiveFreshness(string change, bool accepted)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeSnapshot expected = ProjectReviewMcpTests.CreateReadyReview(temporary).Read().Snapshot!;
        DateTimeOffset now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset started = change == "beforeStart" ? now : now.AddSeconds(-10);
        DateTimeOffset captured = change switch
        {
            "oldBoundary" => now.AddSeconds(-5),
            "oldTick" => now.AddSeconds(-5).AddTicks(-1),
            "futureBoundary" => now.AddSeconds(5),
            "futureTick" => now.AddSeconds(5).AddTicks(1),
            "beforeStart" => started.AddTicks(-1),
            "nonUtc" => now.ToOffset(TimeSpan.FromHours(1)),
            _ => now,
        };
        string launch = change == "launch" ? new string('f', 32) : expected.LaunchId;
        string topology = change == "topology" ? "network-2" : expected.Topology;
        string? role = change == "role" ? "host" : expected.Role;
        Assert.Equal(accepted, ProjectReviewInventoryService.ValidResponse(
            new(1, "unavailable", "inventoryCaptureFailed", launch, topology, role, captured, null),
            expected, new string('a', 32), started, now));
        Assert.Equal(accepted, ProjectReviewContainerService.ValidResponse(
            new(1, "unavailable", "containerCaptureFailed", launch, topology, role, captured, null),
            expected, new string('a', 32), started, now));
        Assert.Equal(accepted, ProjectReviewWorldService.ValidResponse(
            new(1, "unavailable", "worldCaptureFailed", launch, topology, role, captured, null),
            expected, new(0, 0, 1, 1), started, now));
        Assert.Equal(accepted, ProjectReviewShopService.ValidResponse(
            new(1, "unavailable", "shopCaptureFailed", launch, topology, role, captured, null),
            expected, started, now));
        Assert.Equal(accepted, ProjectReviewMenuService.ValidResponse(
            new(1, "unavailable", "menuCaptureFailed", launch, topology, role, captured,
                null, null, false, false, false, [], []), expected, started, now));
    }

    [Fact]
    public void SharedBindingDoesNotExpandShopTopologyOrAcceptUnknownPayloadErrors()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeSnapshot expected = ProjectReviewMcpTests.CreateReadyReview(temporary).Read().Snapshot!;
        expected = expected with { Topology = "network-2", Role = "host" };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(ProjectReviewInventoryService.ValidResponse(
            new(1, "unavailable", "inventoryCaptureFailed", expected.LaunchId, expected.Topology, expected.Role, now, null),
            expected, new string('a', 32), now, now));
        Assert.False(ProjectReviewShopService.ValidResponse(
            new(1, "unavailable", "shopCaptureFailed", expected.LaunchId, expected.Topology, expected.Role, now, null),
            expected, now, now));
        Assert.False(ProjectReviewInventoryService.ValidResponse(
            new(1, "unavailable", "customError", expected.LaunchId, expected.Topology, expected.Role, now, null),
            expected, new string('a', 32), now, now));
    }

    [Fact]
    public void DataExactReadKeepsLegacyNormalizationKeysAndPaginationAtBothBoundaries()
    {
        using TemporaryDirectory temporary = new();
        var query = new ReviewDataQuery("get", "data-things", "internal key", 17, 100);
        var source = new DataSource();
        ReviewDataReport runtime = ReviewDataOperation.Execute(query, source);
        Assert.Equal("ready", runtime.State);
        Assert.Equal("Data/Things", runtime.AssetName);
        Assert.Equal(42, runtime.Record!.Value.GetInt32());
        bool dispatched = false;
        LiveLabCommandResult result = ProjectReviewDataService.Execute(query, temporary.Path, send: command =>
        {
            dispatched = true;
            string[] tokens = command.Split(' ');
            Assert.Equal("17", tokens[4]);
            Assert.Equal("100", tokens[5]);
            Assert.True(ReviewTransportToken.TryDecode(tokens[6], 256, out string? asset));
            Assert.Equal(query.Asset, asset);
            Assert.True(ReviewTransportToken.TryDecode(tokens[7], 2048, out string? key));
            Assert.Equal(query.Key, key);
            string path = ReviewDataContract.ResponsePath(LiveLabPaths.Resolve(temporary.Path).RuntimePath, tokens[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ReviewDataResponseEnvelope(1, tokens[2], runtime),
                JsonOptions));
            return new LiveLabCommandResult(0,
                new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        });
        Assert.True(dispatched);
        Assert.Equal(0, result.ExitCode);
        ReviewDataReport cli = Assert.IsType<ReviewDataReport>(result.Report);
        Assert.Equal(runtime.AssetName, cli.AssetName);
        Assert.Equal(runtime.Key, cli.Key);
        Assert.Equal(42, cli.Record!.Value.GetInt32());
    }

    private sealed class DataSource : IReviewDataSource
    {
        public string GameVersion => "test";
        public string GameFileVersion => "test";
        public IReadOnlyList<string> DiscoverCanonicalAssetNames() => ["Data/Things"];
        public object LoadAsset(string assetName) => new Dictionary<string, int> { ["internal key"] = 42 };
    }
    private sealed class NoInput : IProjectReviewConsoleInputSender
    {
        public ProjectReviewConsoleInputResult SendLine(OwnedProcessIdentity expected, string line) =>
            throw new InvalidOperationException("Invalid request was dispatched.");
    }

    private sealed class UnreadSource : IReviewDataSource, IReviewTextureSource, IReviewAudioSource, IReviewModAssetSource
    {
        public int Reads { get; private set; }
        public string GameVersion => "test";
        public string GameFileVersion => "test";
        private InvalidOperationException UnexpectedRead()
        {
            Reads++;
            return new InvalidOperationException("Invalid request reached content or registry access.");
        }
        public IReadOnlyList<string> DiscoverCanonicalAssetNames() => throw UnexpectedRead();
        public object LoadAsset(string assetName) => throw UnexpectedRead();
        public bool TryClassifyTexture(string assetName, long maximumInputBytes, out bool isTexture, out long inputBytes) => throw UnexpectedRead();
        public IReviewTextureAsset LoadTexture(string assetName) => throw UnexpectedRead();
        public IReadOnlyList<ReviewAudioChangeDefinition> LoadAudioChanges() => throw UnexpectedRead();
        public IReadOnlyList<ReviewAudioJukeboxDefinition> LoadJukeboxTracks() => throw UnexpectedRead();
        public ReviewAudioSoundBankStatus GetSoundBankStatus() => throw UnexpectedRead();
        public ReviewAudioCueProbe ProbeCue(string cueId) => throw UnexpectedRead();
        public ReviewModAssetInventorySnapshot GetInventory() => throw UnexpectedRead();
        public ReviewModAssetLoadResult Load(ReviewModAssetObservation asset) => throw UnexpectedRead();
    }
}
