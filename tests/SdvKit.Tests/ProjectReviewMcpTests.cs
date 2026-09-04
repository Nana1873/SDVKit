using System.Diagnostics;
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
public sealed class ProjectReviewMcpTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ObservedAt = StartedAt.AddSeconds(10);

    private const string LaunchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HostLaunchId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string FarmhandLaunchId = "cccccccccccccccccccccccccccccccc";
    private const string NetworkFixtureId = "dddddddddddddddddddddddddddddddd";
    private const string NetworkSaveId = "SDVKit_123456789";
    private const string NetworkBuildIdentity =
        "sha256:9999999999999999999999999999999999999999999999999999999999999999";

    [Fact]
    public void RuntimeReadReturnsOnlyBoundedReviewAndGameState()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyReview(temporary);

        ProjectReviewMcpReadResult result = reader.Read();

        ProjectReviewMcpRuntimeSnapshot snapshot =
            Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(result.Snapshot);
        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal(LaunchId, snapshot.LaunchId);
        Assert.Equal("single", snapshot.Topology);
        Assert.Null(snapshot.Role);
        Assert.Equal(ObservedAt, snapshot.ObservedAtUtc);
        Assert.Equal("Nana.Target", snapshot.Target.UniqueId);
        Assert.Equal("1.0.0", snapshot.Target.Version);
        Assert.StartsWith("sha256:", snapshot.Target.BuildIdentity, StringComparison.Ordinal);
        Assert.Null(snapshot.TestSave);
        Assert.True(snapshot.Runtime.WorldReady);
        Assert.Equal("summer", snapshot.Runtime.Season);
        Assert.Equal(7, snapshot.Runtime.DayOfMonth);
        Assert.Equal(3, snapshot.Runtime.Year);
        Assert.Equal(930, snapshot.Runtime.TimeOfDay);
        Assert.Equal("Farm", snapshot.Runtime.LocationId);
        Assert.Equal(64, snapshot.Runtime.TileX);
        Assert.Equal(15, snapshot.Runtime.TileY);
        Assert.False(snapshot.Runtime.MenuOpen);
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole, HostLaunchId, "Farm", 930)]
    [InlineData(NetworkTwoContract.FarmhandRole, FarmhandLaunchId, "FarmHouse", 940)]
    public void RuntimeReadReturnsOnlyTheSelectedReadyNetworkRole(
        string role,
        string expectedLaunchId,
        string expectedLocation,
        int expectedTime)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyNetworkReview(
            temporary,
            role);

        ProjectReviewMcpRuntimeSnapshot snapshot =
            Assert.IsType<ProjectReviewMcpRuntimeSnapshot>(reader.Read().Snapshot);

        Assert.Equal(NetworkTwoContract.Topology, snapshot.Topology);
        Assert.Equal(role, snapshot.Role);
        Assert.Equal(expectedLaunchId, snapshot.LaunchId);
        Assert.Equal(expectedLocation, snapshot.Runtime.LocationId);
        Assert.Equal(expectedTime, snapshot.Runtime.TimeOfDay);
        Assert.Equal(NetworkFixtureId, snapshot.TestSave?.FixtureId);
        Assert.Equal(NetworkSaveId, snapshot.TestSave?.SaveId);
        Assert.Equal("Nana.Target", snapshot.Target.UniqueId);
    }

    [Fact]
    public void NetworkRuntimeReadFailsClosedWhenPeerTargetBindingChanges()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyNetworkReview(
            temporary,
            NetworkTwoContract.HostRole);
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        LiveLabState farmhand = Assert.IsType<LiveLabState>(
            new JsonLiveLabStateStore(farmhandPaths.StatePath).Read());
        new JsonLiveLabStateStore(farmhandPaths.StatePath).Write(
            farmhand with
            {
                ProjectMod = farmhand.ProjectMod! with
                {
                    BuildIdentity = "sha256:" + new string('1', 64),
                },
            });

        ProjectReviewMcpReadResult result = reader.Read();

        Assert.False(result.Succeeded);
        Assert.Equal("reviewOwnershipMismatch", result.ErrorCode);
    }

    [Fact]
    public void NetworkRuntimeReadFailsClosedWhenPairIsNotReciprocal()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyNetworkReview(
            temporary,
            NetworkTwoContract.FarmhandRole,
            reciprocalPair: false);

        ProjectReviewMcpReadResult result = reader.Read();

        Assert.False(result.Succeeded);
        Assert.Equal("reviewPairNotReady", result.ErrorCode);
    }

    [Fact]
    public void NetworkRuntimeReadFailsClosedWhenPairProcessIsNotRunning()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyNetworkReview(
            temporary,
            NetworkTwoContract.HostRole,
            processStatus: LabProcessInspectStatus.Exited);

        ProjectReviewMcpReadResult result = reader.Read();

        Assert.False(result.Succeeded);
        Assert.Equal("reviewPairProcessExited", result.ErrorCode);
    }

    [Theory]
    [InlineData("Exited", "reviewProcessExited")]
    [InlineData("IdentityMismatch", "reviewProcessMismatch")]
    [InlineData("Unreadable", "reviewProcessUnreadable")]
    public void RuntimeReadFailsClosedWhenExactProcessIsNotRunning(
        string processStatus,
        string expectedCode)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyReview(
            temporary,
            processStatus: Enum.Parse<LabProcessInspectStatus>(processStatus));

        ProjectReviewMcpReadResult result = reader.Read();

        Assert.False(result.Succeeded);
        Assert.Null(result.Snapshot);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.DoesNotContain(temporary.Path, result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeReadFailsClosedAfterTargetBuildBindingChanges()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyReview(temporary);
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabState state = Assert.IsType<LiveLabState>(
            new JsonLiveLabStateStore(paths.StatePath).Read());
        new JsonLiveLabStateStore(paths.StatePath).Write(
            state with
            {
                ProjectMod = state.ProjectMod! with
                {
                    BuildIdentity = "sha256:" + new string('1', 64),
                },
            });

        ProjectReviewMcpReadResult result = reader.Read();

        Assert.False(result.Succeeded);
        Assert.Equal("reviewOwnershipMismatch", result.ErrorCode);
    }

    [Fact]
    public void RuntimeReadFailsClosedWhenAlwaysOnSnapshotIsStale()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyReview(
            temporary,
            nowUtc: ObservedAt.AddSeconds(6));

        ProjectReviewMcpReadResult result = reader.Read();

        Assert.False(result.Succeeded);
        Assert.Equal("reviewRuntimeNotReady", result.ErrorCode);
    }

    [Fact]
    public async Task OfficialClientListsAndCallsTheSingleTypedReadOnlyTools()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyReview(temporary);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "sdvkit-test");
        await using McpServer server = McpServer.Create(
            serverTransport,
            ProjectReviewMcpServer.CreateOptions(
                reader,
                _ => throw new InvalidOperationException(
                    "This runtime-only call must not dispatch review data.")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = server.RunAsync(timeout.Token);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream());
        await using McpClient client = await McpClient.CreateAsync(
            clientTransport,
            cancellationToken: timeout.Token);

        ListToolsResult listed = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            timeout.Token);
        Assert.Equal(7, listed.Tools.Count);
        Tool tool = Assert.Single(
            listed.Tools,
            candidate => string.Equals(
                candidate.Name,
                ProjectReviewMcpServer.RuntimeToolName,
                StringComparison.Ordinal));
        Assert.Equal(ProjectReviewMcpServer.RuntimeToolName, tool.Name);
        Assert.True(tool.Annotations?.ReadOnlyHint);
        Assert.False(tool.Annotations?.DestructiveHint);
        Assert.True(tool.Annotations?.IdempotentHint);
        Assert.False(tool.Annotations?.OpenWorldHint);
        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        JsonElement outputSchema = Assert.IsType<JsonElement>(tool.OutputSchema);
        Assert.Equal("object", outputSchema.GetProperty("type").GetString());
        Assert.Contains("role", outputSchema.GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString()));
        Assert.Equal(
            ["single", "network-2"],
            outputSchema.GetProperty("properties")
                .GetProperty("topology")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());

        CallToolResult called = await client.CallToolAsync(
            new CallToolRequestParams { Name = ProjectReviewMcpServer.RuntimeToolName },
            timeout.Token);

        Assert.NotEqual(true, called.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(called.StructuredContent);
        Assert.Equal(1, structured.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("role").ValueKind);
        Assert.Equal("Nana.Target", structured
            .GetProperty("target").GetProperty("uniqueId").GetString());
        Assert.False(structured.TryGetProperty("testSave", out _));
        Assert.DoesNotContain("Path", structured.GetRawText(), StringComparison.OrdinalIgnoreCase);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(called.Content));
        Assert.True(JsonElement.DeepEquals(
            structured,
            JsonDocument.Parse(text.Text).RootElement));

        CallToolResult invalidArguments = await client.CallToolAsync(
            ProjectReviewMcpServer.RuntimeToolName,
            new Dictionary<string, object?> { ["unexpected"] = true },
            cancellationToken: timeout.Token);
        Assert.True(invalidArguments.IsError);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole)]
    [InlineData(NetworkTwoContract.FarmhandRole)]
    public async Task OfficialClientCallsTheSelectedNetworkRole(string role)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewMcpRuntimeReader reader = CreateReadyNetworkReview(
            temporary,
            role);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            "sdvkit-network-test");
        await using McpServer server = McpServer.Create(
            serverTransport,
            ProjectReviewMcpServer.CreateOptions(reader));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serverTask = server.RunAsync(timeout.Token);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream());
        await using McpClient client = await McpClient.CreateAsync(
            clientTransport,
            cancellationToken: timeout.Token);

        ListToolsResult listed = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            timeout.Token);
        Assert.Equal(
            [
                ProjectReviewMcpDiagnosticsTools.ModsToolName,
                ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                ProjectReviewMcpServer.RuntimeToolName,
                ProjectReviewMcpScreenshotTools.CaptureToolName,
            ],
            listed.Tools.Select(tool => tool.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

        CallToolResult called = await client.CallToolAsync(
            new CallToolRequestParams
            {
                Name = ProjectReviewMcpServer.RuntimeToolName,
            },
            timeout.Token);

        Assert.NotEqual(true, called.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(called.StructuredContent);
        Assert.Equal(NetworkTwoContract.Topology, structured
            .GetProperty("topology").GetString());
        Assert.Equal(role, structured.GetProperty("role").GetString());
        Assert.Equal(
            role == NetworkTwoContract.HostRole ? HostLaunchId : FarmhandLaunchId,
            structured.GetProperty("launchId").GetString());
        Assert.Equal(NetworkFixtureId, structured
            .GetProperty("testSave").GetProperty("fixtureId").GetString());

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStdioEntryPointEndsOnClientEofWithoutStoppingTheReview(
        bool allowInput)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        using Process reviewProcess = StartReviewProcess();
        Process? serverProcess = null;
        try
        {
            var reviewIdentity = new OwnedProcessIdentity(
                reviewProcess.Id,
                reviewProcess.StartTime.ToUniversalTime(),
                reviewProcess.MainModule?.FileName
                    ?? reviewProcess.StartInfo.FileName);
            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            PrepareReadyReview(temporary, reviewIdentity, observedAt);
            string cliPath = Path.Combine(AppContext.BaseDirectory, "sdvkit.exe");
            Assert.True(File.Exists(cliPath), $"Missing test CLI: {cliPath}");
            serverProcess = new Process
            {
                StartInfo = new ProcessStartInfo(cliPath)
                {
                    WorkingDirectory = temporary.Path,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            serverProcess.StartInfo.ArgumentList.Add("project");
            serverProcess.StartInfo.ArgumentList.Add("review");
            serverProcess.StartInfo.ArgumentList.Add("mcp");
            serverProcess.StartInfo.ArgumentList.Add("serve");
            if (allowInput)
            {
                serverProcess.StartInfo.ArgumentList.Add("--allow-input");
            }
            Assert.True(serverProcess.Start());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var transport = new StreamClientTransport(
                serverProcess.StandardInput.BaseStream,
                serverProcess.StandardOutput.BaseStream);
            await using (McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: timeout.Token))
            {
                ListToolsResult listed = await client.ListToolsAsync(
                    new ListToolsRequestParams(),
                    timeout.Token);
                string[] expectedTools =
                [
                    ProjectReviewMcpDataTools.AssetsToolName,
                    ProjectReviewMcpDataTools.KeysToolName,
                    ProjectReviewMcpDataTools.RecordToolName,
                    ProjectReviewMcpDiagnosticsTools.ModsToolName,
                    ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                    ProjectReviewMcpServer.RuntimeToolName,
                    ProjectReviewMcpScreenshotTools.CaptureToolName,
                ];
                if (allowInput)
                {
                    expectedTools =
                    [
                        .. expectedTools,
                        ProjectReviewMcpInputTools.PressToolName,
                        ProjectReviewMcpInputTools.CursorSetToolName,
                        ProjectReviewMcpInputTools.CursorClearToolName,
                        ProjectReviewMcpInputTools.WheelToolName,
                    ];
                }
                Assert.Equal(
                    expectedTools.Order(StringComparer.Ordinal).ToArray(),
                    listed.Tools.Select(tool => tool.Name)
                        .Order(StringComparer.Ordinal)
                        .ToArray());

                CallToolResult called = await client.CallToolAsync(
                    new CallToolRequestParams
                    {
                        Name = ProjectReviewMcpServer.RuntimeToolName,
                    },
                    timeout.Token);
                Assert.NotEqual(true, called.IsError);
                Assert.Equal(LaunchId, called.StructuredContent?.GetProperty("launchId").GetString());

                CallToolResult review = await client.CallToolAsync(
                    new CallToolRequestParams
                    {
                        Name = ProjectReviewMcpDiagnosticsTools.ReviewToolName,
                    },
                    timeout.Token);
                Assert.NotEqual(true, review.IsError);
                Assert.Equal(
                    LaunchId,
                    review.StructuredContent?.GetProperty("launchId").GetString());

                CallToolResult mods = await client.CallToolAsync(
                    ProjectReviewMcpDiagnosticsTools.ModsToolName,
                    new Dictionary<string, object?> { ["limit"] = 1 },
                    cancellationToken: timeout.Token);
                Assert.NotEqual(true, mods.IsError);
                Assert.Equal(
                    2,
                    mods.StructuredContent?.GetProperty("page")
                        .GetProperty("total").GetInt32());

                CallToolResult invalidDataCall = await client.CallToolAsync(
                    ProjectReviewMcpDataTools.KeysToolName,
                    new Dictionary<string, object?>(),
                    cancellationToken: timeout.Token);
                Assert.True(invalidDataCall.IsError);
            }

            serverProcess.StandardInput.Close();
            await serverProcess.WaitForExitAsync(timeout.Token);
            string standardError = await serverProcess.StandardError.ReadToEndAsync(timeout.Token);
            Assert.Equal(0, serverProcess.ExitCode);
            Assert.Equal(string.Empty, standardError);
            Assert.Equal(
                LabProcessInspectStatus.Running,
                new WindowsLabProcessHost().Inspect(reviewIdentity).Status);
        }
        finally
        {
            if (serverProcess is not null)
            {
                if (!serverProcess.HasExited)
                {
                    serverProcess.Kill(entireProcessTree: true);
                    serverProcess.WaitForExit();
                }

                serverProcess.Dispose();
            }

            if (!reviewProcess.HasExited)
            {
                reviewProcess.Kill(entireProcessTree: true);
                reviewProcess.WaitForExit();
            }
        }
    }

    internal static ProjectReviewMcpRuntimeReader CreateReadyNetworkReview(
        TemporaryDirectory temporary,
        string role,
        bool reciprocalPair = true,
        LabProcessInspectStatus processStatus = LabProcessInspectStatus.Running)
    {
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        ProjectReviewPreparedArtifact target = ProjectReviewStagerTests.Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewStagingResult staged = ProjectModStager.StageReview(
            [target],
            NetworkTwoContract.Topology,
            singlePaths);
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(staged.Staging);
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            new string('e', 32),
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
            singlePaths.TestSaveWorkPath,
            hostPaths.TestSaveScenarioLogPath);
        var hostProcess = new OwnedProcessIdentity(
            4242,
            StartedAt,
            Path.Combine(temporary.Path, "StardewModdingAPI.exe"));
        var farmhandProcess = new OwnedProcessIdentity(
            4243,
            StartedAt,
            Path.Combine(temporary.Path, "StardewModdingAPI.exe"));
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
            "Loaded by SMAPI.");
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
            new RuntimeSnapshotMarker(
                RuntimeSnapshotContract.SchemaVersion,
                true,
                "summer",
                7,
                3,
                930,
                "Farm",
                64,
                15,
                false,
                ObservedAt),
            enableServer: true,
            ipConnectionsEnabled: true,
            foregroundProcessId: 9001);
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
                RemotePlayerId: reciprocalPair ? 101 : 303,
                TestSaveContract.PlayerName,
                "Exact pair joined.",
                farmhandNetwork.NetworkLogPath),
            projectMod,
            new RuntimeSnapshotMarker(
                RuntimeSnapshotContract.SchemaVersion,
                true,
                "summer",
                7,
                3,
                940,
                "FarmHouse",
                8,
                9,
                false,
                ObservedAt),
            enableServer: null,
            ipConnectionsEnabled: null,
            foregroundProcessId: 9002);

        return new ProjectReviewMcpRuntimeReader(
            temporary.Path,
            NetworkTwoContract.Topology,
            role,
            new FakeProcessHost(processStatus),
            () => ObservedAt.AddSeconds(1));
    }

    private static void WriteNetworkStatus(
        LiveLabState state,
        TestSaveStatusMarker? testSave,
        NetworkTwoStatusMarker network,
        ProjectModStatusMarker projectMod,
        RuntimeSnapshotMarker runtime,
        bool? enableServer,
        bool? ipConnectionsEnabled,
        int foregroundProcessId)
    {
        var marker = new AlwaysOnStatusMarker(
            1,
            state.LaunchId,
            state.OwnedProcessIdentity.ProcessId,
            state.OwnedProcessIdentity.StartTimeUtc,
            "active",
            600,
            IsActive: false,
            PauseWhenOutOfFocus: false,
            ObservedAt,
            testSave,
            enableServer,
            ipConnectionsEnabled,
            network,
            ForegroundWindowHandle: 1,
            foregroundProcessId,
            projectMod,
            runtime,
            LoadedMods: ReadyLoadedMods(ObservedAt));
        File.WriteAllText(
            state.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    internal static ProjectReviewMcpRuntimeReader CreateReadyReview(
        TemporaryDirectory temporary,
        LabProcessInspectStatus processStatus = LabProcessInspectStatus.Running,
        DateTimeOffset? nowUtc = null)
    {
        var process = new OwnedProcessIdentity(
            4242,
            StartedAt,
            Path.Combine(temporary.Path, "StardewModdingAPI.exe"));
        PrepareReadyReview(temporary, process, ObservedAt);
        return new ProjectReviewMcpRuntimeReader(
            temporary.Path,
            new FakeProcessHost(processStatus),
            () => nowUtc ?? ObservedAt.AddSeconds(1));
    }

    private static void PrepareReadyReview(
        TemporaryDirectory temporary,
        OwnedProcessIdentity process,
        DateTimeOffset observedAt)
    {
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewPreparedArtifact target = ProjectReviewStagerTests.Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewStagingResult staged = ProjectModStager.StageReview([target], paths);
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(staged.Staging);
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
        var marker = new AlwaysOnStatusMarker(
            1,
            LaunchId,
            process.ProcessId,
            process.StartTimeUtc,
            "active",
            600,
            IsActive: false,
            PauseWhenOutOfFocus: false,
            observedAt,
            ForegroundWindowHandle: 1,
            ForegroundProcessId: process.ProcessId,
            ProjectMod: new ProjectModStatusMarker(
                ProjectModContract.SchemaVersion,
                ProjectModContract.LoadedPhase,
                staging.TargetLaunchState.UniqueId,
                staging.TargetLaunchState.Version,
                staging.TargetLaunchState.UniqueId,
                staging.TargetLaunchState.Version,
                staging.TargetLaunchState.BuildIdentity,
                LoadConfirmed: true,
                "Loaded by SMAPI."),
            Runtime: new RuntimeSnapshotMarker(
                RuntimeSnapshotContract.SchemaVersion,
                true,
                "summer",
                7,
                3,
                930,
                "Farm",
                64,
                15,
                false,
                observedAt),
            LoadedMods: ReadyLoadedMods(observedAt));
        File.WriteAllText(
            paths.StatusPath,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
    }

    private static Process StartReviewProcess()
    {
        string executable = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The test review process did not start.");
    }

    private static LoadedModsStatusMarker ReadyLoadedMods(
        DateTimeOffset capturedAtUtc) =>
        LoadedModsContract.CreateReady(
            [
                new LoadedModEntry(
                    LoadedModsContract.AlwaysOnUniqueId,
                    "0.6.1",
                    IsContentPack: false),
                new LoadedModEntry(
                    "Nana.Target",
                    "1.0.0",
                    IsContentPack: false),
            ],
            capturedAtUtc);

    private sealed class FakeProcessHost(LabProcessInspectStatus inspectStatus)
        : ILabProcessHost
    {
        public LabProcessStartResult Start(LabProcessStartSpec specification) =>
            throw new InvalidOperationException("The read-only MCP must not start a process.");

        public LabProcessInspectResult Inspect(OwnedProcessIdentity expected) =>
            new(inspectStatus);

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
