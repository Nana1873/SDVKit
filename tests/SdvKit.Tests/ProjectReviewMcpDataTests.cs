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
public sealed class ProjectReviewMcpDataTests
{
    [Fact]
    public async Task OfficialClientListsAndCallsTheThreeCanonicalDataTools()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var queries = new List<ReviewDataQuery>();
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            query =>
            {
                queries.Add(query);
                return Ready(query);
            });

        ListToolsResult listed = await harness.Client.ListToolsAsync(
            new ListToolsRequestParams(),
            harness.Token);

        Assert.Equal(
            [
                ProjectReviewMcpDataTools.AssetsToolName,
                ProjectReviewMcpDataTools.KeysToolName,
                ProjectReviewMcpDataTools.RecordToolName,
                ProjectReviewMcpDiagnosticsTools.ModsToolName,
                ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                ProjectReviewMcpServer.RuntimeToolName,
                ProjectReviewMcpScreenshotTools.CaptureToolName,
            ],
            listed.Tools.Select(tool => tool.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        foreach (Tool tool in listed.Tools.Where(tool =>
                     tool.Name is ProjectReviewMcpDataTools.AssetsToolName
                         or ProjectReviewMcpDataTools.KeysToolName
                         or ProjectReviewMcpDataTools.RecordToolName))
        {
            Assert.True(tool.Annotations?.ReadOnlyHint);
            Assert.False(tool.Annotations?.DestructiveHint);
            Assert.True(tool.Annotations?.IdempotentHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            if (tool.InputSchema.GetProperty("properties")
                .TryGetProperty("offset", out JsonElement offsetSchema))
            {
                Assert.Equal(int.MaxValue, offsetSchema.GetProperty("maximum").GetInt32());
            }
            JsonElement outputSchema = Assert.IsType<JsonElement>(tool.OutputSchema);
            Assert.Equal("object", outputSchema.GetProperty("type").GetString());
            Assert.False(outputSchema.GetProperty("additionalProperties").GetBoolean());
        }

        CallToolResult assets = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.AssetsToolName,
            new Dictionary<string, object?>
            {
                ["offset"] = 2,
                ["limit"] = 3,
            },
            cancellationToken: harness.Token);
        JsonElement assetsJson = AssertSuccessfulJson(assets);
        Assert.Equal(3, assetsJson.GetProperty("page").GetProperty("limit").GetInt32());
        Assert.True(assetsJson.GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.Equal("Data/Buildings", assetsJson
            .GetProperty("assets")[0].GetProperty("assetName").GetString());

        CallToolResult keys = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.KeysToolName,
            new Dictionary<string, object?>
            {
                ["asset"] = "Data/Buildings",
                ["offset"] = 1,
                ["limit"] = 2,
            },
            cancellationToken: harness.Token);
        JsonElement keysJson = AssertSuccessfulJson(keys);
        Assert.Equal("Data/Buildings", keysJson.GetProperty("assetName").GetString());
        Assert.Collection(
            keysJson.GetProperty("keys").EnumerateArray().ToArray(),
            value => Assert.Equal("Barn", value.GetString()),
            value => Assert.Equal("Coop", value.GetString()));

        foreach ((string asset, string key, string shape) in new[]
                 {
                     ("Data/Dictionary", "Barn", "dictionary"),
                     ("Data/List", "0", "list"),
                     ("Data/Singleton", ReviewDataContract.SingletonKey, "singleton"),
                 })
        {
            CallToolResult record = await harness.Client.CallToolAsync(
                ProjectReviewMcpDataTools.RecordToolName,
                new Dictionary<string, object?>
                {
                    ["asset"] = asset,
                    ["key"] = key,
                },
                cancellationToken: harness.Token);
            JsonElement recordJson = AssertSuccessfulJson(record);
            Assert.Equal(asset, recordJson.GetProperty("assetName").GetString());
            Assert.Equal(shape, recordJson.GetProperty("shape").GetString());
            Assert.Equal(key, recordJson.GetProperty("key").GetString());
            Assert.Equal(key, recordJson.GetProperty("record").GetProperty("id").GetString());
        }

        Assert.Equal(
            [
                new ReviewDataQuery(ReviewDataContract.AssetsOperation, null, null, 2, 3),
                new ReviewDataQuery(ReviewDataContract.KeysOperation, "Data/Buildings", null, 1, 2),
                new ReviewDataQuery(ReviewDataContract.GetOperation, "Data/Dictionary", "Barn", 0, 1),
                new ReviewDataQuery(ReviewDataContract.GetOperation, "Data/List", "0", 0, 1),
                new ReviewDataQuery(
                    ReviewDataContract.GetOperation,
                    "Data/Singleton",
                    ReviewDataContract.SingletonKey,
                    0,
                    1),
            ],
            queries);
    }

    [Fact]
    public async Task DataToolsApplyDefaultsAndReturnDeterministicStructuredAndTextJson()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var queries = new List<ReviewDataQuery>();
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            query =>
            {
                queries.Add(query);
                return Ready(query);
            });

        CallToolResult firstAssets = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.AssetsToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token);
        CallToolResult firstRecord = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.RecordToolName,
            new Dictionary<string, object?>
            {
                ["asset"] = "Data/Dictionary",
                ["key"] = "Barn",
            },
            cancellationToken: harness.Token);
        CallToolResult secondRecord = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.RecordToolName,
            new Dictionary<string, object?>
            {
                ["asset"] = "Data/Dictionary",
                ["key"] = "Barn",
            },
            cancellationToken: harness.Token);

        AssertSuccessfulJson(firstAssets);
        string firstJson = AssertSuccessfulJson(firstRecord).GetRawText();
        string secondJson = AssertSuccessfulJson(secondRecord).GetRawText();
        Assert.Equal(firstJson, secondJson);
        Assert.Equal(
            new ReviewDataQuery(
                ReviewDataContract.AssetsOperation,
                null,
                null,
                0,
                ReviewDataContract.DefaultPageLimit),
            queries[0]);
    }

    [Fact]
    public async Task RecordToolAcceptsTheCanonicalServiceNormalization()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            query =>
            {
                ReviewDataReport report = Assert.IsType<ReviewDataReport>(Ready(query).Report);
                return new LiveLabCommandResult(
                    0,
                    report with
                    {
                        AssetName = "Data/Dictionary",
                        Key = "Barn",
                    });
            });

        CallToolResult result = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.RecordToolName,
            new Dictionary<string, object?>
            {
                ["asset"] = "data_dictionary",
                ["key"] = "barn",
            },
            cancellationToken: harness.Token);

        JsonElement structured = AssertSuccessfulJson(result);
        Assert.Equal("Data/Dictionary", structured.GetProperty("assetName").GetString());
        Assert.Equal("Barn", structured.GetProperty("key").GetString());
    }

    [Fact]
    public async Task AssetsToolAcceptsAnEmptyPageBeyondTheInventoryEnd()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            query =>
            {
                ReviewDataReport report = Assert.IsType<ReviewDataReport>(Ready(query).Report);
                return new LiveLabCommandResult(
                    0,
                    report with
                    {
                        Assets = [],
                        Page = new ReviewDataPage(
                            query.Offset,
                            query.Limit,
                            Returned: 0,
                            Total: 125,
                            NextOffset: null),
                    });
            });

        CallToolResult result = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.AssetsToolName,
            new Dictionary<string, object?>
            {
                ["offset"] = 1000,
                ["limit"] = 50,
            },
            cancellationToken: harness.Token);

        JsonElement structured = AssertSuccessfulJson(result);
        Assert.Empty(structured.GetProperty("assets").EnumerateArray());
        Assert.Equal(125, structured.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, structured
            .GetProperty("page").GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task DataToolsRejectInvalidArgumentsBeforeReviewDispatch()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var dispatchCount = 0;
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            query =>
            {
                dispatchCount++;
                return Ready(query);
            });
        (string Tool, IReadOnlyDictionary<string, object?> Arguments)[] cases =
        [
            (ProjectReviewMcpDataTools.AssetsToolName, Args(("extra", true))),
            (ProjectReviewMcpDataTools.AssetsToolName, Args(("offset", -1))),
            (ProjectReviewMcpDataTools.AssetsToolName, Args(("offset", (long)int.MaxValue + 1))),
            (ProjectReviewMcpDataTools.AssetsToolName, Args(("limit", 0))),
            (ProjectReviewMcpDataTools.AssetsToolName, Args(("limit", 101))),
            (ProjectReviewMcpDataTools.AssetsToolName, Args(("limit", "2"))),
            (ProjectReviewMcpDataTools.KeysToolName, Args()),
            (ProjectReviewMcpDataTools.KeysToolName, Args(("asset", string.Empty))),
            (ProjectReviewMcpDataTools.KeysToolName, Args(("asset", "Data/\u0001"))),
            (ProjectReviewMcpDataTools.KeysToolName, Args(("asset", new string('a', 257)))),
            (ProjectReviewMcpDataTools.RecordToolName, Args(("asset", "Data/Buildings"))),
            (ProjectReviewMcpDataTools.RecordToolName, Args(("asset", true), ("key", "Barn"))),
            (ProjectReviewMcpDataTools.RecordToolName, Args(("asset", "Data/Buildings"), ("key", string.Empty))),
            (ProjectReviewMcpDataTools.RecordToolName, Args(("asset", "Data/Buildings"), ("key", "x\u0001"))),
            (ProjectReviewMcpDataTools.RecordToolName, Args(("asset", "Data/Buildings"), ("key", new string('k', 2049)))),
            (ProjectReviewMcpDataTools.RecordToolName, Args(("asset", "Data/Buildings"), ("key", "Barn"), ("offset", 0))),
        ];

        foreach ((string tool, IReadOnlyDictionary<string, object?> arguments) in cases)
        {
            CallToolResult result = await harness.Client.CallToolAsync(
                tool,
                arguments,
                cancellationToken: harness.Token);
            Assert.True(result.IsError);
            Assert.StartsWith(
                "Invalid arguments for stardew_data_",
                Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text,
                StringComparison.Ordinal);
        }

        Assert.Equal(0, dispatchCount);
    }

    [Fact]
    public async Task DataToolsRevalidateTheReviewBeforeEveryDispatch()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        var dispatchCount = 0;
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            query =>
            {
                dispatchCount++;
                return Ready(query);
            });

        CallToolResult ready = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.AssetsToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token);
        Assert.NotEqual(true, ready.IsError);
        File.Delete(LiveLabPaths.Resolve(temporary.Path).StatusPath);

        CallToolResult invalidated = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.AssetsToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token);

        Assert.True(invalidated.IsError);
        Assert.Contains(
            "[reviewRuntimeNotReady]",
            Assert.IsType<TextContentBlock>(Assert.Single(invalidated.Content)).Text,
            StringComparison.Ordinal);
        Assert.Equal(1, dispatchCount);
    }

    [Fact]
    public async Task DataServiceFailuresExposeOnlyABoundedCode()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        string secret = $"secret-at-{temporary.Path}";
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            query => new LiveLabCommandResult(
                3,
                Failure(query.Operation, "dataKeyUnknown", secret)));

        CallToolResult result = await harness.Client.CallToolAsync(
            ProjectReviewMcpDataTools.RecordToolName,
            new Dictionary<string, object?>
            {
                ["asset"] = "Data/Buildings",
                ["key"] = "Missing",
            },
            cancellationToken: harness.Token);

        Assert.True(result.IsError);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal("SDVKit review data unavailable [dataKeyUnknown].", message);
        Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(temporary.Path, message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("assetsPage")]
    [InlineData("assetsImpossiblePage")]
    [InlineData("keysAsset")]
    [InlineData("keysPage")]
    [InlineData("recordAsset")]
    [InlineData("recordKey")]
    public async Task DataToolsFailClosedForMismatchedSuccessfulReports(
        string mismatch)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader =
            ProjectReviewMcpTests.CreateReadyReview(temporary);
        await using ClientHarness harness = await ClientHarness.StartAsync(
            reader,
            query =>
            {
                ReviewDataReport report = Assert.IsType<ReviewDataReport>(Ready(query).Report);
                ReviewDataReport mismatched = mismatch switch
                {
                    "assetsPage" => report with
                    {
                        Page = report.Page! with { Offset = report.Page.Offset + 1 },
                    },
                    "assetsImpossiblePage" => report with
                    {
                        Page = report.Page! with { Total = 0, NextOffset = null },
                    },
                    "keysAsset" => report with { AssetName = "Data/Characters" },
                    "keysPage" => report with
                    {
                        Page = report.Page! with { Limit = report.Page.Limit + 1 },
                    },
                    "recordAsset" => report with { AssetName = "Data/Characters" },
                    "recordKey" => report with { Key = "Coop" },
                    _ => throw new InvalidOperationException("Unknown mismatch case."),
                };
                return new LiveLabCommandResult(0, mismatched);
            });

        string tool = mismatch switch
        {
            "assetsPage" or "assetsImpossiblePage" =>
                ProjectReviewMcpDataTools.AssetsToolName,
            "keysAsset" or "keysPage" => ProjectReviewMcpDataTools.KeysToolName,
            _ => ProjectReviewMcpDataTools.RecordToolName,
        };
        IReadOnlyDictionary<string, object?> arguments = tool switch
        {
            ProjectReviewMcpDataTools.AssetsToolName => Args(),
            ProjectReviewMcpDataTools.KeysToolName => Args(("asset", "Data/Buildings")),
            _ => Args(("asset", "Data/Buildings"), ("key", "Barn")),
        };

        CallToolResult result = await harness.Client.CallToolAsync(
            tool,
            arguments,
            cancellationToken: harness.Token);

        Assert.True(result.IsError);
        Assert.Equal(
            "SDVKit review data unavailable [dataResponseInvalid].",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private static JsonElement AssertSuccessfulJson(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using JsonDocument textJson = JsonDocument.Parse(text.Text);
        Assert.True(JsonElement.DeepEquals(structured, textJson.RootElement));
        return structured;
    }

    private static Dictionary<string, object?> Args(
        params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value);

    private static LiveLabCommandResult Ready(ReviewDataQuery query)
    {
        ReviewDataReport report = query.Operation switch
        {
            ReviewDataContract.AssetsOperation => new ReviewDataReport(
                ReviewDataContract.SchemaVersion,
                "ready",
                query.Operation,
                "1.6.15",
                "1.6.15.24356",
                null,
                null,
                null,
                null,
                null,
                Enumerable.Range(
                        0,
                        Math.Min(query.Limit, Math.Max(0, 125 - query.Offset)))
                    .Select(index => new ReviewDataAssetReport(
                        index == 0 ? "Data/Buildings" : $"Data/Asset{query.Offset + index}",
                        "System.Collections.Generic.Dictionary",
                        "dictionary",
                        "string",
                        20,
                        Supported: true,
                        ProblemCode: null))
                    .ToArray(),
                null,
                Page(query, 125),
                new ReviewDataCoverageReport(125, 125, 125, 0, 0, 0),
                null,
                []),
            ReviewDataContract.KeysOperation => new ReviewDataReport(
                ReviewDataContract.SchemaVersion,
                "ready",
                query.Operation,
                "1.6.15",
                "1.6.15.24356",
                query.Asset,
                "System.Collections.Generic.Dictionary",
                "dictionary",
                "string",
                null,
                null,
                Enumerable.Range(
                        0,
                        Math.Min(query.Limit, Math.Max(0, 20 - query.Offset)))
                    .Select(index => index switch
                    {
                        0 => "Barn",
                        1 => "Coop",
                        _ => $"Key{query.Offset + index}",
                    })
                    .ToArray(),
                Page(query, 20),
                null,
                null,
                []),
            ReviewDataContract.GetOperation => RecordReport(query),
            _ => throw new InvalidOperationException("Unexpected review-data operation."),
        };
        return new LiveLabCommandResult(0, report);
    }

    private static ReviewDataPage Page(ReviewDataQuery query, int total)
    {
        int returned = Math.Min(query.Limit, Math.Max(0, total - query.Offset));
        long endOffset = (long)query.Offset + returned;
        return new ReviewDataPage(
            query.Offset,
            query.Limit,
            returned,
            total,
            endOffset < total ? (int)endOffset : null);
    }

    private static ReviewDataReport RecordReport(ReviewDataQuery query)
    {
        string shape = query.Asset switch
        {
            "Data/List" => "list",
            "Data/Singleton" => "singleton",
            _ => "dictionary",
        };
        string keyKind = shape switch
        {
            "list" => "index",
            "singleton" => "singleton",
            _ => "string",
        };
        JsonElement record = JsonSerializer.SerializeToElement(new
        {
            id = query.Key,
            value = 7,
        });
        return new ReviewDataReport(
            ReviewDataContract.SchemaVersion,
            "ready",
            query.Operation,
            "1.6.15",
            "1.6.15.24356",
            query.Asset,
            "Example.DataType",
            shape,
            keyKind,
            query.Key,
            null,
            null,
            null,
            null,
            record,
            []);
    }

    private static ReviewDataReport Failure(
        string operation,
        string code,
        string message) =>
        new(
            ReviewDataContract.SchemaVersion,
            "blocked",
            operation,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [new ReviewDataProblem(code, message)]);

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
            ProjectReviewMcpDataQueryRunner runQuery)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                "sdvkit-data-test");
            McpServer server = McpServer.Create(
                transport,
                ProjectReviewMcpServer.CreateOptions(reader, runQuery));
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
