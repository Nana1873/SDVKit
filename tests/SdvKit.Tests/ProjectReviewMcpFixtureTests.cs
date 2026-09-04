using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ProjectReviewMcpFixtureTests
{
    private static readonly string[] FixturePayloadNames =
        ["status", "navigation", "building", "animal", "save"];

    [Fact]
    public async Task DefaultCatalogOmitsFixtureToolsAndOptInCatalogUsesClosedSchemas()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(
            temporary,
            withTestSave: true);
        await using (ClientHarness defaults = await ClientHarness.StartAsync(reader))
        {
            ListToolsResult listed = await defaults.Client.ListToolsAsync(
                new ListToolsRequestParams(),
                defaults.Token);
            Assert.DoesNotContain(
                listed.Tools,
                tool => tool.Name.StartsWith("stardew_fixture_", StringComparison.Ordinal));
        }

        ProjectReviewMcpRuntimeSnapshot expected = Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(
            reader.Read().Snapshot);
        await using ClientHarness optedIn = await ClientHarness.StartAsync(
            reader,
            (query, _, _) => Ready(query, expected));
        ListToolsResult enabled = await optedIn.Client.ListToolsAsync(
            new ListToolsRequestParams(),
            optedIn.Token);
        Tool[] fixtureTools = enabled.Tools
            .Where(tool => tool.Name.StartsWith("stardew_fixture_", StringComparison.Ordinal))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                ProjectReviewMcpFixtureTools.AnimalToolName,
                ProjectReviewMcpFixtureTools.BuildingToolName,
                ProjectReviewMcpFixtureTools.EnterToolName,
                ProjectReviewMcpFixtureTools.FarmToolName,
                ProjectReviewMcpFixtureTools.SaveToolName,
                ProjectReviewMcpFixtureTools.StatusToolName,
            ],
            fixtureTools.Select(tool => tool.Name).ToArray());
        foreach (Tool tool in fixtureTools)
        {
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            JsonElement output = Assert.IsType<JsonElement>(tool.OutputSchema);
            Assert.Equal("object", output.GetProperty("type").GetString());
            Assert.False(output.GetProperty("additionalProperties").GetBoolean());
            Assert.False(tool.Annotations?.OpenWorldHint);
            Assert.Equal(
                tool.Name == ProjectReviewMcpFixtureTools.StatusToolName,
                tool.Annotations?.ReadOnlyHint);
        }

        Assert.True(Find(ProjectReviewMcpFixtureTools.StatusToolName).Annotations?.IdempotentHint);
        Assert.True(Find(ProjectReviewMcpFixtureTools.EnterToolName).Annotations?.IdempotentHint);
        Assert.True(Find(ProjectReviewMcpFixtureTools.FarmToolName).Annotations?.IdempotentHint);
        Assert.True(Find(ProjectReviewMcpFixtureTools.BuildingToolName).Annotations?.IdempotentHint);
        Assert.True(Find(ProjectReviewMcpFixtureTools.AnimalToolName).Annotations?.IdempotentHint);
        Assert.False(Find(ProjectReviewMcpFixtureTools.SaveToolName).Annotations?.IdempotentHint);
        Assert.True(Find(ProjectReviewMcpFixtureTools.BuildingToolName).Annotations?.DestructiveHint);
        Assert.All(
            fixtureTools.Where(tool => tool.Name != ProjectReviewMcpFixtureTools.BuildingToolName),
            tool => Assert.False(tool.Annotations?.DestructiveHint));

        AssertOutput(ProjectReviewMcpFixtureTools.StatusToolName, "status", "status");
        AssertOutput(ProjectReviewMcpFixtureTools.EnterToolName, "enter", "navigation");
        AssertOutput(ProjectReviewMcpFixtureTools.FarmToolName, "farm", "navigation");
        AssertOutput(ProjectReviewMcpFixtureTools.BuildingToolName, "buildingEnsure", "building");
        AssertOutput(ProjectReviewMcpFixtureTools.AnimalToolName, "animalEnsure", "animal");
        AssertOutput(ProjectReviewMcpFixtureTools.SaveToolName, "save", "save");

        Tool building = Assert.Single(
            fixtureTools,
            tool => tool.Name == ProjectReviewMcpFixtureTools.BuildingToolName);
        Assert.Equal(
            ["alias", "kind", "x", "y"],
            building.InputSchema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            "^[a-z][a-z0-9_-]{0,31}$",
            building.InputSchema.GetProperty("properties")
                .GetProperty("alias")
                .GetProperty("pattern")
                .GetString());

        Tool Find(string name) => Assert.Single(fixtureTools, tool => tool.Name == name);

        void AssertOutput(string name, string operation, string payload)
        {
            JsonElement schema = Assert.IsType<JsonElement>(Find(name).OutputSchema);
            JsonElement properties = schema.GetProperty("properties");
            Assert.Equal(
                operation,
                properties.GetProperty("operation").GetProperty("const").GetString());
            Assert.True(properties.TryGetProperty(payload, out _));
            Assert.All(
                FixturePayloadNames
                    .Where(candidate => candidate != payload),
                candidate => Assert.False(properties.TryGetProperty(candidate, out _)));
            JsonElement[] branches = schema.GetProperty("oneOf")
                .EnumerateArray()
                .ToArray();
            JsonElement ready = Assert.Single(branches, branch =>
                branch.GetProperty("properties")
                    .GetProperty("state")
                    .GetProperty("const")
                    .GetString() == "ready");
            JsonElement blocked = Assert.Single(branches, branch =>
                branch.GetProperty("properties")
                    .GetProperty("state")
                    .GetProperty("const")
                    .GetString() == "blocked");
            Assert.Contains(
                payload,
                ready.GetProperty("required")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
            Assert.Contains(
                payload,
                blocked.GetProperty("not")
                    .GetProperty("required")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }
    }

    [Fact]
    public async Task OptedInToolsReturnTypedStatusNavigationBuildingAnimalAndSaveEvidence()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(
            temporary,
            withTestSave: true);
        ProjectReviewMcpRuntimeSnapshot expected = Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(
            reader.Read().Snapshot);
        var queries = new List<ReviewFixtureQuery>();
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            (query, _, _) =>
            {
                queries.Add(query);
                return Ready(query, expected);
            });

        JsonElement status = await Call(harness, ProjectReviewMcpFixtureTools.StatusToolName);
        JsonElement enter = await Call(
            harness,
            ProjectReviewMcpFixtureTools.EnterToolName,
            ("building", "barn-a"));
        JsonElement farm = await Call(harness, ProjectReviewMcpFixtureTools.FarmToolName);
        JsonElement building = await Call(
            harness,
            ProjectReviewMcpFixtureTools.BuildingToolName,
            ("alias", "barn-a"),
            ("kind", "Deluxe Barn"),
            ("x", 16),
            ("y", 20));
        JsonElement animal = await Call(
            harness,
            ProjectReviewMcpFixtureTools.AnimalToolName,
            ("building", "barn-a"),
            ("kind", "White Cow"));
        JsonElement save = await Call(harness, ProjectReviewMcpFixtureTools.SaveToolName);

        Assert.Equal("Farm", status.GetProperty("status").GetProperty("locationId").GetString());
        Assert.Equal("Barn-111", enter.GetProperty("navigation").GetProperty("locationId").GetString());
        Assert.Equal("Farm", farm.GetProperty("navigation").GetProperty("locationId").GetString());
        Assert.Equal("barn-a", building.GetProperty("building").GetProperty("alias").GetString());
        Assert.Equal(111, animal.GetProperty("animal").GetProperty("animalId").GetInt64());
        Assert.Equal(expected.TestSave!.SaveId, save.GetProperty("save").GetProperty("saveId").GetString());
        Assert.All(
            new[] { status, enter, farm, building, animal, save },
            result =>
            {
                Assert.Equal(expected.LaunchId, result.GetProperty("launchId").GetString());
                Assert.Equal(expected.TestSave.FixtureId, result.GetProperty("fixtureId").GetString());
                Assert.Equal(0, result.GetProperty("problems").GetArrayLength());
            });
        Assert.Equal(6, queries.Count);
    }

    [Fact]
    public async Task ToolPreflightRejectsMissingOrChangedTestSaveBeforeDispatch()
    {
        using TemporaryDirectory withoutFixture = new();
        ProjectReviewMcpRuntimeReader normalSaveReader =
            ProjectReviewMcpTests.CreateReadyReview(withoutFixture);
        var dispatches = 0;
        await using (ClientHarness normalSave = await ClientHarness.StartAsync(
            normalSaveReader,
            (_, _, _) =>
            {
                dispatches++;
                throw new InvalidOperationException("Must not dispatch.");
            }))
        {
            CallToolResult result = await normalSave.Client.CallToolAsync(
                ProjectReviewMcpFixtureTools.StatusToolName,
                new Dictionary<string, object?>(),
                cancellationToken: normalSave.Token);
            Assert.True(result.IsError);
            Assert.Contains("fixture", Text(result), StringComparison.OrdinalIgnoreCase);
        }

        using TemporaryDirectory changedFixture = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(
            changedFixture,
            withTestSave: true);
        await using (ClientHarness changed = await ClientHarness.StartAsync(
            reader,
            (_, _, _) =>
            {
                dispatches++;
                throw new InvalidOperationException("Must not dispatch.");
            }))
        {
            LiveLabPaths paths = LiveLabPaths.Resolve(changedFixture.Path);
            LiveLabState state = Assert.IsType<LiveLabState>(
                new JsonLiveLabStateStore(paths.StatePath).Read());
            new JsonLiveLabStateStore(paths.StatePath).Write(state with { TestSave = null });

            CallToolResult result = await changed.Client.CallToolAsync(
                ProjectReviewMcpFixtureTools.BuildingToolName,
                new Dictionary<string, object?>
                {
                    ["alias"] = "barn-a",
                    ["kind"] = "Deluxe Barn",
                    ["x"] = 16,
                    ["y"] = 20,
                },
                cancellationToken: changed.Token);
            Assert.True(result.IsError);
            Assert.Contains("unavailable", Text(result), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0, dispatches);
    }

    [Fact]
    public async Task FarmhandCatalogCannotDiscoverOrDispatchFixtureMutations()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyNetworkReview(
            temporary,
            NetworkTwoContract.FarmhandRole);
        ProjectReviewMcpRuntimeSnapshot expected = Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(
            reader.Read().Snapshot);
        var queries = new List<ReviewFixtureQuery>();
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            (query, _, _) =>
            {
                queries.Add(query);
                return Ready(query, expected);
            },
            NetworkTwoContract.Topology,
            NetworkTwoContract.FarmhandRole);

        ListToolsResult listed = await harness.Client.ListToolsAsync(
            new ListToolsRequestParams(),
            harness.Token);
        string[] fixtures = listed.Tools
            .Select(tool => tool.Name)
            .Where(name => name.StartsWith("stardew_fixture_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                ProjectReviewMcpFixtureTools.EnterToolName,
                ProjectReviewMcpFixtureTools.FarmToolName,
                ProjectReviewMcpFixtureTools.StatusToolName,
            ],
            fixtures);

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Client.CallToolAsync(
            ProjectReviewMcpFixtureTools.SaveToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token).AsTask());
        Assert.Empty(queries);
    }

    [Fact]
    public async Task BuildingAndAnimalEnsureRemainDeterministicAcrossIdempotentCalls()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(
            temporary,
            withTestSave: true);
        ProjectReviewMcpRuntimeSnapshot expected = Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(
            reader.Read().Snapshot);
        var queries = new List<ReviewFixtureQuery>();
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            (query, _, _) =>
            {
                queries.Add(query);
                return Ready(query, expected);
            });

        string firstBuilding = (await Call(
            harness,
            ProjectReviewMcpFixtureTools.BuildingToolName,
            ("alias", "barn-a"),
            ("kind", "Deluxe Barn"),
            ("x", 16),
            ("y", 20))).GetRawText();
        string secondBuilding = (await Call(
            harness,
            ProjectReviewMcpFixtureTools.BuildingToolName,
            ("alias", "barn-a"),
            ("kind", "Deluxe Barn"),
            ("x", 16),
            ("y", 20))).GetRawText();
        string firstAnimal = (await Call(
            harness,
            ProjectReviewMcpFixtureTools.AnimalToolName,
            ("building", "barn-a"),
            ("kind", "White Cow"))).GetRawText();
        string secondAnimal = (await Call(
            harness,
            ProjectReviewMcpFixtureTools.AnimalToolName,
            ("building", "barn-a"),
            ("kind", "White Cow"))).GetRawText();

        Assert.Equal(firstBuilding, secondBuilding);
        Assert.Equal(firstAnimal, secondAnimal);
        Assert.Equal(queries[0], queries[1]);
        Assert.Equal(queries[2], queries[3]);
        Assert.All(
            new[] { firstBuilding, firstAnimal },
            json => Assert.DoesNotContain(
                JsonDocument.Parse(json).RootElement.EnumerateObject(),
                property => property.Name == "path"));
    }

    [Fact]
    public async Task AmbiguousPostDispatchFailureIsMachineReadableAndNotRetried()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = ProjectReviewMcpTests.CreateReadyReview(
            temporary,
            withTestSave: true);
        ProjectReviewMcpRuntimeSnapshot expected = Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(
            reader.Read().Snapshot);
        var calls = 0;
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            (query, _, _) =>
            {
                calls++;
                return new LiveLabCommandResult(
                    3,
                    new ReviewFixtureReport(
                        ReviewFixtureTransportContract.SchemaVersion,
                        "blocked",
                        query.Operation,
                        expected.LaunchId,
                        expected.Topology,
                        expected.Role,
                        DateTimeOffset.UtcNow,
                        expected.TestSave!.FixtureId,
                        expected.TestSave.SaveId,
                        "The acknowledgement did not arrive within the operation bound.",
                        [new ReviewFixtureProblem(
                            "fixtureResponseTimedOut",
                            "The action may have run and was not retried.")],
                        CommandWritten: true,
                        MayHaveRun: true,
                        CancellationRequested: true));
            });

        CallToolResult result = await harness.Client.CallToolAsync(
            ProjectReviewMcpFixtureTools.StatusToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token);

        Assert.True(result.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.True(structured.GetProperty("commandWritten").GetBoolean());
        Assert.True(structured.GetProperty("mayHaveRun").GetBoolean());
        Assert.True(structured.GetProperty("cancellationRequested").GetBoolean());
        Assert.Equal("fixtureResponseTimedOut", structured
            .GetProperty("problems")[0]
            .GetProperty("code")
            .GetString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ServiceRejectsStaleResponseAndCancellationAndActionLockRejectsParallelWork()
    {
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        var expected = new ProjectReviewMcpRuntimeSnapshot(
            1,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            LiveLabState.SingleTopology,
            null,
            requestedAt,
            new ProjectReviewMcpTarget(
                "Nana.Target",
                "1.0.0",
                "sha256:" + new string('9', 64)),
            new ProjectReviewMcpTestSave(
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "SDVKit_123"),
            new ProjectReviewMcpRuntime(1, true, "spring", 1, 1, 600, "Farm", 1, 2, false),
            StatusTick: 600,
            StatusObservedAtUtc: requestedAt,
            ForegroundWindowHandle: 1,
            ForegroundProcessId: Environment.ProcessId);
        ReviewFixtureQuery query = new(ReviewFixtureTransportContract.StatusOperation);
        ReviewFixtureResponseEnvelope stale = Envelope(
            query,
            expected,
            requestedAt.AddSeconds(-1));
        ReviewFixtureResponseEnvelope fresh = Envelope(
            query,
            expected,
            requestedAt.AddMilliseconds(1));

        Assert.True(ProjectReviewFixtureService.HasSameActionBinding(expected, expected));
        Assert.False(ProjectReviewFixtureService.HasSameActionBinding(
            expected with { LaunchId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            expected));
        Assert.False(ProjectReviewFixtureService.HasSameActionBinding(
            expected with
            {
                Target = expected.Target with
                {
                    BuildIdentity = "sha256:" + new string('8', 64),
                },
            },
            expected));
        Assert.False(ProjectReviewFixtureService.HasSameActionBinding(
            expected with
            {
                TestSave = expected.TestSave! with { SaveId = "SDVKit_other" },
            },
            expected));

        Assert.False(ProjectReviewFixtureService.Matches(
            stale,
            stale.RequestId,
            stale.Binding,
            query,
            expected,
            requestedAt));
        Assert.True(ProjectReviewFixtureService.Matches(
            fresh,
            fresh.RequestId,
            fresh.Binding,
            query,
            expected,
            requestedAt));
        ReviewFixtureResponseEnvelope bindingChanged = fresh with
        {
            Report = fresh.Report with
            {
                State = "blocked",
                LaunchId = "cccccccccccccccccccccccccccccccc",
                FixtureId = null,
                SaveId = null,
                Message = "The live review fixture identity changed after preflight.",
                Problems = [new ReviewFixtureProblem(
                    "fixtureBindingChanged",
                    "No fixture action was run.")],
                Status = null,
            },
        };
        Assert.True(ProjectReviewFixtureService.Matches(
            bindingChanged,
            bindingChanged.RequestId,
            bindingChanged.Binding,
            query,
            expected,
            requestedAt));
        Assert.False(ProjectReviewFixtureService.Matches(
            bindingChanged with
            {
                Binding = bindingChanged.Binding with { SaveId = "SDVKit_other" },
            },
            bindingChanged.RequestId,
            bindingChanged.Binding,
            query,
            expected,
            requestedAt));

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        using ProjectReviewActionLock first = Assert.IsType<ProjectReviewActionLock>(
            ProjectReviewActionLock.TryAcquire(paths.RuntimePath));
        Assert.Null(ProjectReviewActionLock.TryAcquire(paths.RuntimePath));
        string sentinel = Path.Combine(paths.RuntimePath, "foreign-state.txt");
        File.WriteAllText(sentinel, "untouched");
        Assert.Equal("untouched", File.ReadAllText(sentinel));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ProjectReviewFixtureService.Execute(
                query,
                LiveLabState.SingleTopology,
                role: null,
                temporary.Path,
                cancellationToken: cancellation.Token));

        Assert.Equal(
            TimeSpan.FromSeconds(15),
            ProjectReviewFixtureService.ResponseTimeoutFor(
                ReviewFixtureTransportContract.StatusOperation));
        Assert.Equal(
            TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5),
            ProjectReviewFixtureService.ResponseTimeoutFor(
                ReviewFixtureTransportContract.SaveOperation));
    }

    private static ReviewFixtureResponseEnvelope Envelope(
        ReviewFixtureQuery query,
        ProjectReviewMcpRuntimeSnapshot expected,
        DateTimeOffset completedAt)
    {
        string requestId = Guid.NewGuid().ToString("N");
        ReviewFixtureReport report = Assert.IsType<ReviewFixtureReport>(
            Ready(query, expected, completedAt).Report);
        var binding = new ReviewFixtureRequestBinding(
            expected.LaunchId,
            expected.Topology,
            expected.Role,
            expected.TestSave!.FixtureId,
            expected.TestSave.SaveId);
        return new ReviewFixtureResponseEnvelope(
            ReviewFixtureTransportContract.SchemaVersion,
            requestId,
            binding,
            report);
    }

    private static LiveLabCommandResult Ready(
        ReviewFixtureQuery query,
        ProjectReviewMcpRuntimeSnapshot expected,
        DateTimeOffset? completedAt = null)
    {
        var building = new ReviewFixtureBuildingReport(
            "barn-a",
            "11111111-1111-1111-1111-111111111111",
            "Deluxe Barn",
            "deluxe-barn",
            16,
            20,
            "Barn-111",
            "Maps/Barn",
            0,
            query.Operation == ReviewFixtureTransportContract.AnimalEnsureOperation ? 1 : 0,
            Changed: false);
        var report = new ReviewFixtureReport(
            ReviewFixtureTransportContract.SchemaVersion,
            "ready",
            query.Operation,
            expected.LaunchId,
            expected.Topology,
            expected.Role,
            completedAt ?? expected.ObservedAtUtc,
            expected.TestSave!.FixtureId,
            expected.TestSave.SaveId,
            "Exact fixture action completed.",
            [],
            Status: query.Operation == ReviewFixtureTransportContract.StatusOperation
                ? new ReviewFixtureStatusReport("Farm", 101, true, false, [])
                : null,
            Navigation: query.Operation is ReviewFixtureTransportContract.EnterOperation
                or ReviewFixtureTransportContract.FarmOperation
                    ? new ReviewFixtureNavigationReport(
                        query.Operation == ReviewFixtureTransportContract.FarmOperation
                            ? "Farm"
                            : "Barn-111",
                        4,
                        5,
                        Changed: false)
                    : null,
            Building: query.Operation == ReviewFixtureTransportContract.BuildingEnsureOperation
                ? building with
                {
                    Alias = query.Alias!,
                    CanonicalToken = StableIdentityNormalizer.Normalize(query.Kind!),
                    X = query.X!.Value,
                    Y = query.Y!.Value,
                }
                : null,
            Animal: query.Operation == ReviewFixtureTransportContract.AnimalEnsureOperation
                ? new ReviewFixtureAnimalReport(
                    111,
                    "White Cow",
                    StableIdentityNormalizer.Normalize(query.Kind!),
                    building.BuildingId,
                    Assigned: true,
                    Changed: false)
                : null,
            Save: query.Operation == ReviewFixtureTransportContract.SaveOperation
                ? new ReviewFixtureSaveReport(
                    expected.TestSave.SaveId,
                    completedAt ?? expected.ObservedAtUtc)
                : null,
            CommandWritten: true);
        return new LiveLabCommandResult(0, report);
    }

    private static async Task<JsonElement> Call(
        ClientHarness harness,
        string tool,
        params (string Name, object? Value)[] values)
    {
        CallToolResult result = await harness.Client.CallToolAsync(
            tool,
            values.ToDictionary(value => value.Name, value => value.Value),
            cancellationToken: harness.Token);
        Assert.NotEqual(true, result.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
        using JsonDocument text = JsonDocument.Parse(Text(result));
        Assert.True(JsonElement.DeepEquals(structured, text.RootElement));
        return structured;
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class ClientHarness : IAsyncDisposable
    {
        private readonly Pipe _clientToServer;
        private readonly StreamServerTransport _transport;
        private readonly McpServer _server;
        private readonly CancellationTokenSource _timeout;
        private readonly Task _serverTask;

        private ClientHarness(
            Pipe clientToServer,
            StreamServerTransport transport,
            McpServer server,
            CancellationTokenSource timeout,
            Task serverTask,
            McpClient client)
        {
            _clientToServer = clientToServer;
            _transport = transport;
            _server = server;
            _timeout = timeout;
            _serverTask = serverTask;
            Client = client;
        }

        public McpClient Client { get; }

        public CancellationToken Token => _timeout.Token;

        public static async Task<ClientHarness> StartAsync(
            ProjectReviewMcpRuntimeReader reader,
            ProjectReviewMcpFixtureQueryRunner? runFixture = null,
            string topology = LiveLabState.SingleTopology,
            string? role = null)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                "sdvkit-fixture-test");
            McpServer server = McpServer.Create(
                transport,
                ProjectReviewMcpServer.CreateOptions(
                    reader,
                    runData: null,
                    runFixture: runFixture,
                    topology: topology,
                    role: role));
            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task serverTask = server.RunAsync(timeout.Token);
            var clientTransport = new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream());
            McpClient client = await McpClient.CreateAsync(
                clientTransport,
                cancellationToken: timeout.Token);
            return new ClientHarness(
                clientToServer,
                transport,
                server,
                timeout,
                serverTask,
                client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _clientToServer.Writer.CompleteAsync();
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            await _server.DisposeAsync();
            await _transport.DisposeAsync();
            _timeout.Dispose();
        }
    }
}
