using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewShopTests
{
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("vanilla", null)]
    [InlineData("subclass", "shopMenuUnsupported")]
    [InlineData("noMenu", "shopMenuUnsupported")]
    [InlineData("otherId", "shopFamilyUnsupported")]
    [InlineData("noData", "shopFamilyUnsupported")]
    [InlineData("currency", "shopCurrencyUnsupported")]
    [InlineData("dataCurrency", "shopCurrencyUnsupported")]
    [InlineData("child", "shopBehaviorUnsupported")]
    [InlineData("readOnly", "shopBehaviorUnsupported")]
    [InlineData("purchaseCallback", "shopBehaviorUnsupported")]
    [InlineData("purchaseCheck", "shopBehaviorUnsupported")]
    public void UnsupportedShopFamiliesAndBehaviorsAreUnavailable(string change, string? expected) =>
        Assert.Equal(expected, ReviewShopContract.ShopUnavailableReason(change is not ("subclass" or "noMenu"),
            change == "otherId" ? "CustomShop" : "SeedShop", change != "noData", change == "currency" ? 4 : 0,
            change == "dataCurrency" ? 2 : 0, change == "child", change == "readOnly",
            change == "purchaseCallback", change == "purchaseCheck"));

    [Theory]
    [InlineData("vanilla", null)]
    [InlineData("free", null)]
    [InlineData("infinite", null)]
    [InlineData("soldOut", null)]
    [InlineData("subclass", "shopItemUnsupported")]
    [InlineData("recipe", "shopItemUnsupported")]
    [InlineData("bigCraftable", "shopItemUnsupported")]
    [InlineData("lostItem", "shopItemUnsupported")]
    [InlineData("stardrop", "shopItemUnsupported")]
    [InlineData("qiGem", "shopItemUnsupported")]
    [InlineData("bundle", "shopItemUnsupported")]
    [InlineData("buyback", "shopOfferBehaviorUnsupported")]
    [InlineData("trade", "shopOfferBehaviorUnsupported")]
    [InlineData("purchaseActions", "shopOfferBehaviorUnsupported")]
    [InlineData("syncItem", "shopOfferBehaviorUnsupported")]
    [InlineData("negativePrice", "shopOfferValuesInvalid")]
    [InlineData("negativeStock", "shopOfferValuesInvalid")]
    public void OnlyOrdinaryUnitGoldOffersHaveInterpretedPriceAndStock(string change, string? expected) =>
        Assert.Equal(expected, ReviewShopContract.OfferUnavailableReason(change != "subclass", change == "recipe",
            change == "bigCraftable", change == "lostItem", change switch { "stardrop" => "(O)434", "qiGem" => "(O)858", _ => "(O)472" },
            change == "bundle" ? 5 : 1, change == "buyback", change == "trade", change == "purchaseActions",
            change == "syncItem", change switch { "negativePrice" => -1, "free" => 0, _ => 20 },
            change switch { "infinite" => int.MaxValue, "negativeStock" => -1, "soldOut" => 0, _ => 10 }));

    [Fact]
    public void CapturesKeepHeldPurchasesSeparateFromInventoryAndFiniteStock()
    {
        ReviewShopValues before = Data();
        ReviewShopValues held = before with
        {
            Money = 480,
            Offers = [before.Offers[0] with { Stock = 9 }],
            HeldItem = new("(O)472", 1, 0),
        };
        ReviewShopValues placed = held with { Inventory = [new(0, held.HeldItem)], HeldItem = null };
        Assert.True(ReviewShopContract.DataValid(before));
        Assert.True(ReviewShopContract.DataValid(held));
        Assert.True(ReviewShopContract.DataValid(placed));
        Assert.Equal(-20, held.Money - before.Money);
        Assert.Equal(-1, held.Offers[0].Stock - before.Offers[0].Stock);
        Assert.Null(held.Inventory[0].Item);
        Assert.Equal(1, placed.Inventory[0].Item!.Stack);
        Assert.True(ReviewShopContract.DataValid(before with { Offers = [] }));
        Assert.True(ReviewShopContract.DataValid(before with
        {
            Offers = [before.Offers[0] with { Stock = null, UnlimitedStock = true },
                new(1, "unavailable", "shopOfferBehaviorUnsupported", null, null, null, null)],
        }));
    }

    [Fact]
    public void InvalidOrUnboundedTypedValuesAreRejected()
    {
        ReviewShopValues data = Data();
        foreach (ReviewShopValues invalid in new[]
        {
            data with { ShopId = "Other" }, data with { Currency = "QiGems" },
            data with { IdentityScope = "unknown" }, data with { PlayerId = " 123 " }, data with { PlayerId = "0" },
            data with { ScrollIndex = 2 },
            data with { Inventory = Enumerable.Range(0, 145).Select(i => new ReviewShopInventorySlot(i, null)).ToArray() },
            data with { Inventory = [new(1, null)] },
            data with { HeldItem = new("(O)472", 0, 0) },
            data with { Offers = Enumerable.Range(0, 257).Select(i => data.Offers[0] with { ForSaleIndex = i }).ToArray() },
            data with { Offers = [data.Offers[0] with { ForSaleIndex = 1 }] },
            data with { Offers = [data.Offers[0] with { Price = -1 }] },
            data with { Offers = [data.Offers[0] with { Stock = int.MaxValue }] },
            data with { Offers = [data.Offers[0] with { UnlimitedStock = true }] },
            data with { Offers = [data.Offers[0] with { Item = new("(O)858", 1, 0) }] },
            data with { Offers = [data.Offers[0] with { Item = new("(O)472", 5, 0) }] },
            data with { Offers = [data.Offers[0] with { Availability = "unavailable", Reason = "shopItemUnsupported" }] },
        }) Assert.False(ReviewShopContract.DataValid(invalid));
    }

    [Fact]
    public void MissingDuplicateUnknownAndOversizedJsonFieldsAreRejected()
    {
        string json = JsonSerializer.Serialize(new ReviewShopResponseEnvelope(1, Launch, Report()), JsonOptions);
        Assert.NotNull(ProjectReviewShopService.DeserializeResponse(Encoding.UTF8.GetBytes(json)));
        foreach (string invalid in new[]
        {
            json.Replace("\"role\":null,", "", StringComparison.Ordinal),
            json.Replace("\"role\":null,", "\"role\":null,\"role\":null,", StringComparison.Ordinal),
            json.Replace("\"price\":20,", "\"price\":20,\"privatePath\":\"hidden\",", StringComparison.Ordinal),
            json.Replace("\"quality\":0", "\"quality\":0,\"quality\":0", StringComparison.Ordinal),
            JsonSerializer.Serialize(new ReviewShopResponseEnvelope(1, Launch, Report() with
            {
                Data = Data() with { Inventory = Enumerable.Range(0, 145).Select(i => new ReviewShopInventorySlot(i, null)).ToArray() },
            }), JsonOptions),
        }) Assert.Throws<InvalidDataException>(() => ProjectReviewShopService.DeserializeResponse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(5, 5, null)]
    [InlineData(6, 6, "shopResponseInvalid")]
    [InlineData(1, 6, "shopResponseStale")]
    public void TransportRequiresFreshCaptureIncludingAfterFinalBindingCheck(int responseSeconds, int returnSeconds, string? expected)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        int reads = 0;
        DateTimeOffset Clock() => start.AddSeconds(++reads switch { 1 => 0, 2 => responseSeconds, _ => returnSeconds });
        ReviewShopReport result = ProjectReviewShopService.Execute(reader, command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal("shop", parts[1]);
            Assert.Equal(Launch, parts[3]);
            Publish(temporary.Path, parts[2], Report() with { CapturedAtUtc = start });
            return Sent(temporary.Path);
        }, TimeSpan.Zero, Clock);
        Assert.Equal(expected, result.ErrorCode);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("launch")]
    [InlineData("role")]
    [InlineData("rebind")]
    [InlineData("unsupported")]
    public void TransportBindsExactReviewAndPreservesExplicitUnavailable(string change)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewShopReport result = ProjectReviewShopService.Execute(reader, command =>
        {
            string id = command.Split(' ')[2];
            ReviewShopReport report = Report();
            if (change == "launch") report = report with { LaunchId = new string('b', 32) };
            if (change == "role") report = report with { Role = "host" };
            if (change == "unsupported") report = report with { State = "unavailable", ErrorCode = "shopCurrencyUnsupported", Data = null };
            Publish(temporary.Path, id, report, change == "request" ? new string('b', 32) : null);
            if (change == "rebind")
            {
                var store = new JsonLiveLabStateStore(LiveLabPaths.Resolve(temporary.Path).StatePath);
                store.Write(store.Read()! with { LaunchId = new string('b', 32) });
            }
            return Sent(temporary.Path);
        }, TimeSpan.Zero);
        Assert.Equal(change switch { "rebind" => "reviewBindingChanged", "unsupported" => "shopCurrencyUnsupported", _ => "shopResponseInvalid" }, result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public void NetworkAndCancellationDoNotSendCommands()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, "host");
        Assert.Equal("shopTopologyUnsupported", ProjectReviewShopService.Execute(reader,
            _ => throw new InvalidOperationException("Must not send.")).ErrorCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectReviewShopService.Execute(reader,
            _ => throw new InvalidOperationException("Must not send."), cancellationToken: cancellation.Token));
        Assert.DoesNotContain(ProjectReviewMcpServer.CreateOptions(reader).ToolCollection!,
            tool => tool.ProtocolTool.Name == ProjectReviewMcpShopTools.ToolName);
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("--json --json", 2)]
    [InlineData("--topology network-2 --role host --json", 2)]
    [InlineData("--buy 1 --json", 2)]
    public void CliRoutingRejectsMutationAndUnsupportedTopology(string suffix, int expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(expected, CliApplication.Run(("project review shop " + suffix).Split(' '), output, error));
        Assert.Contains("project review shop", expected == 0 ? output.ToString() : error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public void CliWritesTypedReadyOrUnavailableOutput(bool unavailable, int expectedExit)
    {
        ReviewShopReport report = Report();
        if (unavailable) report = report with { State = "unavailable", ErrorCode = "shopMenuUnsupported", Data = null };
        using var output = new StringWriter();
        Assert.Equal(expectedExit, CliApplication.WriteReviewShopReport(report, output));
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal(report.State, document.RootElement.GetProperty("state").GetString());
        if (!unavailable)
        {
            JsonElement data = document.RootElement.GetProperty("data");
            Assert.Equal(20, data.GetProperty("offers")[0].GetProperty("price").GetInt32());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("heldItem").ValueKind);
        }
    }

    [Fact]
    public async Task McpHasNarrowSchemaReturnsSameCaptureAndRejectsArguments()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewShopReport report = Report();
        McpServerTool tool = ProjectReviewMcpShopTools.Create(reader, _ => report);
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations.DestructiveHint);
        JsonElement schema = Assert.IsType<JsonElement>(tool.ProtocolTool.OutputSchema);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("single", schema.GetProperty("properties").GetProperty("topology").GetProperty("const").GetString());
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "shop-test", Version = "1" }, ToolCollection = [tool] };
        await using McpTestClient harness = await McpTestClient.StartAsync(options);
        CallToolResult result = await harness.Client.CallToolAsync(ProjectReviewMcpShopTools.ToolName,
            new Dictionary<string, object?>(), cancellationToken: harness.Token);
        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(report, JsonOptions),
            JsonSerializer.Serialize(Assert.IsType<JsonElement>(result.StructuredContent).Deserialize<ReviewShopReport>(JsonOptions), JsonOptions));
        report = report with { State = "unavailable", ErrorCode = "shopMenuUnsupported", Data = null };
        result = await harness.Client.CallToolAsync(ProjectReviewMcpShopTools.ToolName,
            new Dictionary<string, object?>(), cancellationToken: harness.Token);
        Assert.True(result.IsError);
        result = await harness.Client.CallToolAsync(ProjectReviewMcpShopTools.ToolName,
            new Dictionary<string, object?> { ["buy"] = 1 }, cancellationToken: harness.Token);
        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
    }

    private static ReviewShopValues Data() => new("SeedShop", "Gold", Launch, "123", 0,
        [new(0, "available", null, new("(O)472", 1, 0), 20, 10, false)], 500, [new(0, null)], null);
    private static ReviewShopReport Report() => new(1, "ready", null, Launch, "single", null, DateTimeOffset.UtcNow, Data());
    private static LiveLabCommandResult Sent(string root) => new(0, new ProjectReviewCommandReport(1, null, root, "ready", null, true, [], []));
    private static void Publish(string root, string id, ReviewShopReport report, string? envelopeId = null) =>
        File.WriteAllText(ReviewShopContract.ResponsePath(LiveLabPaths.Resolve(root).RuntimePath, id),
            JsonSerializer.Serialize(new ReviewShopResponseEnvelope(1, envelopeId ?? id, report), JsonOptions));
}
