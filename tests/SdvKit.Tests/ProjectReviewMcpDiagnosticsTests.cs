using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed partial class ProjectReviewMcpDiagnosticsTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CapturedAt = StartedAt.AddSeconds(9);
    private static readonly DateTimeOffset ObservedAt = StartedAt.AddSeconds(10);

    private const int ProcessId = 987654321;
    private const string LaunchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HostLaunchId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string FarmhandLaunchId = "cccccccccccccccccccccccccccccccc";
    private const string NetworkFixtureId = "dddddddddddddddddddddddddddddddd";
    private const string NetworkSaveId = "SDVKit_123456789";
    private const string NetworkBuildIdentity =
        "sha256:9999999999999999999999999999999999999999999999999999999999999999";

    [Fact]
    public async Task OfficialClientListsClosedReadOnlyDiagnosticsSchemasForEachTopology()
    {
        using TemporaryDirectory singleTemporary = new();
        PreparedReview single = PrepareSingle(
            singleTemporary,
            CompleteArtifacts(singleTemporary.Path),
            ReadyLoadedMods(
                new LoadedModEntry("Alpha.Missing", "1.0.0", false),
                new LoadedModEntry("Beta.Version", "2.0.0", false),
                new LoadedModEntry("Gamma.Pack", "3.0.0", true),
                new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false),
                new LoadedModEntry("Zulu.Target", "1.0.0", false)));
        await using McpTestClient singleHarness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            single.Reader,
            runData: true ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null));

        ListToolsResult singleTools = await singleHarness.Client.ListToolsAsync(
            new ListToolsRequestParams(),
            singleHarness.Token);

        Assert.Equal(
            [
                ProjectReviewMcpDataTools.AssetsToolName,
                ProjectReviewMcpDataTools.KeysToolName,
                ProjectReviewMcpDataTools.RecordToolName,
                ProjectReviewMcpMenuTools.ToolName,
                ProjectReviewMcpLogTools.ToolName,
                ProjectReviewMcpDiagnosticsTools.ModsToolName,
                ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                ProjectReviewMcpServer.RuntimeToolName,
                ProjectReviewMcpScreenshotTools.CaptureToolName,
            ],
            singleTools.Tools.Select(tool => tool.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        AssertDiagnosticsContracts(singleTools.Tools);

        using TemporaryDirectory networkTemporary = new();
        PreparedNetwork network = PrepareNetwork(
            networkTemporary,
            NetworkArtifacts(networkTemporary.Path),
            ReadyLoadedMods(
                new LoadedModEntry("Nana.Companion", "1.0.0", false),
                new LoadedModEntry("Nana.Pack", "1.0.0", true),
                new LoadedModEntry("Nana.Target", "1.0.0", false),
                new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false)),
            ReadyLoadedMods(
                new LoadedModEntry("Nana.Companion", "8.8.8-farmhand", false),
                new LoadedModEntry("Nana.Pack", "1.0.0", true),
                new LoadedModEntry("Nana.Target", "1.0.0", false),
                new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false)));
        await using McpTestClient networkHarness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            network.HostReader,
            runData: false ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null));

        ListToolsResult networkTools = await networkHarness.Client.ListToolsAsync(
            new ListToolsRequestParams(),
            networkHarness.Token);

        Assert.Equal(
            [
                ProjectReviewMcpMenuTools.ToolName,
                ProjectReviewMcpLogTools.ToolName,
                ProjectReviewMcpDiagnosticsTools.ModsToolName,
                ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                ProjectReviewMcpServer.RuntimeToolName,
                ProjectReviewMcpScreenshotTools.CaptureToolName,
            ],
            networkTools.Tools.Select(tool => tool.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        AssertDiagnosticsContracts(networkTools.Tools);
    }

    [Fact]
    public async Task SingleToolsProjectExactStagingAndRoleLocalLoadDiagnosticsWithoutSecrets()
    {
        using TemporaryDirectory temporary = new();
        string secret = $"ENV_TOKEN=do-not-leak::{temporary.Path}\nPID={ProcessId}";
        string normalModsPath = Path.Combine(temporary.Path, "normal-mod-manager-Mods");
        Directory.CreateDirectory(normalModsPath);
        string normalModsSentinel = Path.Combine(normalModsPath, "private-sentinel.txt");
        File.WriteAllText(normalModsSentinel, secret);
        PreparedReview prepared = PrepareSingle(
            temporary,
            CompleteArtifacts(temporary.Path),
            ReadyLoadedMods(
                new LoadedModEntry("Beta.Version", "9.9.9", false),
                new LoadedModEntry("Gamma.Pack", "3.0.0", false),
                new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false),
                new LoadedModEntry("Zulu.Target", "1.0.0", false)),
            secret);
        await using McpTestClient harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            prepared.Reader,
            runData: true ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null));

        JsonElement review = AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpDiagnosticsTools.ReviewToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token));
        JsonElement mods = AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpDiagnosticsTools.ModsToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token));

        Assert.Equal("ready", review.GetProperty("state").GetString());
        Assert.Equal(LaunchId, review.GetProperty("launchId").GetString());
        Assert.Equal("single", review.GetProperty("topology").GetString());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("role").ValueKind);
        Assert.Equal("running", review.GetProperty("process").GetProperty("state").GetString());
        Assert.True(review.GetProperty("process").GetProperty("identityVerified").GetBoolean());
        Assert.True(review.GetProperty("process").GetProperty("statusFresh").GetBoolean());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("testSave").ValueKind);
        JsonElement target = review.GetProperty("target");
        Assert.Equal("Zulu.Target", target.GetProperty("uniqueId").GetString());
        Assert.Equal("1.0.0", target.GetProperty("version").GetString());
        Assert.Equal("smapiMod", target.GetProperty("kind").GetString());
        Assert.Equal("loaded", target.GetProperty("loadStatus").GetString());
        Assert.StartsWith(
            "sha256:",
            target.GetProperty("buildIdentity").GetString(),
            StringComparison.Ordinal);

        JsonElement[] staged = review.GetProperty("stagedArtifacts")
            .EnumerateArray().ToArray();
        Assert.Equal(
            ["Zulu.Target", "Alpha.Missing", "Beta.Version", "Gamma.Pack"],
            staged.Select(item => item.GetProperty("uniqueId").GetString()!).ToArray());
        Assert.Equal(
            ["target", "companion", "companion", "contentPack"],
            staged.Select(item => item.GetProperty("role").GetString()!).ToArray());
        Assert.Equal(
            "Zulu.Target",
            staged[3].GetProperty("contentPackFor").GetString());

        Assert.Equal(50, mods.GetProperty("page").GetProperty("limit").GetInt32());
        Assert.Equal(5, mods.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(
            [
                "Alpha.Missing",
                "Beta.Version",
                "Gamma.Pack",
                "SDVKit.AlwaysOn",
                "Zulu.Target",
            ],
            mods.GetProperty("mods").EnumerateArray()
                .Select(item => item.GetProperty("uniqueId").GetString()!).ToArray());

        AssertMod(
            FindMod(mods, "Alpha.Missing"),
            "companion",
            "smapiMod",
            loadedKind: null,
            "1.0.0",
            loadedVersion: null,
            "notLoaded",
            "modNotLoaded",
            "The selected mod was not reported as loaded by the role-local SMAPI registry.");
        AssertMod(
            FindMod(mods, "Beta.Version"),
            "companion",
            "smapiMod",
            "smapiMod",
            "2.0.0",
            "9.9.9",
            "versionMismatch",
            "modVersionMismatch",
            "The role-local SMAPI registry reported a different version than the selected staging metadata.");
        AssertMod(
            FindMod(mods, "Gamma.Pack"),
            "contentPack",
            "contentPack",
            "smapiMod",
            "3.0.0",
            "3.0.0",
            "kindMismatch",
            "modKindMismatch",
            "The role-local SMAPI registry reported a different mod kind than the selected staging metadata.");
        AssertMod(
            FindMod(mods, "SDVKit.AlwaysOn"),
            "sdvkitSupport",
            expectedKind: null,
            "smapiMod",
            expectedVersion: null,
            "0.6.1",
            "loaded",
            errorCode: null,
            errorMessage: null);
        AssertMod(
            FindMod(mods, "Zulu.Target"),
            "target",
            "smapiMod",
            "smapiMod",
            "1.0.0",
            "1.0.0",
            "loaded",
            errorCode: null,
            errorMessage: null);

        AssertNoPrivateState(review, temporary.Path, secret);
        AssertNoPrivateState(mods, temporary.Path, secret);
        Assert.Equal(secret, File.ReadAllText(normalModsSentinel));
    }

    [Fact]
    public async Task DiagnosticsRemainAvailableWhenTheSelectedTargetIsNotLoaded()
    {
        using TemporaryDirectory temporary = new();
        PreparedReview prepared = PrepareSingle(
            temporary,
            [ProjectReviewStagerTests.Artifact(
                temporary.Path,
                "Target",
                ProjectReviewArtifactRole.Target,
                "Nana.Target")],
            ReadyLoadedMods(new LoadedModEntry(
                LoadedModsContract.AlwaysOnUniqueId,
                "0.6.1",
                IsContentPack: false)),
            targetLoaded: false);
        await using McpTestClient harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            prepared.Reader,
            runData: true ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null));

        JsonElement review = AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpDiagnosticsTools.ReviewToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token));
        JsonElement mods = AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpDiagnosticsTools.ModsToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token));
        CallToolResult runtime = await harness.Client.CallToolAsync(
            ProjectReviewMcpServer.RuntimeToolName,
            new Dictionary<string, object?>(),
            cancellationToken: harness.Token);

        Assert.Equal(
            "notLoaded",
            review.GetProperty("target").GetProperty("loadStatus").GetString());
        Assert.Equal(
            "notLoaded",
            FindMod(mods, "Nana.Target").GetProperty("loadStatus").GetString());
        Assert.True(runtime.IsError);
        Assert.Contains(
            "[reviewRuntimeNotReady]",
            Assert.IsType<TextContentBlock>(Assert.Single(runtime.Content)).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModsPaginationIsDeterministicForDefaultMiddleEndAndBeyondEndPages()
    {
        using TemporaryDirectory temporary = new();
        PreparedReview prepared = PrepareSingle(
            temporary,
            CompleteArtifacts(temporary.Path),
            ReadyLoadedMods(
                new LoadedModEntry("Beta.Version", "2.0.0", false),
                new LoadedModEntry("Gamma.Pack", "3.0.0", true),
                new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false),
                new LoadedModEntry("Zulu.Target", "1.0.0", false)));
        await using McpTestClient harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            prepared.Reader,
            runData: true ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null));

        JsonElement defaultPage = await CallMods(harness, Args());
        JsonElement middle = await CallMods(
            harness,
            Args(("offset", 1), ("limit", 2)));
        JsonElement repeatedMiddle = await CallMods(
            harness,
            Args(("offset", 1), ("limit", 2)));
        JsonElement end = await CallMods(
            harness,
            Args(("offset", 4), ("limit", 2)));
        JsonElement beyond = await CallMods(
            harness,
            Args(("offset", 1000), ("limit", 3)));

        AssertPage(defaultPage, 0, 50, 5, 5, null);
        AssertPage(middle, 1, 2, 2, 5, 3);
        Assert.Equal(
            ["Beta.Version", "Gamma.Pack"],
            ModIds(middle));
        Assert.Equal(middle.GetRawText(), repeatedMiddle.GetRawText());
        AssertPage(end, 4, 2, 1, 5, null);
        Assert.Equal(["Zulu.Target"], ModIds(end));
        AssertPage(beyond, 1000, 3, 0, 5, null);
        Assert.Empty(ModIds(beyond));
    }

    [Fact]
    public async Task InvalidArgumentsAreRejectedBeforeReviewStateIsRead()
    {
        using TemporaryDirectory temporary = new();
        var processHost = new CountingProcessHost();
        PreparedReview prepared = PrepareSingle(
            temporary,
            CompleteArtifacts(temporary.Path),
            ReadyLoadedMods(
                new LoadedModEntry("Beta.Version", "2.0.0", false),
                new LoadedModEntry("Gamma.Pack", "3.0.0", true),
                new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false),
                new LoadedModEntry("Zulu.Target", "1.0.0", false)),
            processHost: processHost);
        await using McpTestClient harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            prepared.Reader,
            runData: true ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null));

        (string Tool, IReadOnlyDictionary<string, object?> Arguments)[] cases =
        [
            (ProjectReviewMcpDiagnosticsTools.ReviewToolName, Args(("extra", true))),
            (ProjectReviewMcpDiagnosticsTools.ModsToolName, Args(("extra", true))),
            (ProjectReviewMcpDiagnosticsTools.ModsToolName, Args(("offset", -1))),
            (ProjectReviewMcpDiagnosticsTools.ModsToolName, Args(("offset", (long)int.MaxValue + 1))),
            (ProjectReviewMcpDiagnosticsTools.ModsToolName, Args(("offset", "1"))),
            (ProjectReviewMcpDiagnosticsTools.ModsToolName, Args(("limit", 0))),
            (ProjectReviewMcpDiagnosticsTools.ModsToolName, Args(("limit", 101))),
            (ProjectReviewMcpDiagnosticsTools.ModsToolName, Args(("limit", null))),
        ];

        foreach ((string tool, IReadOnlyDictionary<string, object?> arguments) in cases)
        {
            CallToolResult result = await harness.Client.CallToolAsync(
                tool,
                arguments,
                cancellationToken: harness.Token);

            Assert.True(result.IsError);
            Assert.StartsWith(
                "Invalid arguments",
                Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text,
                StringComparison.Ordinal);
        }

        Assert.Equal(0, processHost.InspectCount);
    }

    [Theory]
    [InlineData("unexpectedIdentity", "reviewLoadedModsMismatch")]
    [InlineData("missingAlwaysOn", "reviewLoadedModsInvalid")]
    [InlineData("malformedInventory", "reviewLoadedModsInvalid")]
    public async Task InvalidRoleLocalInventoriesFailClosedWithoutSentinelLeak(
        string failure,
        string expectedCode)
    {
        using TemporaryDirectory temporary = new();
        string sentinel = "DoNotLeakRegistrySentinel";
        LoadedModsStatusMarker inventory = failure switch
        {
            "unexpectedIdentity" => ReadyLoadedMods(
                new LoadedModEntry("DoNotLeakRegistrySentinel", "7.7.7", false),
                new LoadedModEntry("Nana.Target", "1.0.0", false),
                new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false)),
            "missingAlwaysOn" => new LoadedModsStatusMarker(
                LoadedModsContract.SchemaVersion,
                CapturedAt,
                [new LoadedModEntry("Nana.Target", "1.0.0", false)],
                ProblemCode: null),
            "malformedInventory" => new LoadedModsStatusMarker(
                LoadedModsContract.SchemaVersion,
                CapturedAt,
                [
                    new LoadedModEntry("Nana.Target", "1.0.0", false),
                    new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false),
                ],
                ProblemCode: sentinel),
            _ => throw new InvalidOperationException("Unknown inventory failure."),
        };
        PreparedReview prepared = PrepareSingle(
            temporary,
            [ProjectReviewStagerTests.Artifact(
                temporary.Path,
                "Target",
                ProjectReviewArtifactRole.Target,
                "Nana.Target")],
            inventory,
            $"free-form::{sentinel}::{temporary.Path}");
        await using McpTestClient harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            prepared.Reader,
            runData: true ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null));

        foreach (string tool in new[]
                 {
                     ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                     ProjectReviewMcpDiagnosticsTools.ModsToolName,
                 })
        {
            CallToolResult result = await harness.Client.CallToolAsync(
                tool,
                new Dictionary<string, object?>(),
                cancellationToken: harness.Token);

            Assert.True(result.IsError);
            Assert.Null(result.StructuredContent);
            string message = Assert.IsType<TextContentBlock>(
                Assert.Single(result.Content)).Text;
            Assert.Equal(
                $"SDVKit review diagnostics unavailable [{expectedCode}].",
                message);
            Assert.DoesNotContain(sentinel, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                temporary.Path,
                message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                message,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NetworkToolsReturnOnlyTheExplicitSelectedRoleInventory()
    {
        using TemporaryDirectory temporary = new();
        PreparedNetwork prepared = PrepareNetwork(
            temporary,
            NetworkArtifacts(temporary.Path),
            ReadyLoadedMods(
                new LoadedModEntry("Nana.Companion", "1.0.0", false),
                new LoadedModEntry("Nana.Pack", "1.0.0", true),
                new LoadedModEntry("Nana.Target", "1.0.0", false),
                new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false)),
            LoadedModsContract.CreateReady(
                [
                    new LoadedModEntry("Nana.Companion", "8.8.8-farmhand", false),
                    new LoadedModEntry("Nana.Pack", "1.0.0", true),
                    new LoadedModEntry("Nana.Target", "1.0.0", false),
                    new LoadedModEntry("SDVKit.AlwaysOn", "0.6.1", false),
                ],
                CapturedAt.AddMilliseconds(1)));

        JsonElement hostReview;
        JsonElement hostMods;
        await using (McpTestClient hostHarness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            prepared.HostReader,
            runData: false ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null)))
        {
            hostReview = AssertSuccessfulJson(await hostHarness.Client.CallToolAsync(
                ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                new Dictionary<string, object?>(),
                cancellationToken: hostHarness.Token)).Clone();
            hostMods = AssertSuccessfulJson(await hostHarness.Client.CallToolAsync(
                ProjectReviewMcpDiagnosticsTools.ModsToolName,
                new Dictionary<string, object?>(),
                cancellationToken: hostHarness.Token)).Clone();
        }

        JsonElement farmhandReview;
        JsonElement farmhandMods;
        await using (McpTestClient farmhandHarness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(
            prepared.FarmhandReader,
            runData: false ? _ => throw new InvalidOperationException(
                "Diagnostics tests do not dispatch canonical Data tools.") : null)))
        {
            farmhandReview = AssertSuccessfulJson(await farmhandHarness.Client.CallToolAsync(
                ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                new Dictionary<string, object?>(),
                cancellationToken: farmhandHarness.Token)).Clone();
            farmhandMods = AssertSuccessfulJson(await farmhandHarness.Client.CallToolAsync(
                ProjectReviewMcpDiagnosticsTools.ModsToolName,
                new Dictionary<string, object?>(),
                cancellationToken: farmhandHarness.Token)).Clone();
        }

        Assert.Equal("host", hostReview.GetProperty("role").GetString());
        Assert.Equal(HostLaunchId, hostReview.GetProperty("launchId").GetString());
        Assert.Equal("farmhand", farmhandReview.GetProperty("role").GetString());
        Assert.Equal(FarmhandLaunchId, farmhandReview.GetProperty("launchId").GetString());
        Assert.Equal("host", hostMods.GetProperty("role").GetString());
        Assert.Equal("farmhand", farmhandMods.GetProperty("role").GetString());
        Assert.Equal(
            "loaded",
            FindMod(hostMods, "Nana.Companion").GetProperty("loadStatus").GetString());
        Assert.Equal(
            "1.0.0",
            FindMod(hostMods, "Nana.Companion").GetProperty("loadedVersion").GetString());
        Assert.Equal(
            "smapiMod",
            FindMod(hostMods, "Nana.Companion").GetProperty("expectedKind").GetString());
        Assert.Equal(
            "smapiMod",
            FindMod(hostMods, "Nana.Companion").GetProperty("loadedKind").GetString());
        Assert.Equal(
            "versionMismatch",
            FindMod(farmhandMods, "Nana.Companion").GetProperty("loadStatus").GetString());
        Assert.Equal(
            "8.8.8-farmhand",
            FindMod(farmhandMods, "Nana.Companion").GetProperty("loadedVersion").GetString());
        Assert.DoesNotContain(
            "8.8.8-farmhand",
            hostMods.GetRawText(),
            StringComparison.Ordinal);
        Assert.Equal(
            CapturedAt,
            hostMods.GetProperty("capturedAtUtc").GetDateTimeOffset());
        Assert.Equal(
            CapturedAt.AddMilliseconds(1),
            farmhandMods.GetProperty("capturedAtUtc").GetDateTimeOffset());
    }

    private static void AssertDiagnosticsContracts(IEnumerable<Tool> tools)
    {
        Tool[] diagnostics = tools.Where(tool => tool.Name is
                ProjectReviewMcpDiagnosticsTools.ReviewToolName
                or ProjectReviewMcpDiagnosticsTools.ModsToolName or ProjectReviewMcpLogTools.ToolName)
            .ToArray();
        Assert.Equal(3, diagnostics.Length);
        foreach (Tool tool in diagnostics)
        {
            Assert.True(tool.Annotations?.ReadOnlyHint);
            Assert.False(tool.Annotations?.DestructiveHint);
            Assert.True(tool.Annotations?.IdempotentHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
            AssertClosedObjectSchemas(tool.InputSchema);
            AssertClosedObjectSchemas(Assert.IsType<JsonElement>(tool.OutputSchema));
        }
    }

    private static void AssertClosedObjectSchemas(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (DeclaresObject(schema))
            {
                Assert.True(
                    schema.TryGetProperty("additionalProperties", out JsonElement additional)
                    && additional.ValueKind == JsonValueKind.False,
                    $"Object schema is not closed: {schema.GetRawText()}");
            }

            foreach (JsonProperty property in schema.EnumerateObject())
            {
                AssertClosedObjectSchemas(property.Value);
            }
        }
        else if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in schema.EnumerateArray())
            {
                AssertClosedObjectSchemas(item);
            }
        }
    }

    private static bool DeclaresObject(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            return false;
        }

        return type.ValueKind == JsonValueKind.String
            ? string.Equals(type.GetString(), "object", StringComparison.Ordinal)
            : type.ValueKind == JsonValueKind.Array
                && type.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), "object", StringComparison.Ordinal));
    }

    private static void AssertMod(
        JsonElement mod,
        string sourceCategory,
        string? expectedKind,
        string? loadedKind,
        string? expectedVersion,
        string? loadedVersion,
        string loadStatus,
        string? errorCode,
        string? errorMessage)
    {
        Assert.Equal(sourceCategory, mod.GetProperty("sourceCategory").GetString());
        AssertNullableString(expectedKind, mod.GetProperty("expectedKind"));
        AssertNullableString(loadedKind, mod.GetProperty("loadedKind"));
        AssertNullableString(expectedVersion, mod.GetProperty("expectedVersion"));
        AssertNullableString(loadedVersion, mod.GetProperty("loadedVersion"));
        Assert.Equal(loadStatus, mod.GetProperty("loadStatus").GetString());
        Assert.Empty(mod.GetProperty("warnings").EnumerateArray());
        JsonElement[] errors = mod.GetProperty("errors").EnumerateArray().ToArray();
        if (errorCode is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            JsonElement error = Assert.Single(errors);
            Assert.Equal(errorCode, error.GetProperty("code").GetString());
            Assert.Equal(errorMessage, error.GetProperty("message").GetString());
        }
    }

    private static void AssertNullableString(string? expected, JsonElement actual)
    {
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, actual.ValueKind);
        }
        else
        {
            Assert.Equal(expected, actual.GetString());
        }
    }

    private static void AssertNoPrivateState(
        JsonElement json,
        string projectRoot,
        string secret)
    {
        string text = json.GetRawText();
        Assert.DoesNotContain(projectRoot, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sourceRoot", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stagingPath", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("statusPath", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENV_TOKEN", text, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement FindMod(JsonElement snapshot, string uniqueId) =>
        snapshot.GetProperty("mods").EnumerateArray().Single(mod => string.Equals(
            mod.GetProperty("uniqueId").GetString(),
            uniqueId,
            StringComparison.Ordinal));

    private static string[] ModIds(JsonElement snapshot) =>
        snapshot.GetProperty("mods").EnumerateArray()
            .Select(item => item.GetProperty("uniqueId").GetString()!).ToArray();

    private static void AssertPage(
        JsonElement snapshot,
        int offset,
        int limit,
        int returned,
        int total,
        int? nextOffset)
    {
        JsonElement page = snapshot.GetProperty("page");
        Assert.Equal(offset, page.GetProperty("offset").GetInt32());
        Assert.Equal(limit, page.GetProperty("limit").GetInt32());
        Assert.Equal(returned, page.GetProperty("returned").GetInt32());
        Assert.Equal(total, page.GetProperty("total").GetInt32());
        if (nextOffset is null)
        {
            Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
        }
        else
        {
            Assert.Equal(nextOffset, page.GetProperty("nextOffset").GetInt32());
        }
    }

    private static async Task<JsonElement> CallMods(
        McpTestClient harness,
        IReadOnlyDictionary<string, object?> arguments) =>
        AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpDiagnosticsTools.ModsToolName,
            arguments,
            cancellationToken: harness.Token));

    private static JsonElement AssertSuccessfulJson(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using JsonDocument parsed = JsonDocument.Parse(text.Text);
        Assert.True(JsonElement.DeepEquals(structured, parsed.RootElement));
        return structured;
    }

    private static Dictionary<string, object?> Args(
        params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value);

    private static IReadOnlyList<ProjectReviewPreparedArtifact> CompleteArtifacts(
        string root) =>
    [
        ProjectReviewStagerTests.Artifact(
            root,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Zulu.Target"),
        ProjectReviewStagerTests.Artifact(
            root,
            "MissingCompanion",
            ProjectReviewArtifactRole.Companion,
            "Alpha.Missing"),
        ProjectReviewStagerTests.Artifact(
            root,
            "VersionCompanion",
            ProjectReviewArtifactRole.Companion,
            "Beta.Version",
            version: "2.0.0"),
        ProjectReviewStagerTests.Artifact(
            root,
            "Pack",
            ProjectReviewArtifactRole.ContentPack,
            "Gamma.Pack",
            version: "3.0.0",
            contentPackFor: "Zulu.Target"),
    ];

    private static IReadOnlyList<ProjectReviewPreparedArtifact> NetworkArtifacts(
        string root) =>
    [
        ProjectReviewStagerTests.Artifact(
            root,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target"),
        ProjectReviewStagerTests.Artifact(
            root,
            "Companion",
            ProjectReviewArtifactRole.Companion,
            "Nana.Companion"),
        ProjectReviewStagerTests.Artifact(
            root,
            "Pack",
            ProjectReviewArtifactRole.ContentPack,
            "Nana.Pack",
            contentPackFor: "Nana.Target"),
    ];

    private static LoadedModsStatusMarker ReadyLoadedMods(
        params LoadedModEntry[] mods) =>
        LoadedModsContract.CreateReady(mods, CapturedAt);

    private static PreparedReview PrepareSingle(
        TemporaryDirectory temporary,
        IReadOnlyList<ProjectReviewPreparedArtifact> artifacts,
        LoadedModsStatusMarker loadedMods,
        string? freeFormMessage = null,
        CountingProcessHost? processHost = null,
        bool targetLoaded = true)
    {
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewStagingResult staged = ProjectModStager.StageReview(artifacts, paths);
        Assert.Null(staged.Problem);
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(staged.Staging);
        var process = new OwnedProcessIdentity(
            ProcessId,
            StartedAt,
            Path.Combine(temporary.Path, "private", "StardewModdingAPI.exe"));
        var state = new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            LiveLabState.SingleTopology,
            LaunchId,
            process,
            paths.ModsPath,
            paths.StatusPath,
            paths.StopRequestPath,
            ProjectMod: staging.TargetLaunchState);
        new JsonLiveLabStateStore(paths.StatePath).Write(state);
        WriteStatus(
            state,
            targetLoaded
                ? new ProjectModStatusMarker(
                    ProjectModContract.SchemaVersion,
                    ProjectModContract.LoadedPhase,
                    staging.TargetLaunchState.UniqueId,
                    staging.TargetLaunchState.Version,
                    staging.TargetLaunchState.UniqueId,
                    staging.TargetLaunchState.Version,
                    staging.TargetLaunchState.BuildIdentity,
                    LoadConfirmed: true,
                    freeFormMessage ?? "Loaded by SMAPI.")
                : new ProjectModStatusMarker(
                    ProjectModContract.SchemaVersion,
                    ProjectModContract.FailedPhase,
                    staging.TargetLaunchState.UniqueId,
                    staging.TargetLaunchState.Version,
                    LoadedUniqueId: null,
                    LoadedVersion: null,
                    staging.TargetLaunchState.BuildIdentity,
                    LoadConfirmed: false,
                    freeFormMessage ?? "Private target load failure."),
            loadedMods);
        CountingProcessHost host = processHost ?? new CountingProcessHost();
        return new PreparedReview(
            new ProjectReviewMcpRuntimeReader(
                temporary.Path,
                host,
                () => ObservedAt.AddSeconds(1)),
            staging,
            host);
    }

    private static void WriteStatus(
        LiveLabState state,
        ProjectModStatusMarker projectMod,
        LoadedModsStatusMarker loadedMods)
    {
        var marker = new AlwaysOnStatusMarker(
            SchemaVersion: 1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            Phase: "active",
            Tick: 600,
            IsActive: false,
            PauseWhenOutOfFocus: false,
            ObservedAt,
            ProjectMod: projectMod,
            Runtime: Runtime("Farm", 930),
            LoadedMods: loadedMods);
        File.WriteAllText(
            state.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    private static PreparedNetwork PrepareNetwork(
        TemporaryDirectory temporary,
        IReadOnlyList<ProjectReviewPreparedArtifact> artifacts,
        LoadedModsStatusMarker hostLoadedMods,
        LoadedModsStatusMarker farmhandLoadedMods)
    {
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        ProjectReviewStagingResult staged = ProjectModStager.StageReview(
            artifacts,
            NetworkTwoContract.Topology,
            paths);
        Assert.Null(staged.Problem);
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(staged.Staging);
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            NetworkFixtureId,
            123456789,
            NetworkSaveId,
            TestSaveContract.PlayerName,
            TestSaveContract.FarmName,
            TestSaveContract.FavoriteThing);
        var testSave = new TestSaveLaunchState(
            TestSaveContract.ReviewMode,
            identity,
            Path.Combine(hostPaths.SavesPath, NetworkSaveId),
            paths.TestSaveWorkPath,
            hostPaths.TestSaveScenarioLogPath);
        var hostProcess = new OwnedProcessIdentity(
            ProcessId,
            StartedAt,
            Path.Combine(temporary.Path, "private", "StardewModdingAPI.exe"));
        var farmhandProcess = new OwnedProcessIdentity(
            ProcessId + 1,
            StartedAt,
            Path.Combine(temporary.Path, "private", "StardewModdingAPI.exe"));
        var hostNetwork = new NetworkTwoLaunchState(
            NetworkTwoContract.HostRole,
            NetworkBuildIdentity,
            NetworkFixtureId,
            NetworkSaveId,
            Path.Combine(hostPaths.RuntimePath, "network-2.log"));
        var farmhandNetwork = new NetworkTwoLaunchState(
            NetworkTwoContract.FarmhandRole,
            NetworkBuildIdentity,
            NetworkFixtureId,
            NetworkSaveId,
            Path.Combine(farmhandPaths.RuntimePath, "network-2.log"),
            ExpectedFarmhandId: 202);
        var hostState = new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            HostLaunchId,
            hostProcess,
            hostPaths.ModsPath,
            hostPaths.StatusPath,
            hostPaths.StopRequestPath,
            testSave,
            hostNetwork,
            staging.TargetLaunchState);
        var farmhandState = new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            FarmhandLaunchId,
            farmhandProcess,
            farmhandPaths.ModsPath,
            farmhandPaths.StatusPath,
            farmhandPaths.StopRequestPath,
            TestSave: null,
            farmhandNetwork,
            staging.TargetLaunchState);
        new JsonLiveLabStateStore(hostPaths.StatePath).Write(hostState);
        new JsonLiveLabStateStore(farmhandPaths.StatePath).Write(farmhandState);
        var projectMod = new ProjectModStatusMarker(
            ProjectModContract.SchemaVersion,
            ProjectModContract.LoadedPhase,
            staging.TargetLaunchState.UniqueId,
            staging.TargetLaunchState.Version,
            staging.TargetLaunchState.UniqueId,
            staging.TargetLaunchState.Version,
            staging.TargetLaunchState.BuildIdentity,
            LoadConfirmed: true,
            "private-free-form-message");
        WriteNetworkStatus(
            hostState,
            new TestSaveStatusMarker(
                TestSaveContract.SchemaVersion,
                TestSaveContract.ReviewMode,
                "passed",
                NetworkFixtureId,
                NetworkSaveId,
                IdentityVerified: true,
                WaitedTicks: 0,
                "Exact review fixture loaded.",
                hostPaths.TestSaveScenarioLogPath),
            new NetworkTwoStatusMarker(
                NetworkTwoContract.SchemaVersion,
                NetworkTwoContract.HostRole,
                "passed",
                NetworkBuildIdentity,
                NetworkFixtureId,
                NetworkSaveId,
                IdentityVerified: true,
                NetworkTwoContract.RequiredJoinedTicks,
                LocalPlayerId: 101,
                TestSaveContract.PlayerName,
                RemotePlayerId: 202,
                NetworkTwoContract.FarmhandName,
                "Exact pair joined.",
                hostNetwork.NetworkLogPath),
            projectMod,
            hostLoadedMods,
            enableServer: true,
            ipConnectionsEnabled: true,
            foregroundProcessId: 9001,
            Runtime("Farm", 930));
        WriteNetworkStatus(
            farmhandState,
            testSave: null,
            new NetworkTwoStatusMarker(
                NetworkTwoContract.SchemaVersion,
                NetworkTwoContract.FarmhandRole,
                "passed",
                NetworkBuildIdentity,
                NetworkFixtureId,
                NetworkSaveId,
                IdentityVerified: true,
                NetworkTwoContract.RequiredJoinedTicks,
                LocalPlayerId: 202,
                NetworkTwoContract.FarmhandName,
                RemotePlayerId: 101,
                TestSaveContract.PlayerName,
                "Exact pair joined.",
                farmhandNetwork.NetworkLogPath),
            projectMod,
            farmhandLoadedMods,
            enableServer: null,
            ipConnectionsEnabled: null,
            foregroundProcessId: 9002,
            Runtime("FarmHouse", 940));

        var processHost = new CountingProcessHost();
        return new PreparedNetwork(
            new ProjectReviewMcpRuntimeReader(
                temporary.Path,
                NetworkTwoContract.Topology,
                NetworkTwoContract.HostRole,
                processHost,
                () => ObservedAt.AddSeconds(1)),
            new ProjectReviewMcpRuntimeReader(
                temporary.Path,
                NetworkTwoContract.Topology,
                NetworkTwoContract.FarmhandRole,
                processHost,
                () => ObservedAt.AddSeconds(1)));
    }

    private static void WriteNetworkStatus(
        LiveLabState state,
        TestSaveStatusMarker? testSave,
        NetworkTwoStatusMarker network,
        ProjectModStatusMarker projectMod,
        LoadedModsStatusMarker loadedMods,
        bool? enableServer,
        bool? ipConnectionsEnabled,
        int foregroundProcessId,
        RuntimeSnapshotMarker runtime)
    {
        var marker = new AlwaysOnStatusMarker(
            SchemaVersion: 1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            Phase: "active",
            Tick: 600,
            IsActive: false,
            PauseWhenOutOfFocus: false,
            ObservedAt,
            TestSave: testSave,
            EnableServer: enableServer,
            IpConnectionsEnabled: ipConnectionsEnabled,
            NetworkTwo: network,
            ForegroundWindowHandle: 1,
            ForegroundProcessId: foregroundProcessId,
            ProjectMod: projectMod,
            Runtime: runtime,
            LoadedMods: loadedMods);
        File.WriteAllText(
            state.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    private static RuntimeSnapshotMarker Runtime(string locationId, int timeOfDay) =>
        new(
            RuntimeSnapshotContract.SchemaVersion,
            WorldReady: true,
            Season: "summer",
            DayOfMonth: 7,
            Year: 3,
            timeOfDay,
            locationId,
            TileX: 64,
            TileY: 15,
            MenuOpen: false,
            ObservedAt);

    private sealed record PreparedReview(
        ProjectReviewMcpRuntimeReader Reader,
        ProjectReviewStaging Staging,
        CountingProcessHost ProcessHost);

    private sealed record PreparedNetwork(
        ProjectReviewMcpRuntimeReader HostReader,
        ProjectReviewMcpRuntimeReader FarmhandReader);

    private sealed class CountingProcessHost : ILabProcessHost
    {
        public int InspectCount { get; private set; }

        public LabProcessStartResult Start(LabProcessStartSpec specification) =>
            throw new InvalidOperationException("The read-only MCP must not start a process.");

        public LabProcessInspectResult Inspect(OwnedProcessIdentity expected)
        {
            InspectCount++;
            return new LabProcessInspectResult(LabProcessInspectStatus.Running);
        }

        public LabProcessWaitResult WaitForExit(
            OwnedProcessIdentity expected,
            TimeSpan timeout) =>
            throw new InvalidOperationException("The read-only MCP must not wait for a process.");

        public LabProcessCloseResult RequestCloseAndWait(
            OwnedProcessIdentity expected,
            TimeSpan timeout) =>
            throw new InvalidOperationException("The read-only MCP must not close a process.");
    }

}
