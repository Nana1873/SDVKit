using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ReviewMenuTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Launch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly ReviewMenuRectangle Bounds = new(0, 0, 100, 100);

    [Fact]
    public void NoMenuResetsIdentityLifetime()
    {
        var source = new Source();
        var capture = new ReviewMenuCapture();
        ReviewMenuReport absent = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.False(absent.MenuOpen);
        Assert.True(absent.Complete);
        Assert.Null(absent.IdentityScope);
        object root = source.Add("GameMenu", "gameMenu");
        source.Root = root;
        ReviewMenuReport first = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        source.Root = null;
        capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        source.Root = root;
        Assert.NotEqual(first.IdentityScope, capture.Capture(source, Launch, DateTimeOffset.UtcNow).IdentityScope);
    }

    [Fact]
    public void ReorderDuplicateControllerIdsResizeAndReplacementUseObjectIdentity()
    {
        var source = new Source();
        object a = new(), b = new();
        source.Root = source.Add("ShopMenu", "shopMenu", [Component(a), Component(b), Component(a)]);
        var capture = new ReviewMenuCapture();
        ReviewMenuReport first = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        ReviewMenuComponent[] firstComponents = first.Menus[0].Components.ToArray();
        Assert.Equal(2, firstComponents.Length);
        Assert.NotEqual(firstComponents[0].Id, firstComponents[1].Id);
        source.Nodes[source.Root] = source.Nodes[source.Root] with
        {
            ScrollIndex = 3,
            Bounds = new(20, 30, 400, 500),
            Components = [Component(b), Component(a) with { Bounds = new(200, 200, 20, 20), VisibleFlag = true }],
        };
        source.Viewport = new(0, 0, 150, 150);
        ReviewMenuReport second = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.Equal(first.IdentityScope, second.IdentityScope);
        Assert.Equal(firstComponents.Select(c => c.Id), second.Menus[0].Components.Select(c => c.Id));
        Assert.Equal(3, second.Menus[0].ScrollIndex);
        Assert.False(second.Menus[0].Components[0].IntersectsViewport);
        Assert.True(second.Menus[0].Components[0].VisibleFlag);
        Assert.Equal(Bounds, first.Menus[0].Bounds);
        Assert.Equal(Bounds, first.Menus[0].Components[0].Bounds);
        source.Nodes[source.Root] = source.Nodes[source.Root] with { Components = [Component(new object())] };
        Assert.DoesNotContain(capture.Capture(source, Launch, DateTimeOffset.UtcNow).Menus[0].Components[0].Id,
            firstComponents.Select(c => c.Id));
    }

    [Fact]
    public void MenuNotificationPreservesAlreadyObservedRootButClosureChangesScope()
    {
        var source = new Source();
        source.Root = source.Add("Menu", "publicBase", [Component(new object())]);
        var capture = new ReviewMenuCapture();
        ReviewMenuReport beforeNotification = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        capture.ObserveRoot(source.Root);
        ReviewMenuReport afterNotification = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.Equal(beforeNotification.IdentityScope, afterNotification.IdentityScope);
        Assert.Equal(beforeNotification.Menus[0].Components[0].Id, afterNotification.Menus[0].Components[0].Id);
        capture.ObserveRoot(null);
        capture.ObserveRoot(source.Root);
        Assert.NotEqual(beforeNotification.IdentityScope, capture.Capture(source, Launch, DateTimeOffset.UtcNow).IdentityScope);
    }

    [Fact]
    public void NestedPagesAndCustomBaseCoverageRemainExplicit()
    {
        var source = new Source();
        object inventory = source.Add("InventoryMenu", "inventoryMenu");
        object page = source.Add("InventoryPage", "inventoryPage", children: [new(inventory, "inventory")]);
        source.Root = source.Add("GameMenu", "gameMenu", children: [new(page, "activePage")]);
        var capture = new ReviewMenuCapture();
        ReviewMenuReport report = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.True(report.Complete);
        Assert.Equal(["gameMenu", "inventoryPage", "inventoryMenu"], report.Menus.Select(n => n.Adapter));
        Assert.Equal(report.Menus[0].Id, report.Menus[1].ParentId);
        source.Root = source.Add("OriginalModMenu", "publicBase", [Component(new object())]);
        report = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.False(report.Complete);
        Assert.False(report.Truncated);
        Assert.Equal("partial", report.Menus[0].Coverage);
        Assert.Contains("publicBaseOnly", report.Limitations);
    }

    [Fact]
    public void WorldCameraOffsetDoesNotExcludeVisibleScreenLocalControls()
    {
        var source = new Source { Viewport = new(-256, 48, 1280, 720) };
        source.Root = source.Add("InventoryPage", "inventoryPage",
        [
            Component(new object()) with { Bounds = new(1044, 12, 48, 48), Kind = "close" },
            Component(new object()) with { Bounds = new(1101, 412, 64, 104), Kind = "trashCan" },
            Component(new object()) with { Bounds = new(1280, 10, 20, 20) },
        ]);
        ReviewMenuReport report = new ReviewMenuCapture().Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.Equal(new ReviewMenuRectangle(0, 0, 1280, 720), report.Viewport);
        Assert.Equal([true, true, false], report.Menus[0].Components.Select(c => c.IntersectsViewport));
    }

    [Fact]
    public void DepthComponentsScanAndTextAreBoundedWithoutClaimingComplete()
    {
        var source = new Source();
        object child = source.Add("Secret:/private/path", "publicBase");
        for (int i = 0; i < 7; i++) child = source.Add("Menu", "publicBase", children: [new(child, "child")]);
        source.Root = child;
        var capture = new ReviewMenuCapture();
        ReviewMenuReport report = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.Equal(4, report.Menus.Count);
        Assert.True(report.Truncated);
        source.Root = source.Add(new string('x', 129), "publicBase",
            Enumerable.Range(0, 600).Select(_ => Component(new object())).ToArray());
        report = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.Equal(128, report.Menus[0].Components.Count);
        Assert.Contains("componentLimit", report.Limitations);
        Assert.Equal("UnknownMenu", report.Menus[0].Type);
        Assert.False(report.Complete);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(new ReviewMenuResponseEnvelope(1, Launch, report)).Length
            < ReviewMenuContract.MaximumResponseBytes);
        object duplicate = new();
        source.Nodes[source.Root] = source.Nodes[source.Root] with
        {
            Components = Enumerable.Range(0, 600).Select(_ => Component(duplicate)).ToArray(),
        };
        Assert.Contains("componentScanLimit", capture.Capture(source, Launch, DateTimeOffset.UtcNow).Limitations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("host")]
    [InlineData("farmhand")]
    public void TransportUsesExactRoleAndRejectsStaleOrWrongRole(string? role)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = role is null ? ProjectReviewMcpTests.CreateReadyReview(temporary)
            : ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, role);
        ProjectReviewMcpRuntimeSnapshot expected = reader.Read().Snapshot!;
        ReviewMenuReport Call(bool stale, bool wrongRole) => ProjectReviewMenuService.Execute(reader, command =>
        {
            string[] parts = command.Split(' ');
            Assert.Equal(expected.LaunchId, parts[3]);
            var report = new ReviewMenuCapture().Capture(new Source(), expected.LaunchId,
                stale ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow,
                reader.Topology, wrongRole ? "invalid" : reader.Role);
            string path = ReviewMenuContract.ResponsePath(ProjectReviewInputService.RuntimePath(
                temporary.Path, reader.Topology, reader.Role), parts[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ReviewMenuResponseEnvelope(1, parts[2], report),
                JsonOptions));
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        }, TimeSpan.Zero);
        Assert.Equal("ready", Call(false, false).State);
        Assert.Equal("menuResponseInvalid", Call(true, false).ErrorCode);
        Assert.Equal("menuResponseInvalid", Call(false, true).ErrorCode);
    }

    [Fact]
    public void ReboundAndUnavailableContextsFailClosed()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ProjectReviewMcpRuntimeSnapshot snapshot = reader.Read().Snapshot!;
        Assert.False(ProjectReviewMenuService.SameBinding(snapshot, snapshot with { LaunchId = new string('b', 32) }));
        Assert.False(ProjectReviewMenuService.SameBinding(snapshot, snapshot with { Target = snapshot.Target with { BuildIdentity = "changed" } }));
        var result = ProjectReviewMenuService.Execute(reader, command =>
        {
            string id = command.Split(' ')[2];
            var report = new ReviewMenuCapture().Capture(new Source(), snapshot.LaunchId, DateTimeOffset.UtcNow);
            string path = ReviewMenuContract.ResponsePath(LiveLabPaths.Resolve(temporary.Path).RuntimePath, id);
            File.WriteAllText(path, JsonSerializer.Serialize(new ReviewMenuResponseEnvelope(1, id, report), JsonOptions));
            var store = new JsonLiveLabStateStore(LiveLabPaths.Resolve(temporary.Path).StatePath);
            store.Write(store.Read()! with { LaunchId = new string('b', 32) });
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        }, TimeSpan.Zero);
        Assert.Equal("reviewBindingChanged", result.ErrorCode);
        Assert.Equal("unavailable", ProjectReviewMenuService.Execute(reader).State);
    }

    [Theory]
    [InlineData("--json", true)]
    [InlineData("--topology network-2 --role host --json", true)]
    [InlineData("--topology network-2 --role farmhand --json", true)]
    [InlineData("--role host --json", false)]
    [InlineData("--topology network-2 --json", false)]
    [InlineData("--json --json", false)]
    [InlineData("--click 1 --json", false)]
    public void CliArgumentsAreClosedAndRoleSpecific(string args, bool expected) =>
        Assert.Equal(expected, CliApplication.TryParseReviewMenu(("project review menu " + args).Split(' '), out _, out _));

    [Fact]
    public void MissingDuplicateAndUnknownFieldsAreRejected()
    {
        var report = new ReviewMenuCapture().Capture(new Source(), Launch, DateTimeOffset.UtcNow);
        string json = JsonSerializer.Serialize(new ReviewMenuResponseEnvelope(1, Launch, report), JsonOptions);
        Assert.NotNull(ProjectReviewMenuService.DeserializeResponse(System.Text.Encoding.UTF8.GetBytes(json)));
        foreach (string invalid in new[]
        {
            json.Replace("\"role\":null,", "", StringComparison.Ordinal),
            json.Replace("\"role\":null,", "\"role\":null,\"role\":null,", StringComparison.Ordinal),
            json.Replace("\"role\":null,", "\"role\":null,\"privatePath\":\"secret\",", StringComparison.Ordinal),
        })
        {
            Assert.Throws<InvalidDataException>(() => ProjectReviewMenuService.DeserializeResponse(System.Text.Encoding.UTF8.GetBytes(invalid)));
        }
    }

    [Theory]
    [InlineData(5, 5, null)]
    [InlineData(10, 10, "menuResponseInvalid")]
    [InlineData(1, 6, "menuResponseStale")]
    public void CaptureAgeIsBoundedBothAtResponseAndAfterBinding(int responseSeconds, int returnSeconds, string? error)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        int reads = 0;
        DateTimeOffset Clock() => start.AddSeconds(++reads switch { 1 => 0, 2 => responseSeconds, _ => returnSeconds });
        ReviewMenuReport result = ProjectReviewMenuService.Execute(reader, command =>
        {
            string id = command.Split(' ')[2];
            ReviewMenuReport report = new ReviewMenuCapture().Capture(new Source(), Launch, start);
            File.WriteAllText(ReviewMenuContract.ResponsePath(LiveLabPaths.Resolve(temporary.Path).RuntimePath, id),
                JsonSerializer.Serialize(new ReviewMenuResponseEnvelope(1, id, report), JsonOptions));
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        }, TimeSpan.Zero, utcNow: Clock);
        Assert.Equal(error, result.ErrorCode);
    }

    [Fact]
    public void FullTypeAndSimpleAssemblyDistinguishSameNamedModMenusWithoutPaths()
    {
        var source = new Source();
        object child = source.Add("ModB.ConfigMenu", "publicBase");
        source.Nodes[child] = source.Nodes[child] with { Assembly = "ModB" };
        source.Root = source.Add("ModA.ConfigMenu", "publicBase", children: [new(child, "child")]);
        source.Nodes[source.Root] = source.Nodes[source.Root] with { Assembly = "ModA" };
        var capture = new ReviewMenuCapture();
        ReviewMenuReport result = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.Equal(["ModA.ConfigMenu", "ModB.ConfigMenu"], result.Menus.Select(n => n.Type));
        Assert.Equal(["ModA", "ModB"], result.Menus.Select(n => n.Assembly));
        source.Nodes[source.Root] = source.Nodes[source.Root] with { Assembly = "C:/private/path.dll" };
        result = capture.Capture(source, Launch, DateTimeOffset.UtcNow);
        Assert.Equal("UnknownAssembly", result.Menus[0].Assembly);
        Assert.Contains("typeIdentifierWithheld", result.Limitations);
    }

    [Fact]
    public async Task NativeToolReturnsSameTypedReportAndRejectsArguments()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(temporary);
        ReviewMenuReport report = new ReviewMenuCapture().Capture(new Source(), Launch, DateTimeOffset.UtcNow);
        McpServerTool tool = ProjectReviewMcpMenuTools.Create(reader, _ => report);
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.NotNull(tool.ProtocolTool.OutputSchema);
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "menu-test", Version = "1" }, ToolCollection = [tool] };
        await using McpTestClient harness = await McpTestClient.StartAsync(options);
        CallToolResult result = await harness.Client.CallToolAsync(ProjectReviewMcpMenuTools.ToolName,
            new Dictionary<string, object?>(), cancellationToken: harness.Token);
        Assert.False(result.IsError);
        Assert.Equal("ready", Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("state").GetString());
        result = await harness.Client.CallToolAsync(ProjectReviewMcpMenuTools.ToolName,
            new Dictionary<string, object?> { ["click"] = 1 }, cancellationToken: harness.Token);
        Assert.True(result.IsError);
    }

    private static MenuComponentObservation Component(object instance) => new(instance, "publicComponent", 7, Bounds, true, false);
    private sealed class Source : IReviewMenuSource
    {
        public object? Root { get; set; }
        public ReviewMenuRectangle Viewport { get; set; } = Bounds;
        public Dictionary<object, MenuObservation> Nodes { get; } = new();
        public MenuObservation Read(object menu) => Nodes[menu];
        public object Add(string type, string adapter, IReadOnlyList<MenuComponentObservation>? components = null,
            IReadOnlyList<MenuChildObservation>? children = null)
        {
            object instance = new();
            Nodes.Add(instance, new(type, adapter, adapter != "publicBase", Bounds, null, null,
                components ?? [], children ?? []));
            return instance;
        }
    }
}
