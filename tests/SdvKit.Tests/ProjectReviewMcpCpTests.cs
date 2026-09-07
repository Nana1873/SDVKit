using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

public sealed partial class ProjectReviewMcpDiagnosticsTests
{
    private static Dictionary<string, object?> CpArgs(bool refresh = false) => refresh
        ? Args(("packId", "Test.Pack"), ("providerId", ProjectReviewCpDiagnosis.ProviderId),
            ("files", (string[])["patches/item.json"]), ("asset", "Data/Objects"), ("key", "388"))
        : Args(("packId", "Test.Pack"), ("providerId", ProjectReviewCpDiagnosis.ProviderId), ("asset", "Data/Objects"));

    private static Func<string, LiveLabCommandResult> CpMcpSender(TemporaryDirectory temporary, PreparedReview review,
        List<string> commands, string mode = "success") => command =>
    {
        commands.Add(command);
        string? message = null;
        if (command.StartsWith("patch reload", StringComparison.Ordinal))
        {
            if (mode is "uncertain" or "timeout") return new(mode == "uncertain" ? 3 : 0,
                new ProjectReviewCommandReport(1, null, temporary.Path, "running", null, mode == "uncertain" ? null : true, [], []));
            message = "[08:00:02 INFO  ContentPatcher] Content pack reloaded.\n";
        }
        else if (command.StartsWith("patch summary", StringComparison.Ordinal))
            message = CpSummary.Replace("Content Patcher", "ContentPatcher", StringComparison.Ordinal)
                .Replace("Patcher]\n", "Patcher] \n", StringComparison.Ordinal) + "\n";
        else if (command.StartsWith("patch parse", StringComparison.Ordinal))
            message = CpMarker(command.Split('"')[1]).Replace("Content Patcher", "ContentPatcher", StringComparison.Ordinal);
        else
        {
            Assert.StartsWith("sdvkit data ", command);
            if (mode == "observationFailure") return new(3, new ProjectReviewCommandReport(1, null, temporary.Path, "blocked", null, false, [], []));
            string id = command.Split(' ')[2];
            var data = new ReviewDataReport(1, "ready", "get", "test", "test", "Data/Objects", "object", "dictionary", "string", mode == "mismatchedObservation" ? "390" : "388",
                null, null, null, null, JsonSerializer.SerializeToElement(new { DisplayName = "after" }), []);
            File.WriteAllText(ReviewDataContract.ResponsePath(LiveLabPaths.Resolve(temporary.Path).RuntimePath, id),
                JsonSerializer.Serialize(new ReviewDataResponseEnvelope(1, id, data)));
        }
        if (message is not null)
            File.AppendAllText(Path.Combine(LiveLabPaths.Resolve(temporary.Path).StardewDataPath, "ErrorLogs", "SMAPI-latest.txt"), message);
        return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "running", null, true, [], []));
    };

    [Fact]
    public async Task CpMcpDefaultDiagnosisAndSeparatelyGrantedRefreshHaveClosedContracts()
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var commands = new List<string>();
        var send = CpMcpSender(temporary, review, commands);
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader,
            runCpDiagnosis: (pack, provider, asset, parse) => ProjectReviewCpDiagnosis.Execute(review.Reader, pack, provider, asset, parse, send)));
        var tools = (await harness.Client.ListToolsAsync(new ListToolsRequestParams(), harness.Token)).Tools;
        var diagnose = Assert.Single(tools, t => t.Name == ProjectReviewMcpCpTools.DiagnoseToolName);
        Assert.DoesNotContain(tools, t => t.Name == ProjectReviewMcpCpTools.RefreshToolName);
        Assert.True(diagnose.Annotations!.ReadOnlyHint);
        Assert.False(diagnose.InputSchema.GetProperty("additionalProperties").GetBoolean());
        var result = await harness.Client.CallToolAsync(diagnose.Name, CpArgs(), cancellationToken: harness.Token);
        var json = AssertSuccessfulJson(result);
        Assert.True(Json.Schema.JsonSchema.FromText(diagnose.OutputSchema!.Value.GetRawText()).Evaluate(JsonNode.Parse(json.GetRawText())).IsValid);
        Assert.Equal("ready", json.GetProperty("state").GetString());
        Assert.Equal(4, json.GetProperty("summary").GetProperty("patches").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reload").ValueKind);
        Assert.DoesNotContain(commands, c => c.StartsWith("patch reload", StringComparison.Ordinal) || c.StartsWith("sdvkit data", StringComparison.Ordinal));
        // Listing and diagnosis use the same connection; absence is permission, not a client allowlist.
        var listedAgain = await harness.Client.ListToolsAsync(new ListToolsRequestParams(), harness.Token);
        Assert.DoesNotContain(listedAgain.Tools, t => t.Name == ProjectReviewMcpCpTools.RefreshToolName);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("uncertain")]
    [InlineData("timeout")]
    [InlineData("observationFailure")]
    [InlineData("mismatchedObservation")]
    [InlineData("copyFailure")]
    public async Task CpMcpRefreshPreservesValidatedResultsAndIncompleteReceiptsWithoutRetries(string mode)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var permission = review.Reader.ReadContext().Context!;
        var commands = new List<string>();
        var send = CpMcpSender(temporary, review, commands, mode);
        int invocations = 0;
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader,
            runCpRefresh: (pack, provider, files, asset, key, grant) =>
            {
                invocations++;
                Assert.Same(permission, grant);
                return ProjectReviewCpRefresh.Execute(temporary.Path, grant.Staging.Target.SourceRoot, pack, provider, files, asset, key,
                    review.ProcessHost, () => ObservedAt.AddSeconds(1), send,
                    replace: mode == "copyFailure" ? (_, _) => throw new IOException("deliberate partial copy") : null,
                    responseTimeout: TimeSpan.FromMilliseconds(150), expectedContext: grant);
            }, cpRefreshPermission: permission));
        var tools = (await harness.Client.ListToolsAsync(new ListToolsRequestParams(), harness.Token)).Tools;
        var refresh = Assert.Single(tools, t => t.Name == ProjectReviewMcpCpTools.RefreshToolName);
        Assert.False(refresh.Annotations!.ReadOnlyHint);
        Assert.True(refresh.Annotations.DestructiveHint);
        Assert.False(refresh.Annotations.IdempotentHint);
        var result = await harness.Client.CallToolAsync(refresh.Name, CpArgs(refresh: true), cancellationToken: harness.Token);
        var json = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.True(Json.Schema.JsonSchema.FromText(refresh.OutputSchema!.Value.GetRawText()).Evaluate(JsonNode.Parse(json.GetRawText())).IsValid);
        Assert.Equal(1, invocations);
        Assert.Equal(mode == "copyFailure" ? 0 : 1, commands.Count(c => c.StartsWith("patch reload", StringComparison.Ordinal)));
        Assert.Equal(mode != "success", result.IsError);
        Assert.Equal(mode == "success" ? "observed" : "incomplete", json.GetProperty("state").GetString());
        Assert.Equal(mode != "success", json.GetProperty("refresh").GetProperty("requiresRestart").GetBoolean());
        Assert.False(json.GetProperty("process").TryGetProperty("executablePath", out _));
        if (mode == "success") Assert.Equal("after", json.GetProperty("observation").GetProperty("record").GetProperty("DisplayName").GetString());
        else
        {
            Assert.NotEqual("none", json.GetProperty("recovery").GetString());
            if (mode == "uncertain")
            {
                Assert.Equal(JsonValueKind.Null, json.GetProperty("refresh").GetProperty("commandWritten").ValueKind);
                Assert.True(json.GetProperty("diagnosis").GetProperty("reload").GetProperty("commandMayHaveBeenWritten").GetBoolean());
            }
            var retry = await harness.Client.CallToolAsync(refresh.Name, CpArgs(refresh: true), cancellationToken: harness.Token);
            Assert.True(retry.IsError);
            Assert.Equal(mode == "copyFailure" ? 0 : 1, commands.Count(c => c.StartsWith("patch reload", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task CpMcpRejectsUnsafeIncompleteAndForeignArgumentsBeforeDispatch()
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var permission = review.Reader.ReadContext().Context!;
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader,
            runCpDiagnosis: (_, _, _, _) => throw new InvalidOperationException("Invalid diagnosis dispatched"),
            runCpRefresh: (_, _, _, _, _, _) => throw new InvalidOperationException("Invalid refresh dispatched"), cpRefreshPermission: permission));
        foreach (var change in new Action<Dictionary<string, object?>>[]
        {
            a => a.Remove("packId"), a => a["packId"] = "full", a => a["providerId"] = "Other.Provider",
            a => a["asset"] = "Data/Objects;exit", a => a["parse"] = null, a => a["reload"] = true,
            a => a["parse"] = "{{Season}}\" other", a => a["asset"] = "Data/../private", a => a["role"] = "host",
        })
        {
            var args = CpArgs(); change(args);
            var result = await harness.Client.CallToolAsync(ProjectReviewMcpCpTools.DiagnoseToolName, args, cancellationToken: harness.Token);
            Assert.True(result.IsError); Assert.Null(result.StructuredContent);
        }
        foreach (var change in new Action<Dictionary<string, object?>>[]
        {
            a => a.Remove("files"), a => a["packId"] = "Other.Pack", a => a["sourceRoot"] = temporary.Path,
            a => a["files"] = new[] { "../outside.json" }, a => a["files"] = new[] { "config.json" },
            a => a["files"] = new[] { "a.json", "A.json" }, a => a["files"] = new[] { "content.json:secret.json" },
            a => a["files"] = Array.Empty<string>(), a => a["files"] = "content.json", a => a["key"] = 388,
            a => a["key"] = "\nexit", a => a["files"] = new[] { "content.json", null }, a => a["asset"] = "Maps/Town",
        })
        {
            var args = CpArgs(refresh: true); change(args);
            var result = await harness.Client.CallToolAsync(ProjectReviewMcpCpTools.RefreshToolName, args, cancellationToken: harness.Token);
            Assert.True(result.IsError); Assert.Null(result.StructuredContent);
        }
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("process")]
    [InlineData("source")]
    public async Task CpMcpPermissionAndLockedServiceBothRejectChangedStartupBindings(string changed)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var current = review.Reader.ReadContext().Context!;
        // A formerly granted binding differs from today's valid review, even for the same pack ID.
        var permission = changed switch
        {
            "launch" => current with { State = current.State with { LaunchId = new string('b', 32) } },
            "process" => current with { State = current.State with { OwnedProcessIdentity = current.State.OwnedProcessIdentity with { StartTimeUtc = StartedAt.AddSeconds(-1) } } },
            _ => current with
            {
                Staging = current.Staging with
                {
                    Artifacts = current.Staging.Artifacts.Select(a => a == current.Staging.Target
                ? a with { SourceRoot = Path.Combine(temporary.Path, "other-source") } : a).ToArray()
                }
            },
        };
        string before = ModBuildIdentity.ComputeFileSet(current.Staging.Target.StagingPath);
        var locked = ProjectReviewCpRefresh.Execute(temporary.Path, current.Staging.Target.SourceRoot, "Test.Pack",
            ProjectReviewCpDiagnosis.ProviderId, ["patches/item.json"], "Data/Objects", "388", review.ProcessHost,
            () => ObservedAt.AddSeconds(1), _ => throw new InvalidOperationException("Wrong binding dispatched"), expectedContext: permission);
        Assert.Equal("cpRefreshBindingChanged", locked.ErrorCode);
        Assert.Equal(0, locked.FilesReplaced);
        Assert.Null(locked.Refresh);
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(current.Staging.Target.StagingPath));
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader,
            runCpRefresh: (_, _, _, _, _, _) => throw new InvalidOperationException("Wrong permission dispatched"), cpRefreshPermission: permission));
        var result = await harness.Client.CallToolAsync(ProjectReviewMcpCpTools.RefreshToolName, CpArgs(refresh: true), cancellationToken: harness.Token);
        Assert.True(result.IsError);
        Assert.Contains("cpRefreshBindingChanged", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task CpMcpRejectsStaleAndMismatchedDiagnosisWithoutPublishingPayload()
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var commands = new List<string>();
        var send = CpMcpSender(temporary, review, commands);
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader,
            runCpDiagnosis: (pack, provider, asset, parse) => ProjectReviewCpDiagnosis.Execute(review.Reader, pack, provider, asset, parse, send)
                with
            { PackId = "Other.Pack" }));
        var result = await harness.Client.CallToolAsync(ProjectReviewMcpCpTools.DiagnoseToolName, CpArgs(), cancellationToken: harness.Token);
        Assert.True(result.IsError); Assert.Null(result.StructuredContent);
        int sent = commands.Count;
        File.Delete(review.Reader.ReadContext().Context!.State.StatusPath);
        var stale = await harness.Client.CallToolAsync(ProjectReviewMcpCpTools.DiagnoseToolName, CpArgs(), cancellationToken: harness.Token);
        Assert.True(stale.IsError); Assert.Null(stale.StructuredContent); Assert.Equal(sent, commands.Count);
    }

    [Theory]
    [InlineData("previousHash")]
    [InlineData("incompleteDiagnosis")]
    [InlineData("observationKey")]
    [InlineData("receiptFiles")]
    public async Task CpMcpRejectsMismatchedSuccessfulReceiptsAndObservations(string mode)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var permission = review.Reader.ReadContext().Context!;
        var commands = new List<string>();
        var send = CpMcpSender(temporary, review, commands);
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader,
            runCpRefresh: (pack, provider, files, asset, key, grant) =>
            {
                var result = ProjectReviewCpRefresh.Execute(temporary.Path, grant.Staging.Target.SourceRoot, pack, provider, files, asset, key,
                    review.ProcessHost, () => ObservedAt.AddSeconds(1), send, expectedContext: grant);
                Assert.Equal("observed", result.State);
                return mode switch
                {
                    "previousHash" => result with { Refresh = result.Refresh! with { PreviousBuildIdentity = "sha256:" + new string('9', 64) } },
                    "incompleteDiagnosis" => result with { Diagnosis = result.Diagnosis! with { State = "incomplete", ErrorCode = "cpResponseTimedOut" } },
                    "receiptFiles" => result with { Refresh = result.Refresh! with { Files = ["content.json"] } },
                    _ => result with { Observation = Assert.IsType<ReviewDataReport>(result.Observation) with { Key = "390" } },
                };
            }, cpRefreshPermission: permission));
        var called = await harness.Client.CallToolAsync(ProjectReviewMcpCpTools.RefreshToolName, CpArgs(refresh: true), cancellationToken: harness.Token);
        Assert.True(called.IsError);
        Assert.Null(called.StructuredContent);
        Assert.Contains("cpRefreshResponseInvalid", Assert.IsType<TextContentBlock>(Assert.Single(called.Content)).Text);
        Assert.Single(commands, c => c.StartsWith("patch reload", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("diagnosisIncomplete")]
    [InlineData("generationChanged")]
    public async Task CpMcpIncompleteDiagnosisAndChangedGenerationCannotBecomeSuccessful(string mode)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var commands = new List<string>();
        var send = CpMcpSender(temporary, review, commands);
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader,
            runCpDiagnosis: (pack, provider, asset, parse) =>
            {
                var result = ProjectReviewCpDiagnosis.Execute(review.Reader, pack, provider, asset, parse, send);
                if (mode == "diagnosisIncomplete") return result with
                {
                    State = "incomplete",
                    ErrorCode = "cpResponseTimedOut",
                    Summary = result.Summary! with
                    { State = "incomplete", ErrorCode = "cpResponseTimedOut", CommandWritten = false, CommandMayHaveBeenWritten = true },
                };
                var before = review.Reader.ReadContext().Context!;
                var staging = before.Staging with
                {
                    Artifacts = before.Staging.Artifacts.Select(a => a == before.Staging.Target
                    ? a with { CpRefresh = new(new string('e', 32), before.State.LaunchId, a.StagedBuildIdentity, a.StagedBuildIdentity, ["content.json"], true, false) }
                    : a).ToArray()
                };
                ProjectModStager.WriteReviewOwnership(staging.OwnershipPath, staging, replace: true);
                return result;
            }));
        var called = await harness.Client.CallToolAsync(ProjectReviewMcpCpTools.DiagnoseToolName, CpArgs(), cancellationToken: harness.Token);
        Assert.True(called.IsError);
        if (mode == "diagnosisIncomplete")
        {
            var json = Assert.IsType<JsonElement>(called.StructuredContent);
            Assert.True(json.GetProperty("summary").GetProperty("commandMayHaveBeenWritten").GetBoolean());
            Assert.Equal("cpResponseTimedOut", json.GetProperty("errorCode").GetString());
        }
        else Assert.Null(called.StructuredContent);
        Assert.Single(commands, c => c.StartsWith("patch summary", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missingReceipt")]
    [InlineData("restartCleared")]
    [InlineData("missingRecovery")]
    [InlineData("missingError")]
    [InlineData("rejectedReceipt")]
    [InlineData("foreignPendingReceipt")]
    public async Task CpMcpRejectsContradictoryPartialAndRecoveryReceipts(string mode)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var permission = review.Reader.ReadContext().Context!;
        var commands = new List<string>();
        var send = CpMcpSender(temporary, review, commands, "timeout");
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader,
            runCpRefresh: (pack, provider, files, asset, key, grant) =>
            {
                if (mode is "rejectedReceipt" or "foreignPendingReceipt")
                {
                    var receipt = new CpRefreshReceipt(Guid.NewGuid().ToString("N"), grant.State.LaunchId,
                        grant.Staging.Target.StagedBuildIdentity, grant.Staging.Target.StagedBuildIdentity,
                        files, false, true);
                    return new CpRefreshResult("rejected",
                        mode == "foreignPendingReceipt" ? "cpRefreshRestartRequired" : "cpRefreshNoChanges",
                        mode == "foreignPendingReceipt" ? "Do not retry this refresh." : "none",
                        grant.State.LaunchId, grant.State.OwnedProcessIdentity, grant.Staging.Target.BuildIdentity,
                        receipt, 0, false, null, null, 0);
                }
                var result = ProjectReviewCpRefresh.Execute(temporary.Path, grant.Staging.Target.SourceRoot,
                    pack, provider, files, asset, key, review.ProcessHost, () => ObservedAt.AddSeconds(1), send,
                    responseTimeout: TimeSpan.FromMilliseconds(150), expectedContext: grant);
                Assert.Equal("incomplete", result.State);
                return mode switch
                {
                    "missingReceipt" => result with { Refresh = null },
                    "restartCleared" => result with { Refresh = result.Refresh! with { RequiresRestart = false } },
                    "missingRecovery" => result with { Recovery = "none" },
                    _ => result with { ErrorCode = null },
                };
            }, cpRefreshPermission: permission));

        var called = await harness.Client.CallToolAsync(ProjectReviewMcpCpTools.RefreshToolName,
            CpArgs(refresh: true), cancellationToken: harness.Token);

        Assert.True(called.IsError);
        Assert.Null(called.StructuredContent);
        Assert.Contains("cpRefreshResponseInvalid",
            Assert.IsType<TextContentBlock>(Assert.Single(called.Content)).Text);
        Assert.True(mode is "rejectedReceipt" or "foreignPendingReceipt"
            ? commands.Count == 0
            : commands.Count(command => command.StartsWith("patch reload", StringComparison.Ordinal)) == 1);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("farmhand")]
    public async Task CpMcpToolsAreNeverAdvertisedForNetworkRoles(string role)
    {
        using TemporaryDirectory temporary = new();
        var reader = ProjectReviewMcpTests.CreateReadyNetworkReview(temporary, role);
        await using var harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(reader));
        var tools = (await harness.Client.ListToolsAsync(new ListToolsRequestParams(), harness.Token)).Tools;
        Assert.DoesNotContain(tools, t => t.Name is ProjectReviewMcpCpTools.DiagnoseToolName or ProjectReviewMcpCpTools.RefreshToolName);
    }
}
