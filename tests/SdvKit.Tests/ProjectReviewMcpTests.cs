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
    public async Task OfficialClientListsAndCallsTheSingleTypedReadOnlyTool()
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
        Tool tool = Assert.Single(listed.Tools);
        Assert.Equal(ProjectReviewMcpServer.RuntimeToolName, tool.Name);
        Assert.True(tool.Annotations?.ReadOnlyHint);
        Assert.False(tool.Annotations?.DestructiveHint);
        Assert.True(tool.Annotations?.IdempotentHint);
        Assert.False(tool.Annotations?.OpenWorldHint);
        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("object", tool.OutputSchema?.GetProperty("type").GetString());

        CallToolResult called = await client.CallToolAsync(
            new CallToolRequestParams { Name = ProjectReviewMcpServer.RuntimeToolName },
            timeout.Token);

        Assert.NotEqual(true, called.IsError);
        JsonElement structured = Assert.IsType<JsonElement>(called.StructuredContent);
        Assert.Equal(1, structured.GetProperty("schemaVersion").GetInt32());
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

    [Fact]
    public async Task RealStdioEntryPointEndsOnClientEofWithoutStoppingTheReview()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        using Process reviewProcess = StartReviewProcess();
        try
        {
            var reviewIdentity = new OwnedProcessIdentity(
                reviewProcess.Id,
                reviewProcess.StartTime.ToUniversalTime(),
                reviewProcess.MainModule?.FileName
                    ?? throw new InvalidOperationException("The test review executable is unavailable."));
            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            PrepareReadyReview(temporary, reviewIdentity, observedAt);
            string cliPath = Path.Combine(AppContext.BaseDirectory, "sdvkit.exe");
            Assert.True(File.Exists(cliPath), $"Missing test CLI: {cliPath}");
            using var serverProcess = new Process
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
            Assert.True(serverProcess.Start());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var transport = new StreamClientTransport(
                serverProcess.StandardInput.BaseStream,
                serverProcess.StandardOutput.BaseStream);
            await using (McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: timeout.Token))
            {
                CallToolResult called = await client.CallToolAsync(
                    new CallToolRequestParams
                    {
                        Name = ProjectReviewMcpServer.RuntimeToolName,
                    },
                    timeout.Token);
                Assert.NotEqual(true, called.IsError);
                Assert.Equal(LaunchId, called.StructuredContent?.GetProperty("launchId").GetString());
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
            if (!reviewProcess.HasExited)
            {
                reviewProcess.Kill(entireProcessTree: true);
                reviewProcess.WaitForExit();
            }
        }
    }

    private static ProjectReviewMcpRuntimeReader CreateReadyReview(
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
                observedAt));
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
